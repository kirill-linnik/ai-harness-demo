import { Link } from "react-router-dom";
import type { FlowSummaryDto } from "../../api/types";
import { StatusPill } from "../../components/StatusPill";
import { lastPathPart, timeAgo } from "../../lib/format";

export function FlowCard({ flow }: { flow: FlowSummaryDto }) {
  const repository = flow.repositoryPath ? lastPathPart(flow.repositoryPath) : "Repository pending";
  return (
    <Link className="flow-card" to={`/factory/${flow.id}`}>
      <div className="flow-card-top">
        <StatusPill status={flow.status} />
        <span className="muted">Iteration {flow.iteration}</span>
      </div>
      <h3>{flow.title}</h3>
      <p>{flow.outcomeLabel || "Open the execution ledger and watch the team work."}</p>
      <div className="flow-card-foot">
        <span>{repository}</span>
        <span>{timeAgo(flow.updatedAt)}</span>
      </div>
    </Link>
  );
}
