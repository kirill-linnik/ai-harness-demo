import { useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { Link, useNavigate } from "react-router-dom";
import type { FlowDetailDto, ReviewIntent } from "../../api/types";
import { queryKeys, useReviewFlowMutation } from "../../api/queries";
import { ApiError } from "../../api/client";
import { CheckIcon, ExternalIcon, RefreshIcon } from "../../lib/icons";
import { statusLabel } from "../../lib/format";
import { flowRoute } from "../../lib/flowRoute";
import { useToast } from "../../lib/toast";

const MAX_GOAL_CHARACTERS = 4_000;
const MAX_CHANGE_CHARACTERS = 4_000;
const MAX_CHANGES = 24;
const MAX_TOTAL_CHANGE_CHARACTERS = 16_000;

function publicationProgress(flow: FlowDetailDto): string {
  const publication = flow.steps
    .filter(step =>
      step.iteration === flow.iteration &&
      step.stage === "AfterApproval" &&
      step.duties.includes("Publish"))
    .at(-1);
  if (publication?.status === "Running") return "Running";
  if (publication?.status === "Failed") return "Failed";
  if (publication?.status === "Completed") return "Published";
  return flow.publicationStatus;
}

export function ReviewCard({ flow }: { flow: FlowDetailDto }) {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const toast = useToast();
  const reviewFlow = useReviewFlowMutation();
  const [showRefinement, setShowRefinement] = useState(false);
  const [goal, setGoal] = useState(flow.outcomeResult?.goal ?? "");
  const [requestedChanges, setRequestedChanges] = useState("");

  const outcome = flow.outcomeResult;
  const review = flow.review;
  const readiness = flow.deliveryReadiness ?? null;
  // The server is the only authority. The Accept action exists here only when the persisted
  // readiness assessment is ReadyToApprove and still names this exact review gate.
  const readinessBlocksDecision =
    readiness !== null && readiness.state !== "ReadyToApprove";
  const awaitingDecision =
    flow.status === "WaitingForFeedback" &&
    review.available &&
    !review.resolved &&
    Boolean(review.gateId) &&
    !readinessBlocksDecision;
  const reviewedPreviewAvailable =
    flow.kind === "Delivery" &&
    awaitingDecision &&
    Boolean(flow.reviewedPreviewUrl);
  const accepted = review.resolved && review.decision === "Accepted";
  const promoted = review.resolved && review.decision === "PromotedToDelivery";
  const promotion = flow.linkedFlows.find(
    child =>
      child.parentIteration === flow.iteration &&
      child.linkKind === "AdvisoryPromotion"
  );
  const canPromoteAcceptedAdvisory =
    flow.kind === "Advisory" &&
    flow.status === "Approved" &&
    accepted &&
    Boolean(review.gateId) &&
    !promotion;

  async function submit(intent: ReviewIntent) {
    if (!review.gateId) {
      toast("The current customer review is no longer available.", "error");
      await queryClient.invalidateQueries({ queryKey: queryKeys.flow(flow.id) });
      return;
    }

    const changes = requestedChanges
      .split(/\r?\n/)
      .map(value => value.trim())
      .filter(Boolean);
    if (intent === "RequestRefinement") {
      if (changes.length === 0) {
        toast("Add at least one requested change.", "error");
        return;
      }
      if (
        changes.length > MAX_CHANGES ||
        changes.some(value => value.length > MAX_CHANGE_CHARACTERS) ||
        changes.reduce((total, value) => total + value.length, 0) >
          MAX_TOTAL_CHANGE_CHARACTERS
      ) {
        toast(
          `Use at most ${MAX_CHANGES} changes, ${MAX_CHANGE_CHARACTERS} characters each and ${MAX_TOTAL_CHANGE_CHARACTERS} total.`,
          "error"
        );
        return;
      }
      if (goal.trim().length > MAX_GOAL_CHARACTERS) {
        toast(`The refined goal must be at most ${MAX_GOAL_CHARACTERS} characters.`, "error");
        return;
      }
    }

    try {
      const result = await reviewFlow.mutateAsync({
        flowId: flow.id,
        body: {
          gateId: review.gateId,
          intent,
          refinement:
            intent === "RequestRefinement"
              ? {
                  goal: goal.trim() || null,
                  requestedChanges: changes
                }
              : null,
          reviewedCandidateId: readiness?.reviewedCandidateId ?? null,
          readinessRevision: readiness?.revision ?? null,
          readinessContractHash: readiness?.contractHash ?? null
        }
      });
      toast(result.message, "success");
      if (result.linkedFlowId) {
        navigate(flowRoute(result.linkedFlowId));
      }
    } catch (error) {
      if (error instanceof ApiError && error.status === 409) {
        await queryClient.invalidateQueries({ queryKey: queryKeys.flow(flow.id) });
        await queryClient.invalidateQueries({
          queryKey: queryKeys.reviewResult(flow.id)
        });
        toast("The review changed. The current server state has been refreshed.", "error");
        return;
      }
      toast(error instanceof Error ? error.message : String(error), "error");
    }
  }

  return (
    <section className="card feedback-card review-card" aria-labelledby="customer-review-heading">
      <div className="card-header">
        <div>
          <div className="eyebrow">{flow.kind} result</div>
          <h3 id="customer-review-heading">Customer review</h3>
          <p>
            This result and every available action come from the persisted flow state.
          </p>
        </div>
        {review.available && (
          <div className="flow-heading-actions">
            {reviewedPreviewAvailable && (
              <a
                className="button primary small"
                href={flow.reviewedPreviewUrl!}
              >
                <ExternalIcon /> Open reviewed preview
              </a>
            )}
            <span className={`status-pill ${review.resolved ? "approved" : "waitingforfeedback"}`}>
              {review.resolved
                ? statusLabel(review.decision ?? "Resolved")
                : "Decision required"}
            </span>
          </div>
        )}
      </div>
      <div className="card-body">
        {readinessBlocksDecision && (
          <div className="callout" role="status">
            <strong>{readiness!.label}</strong>
            <span>
              Acceptance is unavailable because host-derived Delivery readiness is
              {" "}
              {readiness!.state}.
            </span>
          </div>
        )}
        {outcome ? (
          <div className="review-outcome">
            <section>
              <small>Goal</small>
              <h4>{outcome.goal}</h4>
            </section>
            <section>
              <small>Summary</small>
              <p>{outcome.summary}</p>
            </section>
            <section>
              <small>Implementation details</small>
              <ul>
                {outcome.implementationDetails.map((detail, index) => (
                  <li key={`${index}:${detail}`}>{detail}</li>
                ))}
              </ul>
            </section>
            {outcome.artifacts.length > 0 && (
              <section aria-labelledby="review-artifacts-heading">
                <small id="review-artifacts-heading">
                  {flow.kind === "Advisory" ? "Advisory artifacts" : "Result artifacts"}
                </small>
                <div className="artifact-list">
                  {outcome.artifacts.map(artifact => (
                    <div className="artifact-row" key={artifact.id}>
                      <span>
                        <strong>{artifact.path}</strong>
                        <small>
                          {artifact.mediaType} · {artifact.byteLength.toLocaleString()} bytes
                        </small>
                      </span>
                      <span className="flow-heading-actions">
                        <a
                          className="button small"
                          href={artifact.url}
                          target="_blank"
                          rel="noopener noreferrer"
                        >
                          <ExternalIcon /> View
                        </a>
                        <a className="button small" href={artifact.downloadUrl}>
                          Download
                        </a>
                      </span>
                    </div>
                  ))}
                </div>
              </section>
            )}
          </div>
        ) : (
          <p className="muted">The normalized result is being prepared.</p>
        )}

        {accepted && flow.kind === "Delivery" && (
          <div className="callout publication-progress" role="status">
            <strong>Publication {statusLabel(publicationProgress(flow)).toLowerCase()}</strong>
            <span>
              Publication began only after the durable customer acceptance decision.
            </span>
          </div>
        )}
        {accepted && flow.kind === "Advisory" && (
          <div className="callout" role="status">
            <strong>Advisory accepted</strong>
            <span>No source changes or publication were scheduled.</span>
          </div>
        )}
        {promoted && (
          <div className="callout" role="status">
            <strong>Promoted to Delivery</strong>
            <span>The linked Delivery flow has its own workspace and current agent snapshot.</span>
            {promotion && (
              <Link
                className="button small"
                to={flowRoute(promotion.id, promotion.status)}
              >
                Open linked Delivery
              </Link>
            )}
          </div>
        )}

        {awaitingDecision && showRefinement && (
          <div className="review-refinement">
            <label className="field" htmlFor="review-goal">
              <span>Refined goal (optional)</span>
              <textarea
                id="review-goal"
                rows={2}
                maxLength={MAX_GOAL_CHARACTERS}
                value={goal}
                onChange={event => setGoal(event.target.value)}
              />
            </label>
            <label className="field" htmlFor="review-changes">
              <span>Requested changes (one per line)</span>
              <textarea
                id="review-changes"
                rows={4}
                maxLength={MAX_TOTAL_CHANGE_CHARACTERS}
                value={requestedChanges}
                onChange={event => setRequestedChanges(event.target.value)}
              />
            </label>
          </div>
        )}

        {awaitingDecision && (
          <div className="feedback-actions">
            <button
              className="button success"
              disabled={reviewFlow.isPending}
              onClick={() => void submit("Accept")}
            >
              <CheckIcon /> Accept
            </button>
            {showRefinement ? (
              <button
                className="button"
                disabled={reviewFlow.isPending}
                onClick={() => void submit("RequestRefinement")}
              >
                <RefreshIcon /> Submit refinement
              </button>
            ) : (
              <button
                className="button"
                disabled={reviewFlow.isPending}
                onClick={() => setShowRefinement(true)}
              >
                <RefreshIcon /> Request refinement
              </button>
            )}
            {flow.kind === "Advisory" && (
              <button
                className="button primary"
                disabled={reviewFlow.isPending}
                onClick={() => void submit("PromoteToDelivery")}
              >
                Promote to Delivery
              </button>
            )}
          </div>
        )}
        {canPromoteAcceptedAdvisory && (
          <div className="feedback-actions">
            <button
              className="button primary"
              disabled={reviewFlow.isPending}
              onClick={() => void submit("PromoteToDelivery")}
            >
              Promote to Delivery
            </button>
          </div>
        )}
      </div>
    </section>
  );
}
