# Architecture

## Audience and first successful path

This document is for developers and operators who run, extend, or troubleshoot AI Harness Studio.
The shortest successful path is:

1. Start the ASP.NET Core host.
2. In **Settings**, choose a **Source project** and select **Initialize and study repository**.
3. From **Factory**, submit the customer outcome you want.
4. Answer the Account Manager's questions and explicitly confirm the brief and flow kind.
5. Follow the persisted Team Lead plan and agent attempts on the flow page.
6. Review the resulting Advisory, or inspect a Delivery's **Delivery readiness** and
   **Open reviewed preview** before selecting **Accept**.

An accepted Advisory stops without publication and can later be promoted to a fresh Delivery. An
accepted Delivery continues to its planned post-approval Publish step and reaches `Approved` only
after the host verifies the published result.

## System boundary

AI Harness Studio is a standalone .NET 10 application inspired by selected OpenAI Symphony
orchestration ideas. It is not a Symphony implementation or drop-in replacement.

```text
React + Vite dashboard
        |
ASP.NET Core API and background worker
        |
WorkflowEngine + guarded lifecycle coordinators
        |
Copilot CLI agent turns in per-flow workspaces
        |
SQLite orchestration ledger
```

The host is authoritative. Agent output proposes plans, handoffs, QA facts, and customer-facing
outcomes; host code validates those documents, derives permissions and readiness, changes lifecycle
state, materializes artifacts, and performs publication.

Independent flows can use separate worker slots. Steps within one flow execute sequentially.

## Configuration sources

| Source | Purpose |
| --- | --- |
| `WORKFLOW.md` | Typed runtime limits, required duties, permission ceilings, artifact limits, and prompt templates. |
| `.github\agents\*.agent.md` | Agent identity, description, role, and instructions. |
| Studio settings | Source project, repository knowledge, Delivery artifact type, routing strategy, and correction limits. |
| `data\ai-harness.db` | Durable flow, plan, execution, review, readiness, and publication state. |
| `data\worktrees` | Isolated per-flow workspaces. |

`WorkflowDefinitionProvider` reloads `WORKFLOW.md` atomically. A valid definition becomes the
effective definition; an invalid current file blocks new work without partially applying its
contents. A started attempt keeps its persisted workflow revision and effective permission
document. A retry can preserve or tighten that permission, but cannot silently gain authority.

`AgentCatalogLoader` reads the agent files and `AgentCatalog` publishes a valid catalog atomically.
Use **Reload catalog** after changing an agent definition. Invalid optional definitions remain
visible for diagnosis but cannot be selected. An invalid required definition blocks new work.

## One dynamic, duty-based flow

All work uses the same dynamic planner and executor. Agent names identify who performs a step;
duties and host-owned lifecycle state determine what the step may do.

### Intake and immutable roster

Creating a flow captures the effective agent definitions and their enabled state in
`FlowAgentSnapshot` rows. Instructions, definition hashes, enabled flags, and policy flags are then
fixed for that flow. Catalog reloads and toggles affect only flows created afterward.

The Account Manager uses its captured definition to clarify the request and produce a strict
`intake` brief. The customer must explicitly confirm that brief and choose Advisory or Delivery
before execution can be queued.

The catalog has three special definitions:

| Agent | Rule |
| --- | --- |
| Account Manager | Required and always enabled. Owns intake, refinement normalization, and customer-safe blocker explanations. |
| Team Lead | Required and always enabled. Creates the downstream plan. |
| Pre-mortem Sceptic | Required definition but switchable. Runs only at selected pre-review checkpoints. |

Every other definition is optional and switchable. In particular, **Quality Engineer is optional**.
The Team Lead may assign `Verify` to any enabled suitable agent in the immutable roster. Selecting
an independent Quality Engineer is the stronger assurance choice because it separates
implementation from verification, but the runtime does not require that identity.

### Team Lead plan

After confirmation, the Team Lead receives the brief and the exact enabled optional roster from the
flow snapshot. It returns either a strict `team plan` or `MissingQualification`.

A valid plan contains ordered steps with:

- an exact snapshotted `AgentId`;
- `BeforeReview` or `AfterApproval` stage;
- one or more duties: `Analyze`, `Design`, `Implement`, `Verify`, `PrepareOutcome`, or `Publish`;
- dependencies that name earlier plan steps;
- an assignment, selection rationale, and task profile;
- exactly one final `BeforeReview` outcome owner; and
- Delivery acceptance criteria when the flow kind is Delivery.

The plan validator rejects disabled or unknown agent IDs, core agents selected as workers, duplicate
or cyclic dependencies, dependencies on later steps, invalid stages or duties, and missing required
duties. The accepted plan is immutable for its flow iteration.

Required duties are independent of agent names:

| Flow kind | Required plan shape |
| --- | --- |
| Advisory | At least one `BeforeReview` worker and an outcome owner with `PrepareOutcome`; no `Implement`, `Publish`, or `AfterApproval` step. |
| Delivery | `Implement`, `Verify`, `PrepareOutcome`, and `Publish`; exactly one `AfterApproval` step whose sole duty is `Publish`. |

