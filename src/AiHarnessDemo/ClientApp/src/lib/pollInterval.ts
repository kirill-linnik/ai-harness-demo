import type { FlowStatus } from "../api/types";

/** Flow statuses where the factory is still actively working. */
const ACTIVE_STATUSES: ReadonlySet<FlowStatus> = new Set<FlowStatus>([
  "Intake",
  "Queued",
  "Running",
  "Reworking",
  "Abandoning"
]);

/**
 * Matches the original wwwroot/app.js `scheduleFlowPoll` cadence: poll every second
 * while the flow is in a machine-active state, and stop polling once it settles
 * into a durable human or terminal state.
 */
export function flowPollIntervalMs(status: FlowStatus | undefined): number | false {
  if (!status) return false;
  return ACTIVE_STATUSES.has(status) ? 1000 : false;
}
