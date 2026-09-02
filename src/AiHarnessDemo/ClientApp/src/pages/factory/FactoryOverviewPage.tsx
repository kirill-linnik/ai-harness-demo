import { Link, useNavigate } from "react-router-dom";
import { AppShell } from "../../components/AppShell";
import { StatCard } from "../../components/StatCard";
import { useBootstrapQuery } from "../../api/queries";
import { MicIcon, SettingsIcon, waveMarkup } from "../../lib/icons";
import { setDraftPrompt, setStartVoiceHint } from "../../lib/session";
import { FlowCard } from "./FlowCard";
import { FactoryLockedPage } from "./FactoryLockedPage";

export function FactoryOverviewPage() {
  const { data } = useBootstrapQuery();
  const navigate = useNavigate();

  if (!data) return null;
  if (!data.factoryEnabled) return <FactoryLockedPage />;

  const flows = data.flows || [];
  const stats = data.stats;

  const startSpeaking = () => {
    setStartVoiceHint();
    navigate("/intake");
  };

  const startWithPrompt = (prompt: string) => {
    setDraftPrompt(prompt);
    navigate("/intake");
  };

  return (
    <AppShell
      active="factory"
      title="AI Factory"
      subtitle={`${stats.activeFlows} active · ${stats.completedFlows} approved · ${stats.learnedRefinements} learned refinements`}
    >
      <section className="stats-grid">
        <StatCard
          label="Active flows"
          value={stats.activeFlows}
          detail="Independent tasks in motion"
          glow="rgba(81, 168, 255, .14)"
        />
        <StatCard
          label="Approved"
          value={stats.completedFlows}
          detail="Customer-accepted outcomes"
          glow="rgba(66, 214, 164, .13)"
        />
        <StatCard
          label="Prompt refinements"
          value={stats.learnedRefinements}
          detail="Learned across every flow"
          glow="rgba(155, 135, 245, .14)"
        />
        <StatCard
          label="Agent time"
          value={stats.totalAgentMinutes}
          detail="Minutes in the execution ledger"
          glow="rgba(247, 185, 85, .12)"
        />
      </section>
      <section className="factory-hero">
        <div className="hero-copy">
          <div className="eyebrow">Symphony orchestration · Copilot execution</div>
          <h2>
            Describe the outcome.
            <br />
            The factory assembles the team.
          </h2>
          <p>
            Account Manager clarifies and confirms the brief with you. Team Lead then selects enabled
            specialists. The harness observes every handoff, model choice, pushback, and correction.
          </p>
          <div className="hero-actions">
            <button className="button primary" onClick={startSpeaking}>
              <MicIcon /> Listen to the next task
            </button>
            <Link className="button" to="/settings">
              <SettingsIcon /> Configure the team
            </Link>
          </div>
          <div className="quick-prompts">
            <button
              className="prompt-chip"
              onClick={() =>
                startWithPrompt(
                  "Add an accessible onboarding checklist to the selected product and persist completion across sessions."
                )
              }
            >
              Onboarding checklist
            </button>
            <button
              className="prompt-chip"
              onClick={() =>
                startWithPrompt(
                  "Design and implement a usage dashboard with clear empty states, filtering, and export."
                )
              }
            >
              Usage dashboard
            </button>
            <button
              className="prompt-chip"
              onClick={() =>
                startWithPrompt(
                  "Add secure role-based access for administrative settings and prove unauthorized users are blocked."
                )
              }
            >
              Role-based access
            </button>
          </div>
        </div>
        <div className="voice-orbit" aria-hidden="true">
          <div className="orbit one"></div>
          <div className="orbit two"></div>
          <div className="orbit three"></div>
          <button className="mic-button" aria-label="Start a new voice assignment" onClick={startSpeaking}>
            <MicIcon />
          </button>
          {waveMarkup()}
        </div>
      </section>
      {flows.length > 0 && (
        <>
          <div className="section-title" style={{ margin: "28px 0 12px" }}>
            <h3>Factory flows</h3>
            <p>Every workflow has a durable, shareable link.</p>
          </div>
          <section className="flow-grid">
            {flows.map(flow => (
              <FlowCard key={flow.id} flow={flow} />
            ))}
          </section>
        </>
      )}
    </AppShell>
  );
}
