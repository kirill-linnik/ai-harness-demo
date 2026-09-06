import { useState } from "react";
import { useResolveOutcomeMutation } from "../../api/queries";
import type { FlowDetailDto, OutcomeResolutionAction } from "../../api/types";
import { useToast } from "../../lib/toast";
import { AbandonFlowButton } from "./AbandonFlowButton";

export function OutcomeResolutionCard({ flow }: { flow: FlowDetailDto }) {
  const toast = useToast();
  const resolveOutcome = useResolveOutcomeMutation();
  const [reason, setReason] = useState("");
  const gate = flow.outcomeVerification.humanResolutionGate;

  if (!gate) return null;

  async function resolve(action: OutcomeResolutionAction) {
    const explanation = reason.trim();
    if (!explanation) {
      toast("Explain why verification should continue or be replanned.", "error");
      return;
    }
    try {
      await resolveOutcome.mutateAsync({
        flowId: flow.id,
        body: { gateId: gate!.gateId, action, reason: explanation }
      });
      toast(
        action === "Continue"
          ? "One additional QA round was granted."
          : "A new acceptance-plan iteration was queued.",
        "success"
      );
    } catch (error) {
      toast(error instanceof Error ? error.message : String(error), "error");
    }
  }

  return (
    <section className="card" aria-labelledby="outcome-resolution-heading">
      <div className="card-header">
        <div>
          <h3 id="outcome-resolution-heading">Outcome verification needs a decision</h3>
          <p>The QA budget ended without a current PASS. Release approval is unavailable.</p>
        </div>
        <span className="status-pill failed">Human resolution</span>
      </div>
      <div className="card-body">
        <label className="field" htmlFor="outcome-resolution-reason">
          <span>Operator reason</span>
          <textarea
            id="outcome-resolution-reason"
            rows={3}
            value={reason}
            onChange={event => setReason(event.target.value)}
            placeholder="Required for Continue or Replan."
          />
        </label>
        <div className="feedback-actions">
          <AbandonFlowButton flowId={flow.id} />
          <button
            className="button"
            disabled={resolveOutcome.isPending}
            onClick={() => void resolve("Replan")}
          >
            Replan requirements
          </button>
          <button
            className="button primary"
            disabled={resolveOutcome.isPending}
            onClick={() => void resolve("Continue")}
          >
            Grant one QA round
          </button>
        </div>
      </div>
    </section>
  );
}
