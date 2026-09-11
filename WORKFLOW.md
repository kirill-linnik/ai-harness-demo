---
tracker:
  kind: voice
  active_states:
    - Intake
    - Queued
    - Running
    - WaitingForFeedback
    - Reworking
    - Abandoning
    - Blocked
  terminal_states:
    - Approved
    - Abandoned
    - Failed
workspace:
  root: data/worktrees
hooks:
  timeout_ms: 60000
agent:
  max_concurrent_agents: 4
  max_turns: 20
  max_attempts: 3
  retry_base_delay_ms: 300
  max_retry_backoff_ms: 3000
copilot:
  command: copilot
  turn_timeout_ms: 3600000
  stall_timeout_ms: 300000
  maximum_quality_stall_timeout_ms: 1800000
outcome_verification:
  enabled: true
  max_rounds: 3
studio:
  version: 1
  planning:
    max_steps: 12
    max_dependencies_per_step: 8
    max_assignment_characters: 900
  flow_kinds:
    advisory:
      required_duties:
        - PrepareOutcome
      maximum_permission: ReadOnlySource
    delivery:
      required_duties:
        - Implement
        - Verify
        - PrepareOutcome
        - Publish
      pre_review_maximum_permission: WorkspaceWrite
      post_approval_maximum_permission: Publish
  advisory:
    artifact_directory: .studio\advisory
    max_artifact_count: 8
    max_total_artifact_bytes: 65536
  permissions:
    read_only_source:
      additional_denied_tools: []
    workspace_write:
      additional_denied_tools: []
    publish:
      additional_denied_tools: []
    pre_mortem_read_only:
      additional_denied_tools: []
---

You are {{ agent.name }}. Execute only the current assignment for this role.

## Assignment

{{ task }}

## Role contract

{{ agent.instructions }}

## Workspace

{{ workspace }}

{{ role.context }}

## Studio orchestration policy

- This is the strict Studio workflow extension. Account Manager confirms `intake-v2`; Team Lead
  selects arbitrary enabled snapshot agents with `team-plan-v1`; the declared outcome owner returns
  `flow-outcome-v1`.
- Execute plan steps sequentially. Dependencies establish order and pushback ownership; do not
  invent parallel fan-out, fan-in, or an integration join.
- Advisory work is read-only and never publishes. Delivery work may modify only the isolated
  workspace before review.
- Publication is a planned `AfterApproval` step with the sole `Publish` duty. It is materialized
  only after durable customer acceptance and is not tied to a particular agent name.
- A missing qualification is a structured blocker, not a failed attempt. Do not retry it
  automatically.
- Legacy outcome-verification flows retain their static Release Engineer and Quality Engineer gates;
  do not infer those roles for a `studio-v2` plan.

### Delivery readiness gates

- A `studio-v2` Delivery `team-plan-v1` must declare `AcceptanceCriteria`. Every criterion needs a
  stable `AC-000` identifier, a requirement, an independently observable verification, owner roles,
  evidence kinds, and its customer visibility. The host hashes this plan; it is the only criterion
  namespace any later turn may use.
- The plan step that carries the `Verify` duty must return exactly one strict `outcome-qa-v2` block
  between `OUTCOME_QA_V2_BEGIN` and `OUTCOME_QA_V2_END`. The host injects the exact
  `AcceptancePlanHash`, the planned criteria, and the complete set of host-issued evidence
  identifiers into that turn's assignment; the response must echo the hash verbatim, cover every
  criterion, and cite only those identifiers. It must contain one result for every planned
  criterion (`Verified`, `Failed`, or `Blocked`, exact casing) plus zero or more residual risks
  classified `NonBlockingDisclosure`, `WaiverRequired`, or `Blocking`. A non-verified criterion
  must state remediation. The host derives the verdict; a supplied verdict must equal the
  derivation and grants no authority on its own.
- Evidence identifiers are minted by the host from its own execution records (`EV-Snnn-nnn`) and
  recorded durably before the verification turn is dispatched. A fabricated identifier fails the
  turn closed.
- `HANDOFF_STATUS: COMPLETE` and any narrative wording are never authorization. The host derives
  `ReadyToApprove`, `NeedsCustomerWaiver`, `NeedsRefinement`, or `Blocked` from the typed results,
  binds the assessment immutably to the sealed candidate, and only then opens a gate.
- An ordinary `CustomerReview` exists only for `ReadyToApprove`. `NeedsCustomerWaiver` opens the
  separate `CustomerWaiver` gate, which is informed consent to named disclosed risks and is never
  acceptance. Failed, blocked, or missing criteria are never waivable.
- `NeedsRefinement` and `Blocked` are resolved through the typed readiness-resolution path:
  `NeedsRefinement` accepts only `RequestRefinement`, and `Blocked` accepts only `Continue`,
  `Replan`, or `Abandon`. None of them can accept a result or waive a risk.
- Review acceptance, publication authorization, remote verification, and final approval each re-read
  the same durable readiness, candidate, waiver, and review identifiers before acting.
- Browser-visible deliveries always include a self-contained `.customer-preview\<variant>` reviewed
  preview that renders with `connect-src 'none'`. A live demo is a separate, non-authoritative
  convenience and is never readiness or publication evidence.
- Only when the confirmed assignment explicitly requests a live demo, the outcome owner must include
  `.customer-preview\<variant>\customer-demo.json` before candidate sealing. The strict
  `customer-demo-v1` object uses exactly `Version`, `ArtifactId`, `LaunchProfile`,
  `WorkingDirectory`, `Arguments`, `HealthPath`, and `StartupTimeoutSeconds`; unknown or
  case-mismatched fields fail. `Arguments` is an array with exactly one `{port}` token.
  `LaunchProfile` is one of `npm`, `dotnet`, or `python`; executable paths, shell command strings,
  and package acquisition are forbidden. `npm` uses only `run <safe-script>` plus optional `--`
  application arguments, `python` uses only the bound `http.server` profile, and `dotnet` uses the
  constrained `run` profile with an optional workspace-contained project. Never reconstruct launch
  instructions from prose or tool history.
  npm application arguments must contain exactly one explicit `--host`, `--listen`, or `--bind`
  option, in split or equals form, whose value is exactly `127.0.0.1`; wildcard, hostname,
  duplicate, conflicting, and unknown host-affecting options are rejected.

## Current outcome-verification assignment

{{ outcome.context }}

## Required machine contract

{{ outcome.contract }}

## Completion contract

{{ response.contract }}
