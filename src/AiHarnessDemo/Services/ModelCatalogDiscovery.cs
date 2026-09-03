using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Data;
using Microsoft.EntityFrameworkCore;

namespace AiHarnessDemo.Services;

public sealed record ModelCatalogRuntimeStatus(
    bool Ready,
    Guid? SnapshotId,
    string CatalogVersion,
    int CandidateCount,
    string Detail,
    DateTimeOffset CheckedAt);

public sealed record DiscoveredModelEffort(
    string Model,
    string Effort,
    int ModelOrder,
    int EffortOrder,
    bool IsDefaultModel,
    bool IsDefaultEffort,
    double? PremiumMultiplier,
    string Description,
    string MetadataConfidence);

public sealed record DiscoveredModelCatalog(
    string CatalogVersion,
    IReadOnlyList<DiscoveredModelEffort> Candidates);

/// <summary>
/// Discovers the current Copilot model/effort surface through one dedicated ACP child process.
/// A previous database snapshot is audit evidence only and is never used as a readiness fallback.
/// </summary>
public sealed class ModelCatalogDiscovery(
    CopilotCliRuntime cliRuntime,
    IDbContextFactory<HarnessDbContext> databaseFactory,
    TimeProvider timeProvider,
    ILogger<ModelCatalogDiscovery> logger)
{
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(20);
    private readonly SemaphoreSlim _discoveryLock = new(1, 1);
    private readonly Lock _statusLock = new();
    private ModelCatalogRuntimeStatus _status = new(
        false,
        null,
        string.Empty,
        0,
        "Current-process ACP model discovery has not run.",
        DateTimeOffset.MinValue);

    public ModelCatalogRuntimeStatus Current
    {
        get
        {
            lock (_statusLock)
            {
                return _status;
            }
        }
    }

    public async Task<ModelCatalogRuntimeStatus> RefreshAsync(
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        await _discoveryLock.WaitAsync(cancellationToken);
        try
        {
            var cli = cliRuntime.Current;
            if (!cli.Ready || string.IsNullOrWhiteSpace(cli.ResolvedPath))
            {
                return PublishFailure(
                    $"ACP discovery cannot run because Copilot CLI is not ready: {cli.Detail}");
            }

            try
            {
                var discovered = await DiscoverProcessAsync(
                    cli.ResolvedPath,
                    workingDirectory,
                    cancellationToken);
                var snapshot = new ModelCatalogSnapshot
                {
                    CatalogVersion = discovered.CatalogVersion,
                    CliVersion = cli.Version,
                    IsCurrent = true,
                    DiscoveredAt = timeProvider.GetUtcNow(),
                    Candidates = discovered.Candidates.Select(candidate =>
                        new ModelCatalogCandidate
                        {
                            Model = candidate.Model,
                            Effort = candidate.Effort,
                            ModelOrder = candidate.ModelOrder,
                            EffortOrder = candidate.EffortOrder,
                            IsDefaultModel = candidate.IsDefaultModel,
                            IsDefaultEffort = candidate.IsDefaultEffort,
                            PremiumMultiplier = candidate.PremiumMultiplier,
                            Description = candidate.Description,
                            MetadataConfidence = candidate.MetadataConfidence,
                            Enabled = true
                        }).ToList()
                };
                await using var database =
                    await databaseFactory.CreateDbContextAsync(cancellationToken);
                await database.ModelCatalogSnapshots
                    .Where(item => item.IsCurrent)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(item => item.IsCurrent, false),
                        cancellationToken);
                database.ModelCatalogSnapshots.Add(snapshot);
                await database.SaveChangesAsync(cancellationToken);

                var status = new ModelCatalogRuntimeStatus(
                    true,
                    snapshot.Id,
                    snapshot.CatalogVersion,
                    snapshot.Candidates.Count,
                    $"ACP discovered {snapshot.Candidates.Count} enabled explicit model/effort candidates.",
                    timeProvider.GetUtcNow());
                lock (_statusLock)
                {
                    _status = status;
                }
                return status;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Current Copilot ACP model discovery failed.");
                return PublishFailure(
                    "Current Copilot ACP model discovery failed closed: " +
                    exception.GetBaseException().Message);
            }
        }
        finally
        {
            _discoveryLock.Release();
        }
    }

    internal static DiscoveredModelCatalog ParseConfigOptions(params string[] payloads)
    {
        var states = payloads
            .Select(payload =>
                TryReadConfigState(payload, out var state)
                    ? state
                    : null)
            .Where(state => state is not null)
            .Cast<ConfigState>()
            .ToList();
        if (states.Count == 0)
        {
            throw new InvalidOperationException(
                "ACP initialize/session response contained no configOptions array.");
        }

        var candidates = new Dictionary<ModelCandidateKey, DiscoveredModelEffort>();
        var defaultModel = states[0].Model.CurrentValue;
        foreach (var state in states)
        {
            var model = state.Model.Choices.FirstOrDefault(choice =>
                string.Equals(
                    choice.Value,
                    state.Model.CurrentValue,
                    StringComparison.Ordinal));
            if (model is null)
            {
                continue;
            }
            IReadOnlyList<Choice> efforts = state.Effort?.Choices ??
            [
                new Choice(
                    "default",
                    0,
                    IsDefault: true,
                    "This model exposes no configurable reasoning effort.",
                    PremiumMultiplier: null)
            ];
            foreach (var effort in efforts)
            {
                var candidate = new DiscoveredModelEffort(
                    model.Value,
                    effort.Value,
                    model.Order,
                    effort.Order,
                    string.Equals(model.Value, defaultModel, StringComparison.Ordinal),
                    effort.IsDefault,
                    model.PremiumMultiplier ?? effort.PremiumMultiplier,
                    string.Join(
                        " ",
                        new[] { model.Description, effort.Description }
                            .Where(value => !string.IsNullOrWhiteSpace(value))),
                    model.PremiumMultiplier is not null || effort.PremiumMultiplier is not null
                        ? "high"
                        : "low");
                candidates[new ModelCandidateKey(candidate.Model, candidate.Effort)] = candidate;
            }
        }
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                "ACP discovery returned no enabled explicit model/effort candidate.");
        }

        var orderedCandidates = candidates.Values
            .OrderBy(candidate => candidate.ModelOrder)
            .ThenBy(candidate => candidate.EffortOrder)
            .ToList();
        var catalogIdentity = string.Join(
            "\n",
            orderedCandidates.Select(candidate =>
                $"{candidate.Model}\t{candidate.Effort}\t{candidate.PremiumMultiplier?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}"));
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(catalogIdentity)))[..16].ToLowerInvariant();
        return new DiscoveredModelCatalog($"acp-{hash}", orderedCandidates);
    }

    private static bool TryReadConfigState(string payload, out ConfigState? state)
    {
        using var document = JsonDocument.Parse(payload);
        if (!TryFindProperty(document.RootElement, "configOptions", out var configOptions) ||
            configOptions.ValueKind != JsonValueKind.Array)
        {
            state = null;
            return false;
        }

        JsonElement? modelOption = null;
        JsonElement? effortOption = null;
        foreach (var option in configOptions.EnumerateArray())
        {
            var category = ReadString(option, "category");
            var identity = (
                ReadString(option, "id") + " " +
                ReadString(option, "name") + " " +
                ReadString(option, "title")).ToLowerInvariant();
            if (modelOption is null &&
                (string.Equals(category, "model", StringComparison.OrdinalIgnoreCase) ||
                 (!string.Equals(category, "model_config", StringComparison.OrdinalIgnoreCase) &&
                  identity.Contains("model", StringComparison.Ordinal))))
            {
                modelOption = option.Clone();
            }
            if (effortOption is null &&
                (string.Equals(category, "thought_level", StringComparison.OrdinalIgnoreCase) ||
                 identity.Contains("effort", StringComparison.Ordinal) ||
                 identity.Contains("reason", StringComparison.Ordinal) ||
                 identity.Contains("thought", StringComparison.Ordinal)))
            {
                effortOption = option.Clone();
            }
        }
        if (modelOption is null)
        {
            throw new InvalidOperationException(
                "ACP configOptions contained no model selector.");
        }
        var modelId = ReadString(modelOption.Value, "id");
        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new InvalidOperationException(
                "ACP model selector must expose a stable configuration ID.");
        }
        var models = ReadChoices(modelOption.Value, excludeAuto: true);
        if (models.Count == 0)
        {
            throw new InvalidOperationException(
                "ACP discovery returned no enabled explicit model.");
        }
        ConfigSelector? effort = null;
        if (effortOption is not null)
        {
            var effortId = ReadString(effortOption.Value, "id");
            if (string.IsNullOrWhiteSpace(effortId))
            {
                throw new InvalidOperationException(
                    "ACP reasoning selector must expose a stable configuration ID.");
            }
            var efforts = ReadChoices(effortOption.Value, excludeAuto: false);
            if (efforts.Count == 0)
            {
                throw new InvalidOperationException(
                    "ACP reasoning selector exposed no enabled effort.");
            }
            effort = new ConfigSelector(
                effortId,
                ReadCurrentValue(effortOption.Value),
                efforts);
        }

        state = new ConfigState(
            new ConfigSelector(
                modelId,
                ReadCurrentValue(modelOption.Value),
                models),
            effort);
        return true;
    }

    private static async Task<DiscoveredModelCatalog> DiscoverProcessAsync(
        string executable,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DiscoveryTimeout);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("--acp");
        if (!process.Start())
        {
            throw new InvalidOperationException("Copilot ACP process did not start.");
        }

        var stderrTask = process.StandardError.ReadToEndAsync();
        string? sessionId = null;
        try
        {
            var initialize = await SendRequestAsync(
                process,
                1,
                "initialize",
                new
                {
                    protocolVersion = 1,
                    clientCapabilities = new { },
                    clientInfo = new { name = "ai-harness-demo", version = "1.0" }
                },
                timeout.Token);
            var session = await SendRequestAsync(
                process,
                2,
                "session/new",
                new
                {
                    cwd = Path.GetFullPath(workingDirectory),
                    mcpServers = Array.Empty<object>()
                },
                timeout.Token);
            using (var sessionDocument = JsonDocument.Parse(session))
            {
                if (TryFindProperty(sessionDocument.RootElement, "sessionId", out var value))
                {
                    sessionId = value.GetString();
                }
            }
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                throw new InvalidOperationException(
                    "Copilot ACP session/new returned no session ID.");
            }
            if (!TryReadConfigState(session, out var initialState) &&
                !TryReadConfigState(initialize, out initialState))
            {
                throw new InvalidOperationException(
                    "Copilot ACP did not expose model configuration for the new session.");
            }

            var payloads = new List<string> { initialize, session };
            var requestId = 3;
            foreach (var model in initialState!.Model.Choices)
            {
                if (string.Equals(
                        model.Value,
                        initialState.Model.CurrentValue,
                        StringComparison.Ordinal))
                {
                    continue;
                }
                var response = await SendRequestAsync(
                    process,
                    requestId++,
                    "session/set_config_option",
                    new
                    {
                        sessionId,
                        configId = initialState.Model.Id,
                        value = model.Value
                    },
                    timeout.Token);
                if (!TryReadConfigState(response, out var selectedState) ||
                    !string.Equals(
                        selectedState!.Model.CurrentValue,
                        model.Value,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Copilot ACP did not confirm model configuration '{model.Value}'.");
                }
                payloads.Add(response);
            }
            return ParseConfigOptions(payloads.ToArray());
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(sessionId) && !process.HasExited)
            {
                try
                {
                    await WriteMessageAsync(
                        process,
                        new
                        {
                            jsonrpc = "2.0",
                            method = "session/cancel",
                            @params = new { sessionId }
                        },
                        CancellationToken.None);
                }
                catch (Exception)
                {
                    // Shutdown continues by closing this exact child's stdin.
                }
            }
            process.StandardInput.Close();
            try
            {
                await process.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
            }
            _ = await stderrTask.ConfigureAwait(false);
        }
    }

    private static async Task<string> SendRequestAsync(
        Process process,
        int id,
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        await WriteMessageAsync(
            process,
            new { jsonrpc = "2.0", id, method, @params = parameters },
            cancellationToken);
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                throw new InvalidOperationException(
                    $"Copilot ACP process closed before responding to {method}.");
            }
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            using var document = JsonDocument.Parse(line);
            if (!document.RootElement.TryGetProperty("id", out var responseId) ||
                !responseId.TryGetInt32(out var responseValue) ||
                responseValue != id)
            {
                continue;
            }
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                throw new InvalidOperationException(
                    $"Copilot ACP {method} failed: {error.GetRawText()}");
            }
            return line;
        }
    }

    private static async Task WriteMessageAsync(
        Process process,
        object message,
        CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteLineAsync(
            JsonSerializer.Serialize(message).AsMemory(),
            cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
    }

    private ModelCatalogRuntimeStatus PublishFailure(string detail)
    {
        var status = new ModelCatalogRuntimeStatus(
            false,
            null,
            string.Empty,
            0,
            detail,
            timeProvider.GetUtcNow());
        lock (_statusLock)
        {
            _status = status;
        }
        return status;
    }

    private static List<Choice> ReadChoices(JsonElement option, bool excludeAuto)
    {
        if (!TryFindDirectArray(option, out var choices))
        {
            return [];
        }

        var current = ReadCurrentValue(option);

        var result = new List<Choice>();
        var order = 0;
        foreach (var choice in choices.EnumerateArray())
        {
            var value = choice.ValueKind == JsonValueKind.String
                ? choice.GetString() ?? string.Empty
                : FirstNonEmpty(
                    ReadString(choice, "value"),
                    ReadString(choice, "id"),
                    ReadString(choice, "name"));
            var name = choice.ValueKind == JsonValueKind.Object
                ? FirstNonEmpty(ReadString(choice, "name"), ReadString(choice, "label"))
                : value;
            if (string.IsNullOrWhiteSpace(value) ||
                (excludeAuto &&
                 (string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(name, "auto", StringComparison.OrdinalIgnoreCase))) ||
                IsDisabled(choice))
            {
                order++;
                continue;
            }
            result.Add(new Choice(
                value,
                order++,
                string.Equals(current, value, StringComparison.Ordinal),
                choice.ValueKind == JsonValueKind.Object
                    ? ReadString(choice, "description")
                    : string.Empty,
                ReadPremiumMultiplier(choice)));
        }
        return result;
    }

    private static string ReadCurrentValue(JsonElement option)
    {
        var current = ReadString(option, "currentValue");
        return string.IsNullOrWhiteSpace(current)
            ? ReadString(option, "defaultValue")
            : current;
    }

    private static bool TryFindDirectArray(JsonElement option, out JsonElement array)
    {
        foreach (var name in new[] { "options", "values", "choices" })
        {
            if (option.TryGetProperty(name, out array) &&
                array.ValueKind == JsonValueKind.Array)
            {
                return true;
            }
        }
        array = default;
        return false;
    }

    private static bool IsDisabled(JsonElement choice)
    {
        if (choice.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        if (choice.TryGetProperty("disabled", out var disabled) &&
            disabled.ValueKind == JsonValueKind.True)
        {
            return true;
        }
        if (choice.TryGetProperty("enabled", out var enabled) &&
            enabled.ValueKind == JsonValueKind.False)
        {
            return true;
        }
        if (choice.TryGetProperty("_meta", out var metadata))
        {
            if (TryFindProperty(metadata, "disabled", out disabled) &&
                disabled.ValueKind == JsonValueKind.True)
            {
                return true;
            }
            if (TryFindProperty(metadata, "enabled", out enabled) &&
                enabled.ValueKind == JsonValueKind.False)
            {
                return true;
            }
            if (TryFindProperty(metadata, "copilotEnablement", out var enablement) &&
                enablement.ValueKind == JsonValueKind.String &&
                !string.Equals(
                    enablement.GetString(),
                    "enabled",
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static double? ReadPremiumMultiplier(JsonElement choice)
    {
        if (choice.ValueKind != JsonValueKind.Object ||
            !choice.TryGetProperty("_meta", out var metadata))
        {
            return null;
        }
        foreach (var name in new[]
                 {
                     "premiumRequestMultiplier", "premium_multiplier",
                     "premiumMultiplier", "usageMultiplier", "copilotUsage", "multiplier"
                 })
        {
            if (TryFindProperty(metadata, name, out var value))
            {
                if (value.ValueKind == JsonValueKind.Number &&
                    value.TryGetDouble(out var number) &&
                    double.IsFinite(number) &&
                    number >= 0)
                {
                    return number;
                }
                if (value.ValueKind == JsonValueKind.String &&
                    TryParseMultiplier(value.GetString(), out number) &&
                    number >= 0)
                {
                    return number;
                }
            }
        }
        return null;
    }

    private static bool TryParseMultiplier(string? value, out double multiplier)
    {
        var normalized = (value ?? string.Empty)
            .Trim()
            .TrimEnd('x', 'X')
            .Trim();
        return double.TryParse(
                   normalized,
                   System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out multiplier) &&
            double.IsFinite(multiplier);
    }

    private static bool TryFindProperty(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
                if (TryFindProperty(property.Value, propertyName, out value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindProperty(item, propertyName, out value))
                {
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static string ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private sealed record Choice(
        string Value,
        int Order,
        bool IsDefault,
        string Description,
        double? PremiumMultiplier);

    private sealed record ConfigSelector(
        string Id,
        string CurrentValue,
        IReadOnlyList<Choice> Choices);

    private sealed record ConfigState(ConfigSelector Model, ConfigSelector? Effort);
}
