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

The confirmed brief remains structured JSON in durable state. Planning, worker, and pre-mortem
prompts present it as Markdown headings and lists for the goal, details, success criteria,
constraints, and assumptions. The host persists that rendered prompt before sending it to Copilot
CLI, so the prompt viewer shows the same content the agent receives. Previously captured prompts
remain unchanged, including when an interrupted attempt resumes.

Intake messages may also upload bounded files. Their bytes and hashes are stored in
`FlowAttachments` under the originating customer message, never as paths into the user's home
directory or as response payloads. Each agent gets a read-only, session-owned copy and a small
attachment index in its prompt. The original customer submission and later customer inputs are
also staged as a complete text context document for planning and workers, so a concise confirmed
brief cannot erase supplied details. Agent definitions stay project-agnostic; each role selects
only the facts relevant to its assignment rather than copying the entire submission into
handoffs or output. The snapshot stores exact document bytes and attachment references; recovery
re-stages uploads from SQLite, verifies their hashes, and never trusts a modified staged file. An
explicitly named outside-project file that was not uploaded keeps intake open until the customer
supplies it. The raw submission is evidence for the requested outcome, not blanket authorization
to publish incidental sensitive fields.

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
For browser-visible data changes, planning and verification must cover the actual product
renderer whenever new fields are needed. A self-contained reviewed preview is required but
cannot prove production pages display a newly stored field.

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
declared dependency and supplies its complete deliverable, including design tokens or specification
content delivered inline. Fresh host working prompts are capped at 32 KiB of UTF-8, leaving room for
reasoning, tool results, and the CLI's own context. Sections above 4 KiB and large combined handoffs
become host-owned, searchable context documents rather than excerpts. Agents read required handoffs
in bounded sections and query the full evidence registry by identifier or command.

The prompt and its complete context-document snapshot are saved atomically through `HarnessDbContext`.
Document hashes, paths, attempt identity, and workflow revision remain bound to that attempt.
Recovery recreates missing staged files from the database and rejects changed files or snapshots;
files are never a success fallback. The document set has a separate 16 MiB storage bound. This is a
working-set policy, not a claim to know an unreported model context-window size. There is no parallel
fan-out or integration join inside a flow.

Optional pre-mortem checkpoints follow a requirements-authoring `Analyze` step before any
Delivery `Design` or `Implement` worker receives that handoff; Advisory checkpoints can challenge
a recommendation before customer feedback. The sceptic forecasts failure as though the proposed
requirements had been implemented, then the requirements author revises or rejects findings
before downstream work. Sceptic turns are read-only and never carry `Verify` or judge a finished
candidate. If available, the sceptic is required for Medium-or-higher-risk downstream design or
implementation and for new Delivery using uploaded customer files; genuinely routine Low-risk
work without uploads can omit it. The Delivery outcome owner
alone performs QA after implementation. Previously persisted checkpoint placements remain valid
on recovery so an active flow cannot be silently replanned under a newer policy.
The six-month failure is a counterfactual premise, not historical evidence: a finding must show
a requirements gap that still matters under faithful implementation, or return CLEAR. The
current `PreMortemReadOnly` CLI profile still permits read-only workspace tools; the
requirements-only scope is a role and assignment contract, not a claim of code-blind filesystem
isolation.

If the enabled roster cannot cover the required work, the Team Lead returns
`MissingQualification`. The Account Manager translates it once into a customer-safe blocker and
the flow remains `Blocked`. Roster repair or scope revision creates a linked flow with a new
snapshot and workspace rather than altering the blocked plan.

## Advisory flow

Workspace preparation handles a selected Git root, several nested repositories, a selected
subfolder of a parent Git repository, or a folder without source control. For each available
`origin`, it fetches the remote default `main` or `master` (or `main`, then `master` when the
remote default is different); without an `origin`, it uses local `main` or `master`. New guarded
snapshots read the selected project files from the fetched commit through temporary worktrees,
which are removed before journaling. Source checkout files are not pulled or merged. Projects
without source control snapshot their present files without attempting a network update. A
recovered guarded snapshot remains immutable at its creation-time baseline.

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

