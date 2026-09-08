import type { AgentDto } from "../../api/types";
import { accentColors } from "../../lib/icons";
import { initials } from "../../lib/format";
import { useToggleAgentMutation } from "../../api/queries";
import { useToast } from "../../lib/toast";

export function AgentCard({ agent }: { agent: AgentDto }) {
  const toggleAgent = useToggleAgentMutation();
  const toast = useToast();

  async function onToggle(nextChecked: boolean) {
    try {
      await toggleAgent.mutateAsync({ agentId: agent.id, body: { enabled: nextChecked } });
      toast(`${nextChecked ? "Enabled" : "Disabled"} ${agent.id}.`, "success");
    } catch (error) {
      toast(error instanceof Error ? error.message : String(error), "error");
    }
  }

  const color = accentColors[agent.accent] || accentColors.violet;

  const invalid = agent.definitionStatus === "Invalid";
  const switchable = agent.switchable;
  const enabled = agent.enabled && !invalid;
  const requiredLabel = agent.id === "pre-mortem-sceptic"
    ? "Required definition · Optional execution"
    : agent.required
      ? "Required"
      : "Optional";

  return (
    <article
      className={`agent-card ${enabled ? "" : "disabled"}`}
      style={{ ["--agent-color" as string]: color }}
    >
      <div className="agent-avatar">{initials(agent.name)}</div>
      <div className="agent-copy">
        <strong>{agent.name}</strong>
        <span>{agent.description}</span>
        <small>{requiredLabel}{invalid ? " · Invalid" : ""}</small>
        {invalid && (
          <small className="pushback-callout" role="status">
            {agent.validationError}
          </small>
        )}
      </div>
      <label
        className="toggle"
        title={
          invalid
            ? `${agent.name} has an invalid definition`
            : switchable
              ? `${enabled ? "Disable" : "Enable"} ${agent.name}`
            : `${agent.name} is required`
        }
      >
        <input
          type="checkbox"
          checked={enabled}
          disabled={!switchable || invalid}
          aria-label={
            invalid
              ? `${agent.name} has an invalid definition and cannot be enabled`
              : switchable
              ? `${enabled ? "Disable" : "Enable"} ${agent.name}`
              : `${agent.name} is required and cannot be disabled`
          }
          onChange={event => void onToggle(event.target.checked)}
        />
        <span className="toggle-track"></span>
      </label>
    </article>
  );
}
