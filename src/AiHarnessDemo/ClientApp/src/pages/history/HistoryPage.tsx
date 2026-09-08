import { Link } from "react-router-dom";
import { useHistoryQuery } from "../../api/queries";
import { AppShell } from "../../components/AppShell";
import { BootScreen } from "../../components/BootScreen";
import { FatalScreen } from "../../components/FatalScreen";
import { StatCard } from "../../components/StatCard";
import { StatusPill } from "../../components/StatusPill";
import { HistoryIcon } from "../../lib/icons";
import { formatDuration, statusLabel, timeAgo } from "../../lib/format";
import { flowRoute } from "../../lib/flowRoute";

export function HistoryPage() {
  const historyQuery = useHistoryQuery();
  const history = historyQuery.data;

  if (historyQuery.isLoading) return <BootScreen />;
  if (historyQuery.isError) {
    const error = historyQuery.error;
    return (
      <FatalScreen
        title="Execution history could not load"
        message={error instanceof Error ? error.message : String(error)}
        onRetry={() => void historyQuery.refetch()}
      />
    );
  }
  if (!history) return null;

  const successCount = history.filter(item => item.status === "Completed").length;
  const failedCount = history.filter(item => ["Failed", "Pushback"].includes(item.status)).length;
  const average = history.length
    ? Math.round(history.reduce((sum, item) => sum + item.durationMilliseconds, 0) / history.length)
    : 0;

  return (
    <AppShell active="history" title="Execution history" subtitle={`${history.length} persisted agent attempts`}>
      <div className="page-head">
        <div>
          <div className="eyebrow">Full transparency</div>
          <h2>Execution history</h2>
          <p>Every agent, model, duration, outcome, and pushback across every factory flow.</p>
        </div>
      </div>
      <section className="stats-grid">
        <StatCard label="Executions" value={history.length} detail="All persisted agent attempts" glow="rgba(155,135,245,.14)" />
        <StatCard label="Completed" value={successCount} detail="Successful handoffs" glow="rgba(66,214,164,.13)" />
        <StatCard label="Pushback or failed" value={failedCount} detail="Visible correction signals" glow="rgba(255,102,125,.13)" />
        <StatCard
          label="Average duration"
          value={Math.round(average / 100) / 10}
          detail="Seconds per agent execution"
          glow="rgba(81,168,255,.13)"
        />
      </section>
      <section className="card">
        <div className="card-header">
          <div>
            <h3>Agent execution ledger</h3>
            <p>Newest execution first</p>
          </div>
        </div>
        <div className="table-wrap">
          {history.length ? (
            <table className="history-table">
              <thead>
                <tr>
                  <th>Factory flow</th>
                  <th>Flow state</th>
                  <th>Iteration</th>
                  <th>Agent</th>
                  <th>Model</th>
                  <th>Duration</th>
                  <th>Outcome</th>
                  <th>Started</th>
                </tr>
              </thead>
              <tbody>
                {history.map((item, index) => (
                  <tr key={`${item.flowId}-${index}`}>
                    <td>
                      <Link
                        className="table-flow"
                        to={flowRoute(item.flowId, item.flowStatus)}
                        title={item.flowTitle}
                      >
                        {item.flowTitle}
                      </Link>
                      <br />
                      <span className="muted">
                        {item.flowKind}
                        {item.parentFlowRunId
                          ? ` · ${statusLabel(item.linkKind)} from ${item.parentFlowRunId.slice(0, 8)}`
                          : ""}
                      </span>
                    </td>
                    <td>
                      <StatusPill status={item.flowStatus} />
                      <br />
                      <span className="muted">
                        {item.customerBlockerMessage ||
                        (item.review.decision
                          ? statusLabel(item.review.decision)
                          : item.outcomeLabel || "No customer decision")}
                        {item.review.publicationStatus !== "NotApplicable" &&
                        item.review.publicationStatus !== "AwaitingApproval"
                          ? ` · publication ${statusLabel(item.review.publicationStatus).toLowerCase()}`
                          : ""}
                      </span>
                    </td>
                    <td>{item.iteration}</td>
                    <td>
                      <strong>{item.agentName}</strong>
                      <br />
                      <span className="muted">{item.agentRole}</span>
                    </td>
                    <td>
                      <span className="model-chip">{item.model || "Pending"}</span>
                    </td>
                    <td>{formatDuration(item.durationMilliseconds)}</td>
                    <td>
                      <StatusPill status={item.status} />
                    </td>
                    <td>{item.startedAt ? timeAgo(item.startedAt) : "Not started"}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          ) : (
            <div className="empty-state">
              <div>
                <div className="empty-icon">
                  <HistoryIcon />
                </div>
                <h3>No executions yet</h3>
                <p>Start a factory flow and every agent attempt will appear here.</p>
              </div>
            </div>
          )}
        </div>
      </section>
    </AppShell>
  );
}