Delivery work starts an isolated Git worktree at the fetched base commit. For a selected folder
inside a repository, the worktree retains its parent's history, while agent instructions and
candidate sealing restrict edits to the selected folder. The selected-folder baseline commit is
recorded in the flow ledger and preserved when a reconciled flow resumes. Without source
control, Studio copies the source into an isolated workspace, initializes Git and commits a
baseline there only, and selects a local commit outcome instead of a pull request. On recovery,
each worktree must retain its owned flow branch and the configured source repository's exact
Git common directory. Upstream advancement does not invalidate in-flight work: recovery does
not fetch, merge, or rebase it. Selected-folder ancestry is checked against the pinned ledger
commit rather than a moving remote ref. Review and publication still bind the exact candidate. Pre-review
duties that need to change or validate the candidate run under at most `WorkspaceWrite`; remote
publication remains unavailable.

### Readiness and reviewed candidate

Delivery readiness is derived by the host from the current iteration:

1. The Team Lead declares ordered acceptance criteria. The host records and hashes that plan.
2. The host records evidence identifiers from completed steps and observed tool calls. A verified
   criterion must cite a successful observation whose kind is allowed by that criterion; a generic
   step-completion record is context only. Issued identifiers retain their original observation
   order and prefix across retries, sequence changes, and restarts; random database row IDs never
   reorder an existing evidence registry. Verification context carries the complete kind/success
   index plus bounded recent observation details, with the complete evidence rows in a searchable
   `evidence.jsonl` context document rather than repeated inline transcripts.
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
| `NeedsRefinement` | Acceptance is unavailable. The harness replans and retries on its own while `studio.flow_kinds.delivery.max_auto_refinement_iterations` allows it and each iteration keeps closing criteria; it asks the customer for another iteration only once that budget is spent or progress stalls. |
| `Blocked` | Acceptance is unavailable; the customer can continue, replan, or abandon as permitted by the host. |

A failed, blocked, missing, or malformed criterion cannot be waived. Review, waiver, publication,
and final approval re-read the current readiness and candidate binding. A stale browser request or
changed candidate fails closed.

For browser-visible work, `.customer-preview\<variant>` is part of the sealed candidate. During
sealing, the host validates every preview file, copies its exact bytes into immutable
`ReviewedPreviewArtifacts` SQLite rows, and binds each row to the flow iteration and candidate
fingerprint. Customer requests verify only the selected stored BLOB's length and digest; they never
rescan or serve from the mutable worktree. Preview-root metadata such as
`.customer-preview\README.md` is sealed too, but does not become a browser artifact or bypass
variant-scoped path authorization. The directory must be at the flow workspace root rather
than inside a registered repository. Candidate sealing fails when a required preview is missing or
when a `customer-demo.json` has an invalid artifact ID, launch policy, working directory, host
binding, port placeholder, health path, or startup timeout. Its reviewed representation is
network-disabled. A separately requested loopback live demo is convenience only and contributes no
readiness evidence or publication authority.

Outbound links in the isolated preview request a host-owned confirmation showing the exact
destination. Only an explicit second click in that trusted confirmation can open the destination
with `noopener` and `noreferrer`. The bridge authenticates the opaque frame's message source,
rejects unsafe URL schemes and credentials, and runs under an exact script hash. The iframe retains
script-only sandbox authority: no popup, same-origin, top-navigation, or network permissions are added.

The flow-detail projection shows **Open reviewed preview** from the current durable seal without
touching the workspace. Preview metadata and file endpoints authorize the active reviewed-candidate
row, validate the persisted file set against its sealed counts and sizes, and hash the requested
BLOB before serving it. A legacy reviewed candidate created before durable preview storage is
reconstructed once from its sealed repository identities plus current scaffold and preview hashes;
tracked repository contents are not rescanned. Subsequent restarts use only the stored snapshot.

Before sealing, the active Delivery verifier receives an absolute verification-preview metadata
URL on Studio's actual listening address. Its unreviewed artifact views share the customer-preview
renderer, sandbox, CSP, and bootstrap, so QA can exercise the real isolation policy before review
exists. These separate endpoints require the current iteration's running `BeforeReview` verification
owner, preserve workspace path containment, and close when that task stops. They neither create a
reviewed seal nor open customer approval or publication.

Before substantive QA dispatch, the host checks the preview handoff. An unambiguous preview under a
registered repository can be materialized at workspace-root `.customer-preview\<variant>`. A durable
preparation intent records the origin and exact file identities, and starts an evidence epoch before
filesystem mutation. Interrupted preparation replays only those identities; links and unrelated
destination bytes are rejected. Canonical worker-authored previews remain authoritative.
Host-owned copies can follow subsequent source changes without replacing a worker's independent
canonical result. Preview file and aggregate byte limits are checked before browser verification.

