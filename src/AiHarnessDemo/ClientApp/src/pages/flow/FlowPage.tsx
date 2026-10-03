import { useEffect, useState } from "react";
import { Link, useParams } from "react-router-dom";
import { useFlowQuery, useRestartFlowMutation } from "../../api/queries";
import type { FlowDetailDto } from "../../api/types";
import { AppShell } from "../../components/AppShell";
import { BootScreen } from "../../components/BootScreen";
import { FatalScreen } from "../../components/FatalScreen";
import { StatusPill } from "../../components/StatusPill";
import { BackIcon, CopyIcon, ExternalIcon, FactoryIcon, RefreshIcon } from "../../lib/icons";
import { groupBy, lastPathPart, statusLabel, timeAgo } from "../../lib/format";
import { flowRoute } from "../../lib/flowRoute";
import { useToast } from "../../lib/toast";
import { IterationLane } from "./IterationLane";
import { StepDetail } from "./StepDetail";
import { Timeline } from "./Timeline";
import { AbandonFlowButton } from "./AbandonFlowButton";
import { BlockedFlowCard } from "./BlockedFlowCard";
import { ReviewCard } from "./ReviewCard";
import { ReadinessPanel } from "./ReadinessPanel";
import { RecoverFlowButton } from "./RecoverFlowButton";
import { CustomerDialogue } from "./CustomerDialogue";

const failedTaskRecoveryEvents = new Set([
  "flow.manual-restart",
  "flow.finalization-retry-queued",
  "flow.completed-output-recovered",
  "flow.contract-correction-queued"
]);

function latestManualRetry(flow: FlowDetailDto) {
  return [...flow.steps]
    .reverse()
    .find(step =>
      step.label.startsWith("Manual restart of ") ||
      step.label.startsWith("Correct invalid response from ")
    );
}

function recoveryCommitted(
  flow: FlowDetailDto,
  previousStepIds: ReadonlySet<string>,
  previousEventIds: ReadonlySet<string>
) {
  return (
    flow.steps.some(
      step =>
        !previousStepIds.has(step.id) &&
        step.label.startsWith("Manual restart of ")
    ) ||
    flow.events.some(
      event =>
        !previousEventIds.has(event.id) &&
        failedTaskRecoveryEvents.has(event.type)
    )
  );
}

