using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Reasoning;

namespace AiHarnessDemo.Services;

internal readonly record struct AgentProgressUpdate(
    bool PhaseChanged,
    bool StateChanged);

internal static class AgentProgressPersistence
{
    public static AgentProgressUpdate Apply(
        FlowStep step,
        AgentRunProgress progress)
    {
        var phaseChanged = step.Phase != progress.Phase;
        var stateChanged = phaseChanged;
        step.Phase = progress.Phase;
        if (progress.ExecutionPrompt is not null &&
            !string.Equals(
                step.ExecutionPrompt,
                progress.ExecutionPrompt,
                StringComparison.Ordinal))
        {
            step.ExecutionPrompt = progress.ExecutionPrompt;
            stateChanged = true;
        }
        if (progress.CopilotSessionId is { } sessionId &&
            (step.CopilotSessionId != sessionId ||
             !string.Equals(
                 step.CopilotSessionHome,
                 progress.CopilotSessionHome ?? string.Empty,
                 StringComparison.Ordinal)))
        {
            step.CopilotSessionId = sessionId;
            step.CopilotSessionHome =
                progress.CopilotSessionHome ?? string.Empty;
            stateChanged = true;
        }
        return new AgentProgressUpdate(phaseChanged, stateChanged);
    }
}
