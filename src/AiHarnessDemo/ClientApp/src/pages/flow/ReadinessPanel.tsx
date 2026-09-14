import { useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { api } from "../../api/endpoints";
import { queryKeys } from "../../api/queries";
import { ApiError } from "../../api/client";
import type {
  DeliveryReadinessDto,
  FlowDetailDto,
  ReadinessResolutionAction
} from "../../api/types";
import { useToast } from "../../lib/toast";

const MAX_ACKNOWLEDGEMENT_CHARACTERS = 4_000;
const MAX_TOTAL_CHANGE_CHARACTERS = 16_000;

/** The exact customer-visible readiness state. Only `ReadyToApprove` is rendered green. */
export function readinessTone(readiness: DeliveryReadinessDto): string {
  switch (readiness.state) {
    case "ReadyToApprove":
      return "ready";
    case "NeedsCustomerWaiver":
      return "waiver";
    case "NeedsRefinement":
      return "refinement";
    default:
      return "blocked";
  }
}

export function ReadinessPanel({ flow }: { flow: FlowDetailDto }) {
  const readiness = flow.deliveryReadiness ?? null;
  const queryClient = useQueryClient();
  const toast = useToast();
  const [acknowledgement, setAcknowledgement] = useState("");
  const [requestedChanges, setRequestedChanges] = useState("");
  const [submitting, setSubmitting] = useState(false);

  if (!readiness) return null;

  const tone = readinessTone(readiness);
  const canWaive =
    readiness.allowedActions.includes("GrantWaiver") &&
    Boolean(readiness.waiverGateId) &&
    readiness.requiredWaiverRiskIds.length > 0;
  const canRequestRefinement =
    readiness.allowedActions.includes("RequestRefinement") &&
    readiness.state === "NeedsRefinement";
  const canResolveBlock = readiness.state === "Blocked";

  async function resolve(
    action: ReadinessResolutionAction,
    changes?: string[]
  ) {
    if (!readiness) return;
    setSubmitting(true);
    try {
      const result = await api.resolveReadiness(flow.id, {
        reviewedCandidateId: readiness.reviewedCandidateId,
        readinessRevision: readiness.revision,
        readinessContractHash: readiness.contractHash,
        action,
        refinement: changes
          ? { goal: null, requestedChanges: changes }
          : null
      });
      toast(result.message, "success");
      setRequestedChanges("");
    } catch (error) {
      if (error instanceof ApiError && error.status === 409) {
        toast(
          "The readiness assessment changed. The current server state has been refreshed.",
          "error"
        );
      } else {
        toast(error instanceof Error ? error.message : String(error), "error");
      }
    } finally {
      setSubmitting(false);
      await queryClient.invalidateQueries({ queryKey: queryKeys.flow(flow.id) });
    }
  }

  function submitRefinement(action: ReadinessResolutionAction) {
    const changes = requestedChanges
      .split(/\r?\n/)
      .map(value => value.trim())
      .filter(Boolean);
    if (changes.length === 0) {
      toast("Add at least one requested change.", "error");
      return;
    }
    void resolve(action, changes);
  }

  async function grantWaiver() {
    if (!readiness || !readiness.waiverGateId) return;
    if (!acknowledgement.trim()) {
      toast("Describe your acknowledgement before waiving the disclosed risks.", "error");
      return;
    }
    setSubmitting(true);
    try {
      const result = await api.grantReadinessWaiver(flow.id, {
        gateId: readiness.waiverGateId,
        reviewedCandidateId: readiness.reviewedCandidateId,
        readinessRevision: readiness.revision,
        readinessContractHash: readiness.contractHash,
        riskIds: readiness.requiredWaiverRiskIds,
        acknowledgement: acknowledgement.trim()
      });
      toast(result.message, "success");
      setAcknowledgement("");
    } catch (error) {
      if (error instanceof ApiError && error.status === 409) {
        toast(
          "The readiness assessment changed. The current server state has been refreshed.",
          "error"
        );
      } else {
        toast(error instanceof Error ? error.message : String(error), "error");
      }
    } finally {
      setSubmitting(false);
      await queryClient.invalidateQueries({ queryKey: queryKeys.flow(flow.id) });
    }
  }

  return (
    <section
      className={`card readiness-card readiness-${tone}`}
      aria-labelledby="delivery-readiness-heading"
      data-readiness-state={readiness.state}
    >
      <div className="card-header">
        <div>
          <div className="eyebrow">Delivery readiness</div>
          <h3 id="delivery-readiness-heading">{readiness.label}</h3>
          <p>
            Revision {readiness.revision} · candidate {readiness.candidateFingerprintPrefix}…
          </p>
        </div>
        <span className={`status-pill readiness-${tone}`}>{readiness.label}</span>
      </div>
      <div className="card-body">
        <p className="muted">{readiness.publicationAssurance}</p>

        {readiness.criteria.length > 0 && (
          <section aria-labelledby="readiness-criteria-heading">
            <small id="readiness-criteria-heading">Acceptance criteria</small>
            <ul className="readiness-criteria">
              {readiness.criteria.map(criterion => (
                <li key={criterion.criterionId} data-outcome={criterion.outcome}>
                  <strong>
                    {criterion.criterionId} · {criterion.outcome}
                  </strong>
                  <span>{criterion.requirement}</span>
                  <small>{criterion.rationale}</small>
                  {criterion.remediation && (
                    <small className="readiness-remediation">
                      Remediation: {criterion.remediation}
                    </small>
                  )}
                  {criterion.evidenceIds.length > 0 && (
                    <small>Evidence: {criterion.evidenceIds.join(", ")}</small>
                  )}
                  {criterion.responsibleRoles.length > 0 && (
                    <small>Owners: {criterion.responsibleRoles.join(", ")}</small>
                  )}
                </li>
              ))}
            </ul>
          </section>
        )}

        {readiness.risks.length > 0 && (
          <section aria-labelledby="readiness-risks-heading">
            <small id="readiness-risks-heading">Residual risks</small>
            <ul className="readiness-risks">
              {readiness.risks.map(risk => (
                <li key={risk.riskId} data-classification={risk.classification}>
                  <strong>
                    {risk.riskId} · {risk.classification} · {risk.severity}
                    {risk.waived ? " · waived" : ""}
                  </strong>
                  <span>{risk.statement}</span>
                  <small>Impact: {risk.impact}</small>
                  {risk.criterionIds.length > 0 && (
                    <small>Criteria: {risk.criterionIds.join(", ")}</small>
                  )}
                  {risk.evidenceIds.length > 0 && (
                    <small>Evidence: {risk.evidenceIds.join(", ")}</small>
                  )}
                  <small>
                    Source: {risk.sourceRole || "unknown role"}
                    {risk.preMortemFindingId ? ` · ${risk.preMortemFindingId}` : ""}
                  </small>
                </li>
              ))}
            </ul>
          </section>
        )}

        {readiness.diagnostics.length > 0 && (
          <section aria-labelledby="readiness-diagnostics-heading">
            <small id="readiness-diagnostics-heading">Why this is not ready</small>
            <ul className="readiness-diagnostics">
              {readiness.diagnostics.map(diagnostic => (
                <li key={diagnostic}>{diagnostic}</li>
              ))}
            </ul>
          </section>
        )}

        {canWaive && (
          <div className="readiness-waiver">
            <label className="field" htmlFor="readiness-acknowledgement">
              <span>
                Acknowledge and waive {readiness.requiredWaiverRiskIds.join(", ")}
              </span>
              <textarea
                id="readiness-acknowledgement"
                rows={3}
                maxLength={MAX_ACKNOWLEDGEMENT_CHARACTERS}
                value={acknowledgement}
                onChange={event => setAcknowledgement(event.target.value)}
              />
            </label>
            <div className="feedback-actions">
              <button
                className="button"
                disabled={submitting}
                onClick={() => void grantWaiver()}
              >
                Acknowledge and waive risks
              </button>
            </div>
            <p className="muted">
              Waiving disclosed risks is not acceptance. The ordinary customer review opens
              only after every required waiver is recorded.
            </p>
          </div>
        )}

        {readiness.state === "NeedsRefinement" && (
          <div className="readiness-resolution">
            <p className="muted">
              Acceptance and publication are unavailable. Request refinement so the team can
              close the listed gaps.
            </p>
            <label className="field" htmlFor="readiness-refinement">
              <span>Requested changes (one per line)</span>
              <textarea
                id="readiness-refinement"
                rows={4}
                maxLength={MAX_TOTAL_CHANGE_CHARACTERS}
                value={requestedChanges}
                onChange={event => setRequestedChanges(event.target.value)}
              />
            </label>
            <div className="feedback-actions">
              <button
                className="button"
                disabled={submitting || !canRequestRefinement}
                onClick={() => submitRefinement("RequestRefinement")}
              >
                Request refinement
              </button>
            </div>
          </div>
        )}
        {canResolveBlock && (
          <div className="readiness-resolution">
            <p className="muted">
              This result is blocked. Neither acceptance nor a waiver is available; choose a
              typed resolution.
            </p>
            <label className="field" htmlFor="readiness-replan">
              <span>Requested changes for a replan (one per line)</span>
              <textarea
                id="readiness-replan"
                rows={4}
                maxLength={MAX_TOTAL_CHANGE_CHARACTERS}
                value={requestedChanges}
                onChange={event => setRequestedChanges(event.target.value)}
              />
            </label>
            <div className="feedback-actions">
              <button
                className="button"
                disabled={submitting}
                onClick={() => void resolve("Continue")}
              >
                Continue
              </button>
              <button
                className="button"
                disabled={submitting}
                onClick={() => submitRefinement("Replan")}
              >
                Replan
              </button>
              <button
                className="button danger"
                disabled={submitting}
                onClick={() => void resolve("Abandon")}
              >
                Abandon
              </button>
            </div>
          </div>
        )}
      </div>
    </section>
  );
}
