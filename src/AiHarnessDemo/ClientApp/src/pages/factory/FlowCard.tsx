import { Link } from "react-router-dom";
import type { FlowSummaryDto } from "../../api/types";
import { StatusPill } from "../../components/StatusPill";
import { lastPathPart, statusLabel, timeAgo } from "../../lib/format";
import { flowRoute } from "../../lib/flowRoute";

export function FlowCard({ flow }: { flow: FlowSummaryDto }) {
  const repository = flow.repositoryPath ? lastPathPart(flow.repositoryPath) : "Repository pending";
  return (
    <Link className="flow-card" to={flowRoute(flow.id, flow.status)}>
      <div className="flow-card-top">
        <StatusPill status={flow.status} />
        <span className="model-chip">{flow.kind}</span>
      </div>
      <h3>{flow.title}</h3>
      <p>
        {flow.customerBlockerMessage ||
          flow.outcomeLabel ||
          "Open the sequential execution ledger and watch the team work."}
      </p>
      <div className="flow-card-context">
        <span>Iteration {flow.iteration}</span>
        {flow.readinessLabel && (
          <span
            className={`readiness-chip readiness-${flow.readinessState}`}
            data-readiness-state={flow.readinessState}
          >
            {flow.readinessLabel}
          </span>
        )}
        {flow.parentFlowRunId && (
          <span>
            {statusLabel(flow.linkKind)} from {flow.parentFlowRunId.slice(0, 8)}
          </span>
        )}
        {flow.linkedFlows.length > 0 && (
          <span>{flow.linkedFlows.length} linked successor{flow.linkedFlows.length === 1 ? "" : "s"}</span>
        )}
        {flow.review.decision && <span>{statusLabel(flow.review.decision)}</span>}
        {flow.review.publicationStatus !== "NotApplicable" &&
          flow.review.publicationStatus !== "AwaitingApproval" && (
            <span>Publication {statusLabel(flow.review.publicationStatus).toLowerCase()}</span>
          )}
      </div>
      <div className="flow-card-foot">
        <span>{repository}</span>
        <span>{timeAgo(flow.updatedAt)}</span>
      </div>
    </Link>
  );
}
