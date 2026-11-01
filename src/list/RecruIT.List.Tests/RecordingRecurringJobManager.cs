using Hangfire;
using Hangfire.Common;

namespace RecruIT.List.Tests;

/// <summary>An IRecurringJobManager that only remembers what it was asked to do.</summary>
internal sealed class RecordingRecurringJobManager : IRecurringJobManager
{
    public Dictionary<string, string> Registered { get; } = [];

    public List<string> Triggered { get; } = [];

    public List<string> Removed { get; } = [];

    public void AddOrUpdate(
        string recurringJobId,
        Job job,
        string cronExpression,
        RecurringJobOptions options
    ) => Registered[recurringJobId] = cronExpression;

    public void Trigger(string recurringJobId) => Triggered.Add(recurringJobId);

    public void RemoveIfExists(string recurringJobId) => Removed.Add(recurringJobId);
}
