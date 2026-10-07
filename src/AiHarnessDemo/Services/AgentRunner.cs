using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Reasoning;
using Polly;
using Polly.Retry;
using DeliveryOutcomeType = AiHarnessDemo.Core.Domain.OutcomeType;

namespace AiHarnessDemo.Services;

public enum StudioDependencyKind
{
    Direct,
    Ancestor
}

public sealed record StudioDependencyOutput(
    string PlanStepKey,
    string AgentId,
    StudioDependencyKind Kind,
    int Distance,
    int Attempt,
    int Sequence,
    string Output);

public sealed record AdvisoryPromotionContext(
    string CanonicalSeedJson,
    string SeedHash);

public sealed record AgentContextDocument(string Name, string Content);

public sealed record AgentExecutionContext(
    Guid FlowId,
    int Iteration,
    string AgentId,
    string AgentName,
    string AgentRole,
    string Model,
    string ModelEffort,
    int Attempt,
    string Task,
    string RepositoryKnowledge,
    string SourceProjectPath,
    string WorkspacePath,
    Guid CopilotSessionId,
    DeliveryOutcomeType Outcome,
    string PlanSummary,
    IReadOnlyList<string> PreviousOutputs,
    IReadOnlyList<HarnessLearning> Learnings,
    string CustomerFeedback = "",
    ModelSelectionStrategy ModelSelectionStrategy = ModelSelectionStrategy.MaximumQuality,
    double ExpectedAcceptedTimeSeconds = 0,
    bool AllowRemotePublication = false,
    bool ResumeSession = false,
    bool RecoverInterruptedSession = false,
    Action<AgentRunProgress>? Progress = null,
    bool IsPreMortemRevision = false,
    DateTimeOffset? InvocationStartedAt = null,
    string OutcomeContext = "",
    string OutcomeContract = "",
    string DirectPrompt = "",
    bool IsHostControlledPublication = false,
    IReadOnlyList<string>? GovernedRepositoryRelativePaths = null,
    Guid FlowStepId = default,
    ExecutionInvocationKind InvocationKind = ExecutionInvocationKind.Worker,
    IReadOnlyList<StudioDependencyOutput>? StudioDependencyOutputs = null,
    AdvisoryPromotionContext? PromotionContext = null,
    bool IsOutcomeOwner = false,
    string PlanStepKey = "",
    FlowKind? FlowKind = null,
    bool RequiresDeliveryReadinessQa = false,
    IReadOnlyList<AgentContextDocument>? ContextDocuments = null,
    bool UsesBoundedWorkingPrompt = false,
    string ResponseCorrectionInstructions = "",
    string QaImplementationOwner = "");

public sealed record AgentExecutionResult(
    string Output,
    string Evidence,
    int ExecutionAttempts,
    IReadOnlyList<ToolCallRecord> ToolCalls);