For Delivery, the final `BeforeReview` outcome owner must be the only step with `Verify` and must
also carry `PrepareOutcome`. Every candidate-changing step, including preview preparation, must be
an earlier dependency. The combined owner verifies the complete candidate and returns both the
strict QA document and the customer-facing `flow outcome`.

### Sequential dependency execution

`WorkflowEngine` materializes only pre-review workers before customer review, orders them
deterministically, and executes one pending attempt at a time. A dependency must point backward in
the accepted plan. Before a worker runs, the host resolves the latest successful attempt for each
declared dependency and provides bounded dependency results in the prompt. There is no parallel
fan-out or integration join inside a flow.

Optional pre-mortem checkpoints are inserted after their target steps. They run read-only and their
findings are advisory input; they do not approve or reject a result.

If the enabled roster cannot cover the required work, the Team Lead returns
`MissingQualification`. The Account Manager translates it once into a customer-safe blocker and
the flow remains `Blocked`. Roster repair or scope revision creates a linked flow with a new
snapshot and workspace rather than altering the blocked plan.

## Advisory flow

Advisory work uses a guarded source snapshot and the `ReadOnlySource` permission profile. Agent
turns cannot modify the source, run a shell, or publish.

The outcome owner returns the customer-facing goal, summary, implementation details, and any
declared artifacts. Artifacts are data in that response, not files written by the agent. Before
opening customer review, the host:

1. verifies the guarded source snapshot against its recorded baseline;
2. validates artifact paths, count, total UTF-8 bytes, and media types;
3. writes supported text, Markdown, CSV, and JSON artifacts under the configured
   `.studio\advisory` area using an atomic materialization; and
4. records artifact lengths and digests in the flow ledger.

A customer can **Accept**, **Request refinement**, or **Promote to Delivery**. Refinement starts a
new iteration in the same snapshot and workspace. Promotion creates one linked Delivery with a
fresh snapshot, fresh workspace, and normal Account Manager intake.

## Delivery flow

Delivery work uses an isolated Git worktree. Pre-review duties that need to change or validate the
candidate run under at most `WorkspaceWrite`; remote publication remains unavailable.

### Readiness and reviewed candidate

Delivery readiness is derived by the host from the current iteration:

1. The Team Lead declares ordered acceptance criteria. The host records and hashes that plan.
2. The host records evidence identifiers from completed steps and observed tool calls. A verified
   criterion must cite a successful observation whose kind is allowed by that criterion; a generic
   step-completion record is context only.
3. The final outcome owner returns one result for every criterion and classifies residual risks.
   It may cite only host-issued evidence identifiers and must report any confirmed requirement that
   the acceptance plan omitted as a plan gap.
4. The host validates the QA document and derives the verdict; prose and
   `HANDOFF_STATUS: COMPLETE` do not authorize release. Any plan gap prevents a passing verdict.
5. The host seals the exact candidate, including repository commit and tree identities and any
   reviewed preview files.
6. The host derives readiness and binds it to that sealed candidate.

The customer-visible states are:

| State | Result |
| --- | --- |
| `ReadyToApprove` | Opens ordinary customer review. The customer may accept or request refinement. |
| `NeedsCustomerWaiver` | Opens a separate informed-consent step for specifically disclosed risks. Recording all required waivers re-derives readiness before ordinary review opens. |
| `NeedsRefinement` | Acceptance is unavailable; the customer can request another iteration. |
| `Blocked` | Acceptance is unavailable; the customer can continue, replan, or abandon as permitted by the host. |

A failed, blocked, missing, or malformed criterion cannot be waived. Review, waiver, publication,
and final approval re-read the current readiness and candidate binding. A stale browser request or
changed candidate fails closed.

For browser-visible work, `.customer-preview\<variant>` is part of the sealed candidate and is
served only after length and digest checks. Its reviewed representation is network-disabled. A
separately requested loopback live demo is convenience only and contributes no readiness evidence
or publication authority.

### Customer review and publication

Only a Delivery in `ReadyToApprove` can open ordinary customer review. **Request refinement**
supersedes the current readiness and candidate binding and starts a new iteration in the same
workspace and snapshot.

**Accept** records durable authorization for the exact reviewed candidate. The host then
materializes the planned `AfterApproval` Publish-only step. The agent can inspect packaging under a
governed read-and-shell policy, but cannot edit the candidate, receive publication credentials, or
perform the remote write. The host publishes the sealed commit and tree identities, records commit
or pull-request results, verifies the remote result against the same binding, and only then marks
the flow `Approved`.

A publication failure is visible and does not create approval. The durable authorization and
publication identity remain available for an explicit restart.

Publication recaps do not overwrite the editable Repository Knowledge baseline. The published
commit or pull request remains isolated from the configured source checkout until the user
integrates it; after integration, **Initialize and study repository** re-derives knowledge from the
actual source bytes so later flows never receive claims from an unmerged branch.

## Pushback and recovery