Missing or ambiguous previews produce a durable host preflight blocker in the verification
assignment. The verifier must immediately push back to the planned upstream implementation owner;
the host rejects COMPLETE even during response-only correction. If that correction still ignores
the blocker, a durable host repair directive routes to the accepted dependency owner without
rewriting the agent's original response. Existing bounded pushback recovery
resumes that owner, preserves permission ceilings, invalidates earlier evidence before repair, and
rechecks the handoff before dispatching substantive QA. Sealing remains strict and never repairs
already-verified bytes implicitly. Recovery of an older seal failure also prepares previews before
scheduling fresh QA. Only a newly materialized candidate receives a distinct verification budget;
the original deadline is unchanged, and retries/corrections retain their assignment budget identity.

### Customer review and publication

Only a Delivery in `ReadyToApprove` can open ordinary customer review. **Request refinement**
supersedes the current readiness and candidate binding and starts a new iteration in the same
workspace and snapshot.

**Accept** records durable authorization for the exact reviewed candidate. The host then
materializes the planned `AfterApproval` Publish-only step. The agent can inspect packaging under a
governed read-and-shell policy, but cannot edit the candidate, receive publication credentials, or
perform the remote write. The host publishes the sealed commit and tree identities, records commit
or pull-request results, verifies the remote result against the same binding, and only then marks
the flow `Approved`. Pull-request Delivery requires GitHub CLI (`gh`) on the Studio server's
`PATH` and a token accessible to that process: intake confirmation and customer acceptance refuse
to advance when either is missing; Advisory and local Commit outcomes remain available. For pull-request delivery,
repositories already at the reviewed commit on
their remote default branch receive a durable already-current record instead of an empty branch
or pull request. A reviewed head with zero files in GitHub's authenticated comparison against the
current default head also gets a durable no-diff record, even if the branches have different
histories; neither no-op state publishes a branch or PR. The same remote head and comparison are
checked again during publication verification.
Before any remote publication side effect, the host formats the confirmed brief as a concise
pull-request description with the goal, scope, requested checks, and boundaries. Older flows
without a structured brief use their validated outcome instead. The description attributes only
the reviewed commit proposed by that pull request; trace identifiers remain in a collapsible
section rather than presenting hashes as the customer outcome.

A publication failure is visible and does not create approval. The durable authorization and
publication identity remain available for an explicit restart. When a completed release turn
failed only because its response contract could not be scheduled, a manual restart verifies the
persisted output digest and queues a read-only response correction instead of rerunning the
release work. If remote publication completed but final approval failed, a manual restart retries
only the host's readiness and journal checks, not the remote push or pull request.

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

Pushback is reserved for missing or unusable required upstream inputs. A completed verification can
report honest failed or blocked criteria; non-blocking disclosures and host response-validation
errors are not upstream pushback.

The full QA contract is validated before handoff acceptance, not just its sentinel markers.
An invalid plan hash, evidence reference, evidence kind, success claim, or JSON shape triggers one
durable response-only correction. The host retains the original output, execution count, and tool
observations even when a contract is rejected. The correction receives the exact recorded evidence
and is restricted to read/search tools, with shell and write denied. It cannot rerun implementation,
weaken acceptance criteria, or authorize release from an invalid response. Both attempts and the
correction event remain visible. QA correction starts a fresh CLI context using the retained
response and evidence, rather than resuming an already bloated conversation. Its own interrupted
attempt still resumes exactly. An interrupted completed QA session follows this same bounded
path during restart reconciliation.

Initial QA, response correction, and legacy restart assignments all repeat the validator's
maximum of 12 unique host-issued evidence identifiers per criterion or residual risk. The
verifier selects a sufficient allowed-kind subset, including genuine aggregate command
results where appropriate; the host never truncates citations, invents aggregate evidence,
or weakens verification to accept an oversized response.

A governed continuation of blocked verification repeats each exact host-bound blocker,
its remediation, and the unchanged plan's allowed evidence kinds. An evidence-only gap
requires a genuine new observation of the complete journey, not relabeling a Test as an
Observation or weakening the acceptance plan. The substantive continuation re-establishes
verification in its new evidence epoch under the original permission ceiling.

