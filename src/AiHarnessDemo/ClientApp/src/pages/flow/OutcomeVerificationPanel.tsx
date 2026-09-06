import type { OutcomeVerificationDto } from "../../api/types";

export function OutcomeVerificationPanel({
  outcome
}: {
  outcome: OutcomeVerificationDto;
}) {
  if (outcome.legacyUnverified) {
    return (
      <section className="card">
        <div className="card-header">
          <div>
            <h3>Outcome verification</h3>
            <p>Legacy flow</p>
          </div>
          <span className="status-pill pending">Unverified</span>
        </div>
        <div className="card-body">
          <div className="callout">
            This flow predates the outcome-verification contract. Its historical release gate remains available,
            but no acceptance plan or independent criterion result was inferred from prose.
          </div>
        </div>
      </section>
    );
  }

  const resultByCriterion = new Map(
    outcome.latestResults.map(result => [result.criterionId, result])
  );
  const counts = outcome.latestResults.reduce(
    (result, item) => {
      result[item.status] += 1;
      return result;
    },
    { PASS: 0, FAIL: 0, BLOCKED: 0 }
  );

  return (
    <section className="card outcome-verification" aria-labelledby="outcome-verification-heading">
      <div className="card-header">
        <div>
          <h3 id="outcome-verification-heading">Outcome verification</h3>
          <p>
            Round {outcome.currentRound} of {outcome.maxRounds} · plan{" "}
            <span className="mono">{outcome.planHashPrefix || "pending"}</span>
          </p>
        </div>
        <span
          className={`status-pill ${
            outcome.releaseReady ? "approved" : outcome.stale ? "failed" : "running"
          }`}
        >
          {outcome.releaseReady ? "Outcome verified" : outcome.stale ? "Candidate stale" : outcome.status}
        </span>
      </div>
      <div className="card-body">
        {outcome.candidateFingerprintPrefix && (
          <div className="runtime-line">
            <small>Candidate fingerprint</small>
            <strong className="mono">{outcome.candidateFingerprintPrefix}</strong>
          </div>
        )}
        {outcome.verifiedAt && (
          <div className="runtime-line">
            <small>Verified at</small>
            <strong>{new Date(outcome.verifiedAt).toLocaleString()}</strong>
          </div>
        )}
        {outcome.latestResults.length > 0 && (
          <div className="flow-meta" aria-label="Latest QA result counts">
            <span>{counts.PASS} PASS</span>
            <span>{counts.FAIL} FAIL</span>
            <span>{counts.BLOCKED} BLOCKED</span>
          </div>
        )}
        <div className="outcome-criteria">
          {outcome.criteria.map(criterion => {
            const result = resultByCriterion.get(criterion.id);
            const evidence = outcome.evidence.filter(item => item.criterionId === criterion.id);
            return (
              <article className="outcome-criterion" key={criterion.id}>
                <div>
                  <strong className="mono">{criterion.id}</strong>{" "}
                  <span>{criterion.requirement}</span>
                </div>
                <div className="flow-meta">
                  <span>{result?.status ?? "Awaiting QA"}</span>
                  <span>{criterion.ownerRoles.join(", ")}</span>
                  <span>{evidence.length} evidence item(s)</span>
                </div>
                <small>{result?.rationale ?? criterion.verification}</small>
                {evidence.length > 0 && (
                  <ul className="outcome-evidence-list">
                    {evidence.map(item => (
                      <li key={item.evidenceId}>
                        <strong>{item.kind}</strong> · {item.locator}: {item.observedResult}
                      </li>
                    ))}
                  </ul>
                )}
                {result?.remediation && <div className="pushback-callout">{result.remediation}</div>}
              </article>
            );
          })}
        </div>
        {outcome.pendingOwnerRoles.length > 0 && (
          <div className="callout" style={{ marginTop: 12 }}>
            Pending corrections: {outcome.pendingOwnerRoles.join(", ")}
          </div>
        )}
      </div>
    </section>
  );
}
