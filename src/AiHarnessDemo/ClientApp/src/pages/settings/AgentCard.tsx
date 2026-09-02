import { useState } from "react";
import type { AgentDto } from "../../api/types";
import { accentColors } from "../../lib/icons";
import { initials } from "../../lib/format";
import { useToggleAgentMutation } from "../../api/queries";
import { useToast } from "../../lib/toast";

export function AgentCard({ agent }: { agent: AgentDto }) {
  const toggleAgent = useToggleAgentMutation();
  const toast = useToast();
  const [checked, setChecked] = useState(agent.enabled);

  async function onToggle(nextChecked: boolean) {
    setChecked(nextChecked);
    try {
      await toggleAgent.mutateAsync({ agentId: agent.id, body: { enabled: nextChecked } });
      toast(`${nextChecked ? "Enabled" : "Disabled"} ${agent.id}.`, "success");
    } catch (error) {
      setChecked(!nextChecked);
      toast(error instanceof Error ? error.message : String(error), "error");
    }
  }

  const color = accentColors[agent.accent] || accentColors.violet;

  return (
    <article className={`agent-card ${checked ? "" : "disabled"}`} style={{ ["--agent-color" as string]: color }}>
      <div className="agent-avatar">{initials(agent.name)}</div>
      <div className="agent-copy">
        <strong>{agent.name}</strong>
        <span>{agent.description}</span>
      </div>
      <label className="toggle" title={`${checked ? "Disable" : "Enable"} ${agent.name}`}>
        <input type="checkbox" checked={checked} onChange={event => void onToggle(event.target.checked)} />
        <span className="toggle-track"></span>
      </label>
    </article>
  );
}