When a CLI process stalls after successful checks, the interrupted journal retains its
completed tool observations without accepting its partial handoff. Only observations bound
to that invocation's session, start time and workspace can be retained. A successful runtime
resume preserves those earlier observations in execution order alongside its own calls,
including failed checks with their original failure status, so reading a report during the
resume cannot erase the actual test execution that produced it.

Shell-driven browser automation is recorded as `Command`, Playwright test-runner invocations as
`Test`, and successful host image views as `Observation`. Browser-visible acceptance criteria
therefore permit `Observation`, `Command`, `SourceInspection`, and `Test`; this keeps the hashed plan
aligned with the host's evidence types without inferring observations from arbitrary source text. A
blocked same-iteration continuation preserves the latest substantive verification permission
ceiling, intersects it with the current workflow ceiling, and can run a fresh browser check without
inheriting a response-correction turn's temporary read-only policy. Because that continuation may
write candidate bytes, it starts a new evidence epoch and cannot cite observations from before the
continuation.

MCP connectivity belongs to Copilot CLI, following Symphony's coding-runtime boundary rather
than embedding browser-specific execution in the orchestrator. `copilot.mcp_config_file` names a
trusted, workflow-relative native `mcpServers` JSON file. Its exact content contributes to the
workflow revision; both files reload as one effective definition, and invalid configuration
blocks new dispatch. Servers declare explicit native `tools` lists because CLI tool exposure is
name-based rather than a server wildcard. Native server fields, including transport, command,
arguments, environment, and URL, pass through unchanged; the CLI owns their interpretation.

Delivery workers receive configured server definitions and tool approvals through their persisted
effective permission document. The host stages that snapshot and uses `--additional-mcp-config`,
`--available-tools`, and exact `server(tool)` approvals. Corrections drop MCP access; permission
tightening intersects tools and drops changed server launch definitions rather than replacing
them with broader ones. Old permission documents without MCP definitions retain no MCP access.
The normal JSONL tool-call pipeline records MCP invocations and results in durable execution
evidence. Provider errors cannot count as successful observations. This does not add a separate
provider lifecycle manager, role-name router, or OS/network sandbox.

Before the final verification turn, and again during same-iteration finalization recovery, Studio
restores only missing host-owned scaffold files from the trusted source. Candidate sealing itself
never mutates verified product files. The registered repository map and existing trusted scaffold
define the product boundary, not tool-specific output names. Additional non-repository files are
execution artifacts: their path, digest, length, iteration, and capture-step attribution are committed
through `HarnessDbContext` to `ExecutionArtifacts`, with a visible
`workspace.execution-artifact-archived` event. The original workspace files remain available to
downstream agents, so preserving tool output does not break evidence references. Replay
deduplicates by flow, iteration, path, and digest across restarts. Artifacts are not scaffold,
previews, reviewed content, or publication inputs. This covers
unknown MCP outputs and agent-written screenshots without adding directory or extension exceptions.
Repository-local generated files continue to follow Git ignore rules; new product files belong
inside registered repositories. Changed scaffold bytes, links, and repository-map drift still fail
closed. Files up to 8 MiB also receive an exact SQLite BLOB copy. Larger files use an explicit
workspace-reference storage mode rather than a size-triggered flow failure or unbounded BLOB
allocation. Their download requires the original file to exist inside the same flow workspace,
without links, and still match its recorded digest; unlike BLOB-backed artifacts, they are not
available after workspace removal. Artifact download links in the execution ledger serve
attachments, never active preview content. Before copying any missing scaffold bytes, Studio
durably starts a new evidence epoch; interrupted
restoration therefore cannot make pre-restoration evidence eligible after restart. Flow abandonment
deletes known Copilot sessions by deterministic ID and uses lightweight workspace metadata for
fallback discovery. An active fallback match is checked against the complete journal, process start
window, and exact CLI session argument; Linux process arguments retain their native boundaries so
prompt text cannot impersonate `--session-id` or `--resume`.

Pre-mortem response envelopes receive the same bounded, read-only format correction rather than
failing an otherwise completed investigation. Their correction uses a distinct persisted CLI
session without consuming another critique round. Pre-mortem and pushback-retry assignments carry
the complete customer brief and evaluated or corrected handoff through the bounded context
documents; they do not truncate either source. A later substantive revision uses the originating
task's persisted permission ceiling, not a response correction's temporary read-only restriction.

