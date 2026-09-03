import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { useAbandonFlowMutation } from "../../api/queries";
import { useToast } from "../../lib/toast";

export function AbandonFlowButton({
  flowId,
  className = "button danger"
}: {
  flowId: string;
  className?: string;
}) {
  const navigate = useNavigate();
  const toast = useToast();
  const abandon = useAbandonFlowMutation();
  const [confirming, setConfirming] = useState(false);

  async function onAbandon() {
    try {
      const result = await abandon.mutateAsync(flowId);
      const ports = result.listeningPortsReleased.length
        ? ` Ports released: ${result.listeningPortsReleased.join(", ")}.`
        : "";
      toast(
        `Flow abandoned and customer artifacts removed.${ports} Learning evidence was retained.`,
        "success"
      );
      navigate("/factory");
    } catch (error) {
      toast(error instanceof Error ? error.message : String(error), "error");
    }
  }

  return (
    <>
      <button className={className} onClick={() => setConfirming(true)}>
        Abandon
      </button>
      {confirming && (
        <div className="modal-backdrop" onClick={() => setConfirming(false)}>
          <div
            className="modal"
            role="dialog"
            aria-modal="true"
            aria-labelledby="abandon-flow-title"
            onClick={event => event.stopPropagation()}
          >
            <div className="modal-head">
              <div>
                <div className="eyebrow">Permanent customer-artifact cleanup</div>
                <h2 id="abandon-flow-title">Abandon this flow?</h2>
              </div>
            </div>
            <div className="modal-body">
              <p>
                This stops active work and removes the flow worktree, generated previews, Copilot sessions, local and
                remote branches, and any running workspace applications.
              </p>
              <p className="muted">
                The execution ledger and model-learning evidence remain in the harness.
              </p>
            </div>
            <div className="modal-foot">
              <button className="button" disabled={abandon.isPending} onClick={() => setConfirming(false)}>
                Keep flow
              </button>
              <button className="button danger" disabled={abandon.isPending} onClick={() => void onAbandon()}>
                {abandon.isPending ? "Removing artifacts..." : "Abandon and remove artifacts"}
              </button>
            </div>
          </div>
        </div>
      )}
    </>
  );
}