public interface IAgentRunner
{
    Task<AgentExecutionResult> ExecuteAsync(
        AgentExecutionContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Routes one role through Copilot CLI. Retry options are read immediately before each dispatch so
/// changes to WORKFLOW.md affect the next execution.
/// </summary>
public sealed class AgentRunner(
    CopilotReasoningHost copilotHost,
    RuntimeCircuitBreaker circuitBreaker,
    WorkflowDefinitionProvider workflowProvider,
    ILogger<AgentRunner> logger)
    : IAgentRunner
{
    public async Task<AgentExecutionResult> ExecuteAsync(
        AgentExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ReasoningHost host = copilotHost;
        if (!circuitBreaker.Allows(
                host,
                context.AgentId,
                cancellationToken,
                out var unavailableReason))
        {
            throw new AgentRunException(
                unavailableReason ?? "Reasoning host is unavailable.",
                AgentRunFailureKind.DependencyUnavailable,
                host.Config.RuntimeName);
        }

        var options = workflowProvider.GetValidated().Config.Agent;
        var attempts = 0;
        var failedToolCalls = new List<ToolCallRecord>();
        var resumeInterruptedSession = false;
        var pipelineBuilder = new ResiliencePipelineBuilder<AgentRunResult>();
        if (options.MaxAttempts > 1)
        {
            pipelineBuilder.AddRetry(new RetryStrategyOptions<AgentRunResult>
            {
                MaxRetryAttempts = options.MaxAttempts - 1,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromMilliseconds(options.RetryBaseDelayMs),
                MaxDelay = TimeSpan.FromMilliseconds(options.MaxRetryBackoffMs),
                UseJitter = true,
                ShouldHandle = new PredicateBuilder<AgentRunResult>()
                    .Handle<AgentRunException>(IsRetryableFailure),
                OnRetry = arguments =>
                {
                    var exception = arguments.Outcome.Exception as AgentRunException;
                    resumeInterruptedSession =
                        ShouldResumeInterruptedSession(exception);
                    context.Progress?.Invoke(new AgentRunProgress(
                        AgentRunPhase.Retrying,
                        resumeInterruptedSession
                            ? $"Runtime attempt {arguments.AttemptNumber + 1} ended without a clean shutdown; resuming the confirmed Copilot session."
                            : $"Runtime attempt {arguments.AttemptNumber + 1} failed transiently; retrying."));
                    logger.LogWarning(
                        exception,
                        "{AgentId} failed on execution attempt {Attempt}; retrying.",
                        context.AgentId,
                        arguments.AttemptNumber + 1);
                    return default;
                }
            });
        }

        var pipeline = pipelineBuilder.Build();
        AgentRunResult result;
        try
        {
            result = await pipeline.ExecuteAsync(
                async token =>
                {
                    attempts++;
                    var attemptContext = resumeInterruptedSession
                        ? context with
                        {
                            ResumeSession = true,
                            RecoverInterruptedSession = true
                        }
                        : context;
                    var runResult = await host.RunAgentAsync(
                        new AgentRunRequest
                        {
                            AgentId = context.AgentId,
                            Model = context.Model,
                            Effort = context.ModelEffort,
                            CorrelationId =
                                $"{context.FlowId:N}:{context.Iteration}:{context.AgentId}:" +
                                $"{context.CopilotSessionId:N}:{context.Attempt}",
                            CopilotSessionId = context.CopilotSessionId,
                            WorkingDirectory = context.WorkspacePath,
                            InputContext = new Dictionary<string, object?>
                            {
                                ["execution"] = attemptContext
                            },
                            Progress = context.Progress
                        },
                        token);

                    if (!runResult.Success)
                    {
                        failedToolCalls.AddRange(runResult.ToolCalls);
                        throw new AgentRunException(
                            runResult.Error ?? "Agent run reported failure without an error.",
                            runResult.FailureKind ?? AgentRunFailureKind.InvalidOutput,
                            runResult.FailedDependency,
                            runResult.CanResumeSession)
                        {
                            ToolCalls = failedToolCalls.ToArray(),
                            ProcessTerminationUnconfirmed = runResult.ProcessTerminationUnconfirmed
                        };
                    }

                    return runResult;
                },
                cancellationToken);
            circuitBreaker.RecordSuccess(host.Config.RuntimeName, context.AgentId);
        }
        catch (AgentRunException exception)
        {
            exception.ExecutionAttempts = Math.Max(1, attempts);
            if (exception.FailureKind == AgentRunFailureKind.DependencyUnavailable)
            {
                circuitBreaker.RecordAvailabilityFailure(
                    exception.FailedDependency ?? host.Config.RuntimeName,
                    exception.Message);
            }
            throw;
        }

        var observedToolCalls = RetainRetryToolCalls(failedToolCalls, result.ToolCalls);
        return new AgentExecutionResult(
            result.OutputSummary,
            $"{host.Config.RuntimeName}: {observedToolCalls.Count} observable tool call(s).",
            attempts,
            observedToolCalls);
    }

    internal static IReadOnlyList<ToolCallRecord> RetainRetryToolCalls(
        IReadOnlyList<ToolCallRecord> interrupted,
        IReadOnlyList<ToolCallRecord> completed) =>
        [.. interrupted, .. completed];

    internal static bool ShouldResumeInterruptedSession(
        AgentRunException? exception) =>
        exception?.CanResumeSession == true &&
        !exception.ProcessTerminationUnconfirmed &&
        exception.FailureKind is
            AgentRunFailureKind.Stalled or
            AgentRunFailureKind.TimedOut;

    internal static bool IsRetryableFailure(AgentRunException exception) =>
        !exception.ProcessTerminationUnconfirmed &&
        exception.FailureKind is AgentRunFailureKind.Transient or
            AgentRunFailureKind.TimedOut or AgentRunFailureKind.Stalled or
            AgentRunFailureKind.AmbiguousCrash;
}