The SQLite ledger and `CopilotSessionJournal` support restart reconciliation. Completed journal
results are applied once; interrupted resumable attempts retain their flow, workspace, semantic
step, Copilot session identity, workflow revision, and permission ceiling. Open customer reviews
remain waiting, accepted Delivery publication can be rematerialized from durable authorization,
and missing-qualification blockers are not retried automatically.

A contract-valid final handoff followed by its matching closed assistant turn starts a separate
60-second CLI shutdown grace period. Further assistant or tool work disarms that watchdog; open
turns, pending tools, partial responses, and session errors cannot trigger it. If the CLI remains
alive, the host terminates its process tree and uses the existing journal-recovery path instead of
waiting out the whole task timeout. The journal must still confirm a current completed result
after process termination, and normal handoff and QA validation still apply. This is visible
recovery, not acceptance of a response while its worker remains active.

The flow page's **Recover stalled execution** action is limited to queued, running, or reworking
work. It stops only the tracked worker, reconciles persisted state, and queues the flow once.
Failed work requires the explicit **Recover failed task** action. When its completed CLI journal
contains a rejected QA response, recovery restores the observed evidence and queues only response
correction in the preserved workspace, without repeating completed upstream steps. The UI labels
host execution failures separately from actual agent pushback. Queue and execution claims prevent
two workers from running the same flow concurrently.

## Execution timeout policy and activity

