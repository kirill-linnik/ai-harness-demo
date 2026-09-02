import { useEffect, useState } from "react";
import { Link, useParams } from "react-router-dom";
import { useFlowQuery } from "../../api/queries";
import { AppShell } from "../../components/AppShell";
import { BootScreen } from "../../components/BootScreen";
import { FatalScreen } from "../../components/FatalScreen";
import { StatusPill } from "../../components/StatusPill";
import { BackIcon, CopyIcon, ExternalIcon, FactoryIcon } from "../../lib/icons";
import { groupBy, lastPathPart, timeAgo } from "../../lib/format";
import { useToast } from "../../lib/toast";
import { IterationLane } from "./IterationLane";
import { StepDetail } from "./StepDetail";
import { Timeline } from "./Timeline";
import { FeedbackCard } from "./FeedbackCard";

export function FlowPage() {
  const { id } = useParams<{ id: string }>();
  const toast = useToast();
  const flowQuery = useFlowQuery(id);
  const flow = flowQuery.data;

  const [selectedStepId, setSelectedStepId] = useState<string | null>(null);

  useEffect(() => {
    if (!flow) return;
    const steps = flow.steps;
    if (selectedStepId && steps.some(step => step.id === selectedStepId)) return;
    const activeStep = steps.find(step => step.status === "Running");
    const fallback = [...steps].reverse().find(step => step.status !== "Pending");
    setSelectedStepId(activeStep?.id ?? fallback?.id ?? steps[0]?.id ?? null);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [flow]);

  if (flowQuery.isLoading) return <BootScreen />;
  if (flowQuery.isError) {
    const message = flowQuery.error instanceof Error ? flowQuery.error.message : String(flowQuery.error);
    return (
      <FatalScreen
        title="The flow could not load"
        message={message}
        onRetry={() => void flowQuery.refetch()}
      />
    );
  }
  if (!flow) return null;

  const allSteps = flow.steps;
  const selectedStep = allSteps.find(step => step.id === selectedStepId);
  const selectedGate = flow.gateRecords.filter(record => record.flowStepId === selectedStepId).at(-1);
  const currentSteps = allSteps.filter(step => step.iteration === flow.iteration);
  const completed = currentSteps.filter(step =>
    ["Completed", "Pushback", "Skipped"].includes(step.status)
  ).length;
  const progress = currentSteps.length
    ? Math.round((completed / currentSteps.length) * 100)
    : flow.status === "Queued"
      ? 5
      : 0;
  const grouped = groupBy(allSteps, step => step.iteration);
  const repositoryName = lastPathPart(flow.repositoryPath);

  async function copyFlowLink() {
    const link = `${location.origin}${location.pathname}#/factory/${flow!.id}`;
    await navigator.clipboard.writeText(link);
    toast("Flow link copied.", "success");
  }

  return (
    <AppShell
      active="factory"
      title={`Flow ${flow.id.slice(0, 8)}`}
      subtitle={`${repositoryName} · isolated Copilot CLI worktree`}
      actions={<StatusPill status={flow.status} />}
    >
      <section className="flow-heading">
        <div>
          <Link className="button ghost small" to="/factory">
            <BackIcon /> All factory flows
          </Link>
          <h2>{flow.title}</h2>
          <p>
            {repositoryName} · iteration {flow.iteration} ·{" "}
            {flow.outcome === "PullRequest" ? "pull request outcome" : "commit outcome"}
          </p>
          <div className="flow-meta">
            <StatusPill status={flow.status} />
            <span>Created {timeAgo(flow.createdAt)}</span>
            <span>{allSteps.length} agent executions</span>
            <span>{allSteps.filter(step => step.status === "Pushback").length} pushbacks observed</span>
          </div>
        </div>
        <div className="flow-heading-actions">
          <button className="button small" onClick={() => void copyFlowLink()}>
            <CopyIcon /> Copy link
          </button>
          {flow.outcomeUrl && (
            <a className="button primary small" href={flow.outcomeUrl}>
              <ExternalIcon /> Customer preview
            </a>
          )}
        </div>
      </section>
      <div className="card lane-card">
        <div className="card-header">
          <div>
            <h3>Observable delivery graph</h3>
            <p>Gray is queued, blue is working, green is complete, red is a pushed-back handoff.</p>
          </div>
          <span className={`status-pill ${flow.status.toLowerCase()}`}>{progress}% current iteration</span>
        </div>
        <div className="progress-line">
          <span style={{ width: `${progress}%` }}></span>
        </div>
        {Object.keys(grouped).length ? (
          Object.entries(grouped).map(([iteration, steps]) => (
            <IterationLane
              key={iteration}
              iteration={iteration}
              steps={steps}
              selectedStepId={selectedStepId}
              onSelectStep={setSelectedStepId}
            />
          ))
        ) : (
          <div className="empty-state">
            <div>
              <div className="empty-icon">
                <FactoryIcon />
              </div>
              <h3>Team Lead is entering the factory</h3>
              <p>The first planning stage will appear here.</p>
            </div>
          </div>
        )}
      </div>
      <section className="factory-detail-grid">
        <div className="card detail-panel">
          <div className="card-header">
            <div>
              <h3>Agent handoff</h3>
              <p>Why this agent and model were selected, plus the observable output.</p>
            </div>
          </div>
          <div className="card-body">
            {selectedStep ? (
              <StepDetail step={selectedStep} gate={selectedGate} />
            ) : (
              <div className="detail-empty">Select an agent node to inspect its handoff.</div>
            )}
          </div>
        </div>
        <div className="card">
          <div className="card-header">
            <div>
              <h3>Execution ledger</h3>
              <p>Append-only events across the full flow.</p>
            </div>
          </div>
          <div className="card-body">
            <Timeline events={flow.events} />
          </div>
        </div>
      </section>
      {flow.status === "WaitingForFeedback" && <FeedbackCard flow={flow} />}
      {flow.status === "Approved" && (
        <section className="approval-banner">
          <div>
            <strong>Customer approved this outcome</strong>
            <span>The factory flow is complete and remains available in execution history.</span>
          </div>
          <a className="button success" href={flow.outcomeUrl}>
            <ExternalIcon /> Open accepted preview
          </a>
        </section>
      )}
      {flow.status === "Failed" && (
        <section
          className="approval-banner"
          style={{ borderColor: "rgba(255,102,125,.3)", background: "rgba(255,102,125,.07)", color: "#ffdbe1" }}
        >
          <div>
            <strong>Flow stopped</strong>
            <span>{flow.failureReason}</span>
          </div>
        </section>
      )}
    </AppShell>
  );
}
