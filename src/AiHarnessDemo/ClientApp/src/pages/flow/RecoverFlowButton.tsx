import { useState } from "react";
import { useRecoverFlowMutation } from "../../api/queries";
import { RefreshIcon } from "../../lib/icons";
import { useToast } from "../../lib/toast";

export function RecoverFlowButton({ flowId }: { flowId: string }) {
  const toast = useToast();
  const recover = useRecoverFlowMutation();
  const [confirming, setConfirming] = useState(false);

  async function onRecover() {
    try {
      await recover.mutateAsync(flowId);
      setConfirming(false);
      toast(
        "Interrupted execution reconciled and queued from its persisted session and workspace.",
        "success"
      );
    } catch (error) {
      toast(error instanceof Error ? error.message : String(error), "error");
    }
  }

  return (
    <>
      <button
        className="button small"
        disabled={recover.isPending}
        onClick={() => setConfirming(true)}
      >
        <RefreshIcon /> {recover.isPending ? "Recovering..." : "Recover stalled execution"}
      </button>
      {confirming && (
        <div className="modal-backdrop" onClick={() => setConfirming(false)}>
          <div
            className="modal"
            role="dialog"
            aria-modal="true"
            aria-labelledby="recover-flow-title"
            onClick={event => event.stopPropagation()}
          >
            <div className="modal-head">
              <div>
                <div className="eyebrow">Interrupted execution recovery</div>
                <h2 id="recover-flow-title">Recover this flow?</h2>
              </div>
            </div>
            <div className="modal-body">
              <p>
                Use this when execution stopped progressing after sleep, a lost terminal, or a
                runtime interruption. Studio will stop the current Copilot CLI process, inspect its
                persisted journal, and queue the same attempt for safe continuation.
              </p>
              <p className="muted">
                The flow keeps its isolated workspace, session identity, workflow revision, and
                effective permission ceiling.
              </p>
            </div>
            <div className="modal-foot">
              <button
                className="button"
                disabled={recover.isPending}
                onClick={() => setConfirming(false)}
              >
                Keep running
              </button>
              <button
                className="button primary"
                disabled={recover.isPending}
                onClick={() => void onRecover()}
              >
                {recover.isPending ? "Recovering..." : "Stop and recover"}
              </button>
            </div>
          </div>
        </div>
      )}
    </>
  );
}
