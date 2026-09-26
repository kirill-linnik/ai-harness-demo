import { Link } from "react-router-dom";
import { AppShell } from "../../components/AppShell";
import { useBootstrapQuery } from "../../api/queries";
import { FolderIcon, SettingsIcon } from "../../lib/icons";

export function FactoryLockedPage() {
  const { data } = useBootstrapQuery();
  const reason = data?.factoryDisabledReason || "Add and study a source repository in Settings.";

  return (
    <AppShell active="factory" title="AI Factory" subtitle="Resolve prerequisites to unlock agent execution">
      <div className="page-head">
        <div>
          <div className="eyebrow">Prerequisite required</div>
          <h2>The factory is waiting for setup</h2>
          <p>Agents stay disabled until the runtime, workflow, and repository checks are ready.</p>
        </div>
      </div>
      <section className="card empty-state" style={{ minHeight: 430 }}>
        <div>
          <div className="empty-icon">
            <FolderIcon />
          </div>
          <h3>AI Factory is locked</h3>
          <p>{reason}</p>
          <div className="quick-prompts" style={{ justifyContent: "center", marginTop: 22 }}>
            <span className="model-chip">1 · Verify Copilot CLI</span>
            <span className="model-chip">2 · Choose a source project</span>
            <span className="model-chip">3 · Review project knowledge</span>
          </div>
          <Link className="button primary" to="/settings" style={{ marginTop: 22 }}>
            <SettingsIcon /> Review prerequisites
          </Link>
        </div>
      </section>
    </AppShell>
  );
}