Worker output must contain exactly one `HANDOFF_STATUS: COMPLETE` or
`HANDOFF_STATUS: PUSHBACK`. Pushback also names an earlier dependency or ancestor plan-step ID and
a bounded reason. The host rejects an unrelated or unfinished owner, records the attribution, and,
within the configured correction limit, schedules:

1. a revision attempt for the named upstream owner; then
2. a retry of the blocked step with that revision result.

Retries preserve or tighten the original permission document. Exhausting the correction limit
fails the flow instead of bypassing the handoff.

The SQLite ledger and `CopilotSessionJournal` support restart reconciliation. Completed journal
results are applied once; interrupted resumable attempts retain their flow, workspace, semantic
step, Copilot session identity, workflow revision, and permission ceiling. Open customer reviews
remain waiting, accepted Delivery publication can be rematerialized from durable authorization,
and missing-qualification blockers are not retried automatically.

The flow page's **Recover stalled execution** action is limited to queued, running, or reworking
work. It stops only the tracked worker, reconciles persisted state, and queues the flow once.
Failed work requires the explicit **Recover failed task** action. Queue and execution claims prevent
two workers from running the same flow concurrently.

## Permission ceilings

`PermissionProfileResolver` derives the requested profile from the host-owned invocation kind, flow
kind, plan stage, duties, and durable review state, then intersects it with the limits in
`WORKFLOW.md`.

| Profile | Use | Ceiling |
| --- | --- | --- |
| `ReadOnlySource` | Intake, planning, Advisory workers, blocker explanation | Read/search only; no write, shell, remote publication, or publication credentials. |
| `WorkspaceWrite` | Delivery implementation, verification, and outcome preparation | Writes and shell are restricted to the isolated workspace; remote publication remains denied. |
| `PreMortemReadOnly` | Optional pre-mortem checkpoint | Read/search only, with no write, shell, or publication. |
| `Publish` | Accepted Delivery's sole planned post-approval step | Host-controlled inspection only for the agent; source mutation and credentials stay denied while the host publishes the sealed objects. |

Agent frontmatter, instructions, display names, and plan-selected identities cannot raise these
ceilings. The effective permission document is persisted before execution and checked again when an
attempt resumes.

These are trusted-host controls, not an operating-system sandbox against a hostile process running
as the same user.

## Durable state and startup

SQLite runs in write-ahead logging mode. The durable model includes:

- `Flows`, `FlowSteps`, `FlowMessages`, and append-only `FlowEvents`;
- `FlowAgentSnapshots` and `FlowPlanDocuments`;
- customer gates, Delivery readiness, reviewed candidates, waivers, and publication records;
- observed tool calls, task profiles, model-routing decisions, and learnings; and
- live-demo process records.

`FlowLifecycleCoordinator` serializes mutations per flow and guards status transitions. API and UI
projections render persisted state; they do not independently authorize lifecycle changes.

Startup calls `Database.EnsureCreatedAsync()`. It creates the current schema only when the database
does not exist and does not update an existing schema. During development, stop Studio and recreate
the database after entity-shape changes:

```powershell
Remove-Item .\data\ai-harness.db, .\data\ai-harness.db-wal, .\data\ai-harness.db-shm `
  -Force -ErrorAction SilentlyContinue
.\Start-Demo.ps1
```

This deletes local settings, flow history, reviews, and readiness records. Treat the database as
development state rather than as an upgradeable production store.

## Runtime components

| Component | Responsibility |
| --- | --- |
| `IntakeCoordinator` | Account Manager turns, strict intake parsing, and explicit confirmation. |
| `FlowAgentSnapshotService` | Immutable per-flow agent definitions and enabled state. |
| `TeamPlanValidator` | Roster, dependency, duty, stage, outcome-owner, and acceptance-criteria validation. |
| `WorkflowEngine` | Plan materialization, sequential execution, pushback, finalization, and reconciliation. |
| `FlowQueue` / `FlowWorker` | Concurrent-flow scheduling, execution claims, and manual recovery. |
| `WorkspaceManager` | Guarded Advisory snapshots and isolated Delivery worktrees. |
| `AdvisoryArtifactCatalog` | Source-baseline verification and host-written Advisory artifacts. |
| `DeliveryReadinessService` | Host evidence, QA validation, readiness derivation, waivers, and candidate binding. |
| `ReviewedCandidateService` | Candidate sealing and identity revalidation. |
| `ReviewCoordinator` | Customer decisions, refinements, promotion, and post-acceptance publication materialization. |
| `VerifiedCandidatePublisher` / `PublishedOutcomeVerifier` | Exact-object publication and post-publication verification. |
| `PermissionProfileResolver` | Host-derived permission profiles and workflow ceilings. |
| `FlowLifecycleCoordinator` | Per-flow mutation lock and guarded status transitions. |

## Local validation

Run from the repository root on Windows:

```powershell
dotnet restore .\AiHarnessDemo.slnx
dotnet build .\AiHarnessDemo.slnx --no-restore
dotnet test .\AiHarnessDemo.slnx --no-build

Push-Location .\src\AiHarnessDemo\ClientApp
if (-not (Test-Path .\node_modules)) { npm ci }
npm run typecheck
npm test
npm run build
Pop-Location
```
