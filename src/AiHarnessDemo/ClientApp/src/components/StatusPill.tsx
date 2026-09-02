import { statusClass, statusLabel } from "../lib/format";

export function StatusPill({ status }: { status: string }) {
  return <span className={`status-pill ${statusClass(status)}`}>{statusLabel(status)}</span>;
}
