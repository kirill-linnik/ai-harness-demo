/**
 * Module-level singleton mirroring `state.feedbackSentForFlow` from wwwroot/app.js: once
 * the customer sends feedback for a flow, the "Re-do with feedback" action unlocks
 * immediately, even before a ProductManager/Harness reply message is visible. This is
 * intentionally global, uncomponentized UI state, not server state, so it lives outside
 * TanStack Query and outside any single component's lifecycle (a flow page can be left
 * and revisited within the same session).
 */
const feedbackSentForFlow = new Set<string>();

export function markFeedbackSent(flowId: string): void {
  feedbackSentForFlow.add(flowId);
}

export function hasFeedbackBeenSent(flowId: string): boolean {
  return feedbackSentForFlow.has(flowId);
}
