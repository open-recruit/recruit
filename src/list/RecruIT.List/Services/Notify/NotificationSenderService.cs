using System.Diagnostics;
using System.Reflection;
using System.Text.Encodings.Web;
using Fluid;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mjml.Net;
using RecruIT.List.Data;
using RecruIT.List.Data.Entities;
using RecruIT.List.Models;
using RecruIT.List.Options;
using RecruIT.List.Services.Access;
using RecruIT.List.Services.Fhir;
using Task = System.Threading.Tasks.Task;

namespace RecruIT.List.Services.Notify;

/// <summary>
/// Recurring job: for every subscription with at least one pending (SentAt still null) Email
/// NotificationDelivery, checks whether that subscription is due a send *right now* - Instant
/// always is; Daily/Weekly/Monthly are checked against the subscription's current
/// Frequency/DayOfWeek/TimeOfDay/TimeZoneId via NotificationScheduling, using the last time this
/// subscription was actually sent (or its CreatedAt, if never sent) as the reference point. Due
/// subscriptions get every pending delivery sent as one grouped email - several events queued for
/// the same subscriber between ticks become one digest rather than several. Subscriptions whose
/// trial access has since been revoked are skipped and cleaned up the same way as ones that were
/// unsubscribed - see TrialAccessService.GetGrantedPairsAsync (no IsAdmin bypass there; see its
/// doc comment). The one exception is Auth:Enabled=false, where that check is skipped entirely -
/// see SendDueAsync.
/// </summary>
public sealed class NotificationSenderService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    ScreeningListService screeningListService,
    TrialAccessService accessService,
    IOptions<EmailNotificationOptions> mailerOptions,
    IOptions<AuthOptions> authOptions,
    INotificationChannel channel,
    RecruitMetrics metrics,
    ILogger<NotificationSenderService> logger
)
{
    /// <summary>
    /// The Hangfire recurring job id this runs under (see Program.cs) - also how the Notification
    /// Test page runs it on demand, through Hangfire rather than calling SendDueAsync directly, so
    /// the lock below keeps the two from sending the same deliveries twice.
    /// </summary>
    public const string RecurringJobId = "send-due-notification-emails";

    private static readonly MjmlRenderer Renderer = new();

    private static readonly TemplateOptions DigestTemplateOptions = CreateDigestTemplateOptions();

    private static readonly IFluidTemplate DigestTemplate = LoadDigestTemplate();

    /// <summary>
    /// A delivery that has failed to send for longer than this is given up on (removed) rather
    /// than retried forever - generous enough to comfortably outlast a Monthly subscriber's own
    /// cadence plus a prolonged SMTP outage, while still bounding retries for a permanently-bad
    /// address.
    /// </summary>
    private static readonly TimeSpan MaxDeliveryAge = TimeSpan.FromDays(30);

    // Nothing below claims a delivery before sending it, so two overlapping runs would both send
    // it - a scheduled tick and an on-demand one can otherwise overlap (ticks alone are 5 minutes
    // apart). A run that can't get the lock within the timeout fails and is retried by Hangfire.
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task SendDueAsync(CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var pendingSubscriptionIds = await db
            .NotificationDeliveries.Where(d =>
                d.Channel == NotificationChannel.Email && d.SentAt == null
            )
            .Select(d => d.NotificationSubscriptionId)
            .Distinct()
            .ToListAsync(ct);
        if (pendingSubscriptionIds.Count == 0)
        {
            return;
        }

        var (dueSubscriptionIds, toCleanUpSubscriptionIds) = await SortPendingSubscriptionsAsync(
            db,
            pendingSubscriptionIds,
            now,
            ct
        );
        await RemovePendingDeliveriesAsync(db, toCleanUpSubscriptionIds, ct);

        if (dueSubscriptionIds.Count == 0)
        {
            await db.SaveChangesAsync(ct); // persists any cleanup above
            return;
        }

        var due = await LoadDueAsync(db, dueSubscriptionIds, ct);

        // One SMTP session for every due recipient this tick, rather than reconnecting and
        // re-authenticating per recipient - real SMTP relays commonly rate-limit new connections
        // per source, so a batch of digests is exactly the kind of connection burst that trips it.
        await using var batch = await channel.BeginBatchAsync(ct: ct);

        foreach (var group in due.Deliveries.GroupBy(d => d.NotificationSubscriptionId))
        {
            await SendDigestAsync(db, batch, group.Key, [.. group], due, now, ct);
        }

        await db.SaveChangesAsync(ct);
    }

    private enum Verdict
    {
        /// <summary>Due a send right now.</summary>
        Send,

        /// <summary>Not due yet, or not possible to say when it is - left alone.</summary>
        Wait,

        /// <summary>Gone, or no longer allowed - its pending deliveries are removed.</summary>
        CleanUp,
    }

    /// <summary>
    /// Splits the subscriptions with a pending email into the ones to send to now and the ones
    /// whose pending deliveries are to be removed; the rest wait for their slot.
    /// </summary>
    private async Task<(List<Guid> Due, List<Guid> ToCleanUp)> SortPendingSubscriptionsAsync(
        AppDbContext db,
        List<Guid> pendingSubscriptionIds,
        DateTimeOffset now,
        CancellationToken ct
    )
    {
        var subscriptionsById = await db
            .NotificationSubscriptions.Where(s => pendingSubscriptionIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, ct);

        // One grouped query for every pending subscriber's last-sent time, instead of one query
        // per subscriber inside the loop below.
        var lastSentBySubscription = await db
            .NotificationDeliveries.Where(d =>
                pendingSubscriptionIds.Contains(d.NotificationSubscriptionId)
                && d.Channel == NotificationChannel.Email
                && d.SentAt != null
            )
            .GroupBy(d => d.NotificationSubscriptionId)
            .Select(g => new { SubscriptionId = g.Key, LastSentAt = g.Max(d => d.SentAt) })
            .ToDictionaryAsync(x => x.SubscriptionId, x => x.LastSentAt, ct);

        // With auth disabled, every request is DevBypassAuthenticationHandler's admin, who can
        // already see every trial - there's no per-user access left to enforce here, and requiring
        // a grant row (admins never otherwise need one) would just mean silently dropping the dev
        // admin's emails. null means "everyone has access", like GetAccessibleTrialIdentifiersAsync.
        var grantedPairs = authOptions.Value.Enabled
            ? await accessService.GetGrantedPairsAsync(
                subscriptionsById.Values.Select(s => s.SubjectId).Distinct().ToList(),
                subscriptionsById.Values.Select(s => s.Email).Distinct().ToList(),
                ct
            )
            : null;

        var due = new List<Guid>();
        var toCleanUp = new List<Guid>();
        foreach (var subscriptionId in pendingSubscriptionIds)
        {
            subscriptionsById.TryGetValue(subscriptionId, out var subscription);
            var verdict = Judge(
                subscription,
                grantedPairs,
                lastSentBySubscription.GetValueOrDefault(subscriptionId),
                now
            );
            switch (verdict)
            {
                case Verdict.Send:
                    due.Add(subscriptionId);
                    break;
                case Verdict.CleanUp:
                    toCleanUp.Add(subscriptionId);
                    break;
            }
        }

        return (due, toCleanUp);
    }

    private Verdict Judge(
        NotificationSubscription? subscription,
        IReadOnlySet<(string Identity, string TrialToken)>? grantedPairs,
        DateTimeOffset? lastSentAt,
        DateTimeOffset now
    )
    {
        if (subscription is null)
        {
            // Unsubscribed since these were queued - don't email them, just clean up rather
            // than leaving them to be re-checked (and re-orphaned) forever.
            return Verdict.CleanUp;
        }

        var hasAccess =
            grantedPairs is null
            || grantedPairs.Contains((subscription.SubjectId, subscription.TrialIdentifier))
            || grantedPairs.Contains((subscription.Email, subscription.TrialIdentifier));
        if (!hasAccess)
        {
            // Trial access was revoked since these were queued - same cleanup as an
            // unsubscribe for the pending deliveries, but the subscription row itself is left
            // alone in case access is restored later.
            return Verdict.CleanUp;
        }

        if (subscription.Frequency == NotificationFrequency.Instant)
        {
            return Verdict.Send;
        }

        DateTimeOffset nextSlot;
        try
        {
            nextSlot = NotificationScheduling.ComputeEmailScheduledFor(
                subscription.Frequency,
                subscription.DayOfWeek,
                subscription.TimeOfDay,
                subscription.TimeZoneId,
                lastSentAt ?? subscription.CreatedAt
            );
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // E.g. a time zone that no longer exists, or a Daily subscription with no time of day.
            // One subscription like that must not stop everyone else's digests - it's skipped (and
            // reported every tick until someone fixes or removes it).
            logger.LogError(
                ex,
                "Cannot work out when subscription {SubscriptionId} ({Frequency}, time zone {TimeZoneId}) is next due - skipping it",
                subscription.Id,
                subscription.Frequency,
                subscription.TimeZoneId
            );
            return Verdict.Wait;
        }

        return nextSlot > now ? Verdict.Wait : Verdict.Send;
    }

    private static async Task RemovePendingDeliveriesAsync(
        AppDbContext db,
        List<Guid> subscriptionIds,
        CancellationToken ct
    )
    {
        if (subscriptionIds.Count == 0)
        {
            return;
        }

        var toRemove = await db
            .NotificationDeliveries.Where(d =>
                subscriptionIds.Contains(d.NotificationSubscriptionId)
                && d.Channel == NotificationChannel.Email
                && d.SentAt == null
            )
            .ToListAsync(ct);
        db.NotificationDeliveries.RemoveRange(toRemove);
    }

    /// <summary>What a run needs to know to word and send the emails of its due subscriptions.</summary>
    private sealed record DueDeliveries(
        List<NotificationDelivery> Deliveries,
        Dictionary<Guid, NotificationEvent> Events,
        IReadOnlyDictionary<string, (string ListId, string StudyAcronym)?> ListInfoByTrial
    );

    private async Task<DueDeliveries> LoadDueAsync(
        AppDbContext db,
        List<Guid> dueSubscriptionIds,
        CancellationToken ct
    )
    {
        var deliveries = await db
            .NotificationDeliveries.Where(d =>
                d.Channel == NotificationChannel.Email
                && d.SentAt == null
                && dueSubscriptionIds.Contains(d.NotificationSubscriptionId)
            )
            .ToListAsync(ct);

        var eventIds = deliveries.Select(d => d.NotificationEventId).Distinct().ToList();
        var events = await db
            .NotificationEvents.AsNoTracking()
            .Where(e => eventIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, ct);

        // Several events commonly share a trial (and several deliveries across groups commonly
        // share an event's trial too) - resolve each trial's current acronym at most once for this
        // whole tick rather than once per delivery. Where the email's link leads is resolved
        // separately, at click time (see Program.cs's /notifications/email endpoint).
        var listInfoByTrial = await screeningListService.ResolveListsForTrialsAsync(
            deliveries
                .Where(d => events.ContainsKey(d.NotificationEventId))
                .Select(d => events[d.NotificationEventId].TrialIdentifier),
            ct
        );

        return new DueDeliveries(deliveries, events, listInfoByTrial);
    }

    /// <summary>
    /// Sends one subscriber's pending deliveries as a single digest email, and records the outcome:
    /// sent, or failed to be retried next tick (or given up on once too old).
    /// </summary>
    private async Task SendDigestAsync(
        AppDbContext db,
        INotificationBatch batch,
        Guid subscriptionId,
        List<NotificationDelivery> deliveries,
        DueDeliveries due,
        DateTimeOffset now,
        CancellationToken ct
    )
    {
        var (includedDeliveries, acronyms) = DescribeIncluded(deliveries, due);
        if (includedDeliveries.Count == 0)
        {
            return;
        }

        var subjectAcronym = acronyms.Count == 1 ? acronyms[0] : $"{acronyms.Count} trials";
        var subject = string.Format(mailerOptions.Value.SubjectTemplate, subjectAcronym);
        var digestId = Guid.NewGuid();
        var html = RenderHtml(includedDeliveries.Count, acronyms, EmailDigestLink(digestId));

        var sendStart = Stopwatch.GetTimestamp();
        try
        {
            await batch.SendAsync(deliveries[0].Email, subject, html, ct);
            metrics.RecordEmailSend(Stopwatch.GetElapsedTime(sendStart), succeeded: true);
            var sentAt = DateTimeOffset.UtcNow;
            foreach (var delivery in includedDeliveries)
            {
                delivery.SentAt = sentAt;
                delivery.EmailDigestId = digestId;
            }
        }
        // The run being cancelled (the app shutting down) isn't a failed send: it ends the run
        // instead of being counted against this recipient and retried against the next. A send
        // that merely timed out is also an OperationCanceledException, but not of our token.
        catch (Exception ex)
            when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            metrics.RecordEmailSend(Stopwatch.GetElapsedTime(sendStart), succeeded: false);
            logger.LogError(
                ex,
                "Failed to send notification email for subscription {SubscriptionId} ({Count} deliveries) - will retry next tick",
                subscriptionId,
                deliveries.Count
            );
            DropStale(db, subscriptionId, includedDeliveries, due.Events, now);
        }

        // Recorded as soon as this recipient's email is out, not once at the end: whatever
        // stops the run later (a failure, the app shutting down) must not make the next run send
        // it again. CancellationToken.None because an email that is already sent has to be
        // recorded even if the run is being cancelled.
        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>
    /// The deliveries a digest can be written for - those whose event is known and whose trial's
    /// screening list still resolves - and the distinct acronyms of their trials.
    /// </summary>
    private (List<NotificationDelivery> Included, List<string> Acronyms) DescribeIncluded(
        List<NotificationDelivery> deliveries,
        DueDeliveries due
    )
    {
        var included = new List<NotificationDelivery>();
        var acronyms = new List<string>();
        foreach (var delivery in deliveries)
        {
            if (!due.Events.TryGetValue(delivery.NotificationEventId, out var notificationEvent))
            {
                continue;
            }

            if (
                !due.ListInfoByTrial.TryGetValue(
                    notificationEvent.TrialIdentifier,
                    out var listInfo
                ) || listInfo is null
            )
            {
                logger.LogWarning(
                    "Could not resolve a screening list for trial {TrialIdentifier} - skipping delivery {DeliveryId} this tick",
                    notificationEvent.TrialIdentifier,
                    delivery.Id
                );
                continue;
            }

            acronyms.Add(listInfo.Value.StudyAcronym);
            included.Add(delivery);
        }

        return (included, [.. acronyms.Distinct()]);
    }

    /// <summary>Removes the deliveries that have now failed to send for longer than MaxDeliveryAge.</summary>
    private void DropStale(
        AppDbContext db,
        Guid subscriptionId,
        List<NotificationDelivery> includedDeliveries,
        Dictionary<Guid, NotificationEvent> events,
        DateTimeOffset now
    )
    {
        var stale = includedDeliveries
            .Where(d =>
                events.TryGetValue(d.NotificationEventId, out var e)
                && now - e.OccurredAt > MaxDeliveryAge
            )
            .ToList();
        if (stale.Count == 0)
        {
            return;
        }

        logger.LogWarning(
            "Giving up on {Count} notification deliveries for subscription {SubscriptionId} - pending longer than {MaxAgeDays} days with repeated send failures",
            stale.Count,
            subscriptionId,
            MaxDeliveryAge.TotalDays
        );
        db.NotificationDeliveries.RemoveRange(stale);
        metrics.RecordEmailDeliveriesDropped(stale.Count);
    }

    /// <summary>
    /// Admin-triggered, one-off send: exercises the exact same SMTP config and MJML template as a
    /// real digest, using one obviously-fake sample item, so an admin can verify EmailNotificationOptions
    /// (SMTP host/credentials, From, subject template, public base URL) actually works without
    /// waiting for a real detected event or touching any subscription/delivery rows. Its link
    /// carries a digest id no delivery has, which the email endpoint treats like any unknown
    /// digest - a plain redirect to the notifications page.
    /// </summary>
    /// <param name="protocolLog">Receives the SMTP transcript, see INotificationChannel.BeginBatchAsync.</param>
    public async Task SendTestEmailAsync(
        string recipientEmail,
        Stream? protocolLog = null,
        CancellationToken ct = default
    )
    {
        const string sampleAcronym = "TEST-TRIAL";

        var subject = "[Test] " + string.Format(mailerOptions.Value.SubjectTemplate, sampleAcronym);
        var html = RenderHtml(1, [sampleAcronym], EmailDigestLink(Guid.NewGuid()));

        await using var batch = await channel.BeginBatchAsync(protocolLog, ct);
        await batch.SendAsync(recipientEmail, subject, html, ct);
    }

    /// <summary>Why a test send failed, beyond the exception's message - see INotificationChannel.DescribeFailure.</summary>
    public IReadOnlyList<FailureDetail> DescribeSendFailure(Exception ex) =>
        channel.DescribeFailure(ex);

    /// <summary>The link a digest email's button points at - see Program.cs's /notifications/email endpoint.</summary>
    private string EmailDigestLink(Guid digestId) =>
        mailerOptions.Value.PublicUrl($"notifications/email/{digestId}");

    /// <summary>
    /// Renders the digest email body from an aggregate count only - no patient names or other
    /// per-subject data is ever passed to the template, since a subscriber must not learn who was
    /// suggested from the notification email itself (data protection). The template gets every
    /// acronym rather than the subject line's pre-joined "N trials" so it can word single- and
    /// multi-trial digests properly.
    /// </summary>
    private static string RenderHtml(
        int numNewSubjects,
        IReadOnlyList<string> studyAcronyms,
        string screeningListLink
    )
    {
        var model = new DigestTemplateModel(numNewSubjects, studyAcronyms, screeningListLink);
        var context = new TemplateContext(model, DigestTemplateOptions);
        var mjml = DigestTemplate.Render(context, HtmlEncoder.Default);

        var result = Renderer.Render(mjml);
        return result.Html;
    }

    private static TemplateOptions CreateDigestTemplateOptions()
    {
        var options = new TemplateOptions();
        options.MemberAccessStrategy.Register<DigestTemplateModel>();
        return options;
    }

    /// <summary>
    /// The digest MJML template is kept as a standalone, designer-editable file
    /// (Templates/NotificationDigest.mjml.liquid) rather than a C# string, and embedded into the
    /// assembly so it ships without relying on the output directory layout.
    /// </summary>
    private static IFluidTemplate LoadDigestTemplate()
    {
        var resourceName =
            $"{typeof(NotificationSenderService).Namespace}.Templates.NotificationDigest.mjml.liquid";
        using var stream =
            Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded MJML template '{resourceName}' was not found."
            );
        using var reader = new StreamReader(stream);
        var source = reader.ReadToEnd();

        var parser = new FluidParser();
        if (!parser.TryParse(source, out var template, out var error))
        {
            throw new InvalidOperationException(
                $"Failed to parse MJML template '{resourceName}': {error}"
            );
        }

        return template;
    }

    private sealed record DigestTemplateModel(
        int NumNewSubjects,
        IReadOnlyList<string> StudyAcronyms,
        string ScreeningListLink
    );
}