Studio borrows the distinctions in the current
[OpenAI Symphony specification](https://github.com/openai/symphony/blob/main/SPEC.md):
section 10.6 defines `codex.turn_timeout_ms` as resettable stream silence, section 8.5 uses event
inactivity, section 10.4 describes structured events, and section 7.1 distinguishes clean
same-thread continuation from worker exit. Studio still uses **Copilot CLI**, not Codex app-server,
and does not claim Symphony conformance. Studio's old `copilot.turn_timeout_ms` was a per-process
total-runtime cap; it was not Symphony's stream-silence timeout.

The strict `copilot` mapping in `WORKFLOW.md` now defines these independent millisecond settings:

| Key | Default | Effect |
| --- | --- | --- |
| `inactivity_timeout_ms` | `300000` (5 minutes) | Stop a process with no recognized structured activity. Always positive; never disabled by diagnostic noise. |
| `silent_tool_timeout_ms` | `1800000` (30 minutes) | A correlated tool start permits silence until this interval from its observed start. It does not grant unlimited protection. |
| `soft_warning_ms` | `3600000` (1 hour) | Persist one operator-visible warning per assignment; **do not terminate**. |
| `execution_budget_ms` | `14400000` (4 hours) | Finite absolute assignment deadline, including retry delays and downtime. No fresh budget per process. |

All values must be positive, `inactivity_timeout_ms <= silent_tool_timeout_ms <= execution_budget_ms`,
and `soft_warning_ms < execution_budget_ms`. Unknown or case-mismatched `copilot` keys fail closed.
Model quality and latency forecasts no longer infer watchdog extensions. Choose explicit limits
for the workload; increase the configured budget for **future assignments** when four hours is
insufficient.

### Explicit migration

Replace all three legacy keys; their presence now rejects the current file with an actionable
migration error, even if the new settings are also present. There is no compatibility alias or
silent reinterpretation:

```yaml
copilot:
  command: copilot
  inactivity_timeout_ms: 300000
  silent_tool_timeout_ms: 1800000
  soft_warning_ms: 3600000
  execution_budget_ms: 14400000
```

For this repository, the previous `turn_timeout_ms: 3600000` is deliberately replaced by a
one-hour **warning**, not a termination point. `stall_timeout_ms: 300000` becomes the explicit
structured-inactivity interval; `maximum_quality_stall_timeout_ms: 1800000` is removed and a
30-minute allowance is configured for trustworthy silent tool lifecycles instead. The new
four-hour absolute deadline is a separate policy decision. Restart Studio on the new binaries
after migrating; an older running binary cannot enforce this contract.

`DatabaseInitializer` upgrades existing SQLite ledgers idempotently. At first execution under
the new policy, an older assignment without a budget is bound to its **earliest known persisted
step start** (including causal retries), not the migration or resume time. If no prior start
was recorded, it starts when the budget is first bound; unavailable historical consumption is
not fabricated. An old assignment already beyond its newly explicit budget fails with
`BudgetExhausted`, rather than receiving another full allowance.

### Durable assignment boundary

`AgentExecutionBudgets` stores the assignment's semantic root and agent identity, first start,
absolute UTC deadline, warning timestamp, and exact policy/revision through `HarnessDbContext`.
Each `FlowStep` binds that root and its serialized policy before dispatch. Runtime retries,
confirmed-session resumes, manual restarts, response corrections, and application recovery reuse
that deadline. A distinct new semantic root (for example a new planned assignment or iteration)
gets a distinct budget; selecting a different model or changing session identity does not reset
the old assignment. The elapsed and remaining values refer to this wall-clock assignment window,
not CPU time or an estimate of work completed. Downtime and retry backoff intentionally consume
it. The running monitor also uses a monotonic clock, so a local clock rollback cannot replenish
an active process's allowance; persisted deadlines assume the host's UTC clock remains accurate.

Reload affects future assignments. An already bound assignment keeps its timeout policy, and
an interrupted attempt keeps its exact prompt, revision, and effective permission ceiling.
The existing permission resolver may only preserve or tighten authority on a retry.
Invalid reloads still block admission while already admitted recovery retains the last valid
definition. No agent-definition changes or additional execution backend are involved.

`BudgetExhausted` is a distinct failure kind and phase, excluded from automatic runtime retry and
confirmed-session auto-resume predicates. The failed-flow restart API returns a typed lifecycle
conflict when no budget remains. Operators should inspect preserved edits and scope any remaining
work as a distinct assignment/flow; there is no budget-extension control. A current contract-valid
completed handoff can still be recovered without running the agent again. Cancellation, the
60-second completed-handoff shutdown grace, owned process-tree termination, isolated workspaces,
and ordinary handoff/QA validation remain separate controls. Timeouts never roll back edits.
Per-session host serialization and active-session journal checks prevent concurrent resumes.

### Activity signals and limits

The Copilot CLI `--output-format json` emits JSONL. The installed CLI was inspected as version
`1.0.91`; its session journal and the
[Copilot runtime event definitions](https://github.com/github/copilot-sdk/blob/main/nodejs/src/generated/session-events.ts)
include assistant message/turn events, reasoning deltas, correlated `tool.execution_start`,
`tool.execution_complete`, and tool progress/partial-result events. Availability varies by CLI
version and output mode. Studio recognizes these existing signals and incrementally tails only
the workspace-bound session's `session-state\<session-id>\events.jsonl` as a supplement to stdout.
It skips pre-resume history, waits for complete lines, and deduplicates tool lifecycles shared
by stdout and journal. Missing/unreadable journals do not disable the watchdog; access failures
are logged and stdout remains usable.

Raw stdout/stderr line arrival is tracked separately and **never** resets structured inactivity.
Unknown events, ordinary logs, usage/info chatter, malformed JSON, unsafe tool names, and
uncorrelated tool progress do not prove work. `assistant.message` is activity, not completion.
An identified start grants a bounded silent allowance; completion removes it, duplicate starts
do not renew it, and concurrent tools use the oldest still-active start. Subsequent genuine
structured events may renew the normal inactivity interval, but every execution still shares
the finite absolute deadline. The running-process watchdog has no progress score or
repetition-based termination; repetition suppression between failed attempts is separate.

### Bounded factory continuation

After the CLI runtime's own retries are exhausted, pre-review `Transient` failures and confirmed
resumable `TimedOut`/`Stalled` failures can schedule a causal continuation in the same flow workspace.
`agent.continuation-scheduled` persists the source/retry identity, semantic budget root, failure
fingerprint, recovery count, and due time in `FlowEvent`, atomically with the pending `FlowStep`,
task profile, and dependency rewiring. Restart reconciliation retains that pending work and backoff;
dispatch checks the absolute deadline both before and after waiting. Resumable interruptions use
the confirmed Copilot session with a new persisted turn-specific prompt, not an exact-instruction
replay of the failed step. Exact replay is reserved for interruption of that same durable turn.
Other continuations inspect preserved work before further edits.

The existing Max handoff retries setting caps continuations per semantic assignment. Backoff uses
`agent.retry_base_delay_ms` and `agent.max_retry_backoff_ms`. The frozen assignment budget and
permission ceiling remain authoritative; downtime and delay consume the original budget. Failed
CLI tool observations are retained, and an identical failure/output/evidence fingerprint stops
repetition before the count limit. Timestamps or diagnostic chatter are not proof of progress.
Implementation continuation invalidates earlier candidate evidence before any further writes.

Publication and remotely authorized steps are excluded. Model/dependency unavailability, invalid
output or guard violations, unresumable interruptions, ambiguous crashes, cancellation, and
exhausted budgets do not trigger blind factory continuation. Contract corrections, accepted-owner
handoff repairs, and failed-acceptance refinement retain their existing independently bounded
paths. Exhaustion emits `agent.recovery-exhausted` and a customer-safe preserved-work blocker;
successful resumption clears only that recovery blocker, not unrelated readiness blockers.

Behavioral acceptance must plan executable actions and assertions with Test or Command evidence.
The verification assignment explicitly rejects a screenshot, source inspection, or a successful
click invocation alone as proof of the required product effect. Host evidence-kind and candidate
binding checks remain strict; unverified behavior must be reported Failed/Blocked for repair,
not converted into customer review.

A rejected QA response is not an accepted assessment, but a complete strict QA envelope with the
current acceptance hash and actionable failed criteria can serve as an untrusted remediation
proposal. The host routes reports attributed to the completed implementation dependency directly
to that owner through the existing bounded handoff loop, rather than spending a format-only
correction on a candidate already reported defective. If a correction returns a truncated or
malformed envelope, recovery may use only the exact completed source report named by its durable
correction event, with matching current iteration, verifier, plan step, semantic lineage, and
acceptance hash. A parseable newer report supersedes the source; recovery never scans arbitrary
older reports or splices response fragments together.
Responsible roles may name the pinned owner's exact agent ID, role, or display name; a display
name is matched only after the accepted dependency graph has selected that implementation owner.
The directive retains the exact source-step identity and complete scoped criterion/remediation
payload. Owner execution receives it as `qa-repair-proposal.json` in host-owned context, rather
than relying on a clipped display reason or the malformed correction text. Staging is idempotent:
restart reuses an existing directive, and exhausted/disabled handoff limits remain typed conflicts
without duplicate ledger events or reset repair counts.
Owner continuations and manual retries resolve that immutable context through the same semantic
root, validating the current iteration, agent, implementation plan step, invocation and stage;
they do not lose the document when a new attempt ID is allocated.
The original response and failed citations remain visible; no claim or evidence is normalized
into success. Legacy failed flows use this same route on restart rather than replaying formatting.
Owner repair starts an evidence epoch. The ensuing substantive verification restores the
original verification task's permission ceiling (tightened by current policy), not the
format-only correction's temporary tool restriction. Verification of the repaired candidate is a
distinct bounded assignment, recorded in `verification.repaired-candidate-assignment`, with its
own budget root bound on launch. Earlier deadlines remain unchanged. Runtime retries and
response corrections of that new verification share its root and cannot reset it. Legacy
failed verification can receive this assignment once per completed host-directed implementation
revision; subsequent exhaustion for the same repaired candidate remains a typed conflict.
The existing bounded handoff count, pinned execution policy, and approval boundary still apply.
Valid QA reports with actionable failed or blocked criteria owned by the existing implementation dependency
also use the remaining same-iteration handoff rounds before whole-iteration refinement. The
configured handoff retry count is a maximum, not a minimum: passing fresh QA closes the repair
immediately; disabled or exhausted rounds leave the valid assessment for ordinary readiness and
bounded broader refinement. Reports requiring a different owner are not forced into that loop.
Missing test infrastructure assigned to an implementation owner is an actionable verification
blocker, not a terminal factory outcome. When local repairs do not converge, the host starts the
next iteration with the preserved workspace and Team Lead replanning from the complete unmet
criteria. Unassigned external blockers remain explicit blockers; no readiness or approval guard
is weakened.
Invalid agent output first receives one read-only follow-up in the same recorded Copilot session,
with the original budget root and deadline. The exact validation error leads a short working
prompt; only the retained response, unchanged response contract and relevant evidence are staged.
The correction remains a distinct durable sub-attempt, not a fresh verification assignment.
If a corrected QA assessment requests upstream rework, its original request remains recorded
while the host routes the validated negative assessment through repair or bounded replanning.
Implementation repair permissions resolve the original task through the durable response-correction
lineage, then tighten against current policy. A read-only report correction is not the permission
ceiling of later coding work; its own restrictions and earlier execution deadlines remain intact.
Before launching a scoped owner repair, the host checks the effective tool capabilities, not just
the profile label. Direct-write and shell capabilities are evaluated independently, preserving
both group and individual tool denials. If neither is authorized, the lifecycle becomes explicitly Blocked
without dispatching an investigation as a repair or broadening access.
Scoped owner assignments require one exact `REPAIR_STATUS: REPAIRED`, `REPAIR_STATUS: NO_CHANGE_NEEDED`,
or `REPAIR_STATUS: BLOCKED` line. No-change reports must explain executed source-backed checks
disproving the proposal; neither repaired nor no-change claims establish readiness without fresh
independent QA. A blocked report stops before the dependent verifier, records
`handoff.owner-repair-blocked`, and preserves all work. Investigation-only `COMPLETE` output must
correct its missing disposition through the existing bounded report-only correction contract.
Host-owned workspace context also distinguishes shell execution directories from CLI path
authorization. Commands that change directory use fully qualified, workspace-bound paths for
logs, evidence and other path arguments. A relative-path approval request must first be checked
against its intended authorized destination; correcting that path does not grant broader access.
Genuine tool, path and network denials remain blockers, and execution evidence is never inferred
from a denied call.
Pre-mortem framing is recovered deterministically when the complete strict report is already
available: the single exact status line is moved first, retaining all other text and JSON bytes.
The unchanged strict parser still enforces markers, schema, field lengths and status/findings
consistency. No broken JSON escapes or missing findings are synthesized. If a format correction
damages its report, only its ledger-bound completed source in the same flow, iteration, agent,
semantic lineage and requirements target may supply the intact report. Newer valid results take
precedence. Original responses and recovered framing are recorded in FlowEvent; legacy restart
uses normal step completion without re-execution, deadline extension or customer acceptance.
Explicit operator rescoping of an exhausted pre-review worker uses a distinct assignment and
budget root, not an extended retry. `flow.remaining-work-assignment` records the full bounded
scope and causal source. Its fresh session avoids replaying a long interrupted turn; the new
45-minute ceiling can only tighten the previous and current policies. Duplicate scopes and
rescoping beyond the configured handoff limit are typed conflicts. This path is unavailable
after acceptance, for publication, or for pre-mortem work, and never marks failed work completed.

Valid mixed Failed/Blocked assessments can schedule bounded refinement when there are concrete
failed criteria to repair. The lifecycle coordinator still requires the exact candidate binding;
Blocked-only assessments cannot use this path. Refinement limits and no-progress detection remain
unchanged, and no blocked result can open acceptance or publication.

`FlowStep.RuntimeActivityJson` and the flow-detail API expose host-observed timestamps, last
recognized event type, oldest active tool name/start/duration, completed-tool count for the current
process invocation (including observed subagent tools), assignment elapsed/remaining time, deadline,
soft-warning state, and termination reason. Timestamps are **host observations**, not claims about
agent progress. Tool arguments, reasoning/message content, and tool output are not included in
these activity snapshots. Fields without trustworthy signals remain null and the UI says
**Unavailable**; no tool lifecycle signals means no completed-tool count, rather than a fabricated
zero. Snapshots update at most once per ten seconds, plus immediate warning/termination and final
updates. The ledger records policy binding, the one-time warning, sampled tool-state transitions,
and termination, not every output line. The flow page shows activity separately from the response
and the persisted policy separately from permission policy.

Repository initialization remains a bounded one-off CLI command. Repository knowledge study is
not a resumable flow assignment: its bounded response-correction loop shares one in-memory deadline,
and an explicit new study operation starts a new assignment. Durable workflow execution budgets
apply to Studio's persisted `FlowStep` assignments.

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
- customer gates, Delivery readiness, reviewed candidates, immutable reviewed-preview artifacts,
  waivers, and publication records;
- observed tool calls, task profiles, model-routing decisions, and learnings; and
- live-demo process records.

`FlowLifecycleCoordinator` serializes mutations per flow and guards status transitions. API and UI
projections render persisted state; they do not independently authorize lifecycle changes.

Startup calls `Database.EnsureCreatedAsync()`. It creates the current schema when the database does
not exist and performs the additive creation of `ReviewedPreviewArtifacts` for existing databases.
Other entity-shape changes are not migrated. During development, stop Studio and recreate the
database after other entity-shape changes:

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
