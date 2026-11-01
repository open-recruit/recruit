using Hl7.Fhir.Rest;
using Microsoft.Extensions.Logging;
using RecruIT.List.Services;
using RecruIT.List.Services.Fhir;

namespace RecruIT.List.Tests;

public sealed class ErrorReporterTests
{
    private readonly CapturingLogger<ErrorReporter> _log = new();

    private ErrorReporter CreateReporter() => new(new FakeStringLocalizer(), _log);

    [Theory]
    [InlineData(typeof(UserFacingException))]
    [InlineData(typeof(FhirAccessException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void A_message_meant_for_the_user_is_shown_as_it_is_without_a_second_log_line(Type type)
    {
        var ex = (Exception)Activator.CreateInstance(type, "Already localized.")!;

        Assert.Equal("Already localized.", CreateReporter().Report(ex, "save the note"));
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void Any_other_failure_is_logged_and_replaced_by_a_generic_message()
    {
        var ex = new InvalidOperationException(
            "23505: duplicate key value violates unique constraint"
        );

        var shown = CreateReporter().Report(ex, "save the note");

        Assert.Equal("App.Errors.Unexpected", shown);
        Assert.DoesNotContain("23505", shown);
        var entry = Assert.Single(_log.Entries);
        Assert.Equal((LogLevel.Error, ex), (entry.Level, entry.Exception));
        Assert.Contains("save the note", entry.Message);
    }

    [Fact]
    public void A_page_can_keep_its_own_fallback_text()
    {
        Assert.Equal(
            "Could not load the dashboard.",
            CreateReporter()
                .Report(new TimeoutException(), "load the trials", "Could not load the dashboard.")
        );
        Assert.Single(_log.Entries);
    }

    [Fact]
    public void A_cancellation_is_not_an_error()
    {
        CreateReporter().Report(new OperationCanceledException(), "load the trials");

        Assert.Equal(LogLevel.Debug, Assert.Single(_log.Entries).Level);
    }

    [Fact]
    public void A_failure_the_page_carries_on_after_is_logged_as_a_warning()
    {
        var ex = new HttpRequestException("connection refused");

        CreateReporter().Swallowed(ex, "load the attrition funnel");

        var entry = Assert.Single(_log.Entries);
        Assert.Equal((LogLevel.Warning, ex), (entry.Level, entry.Exception));
        Assert.Contains("load the attrition funnel", entry.Message);
    }

    [Fact]
    public void A_failure_the_service_already_logged_is_not_logged_again_when_carried_on_after()
    {
        CreateReporter().Swallowed(new FhirAccessException("Could not load."), "load the history");

        Assert.Empty(_log.Entries);
    }
}
