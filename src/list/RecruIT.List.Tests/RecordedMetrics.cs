using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RecruIT.List.Data;
using RecruIT.List.Services;

namespace RecruIT.List.Tests;

/// <summary>
/// Captures what one RecruitMetrics instance records - and only that instance, by listening to
/// the meters of its own IMeterFactory, so tests running in parallel don't see each other's.
/// </summary>
internal sealed class RecordedMetrics : IDisposable
{
    private readonly ServiceProvider _services = new ServiceCollection()
        .AddMetrics()
        .BuildServiceProvider();
    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<Measurement> _measurements = new();

    public RecordedMetrics()
    {
        var meterFactory = _services.GetRequiredService<IMeterFactory>();
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Scope == meterFactory)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>(
            (instrument, value, tags, _) => Record(instrument, value, tags)
        );
        _listener.SetMeasurementEventCallback<double>(
            (instrument, value, tags, _) => Record(instrument, value, tags)
        );
        _listener.Start();
    }

    public RecruitMetrics CreateRecruitMetrics(IDbContextFactory<AppDbContext> dbContextFactory) =>
        new(
            _services.GetRequiredService<IMeterFactory>(),
            dbContextFactory,
            NullLogger<RecruitMetrics>.Instance
        );

    public IReadOnlyList<Measurement> Of(string instrument) =>
        [.. _measurements.Where(m => m.Instrument == instrument)];

    /// <summary>Polls the observable instruments, as a metrics scrape would.</summary>
    public void Scrape() => _listener.RecordObservableInstruments();

    public void Dispose()
    {
        _listener.Dispose();
        _services.Dispose();
    }

    private void Record(
        Instrument instrument,
        double value,
        ReadOnlySpan<KeyValuePair<string, object?>> tags
    ) =>
        _measurements.Enqueue(
            new Measurement(instrument.Name, value, tags.ToArray().ToDictionary())
        );

    public sealed record Measurement(
        string Instrument,
        double Value,
        IReadOnlyDictionary<string, object?> Tags
    );
}
