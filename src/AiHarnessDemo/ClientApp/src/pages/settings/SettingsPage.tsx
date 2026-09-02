import { useState } from "react";
import { useBootstrapQuery, useAnalyzeRepositoryMutation, useSaveSettingsMutation } from "../../api/queries";
import type { OutcomeType } from "../../api/types";
import { AppShell } from "../../components/AppShell";
import { CheckIcon, FolderIcon, RefreshIcon } from "../../lib/icons";
import { lastPathPart } from "../../lib/format";
import { useToast } from "../../lib/toast";
import { AgentCard } from "./AgentCard";
import { DirectoryBrowserModal } from "./DirectoryBrowserModal";

export function SettingsPage() {
  const { data } = useBootstrapQuery();
  const toast = useToast();
  const saveSettings = useSaveSettingsMutation();
  const analyzeRepository = useAnalyzeRepositoryMutation();

  const settings = data?.settings;
  const [repositoryPath, setRepositoryPath] = useState(settings?.repositoryPath ?? "");
  const [knowledge, setKnowledge] = useState(settings?.repositoryKnowledge ?? "");
  const [outcome, setOutcome] = useState<OutcomeType>(settings?.outcome ?? "PullRequest");
  const [maxHandoffRetries, setMaxHandoffRetries] = useState(settings?.maxHandoffRetries ?? 2);
  const [runCopilotInit, setRunCopilotInit] = useState(true);
  const [browserOpen, setBrowserOpen] = useState(false);

  if (!data || !settings) return null;

  const { agents, copilotCli, workflow } = data;
  const enabledCount = agents.filter(agent => agent.enabled).length;

  async function onSave() {
    if (!Number.isInteger(maxHandoffRetries) || maxHandoffRetries < 0 || maxHandoffRetries > 10) {
      toast("Handoff retries must be a whole number between 0 and 10.", "error");
      return;
    }

    try {
      const saved = await saveSettings.mutateAsync({
        repositoryPath,
        repositoryKnowledge: knowledge,
        outcome,
        maxHandoffRetries
      });
      setRepositoryPath(saved.repositoryPath);
      setKnowledge(saved.repositoryKnowledge);
      setOutcome(saved.outcome);
      setMaxHandoffRetries(saved.maxHandoffRetries);
      toast("Harness settings saved.", "success");
    } catch (error) {
      toast(error instanceof Error ? error.message : String(error), "error");
    }
  }

  async function onAnalyze() {
    const path = repositoryPath.trim();
    if (!path) {
      toast("Choose a repository folder first.", "error");
      return;
    }
    try {
      const result = await analyzeRepository.mutateAsync({ path, runCopilotInit });
      setRepositoryPath(result.repositoryPath);
      setKnowledge(result.knowledge);
      toast(result.copilotInitMessage, result.copilotInitSucceeded ? "success" : "error");
    } catch (error) {
      toast(error instanceof Error ? error.message : String(error), "error");
    }
  }

  return (
    <AppShell active="settings" title="Settings" subtitle="Agent catalog · repository grounding · execution policy">
      <div className="page-head">
        <div>
          <div className="eyebrow">Harness configuration</div>
          <h2>Shape the team and its context</h2>
          <p>
            Agent definitions come from <span className="mono">.github/agents</span>; orchestration policy comes
            from hot-reloadable <span className="mono">WORKFLOW.md</span>. Settings are persisted to SQLite.
          </p>
        </div>
        <button className="button" onClick={() => void onSave()} disabled={saveSettings.isPending}>
          <CheckIcon /> {saveSettings.isPending ? "Saving..." : "Save settings"}
        </button>
      </div>
      <section className="settings-grid">
        <div className="settings-stack">
          <div className="card">
            <div className="card-header">
              <div>
                <h3>Copilot CLI runtime</h3>
                <p>Required for repository initialization and every agent execution.</p>
              </div>
              <span className={`status-pill ${copilotCli.ready ? "approved" : "failed"}`}>
                {copilotCli.ready ? "Ready" : "Unavailable"}
              </span>
            </div>
            <div className="card-body">
              <div className="runtime-line">
                <small>Command</small>
                <strong className="mono">{copilotCli.command}</strong>
              </div>
              <div className="runtime-line">
                <small>Version</small>
                <strong>{copilotCli.version || "Not detected"}</strong>
              </div>
              <div className="runtime-line">
                <small>Executable</small>
                <strong title={copilotCli.resolvedPath}>
                  {copilotCli.resolvedPath ? lastPathPart(copilotCli.resolvedPath) : "Not resolved"}
                </strong>
              </div>
              <div className={copilotCli.ready ? "callout" : "pushback-callout"} style={{ marginTop: 14 }}>
                {copilotCli.detail}
              </div>
            </div>
          </div>
          <div className="card">
            <div className="card-header">
              <div>
                <h3>Symphony workflow contract</h3>
                <p>Version-controlled policy, prompt template, hooks, and runtime limits.</p>
              </div>
              <span className={`status-pill ${workflow.ready ? "approved" : "failed"}`}>
                {workflow.ready ? "Hot reload active" : "Invalid"}
              </span>
            </div>
            <div className="card-body">
              <div className="runtime-line">
                <small>Policy</small>
                <strong title={workflow.sourcePath}>{lastPathPart(workflow.sourcePath)}</strong>
              </div>
              <div className="runtime-line">
                <small>Concurrency</small>
                <strong>{workflow.maxConcurrentAgents} flows</strong>
              </div>
              <div className="runtime-line">
                <small>Runtime retries</small>
                <strong>{workflow.maxAttempts} attempts</strong>
              </div>
              <div className="runtime-line">
                <small>Workspaces</small>
                <strong title={workflow.workspaceRoot ?? ""}>{lastPathPart(workflow.workspaceRoot ?? "")}</strong>
              </div>
              {workflow.lastError && <div className="pushback-callout">{workflow.lastError}</div>}
            </div>
          </div>
          <div className="card">
            <div className="card-header">
              <div>
                <h3>Available agents</h3>
                <p>
                  {enabledCount} of {agents.length} enabled for Team Lead selection
                </p>
              </div>
              <span className="status-pill approved">{enabledCount} active</span>
            </div>
            <div className="card-body">
              <div className="agent-grid">
                {agents.map(agent => (
                  <AgentCard agent={agent} key={agent.id} />
                ))}
              </div>
            </div>
          </div>
        </div>
        <div className="settings-stack">
          <div className="card">
            <div className="card-header">
              <div>
                <h3>Source project</h3>
                <p>Choose a project folder containing one or more Git repositories, then run Copilot init and the static study.</p>
              </div>
            </div>
            <div className="card-body">
              <div className="field">
                <label htmlFor="repository-path">Local folder</label>
                <div className="path-control">
                  <input
                    id="repository-path"
                    value={repositoryPath}
                    placeholder="Choose a source code repository"
                    onChange={event => setRepositoryPath(event.target.value)}
                  />
                  <button className="button" onClick={() => setBrowserOpen(true)}>
                    <FolderIcon /> Browse
                  </button>
                </div>
              </div>
              <label className="callout" style={{ marginTop: 14 }}>
                <input
                  id="run-copilot-init"
                  type="checkbox"
                  checked={runCopilotInit}
                  onChange={event => setRunCopilotInit(event.target.checked)}
                />
                <span>
                  <strong>Run Copilot init</strong>
                  <br />
                  Analyzes the code with read-only tools and creates or refreshes repository instructions before the
                  harness study.
                </span>
              </label>
              <button
                className="button primary"
                style={{ width: "100%", marginTop: 14 }}
                onClick={() => void onAnalyze()}
                disabled={analyzeRepository.isPending}
              >
                <RefreshIcon /> {analyzeRepository.isPending ? "Studying code..." : "Initialize and study repository"}
              </button>
            </div>
          </div>
          <div className="card">
            <div className="card-header">
              <div>
                <h3>Repository knowledge</h3>
                <p>Editable context generated from the selected source project and shared with every agent.</p>
              </div>
            </div>
            <div className="card-body">
              <div className="field">
                <label htmlFor="knowledge-editor">Knowledge for the selected project</label>
                <textarea
                  className="knowledge-editor"
                  id="knowledge-editor"
                  placeholder="Select and study a repository first."
                  value={knowledge}
                  onChange={event => setKnowledge(event.target.value)}
                />
                <small>
                  Correct assumptions, add domain language, and document constraints the code alone cannot reveal.
                </small>
              </div>
            </div>
          </div>
          <div className="card">
            <div className="card-header">
              <div>
                <h3>Delivery and recovery</h3>
                <p>Choose the release artifact and bound agent-to-agent revision loops.</p>
              </div>
            </div>
            <div className="card-body">
              <div className="field">
                <label>Delivery artifact</label>
                <div className="segmented">
                  <button
                    className={`segment ${outcome === "Commit" ? "active" : ""}`}
                    onClick={() => setOutcome("Commit")}
                  >
                    Commit
                  </button>
                  <button
                    className={`segment ${outcome === "PullRequest" ? "active" : ""}`}
                    onClick={() => setOutcome("PullRequest")}
                  >
                    Pull request
                  </button>
                </div>
              </div>
              <div className="field" style={{ marginTop: 16 }}>
                <label htmlFor="max-handoff-retries">Handoff retries</label>
                <input
                  id="max-handoff-retries"
                  type="number"
                  min={0}
                  max={10}
                  step={1}
                  value={maxHandoffRetries}
                  onChange={event => setMaxHandoffRetries(Number(event.target.value))}
                />
                <small>
                  Maximum corrective upstream turns for one blocked agent. The flow stops only after this limit is
                  exhausted.
                </small>
              </div>
              <div className="callout" style={{ marginTop: 16 }}>
                <span>
                  <strong>Powered by Copilot CLI.</strong>
                  <br />
                  Every selected agent runs inside an isolated Git worktree and can modify code, run commands, and
                  create the configured outcome.
                </span>
              </div>
            </div>
          </div>
        </div>
      </section>
      {browserOpen && (
        <DirectoryBrowserModal
          initialPath={repositoryPath}
          onClose={() => setBrowserOpen(false)}
          onSelect={path => {
            setRepositoryPath(path);
            setBrowserOpen(false);
          }}
        />
      )}
    </AppShell>
  );
}
