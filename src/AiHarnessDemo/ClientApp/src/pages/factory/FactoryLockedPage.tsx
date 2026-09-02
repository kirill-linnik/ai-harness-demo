import { Link } from "react-router-dom";
import { AppShell } from "../../components/AppShell";
import { useBootstrapQuery } from "../../api/queries";
import { FolderIcon, SettingsIcon } from "../../lib/icons";

export function FactoryLockedPage() {
  const { data } = useBootstrapQuery();
  const reason = data?.factoryDisabledReason || "Add and study a source repository in Settings.";

  return (
    <AppShell active="factory" title="AI Factory" subtitle="Configure a source project to unlock agent execution">
      <div className="page-head">
        <div>
          <div className="eyebrow">Project required</div>
          <h2>The factory is waiting for its codebase</h2>
          <p>Agents stay disabled until the harness has a real repository and shared project knowledge.</p>
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
            <span className="model-chip">1 · Choose a Git repository</span>
            <span className="model-chip">2 · Run Copilot init</span>
            <span className="model-chip">3 · Review project knowledge</span>
          </div>
          <Link className="button primary" to="/settings" style={{ marginTop: 22 }}>
            <SettingsIcon /> Add project in Settings
          </Link>
        </div>
      </section>
    </AppShell>
  );
}