export function FlowPage() {
  const { id } = useParams<{ id: string }>();
  const toast = useToast();
  const flowQuery = useFlowQuery(id);
  const restartFlow = useRestartFlowMutation();
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
  const publishedDelivery =
    flow.kind === "Delivery" &&
    flow.status === "Approved" &&
    flow.publicationStatus === "Published" &&
    Boolean(flow.outcomeUrl);
  const canRecoverExecution =
    flow.status === "Queued" ||
    flow.status === "Running" ||
    flow.status === "Reworking";

  async function copyFlowLink() {
    const link = `${location.origin}${location.pathname}#/factory/${flow!.id}`;
    await navigator.clipboard.writeText(link);
    toast("Flow link copied.", "success");
  }

  async function restartFailedFlow(failed: FlowDetailDto) {
    const previousStepIds = new Set(failed.steps.map(step => step.id));
    const previousEventIds = new Set(failed.events.map(event => event.id));
    try {
      const restarted = await restartFlow.mutateAsync(failed.id);
      const retryStep = latestManualRetry(restarted);
      setSelectedStepId(retryStep?.id ?? null);
      toast("Failed task recovery queued from its preserved session and workspace.", "success");
    } catch (error) {
      try {
        const refreshed = (await flowQuery.refetch()).data;
        if (
          refreshed &&
          recoveryCommitted(refreshed, previousStepIds, previousEventIds)
        ) {
          setSelectedStepId(latestManualRetry(refreshed)?.id ?? null);
          if (refreshed.status === "Failed") {
            toast(
              "Failed task recovery was committed, but the retried task has already failed again.",
              "error"
            );
          } else {
            toast(
              "Failed task recovery was committed despite the interrupted response; the current flow state was refreshed.",
              "success"
            );
          }
          return;
        }
      } catch (refreshError) {
        const recoveryMessage = error instanceof Error ? error.message : String(error);
        const refreshMessage =
          refreshError instanceof Error ? refreshError.message : String(refreshError);
        toast(`${recoveryMessage} Current flow state could not be refreshed: ${refreshMessage}`, "error");
        return;
      }
      toast(error instanceof Error ? error.message : String(error), "error");
    }
  }

  return (
    <AppShell
      active="factory"
      title={`Flow ${flow.id.slice(0, 8)}`}
      subtitle={`${repositoryName} · ${
        flow.kind === "Advisory"
          ? "read-only source snapshot"
          : "isolated delivery workspace"
      }`}
      actions={<StatusPill status={flow.status} />}
    >
      <section className="flow-heading">
        <div>
          <Link className="button ghost small" to="/factory">
            <BackIcon /> All factory flows
          </Link>
          <h2>{flow.title}</h2>
          <p>
            {repositoryName} · {flow.kind} · iteration {flow.iteration} ·{" "}
            {flow.kind === "Advisory"
              ? "read-only recommendation"
              : `${flow.outcome === "PullRequest" ? "pull request" : "commit"} delivery`}{" "}
            · {statusLabel(flow.modelSelectionStrategy)}
          </p>
          <div className="flow-meta">
            <StatusPill status={flow.status} />
            <span>Created {timeAgo(flow.createdAt)}</span>
            <span>{allSteps.length} agent executions</span>
            {flow.agentCatalogRevision && (
              <span title={flow.agentCatalogRevision}>
                Catalog {flow.agentCatalogRevision.slice(0, 12)}
              </span>
            )}
            <span>{allSteps.filter(step => step.status === "Pushback").length} pushbacks observed</span>
          </div>
        </div>
        <div className="flow-heading-actions">
          {canRecoverExecution && <RecoverFlowButton flowId={flow.id} />}
          {flow.status !== "Approved" &&
            flow.status !== "Abandoned" &&
            flow.status !== "Blocked" && (
            <AbandonFlowButton flowId={flow.id} className="button danger small" />
          )}
          <button className="button small" onClick={() => void copyFlowLink()}>
            <CopyIcon /> Copy link
          </button>
          {publishedDelivery && flow.outcomeUrl && (
            <a className="button primary small" href={flow.outcomeUrl}>
              <ExternalIcon /> {publishedDelivery || flow.status === "Approved"
                ? "Published outcome"
                : "Customer preview"}
            </a>
          )}
        </div>
      </section>
      {(flow.parentFlowRunId || flow.linkedFlows.length > 0) && (
        <nav className="card flow-lineage" aria-label="Related flows">
          <div className="card-header">
            <div>
              <h3>Flow lineage</h3>
              <p>Linked flows remain independently addressable.</p>
            </div>
          </div>
          <div className="card-body lineage-links">
            {flow.parentFlowRunId && (
              <Link className="lineage-link" to={`/factory/${flow.parentFlowRunId}`}>
                <span>Parent · {statusLabel(flow.linkKind)}</span>
                <strong>
                  Flow {flow.parentFlowRunId.slice(0, 8)}
                  {flow.parentIteration ? ` · iteration ${flow.parentIteration}` : ""}
                </strong>
              </Link>
            )}
            {flow.linkedFlows.map(child => (
              <Link
                className="lineage-link"
                to={flowRoute(child.id, child.status)}
                key={child.id}
              >
                <span>Child · {statusLabel(child.linkKind)}</span>
                <strong>{child.title}</strong>
                <small>
                  {child.kind} · {statusLabel(child.status)}
                  {child.review.decision ? ` · ${statusLabel(child.review.decision)}` : ""}
                </small>
              </Link>
            ))}
          </div>
        </nav>
      )}
      {flow.status === "Blocked" && <BlockedFlowCard flow={flow} />}
      <CustomerDialogue flow={flow} />
      <div className="card lane-card">
        <div className="card-header">
          <div>
            <h3>Sequential execution plan</h3>
            <p>Persisted steps run in the linear order shown below; status text accompanies every visual state.</p>
          </div>
          <span className={`status-pill ${flow.status.toLowerCase()}`}>{progress}% current iteration</span>
        </div>
        <div className="progress-line">
          <span style={{ transform: `scaleX(${progress / 100})` }}></span>
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
      {flow.deliveryReadiness && <ReadinessPanel flow={flow} />}
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
              <p>Chronological flow decisions with runtime detail grouped by agent attempt.</p>
            </div>
          </div>
          <div className="card-body">
            <Timeline events={flow.events} steps={flow.steps} />
          </div>
        </div>
      </section>
      {flow.review.available && <ReviewCard flow={flow} />}
      {flow.status === "Approved" && (
        <section className="approval-banner">
          <div>
            <strong>
              {flow.kind === "Advisory"
                ? "Customer accepted this Advisory"
                : "Customer approved this Delivery"}
            </strong>
            <span>
              {flow.kind === "Advisory"
                ? "The recommendation is complete; no source publication was created."
                : "The published result and execution evidence remain available in history."}
            </span>
          </div>
          <div className="flow-heading-actions">
            {publishedDelivery && (
              <a className="button success" href={flow.outcomeUrl}>
                <ExternalIcon /> Published outcome
              </a>
            )}
          </div>
        </section>
      )}
      {flow.status === "Abandoning" && (
        <section className="approval-banner">
          <div>
            <strong>Removing customer artifacts</strong>
            <span>Execution history and model-learning evidence will remain available.</span>
          </div>
        </section>
      )}
      {flow.status === "Abandoned" && (
        <section className="approval-banner">
          <div>
            <strong>Flow abandoned</strong>
            <span>Customer artifacts were removed; execution history and learning evidence were retained.</span>
          </div>
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
          <button
            className="button"
            disabled={restartFlow.isPending}
            onClick={() => void restartFailedFlow(flow)}
          >
            <RefreshIcon /> {restartFlow.isPending ? "Recovering..." : "Recover failed task"}
          </button>
        </section>
      )}
    </AppShell>
  );
}
