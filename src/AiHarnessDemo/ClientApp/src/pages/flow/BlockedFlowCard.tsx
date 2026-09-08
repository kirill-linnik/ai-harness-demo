import { useState } from "react";
import { useNavigate } from "react-router-dom";
import type { FlowDetailDto, QualificationResolutionAction } from "../../api/types";
import { useResolveQualificationMutation } from "../../api/queries";
import { RefreshIcon } from "../../lib/icons";
import { useToast } from "../../lib/toast";
import { AbandonFlowButton } from "./AbandonFlowButton";

const MAX_GOAL_CHARACTERS = 4_000;
const MAX_SCOPE_ITEMS = 24;
const MAX_SCOPE_ITEM_CHARACTERS = 4_000;

export function BlockedFlowCard({ flow }: { flow: FlowDetailDto }) {
  const navigate = useNavigate();
  const toast = useToast();
  const resolveQualification = useResolveQualificationMutation();
  const [revising, setRevising] = useState(false);
  const [goal, setGoal] = useState(flow.title);
  const [scope, setScope] = useState("");

  async function resolve(action: QualificationResolutionAction) {
    const scopeItems = scope
      .split(/\r?\n/)
      .map(value => value.trim())
      .filter(Boolean);
    if (action === "ScopeRevision") {
      if (!goal.trim()) {
        toast("Add a revised goal.", "error");
        return;
      }
      if (goal.trim().length > MAX_GOAL_CHARACTERS) {
        toast(`The revised goal must be at most ${MAX_GOAL_CHARACTERS} characters.`, "error");
        return;
      }
      if (
        scopeItems.length === 0 ||
        scopeItems.length > MAX_SCOPE_ITEMS ||
        scopeItems.some(value => value.length > MAX_SCOPE_ITEM_CHARACTERS)
      ) {
        toast(
          `Add 1-${MAX_SCOPE_ITEMS} scope items, one per line and at most ${MAX_SCOPE_ITEM_CHARACTERS} characters each.`,
          "error"
        );
        return;
      }
    }

    try {
      const result = await resolveQualification.mutateAsync({
        flowId: flow.id,
        body:
          action === "RosterRepair"
            ? { action }
            : {
                action,
                scopeRevision: {
                  goal: goal.trim(),
                  scope: scopeItems
                }
              }
      });
      toast(
        result.existingSuccessor
          ? "The existing linked successor was opened."
          : result.accountManagerReply,
        "success"
      );
      navigate(
        result.successorStatus === "Intake"
          ? `/intake/${result.successorFlowId}`
          : `/factory/${result.successorFlowId}`
      );
    } catch (error) {
      toast(error instanceof Error ? error.message : String(error), "error");
    }
  }

  return (
    <section className="card blocked-flow-card" aria-labelledby="blocked-flow-heading">
      <div className="card-header">
        <div>
          <div className="eyebrow">Customer action required</div>
          <h3 id="blocked-flow-heading">This flow needs a different qualification</h3>
        </div>
        <span className="status-pill blocked">Blocked</span>
      </div>
      <div className="card-body">
        <p className="blocked-customer-message">
          {flow.customerBlockerMessage ||
            "The available team cannot complete this request safely as currently scoped."}
        </p>

        <details className="operator-details">
          <summary>Operator diagnostics</summary>
          <dl>
            <div>
              <dt>Code</dt>
              <dd className="mono">{flow.currentBlockerCode || "Unavailable"}</dd>
            </div>
            <div>
              <dt>Summary</dt>
              <dd>{flow.currentBlockerSummary || "No operator summary was recorded."}</dd>
            </div>
          </dl>
          {flow.currentBlockerDataJson && <pre>{flow.currentBlockerDataJson}</pre>}
        </details>

        {revising && (
          <div className="review-refinement">
            <label className="field" htmlFor="qualification-goal">
              <span>Revised goal</span>
              <textarea
                id="qualification-goal"
                rows={2}
                maxLength={MAX_GOAL_CHARACTERS}
                value={goal}
                onChange={event => setGoal(event.target.value)}
              />
            </label>
            <label className="field" htmlFor="qualification-scope">
              <span>Revised scope (one item per line)</span>
              <textarea
                id="qualification-scope"
                rows={4}
                value={scope}
                onChange={event => setScope(event.target.value)}
              />
            </label>
          </div>
        )}

        <div className="feedback-actions">
          <AbandonFlowButton flowId={flow.id} />
          <button
            className="button"
            disabled={resolveQualification.isPending}
            onClick={() => void resolve("RosterRepair")}
          >
            <RefreshIcon /> Retry with repaired roster
          </button>
          {revising ? (
            <button
              className="button primary"
              disabled={resolveQualification.isPending}
              onClick={() => void resolve("ScopeRevision")}
            >
              Create revised-scope flow
            </button>
          ) : (
            <button
              className="button"
              disabled={resolveQualification.isPending}
              onClick={() => setRevising(true)}
            >
              Revise scope
            </button>
          )}
        </div>
      </div>
    </section>
  );
}
