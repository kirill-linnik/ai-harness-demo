import type { JSX, ReactNode } from "react";
import { useNavigate } from "react-router-dom";
import { useBootstrapQuery } from "../api/queries";
import { lastPathPart } from "../lib/format";
import { FactoryIcon, HistoryIcon, MemoryIcon, PlusIcon, SettingsIcon } from "../lib/icons";
import { NavItem } from "./NavItem";

export function AppShell({
  active,
  title,
  subtitle,
  actions,
  children
}: {
  active: "factory" | "settings" | "history" | "memory";
  title: string;
  subtitle: string;
  actions?: ReactNode;
  children: ReactNode;
}): JSX.Element {
  const { data } = useBootstrapQuery();
  const navigate = useNavigate();
  const activeCount = data?.stats?.activeFlows ?? 0;
  const memoryCount = data?.stats?.learnedRefinements ?? 0;
  const repositoryName = data?.settings?.repositoryPath
    ? lastPathPart(data.settings.repositoryPath)
    : "No repository";

  return (
    <div className="app-shell">
      <aside className="sidebar">
        <div className="brand">
          <div className="brand-mark">
            <span></span>
            <span></span>
            <span></span>
          </div>
          <div className="brand-copy">
            <strong>AI Harness Studio</strong>
            <span>Symphony .NET core</span>
          </div>
        </div>
        <div className="nav-label">Workspace</div>
        <nav className="nav-list" aria-label="Main navigation">
          <NavItem
            page="factory"
            label="AI Factory"
            icon={<FactoryIcon />}
            active={active}
            count={activeCount}
            disabled={!data?.factoryEnabled}
            disabledReason={data?.factoryDisabledReason || "Configure a project first."}
          />
          <NavItem page="settings" label="Settings" icon={<SettingsIcon />} active={active} />
          <NavItem page="history" label="Execution history" icon={<HistoryIcon />} active={active} />
          <NavItem
            page="memory"
            label="Harness memory"
            icon={<MemoryIcon />}
            active={active}
            count={memoryCount}
          />
        </nav>
        <div className="sidebar-spacer"></div>
        <div className="runtime-card">
          <div className="runtime-line">
            <span className={`status-light ${data?.copilotCliAvailable ? "" : "offline"}`}></span>
            <strong>{data?.copilotCliAvailable ? "Copilot CLI ready" : "Copilot CLI unavailable"}</strong>
          </div>
          <div className="runtime-line">
            <small>Repository</small>
            <strong title={data?.settings?.repositoryPath || ""}>{repositoryName}</strong>
          </div>
          <div className="runtime-line">
            <span className={`status-light ${data?.workflow?.ready ? "" : "offline"}`}></span>
            <strong title={data?.workflow?.sourcePath || ""}>
              {data?.workflow?.ready ? "WORKFLOW.md live" : "Workflow needs attention"}
            </strong>
          </div>
        </div>
      </aside>
      <div className="main-column">
        <header className="topbar">
          <div className="topbar-title">
            <h1>{title}</h1>
            <p>{subtitle}</p>
          </div>
          <div className="topbar-actions">
            {actions}
            <button
              className="button primary small"
              disabled={!data?.factoryEnabled}
              title={
                data?.factoryEnabled
                  ? "Start a new assignment"
                  : data?.factoryDisabledReason || "Configure a project first."
              }
              onClick={() => navigate("/intake")}
            >
              <PlusIcon /> New assignment
            </button>
          </div>
        </header>
        <main className="content">{children}</main>
      </div>
    </div>
  );
}
