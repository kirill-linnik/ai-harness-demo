using System.Collections.Concurrent;

namespace AiHarnessDemo.Core.Reasoning;

public sealed record OpenRuntimeCircuit(
    string Key,
    DateTimeOffset RetryAfter,
    string Failure);

/// <summary>
/// Suppresses repeated launches of a runtime or agent that has recently reported an availability
/// failure. Successful executions close both the runtime-wide and agent-specific circuits.
/// </summary>
public sealed class RuntimeCircuitBreaker(TimeProvider clock)
{
    private static readonly TimeSpan ReadinessCacheLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, OpenRuntimeCircuit> _open = new(
        StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ReasoningHostReadiness> _readiness = new(
        StringComparer.OrdinalIgnoreCase);

    public bool Allows(
        ReasoningHost host,
        string agentId,
        CancellationToken cancellationToken,
        out string? rejection)
    {
        var now = clock.GetUtcNow();
        RemoveExpired(now);

        if (TryGetActive(host.Config.RuntimeName, now, out var runtimeFailure) ||
            TryGetActive(agentId, now, out runtimeFailure))
        {
            rejection =
                $"{runtimeFailure!.Key} is paused until {runtimeFailure.RetryAfter:u}: " +
                runtimeFailure.Failure;
            return false;
        }

        if (_readiness.TryGetValue(host.Config.RuntimeName, out var cached) &&
            now - cached.ObservedAt < ReadinessCacheLifetime)
        {
            rejection = cached.IsAvailable ? null : cached.Detail;
            return cached.IsAvailable;
        }

        var current = host.CheckReadiness(cancellationToken);
        _readiness[host.Config.RuntimeName] = current;
        if (current.IsAvailable)
        {
            rejection = null;
            return true;
        }

        Open(host.Config.RuntimeName, current.Detail, now);
        rejection = current.Detail;
        return false;
    }

    public void RecordSuccess(string runtimeName, string agentId)
    {
        _open.TryRemove(runtimeName, out _);
        _open.TryRemove(agentId, out _);
        _readiness.TryRemove(runtimeName, out _);
    }

    public void RecordAvailabilityFailure(string key, string failure)
    {
        var normalized = string.IsNullOrWhiteSpace(key) ? "reasoning-runtime" : key.Trim();
        Open(normalized, failure, clock.GetUtcNow());
    }

    public IReadOnlyList<OpenRuntimeCircuit> Current()
    {
        RemoveExpired(clock.GetUtcNow());
        return _open.Values
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private bool TryGetActive(
        string key,
        DateTimeOffset now,
        out OpenRuntimeCircuit? circuit)
    {
        if (_open.TryGetValue(key, out circuit) &&
            circuit.RetryAfter > now)
        {
            return true;
        }

        circuit = null;
        return false;
    }

    private void Open(string key, string failure, DateTimeOffset now) =>
        _open[key] = new OpenRuntimeCircuit(
            key,
            now + FailureCooldown,
            failure);

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var pair in _open)
        {
            if (pair.Value.RetryAfter <= now)
            {
                _open.TryRemove(pair.Key, out _);
            }
        }
    }
}
