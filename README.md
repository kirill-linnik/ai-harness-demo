# AI Harness Studio

AI Harness Studio is a standalone .NET 10 conference demo that turns a spoken product idea into an observable multi-agent engineering flow.

It is intentionally generic and suitable for an external audience. Its orchestration kernel is an
independent .NET adaptation of selected concepts and normative invariants from the
[OpenAI Symphony service specification](https://github.com/openai/symphony/blob/main/SPEC.md), not
a drop-in port of Symphony's issue-tracker and Codex workflow. AI Harness Studio replaces that
workflow with customer intake, GitHub Copilot CLI, a multi-role delivery system, and durable product
feedback.

## Symphony concepts and Studio extensions

In this project, a Symphony work item maps to a `FlowRun`, and its per-issue workspace maps to a
per-flow project workspace. The following concepts come from Symphony:

| Symphony concept | AI Harness Studio adaptation |
| --- | --- |
| Repository-owned workflow contract | Root `WORKFLOW.md` combines YAML runtime policy with the shared prompt template. Configuration is typed and validated, template variables fail closed, and valid changes are hot-reloaded while an invalid reload leaves the last-known-good definition active. |
| Layered service design | Policy, configuration, coordination, execution, integration, and observability remain separate across `AiHarnessDemo.Core`, the orchestration services, the Copilot boundary, and the customer-facing API. |
| One authoritative orchestrator | `FlowQueue`, `FlowWorker`, and `WorkflowEngine` own dispatch and transitions, reject duplicate work, and enforce the hot-reloadable global concurrency bound. |
| Isolated, reusable workspaces | Each flow receives a contained `data\worktrees\<flow-id>` workspace. Agent execution is restricted to that workspace, which is preserved across steps, retries, feedback iterations, and restarts. |
| Workspace lifecycle and safety | Workspace keys are sanitized and collision-resistant, paths are checked against the configured root, and `after_create`, `before_run`, and `after_run` retain Symphony's failure semantics. `before_remove` is recognized by the contract, but current flow workspaces are preserved rather than automatically removed. |
| Explicit run-attempt lifecycle | Workspace preparation, prompt rendering, process launch, streaming, completion, timeout, stall, cancellation, and failure are recorded as visible `FlowStep` phases and `FlowEvent` entries. |
| Bounded recovery | Transient execution failures use capped exponential backoff with jitter; runtime availability has circuit-breaking behavior; persisted work is reconciled after restart instead of being dispatched twice. |
| Pluggable agent-runner boundary | Orchestration depends on reasoning and workspace abstractions rather than a concrete agent process, while this product deliberately routes every real execution through Copilot CLI. |
| Operator-visible state | Structured logs, runtime APIs, and a status surface expose active work, retries, failures, timing, and agent progress without becoming orchestration dependencies. |

AI Harness Studio implements the following capabilities beyond Symphony's scheduler/runner
contract:

| Studio extension | What it adds |
| --- | --- |
| Customer-driven intake | Browser voice or text replaces issue-tracker polling. Account Manager separates clarification from explicit confirmation, and only a customer-confirmed brief can enter delivery. |
| Dynamic multi-agent delivery | Team Lead selects enabled specialists from `.github\agents\*.agent.md`, emits validated role task profiles, and creates an ordered dependency-aware handoff plan rather than running one coding agent per work item. |
| Handoff trust gates | Shadow, gated, and automatic decisions are recorded by action and blast radius. Release always requires a customer decision, and a kill switch dominates configured trust. |
| Bounded pushback and feedback loops | A downstream `PUSHBACK` resumes the responsible upstream Copilot session, retries the blocked handoff in place, and stores a reusable learning. Product Manager can close the flow or send the same flow through another iteration with its ledger intact. |
| Evidence-based pre-mortems | Team Lead can place independent Pre-mortem Sceptic checkpoints after any planned delivery role. Each review uses a different model family and an enforced read-only research toolset, reports at most five evidence-backed findings, and feeds findings into the original agent session before the flow advances. |
| Outcome Verification Loop | Team Lead emits a strict acceptance plan; assigned roles emit criterion-linked evidence; Release Engineer commits an unpublished local candidate; and Quality Engineer independently verifies the exact candidate fingerprint before any customer release gate can exist. Failed criteria return only to their responsible original sessions, followed by candidate refresh and another bounded QA round. |
| Durable product state | SQLite persists settings, intake dialogue, flows, steps, gates, events, tool calls, model catalogs, routing evidence, outcomes, and cross-flow learnings. Symphony's core recovery does not require a durable orchestration database. |
| Copilot-native model routing | A dedicated Copilot ACP process discovers enabled model and reasoning-effort candidates. Each step is profiled and routed immediately before execution using the selected quality, speed, or cost strategy plus normalized historical evidence. |
| Repository study and multi-repository Git isolation | The harness can run `copilot init`, persist editable shared knowledge, discover every Git repository in a project folder, and create the same flow branch in an isolated worktree for each repository. Symphony leaves VCS workspace population implementation-defined. |
| Durable Copilot session continuity | Step-level Copilot session IDs and journal state support same-session handoff correction, completed-turn recovery, interrupted-turn resume after host restart, and explicit manual restart of failed flows. |
| Product UI and API | The React dashboard adds Settings, AI Factory, live execution graphs, customer previews, history, harness memory, model-routing explanations, and independently addressable flow pages on top of Symphony's optional status-surface concept. |

## Run

Prerequisites:

- Windows, macOS, or Linux
- .NET 10 SDK
- Node.js 20.19+ (or 22.12+) and npm (needed to build the browser client; `dotnet build`/`dotnet run` do this automatically)
- GitHub Copilot CLI, installed and authenticated
- Git
- Edge or Chrome for browser speech recognition

Install Copilot CLI using one of the supported methods:

```powershell
# Windows (recommended)
winget install GitHub.Copilot

# Any platform with Node.js 22+
npm install -g @github/copilot
```

Then verify and authenticate it before starting the harness:

```powershell
copilot --version
copilot login
```

At startup, the harness resolves the configured `copilot.command`, rejects interactive editor
bootstrap shims, validates the CLI version and required programmatic/ACP options, then opens a
dedicated `--acp` stdio process to discover enabled model + effort candidates. Discovery must
succeed on every startup; persisted catalog snapshots are audit records, not a readiness fallback.
Runtime and catalog status appear on **Settings**, and the factory remains locked when either check
fails.

On Windows:

```powershell
cd <clone-directory>
dotnet run --project .\src\AiHarnessDemo
```

Or:

```powershell
.\Start-Demo.ps1
```

On macOS or Linux:

```shell
cd <clone-directory>
cd src
cd AiHarnessDemo
dotnet run
```

Open `http://localhost:5283`.

## Rehearsal path

1. Open **Settings**, enable the agents you want Team Lead to consider, and choose a local project folder.
   Choose Maximum quality, Fastest response, or Lowest cost; the strategy is snapshotted when the
   confirmed flow is queued.
2. Leave **Run Copilot init** selected, then choose **Initialize and study repository**. Review and edit the generated shared knowledge.
3. Open **AI Factory**, click **Listen to the next task**, and speak the idea.
4. Correct or confirm Account Manager's understanding. Confirmation sends the brief straight to Team Lead so you can watch the execution graph.
5. Watch the local candidate and independent QA rounds. Open the customer preview only after the
   current candidate receives an all-criteria PASS, then approve it or start another iteration.
6. Show **Execution history** and **Harness memory** to explain model routing and cross-flow learning.

The AI Factory and every new-assignment control remain locked until Settings contains a studied project folder with at least one Git repository.

## Safety and persistence

- All application state is stored in `data\ai-harness.db`.
- Model catalogs, normalized task profiles, routing decisions/alternatives, and normalized outcome
  evidence are stored in SQLite. Prompts and repository content are not copied into routing evidence.
- `WORKFLOW.md` is the hot-reloadable, version-controlled Symphony policy and prompt contract.
- `WORKFLOW.md` also owns `outcome_verification.enabled` and `max_rounds` (`1-10`, default `3`).
  The effective limit is snapshotted when Team Lead's acceptance plan is accepted, so reloads affect
  only new cycles.
- Agent prompts lead with the role-specific assignment and include only compact repository facts, relevant immediate handoffs, applicable learned constraints, and the completion contract; retry diagnostics remain in the execution ledger.
- Agent definitions are loaded from `.github\agents\*.agent.md`.
- Copilot CLI receives explicit project access to `data\worktrees\<flow-id>`; each Git repository discovered in the selected project is materialized there as an isolated worktree, and source-folder paths remain intentionally inaccessible.
- An explicit agent `PUSHBACK` is persisted as a rejected handoff, resumes the responsible upstream agent's Copilot session, then retries the blocked agent in its existing session.
- Team Lead's strict pre-mortem plan can select zero or more downstream checkpoints. A selected Pre-mortem Sceptic investigates the completed result with a different model family, receives only source-search/read and web-research tools with publication credentials removed, and returns `CLEAR` or at most five evidence-backed findings.
- Every non-empty pre-mortem result resumes the evaluated agent's original Copilot session. That agent must return a complete replacement handoff with an `ADJUSTED` or `UNCHANGED` disposition; adjusted results are reviewed again until that checkpoint's round cap is reached.
- Copilot session IDs are persisted per step. After a host restart, completed CLI turns are recovered from the session journal; interrupted turns resume the same session and preserved workspace instead of starting over.
- The quiet-process watchdog is strategy-aware: Maximum quality scales its quiet window from 5 to 15 minutes using the router's predicted accepted time, while other strategies retain the five-minute bound. The 20-minute hard turn limit and configured retry count still cap each attempt.
- If Copilot writes a valid final handoff but hangs before clean shutdown, the harness recovers that handoff from the session journal instead of failing the flow. Confirmed interrupted sessions resume on bounded runtime retries, and each recovery transition remains visible in the event ledger.
- Browser-clickable deliveries publish validated per-variant builds from `.customer-preview\<variant>` into the customer acceptance page, where the customer can inspect the real result and approve it or request another iteration with feedback.
- Each governed flow persists a bounded `outcome-verification-state-v1` proof ledger in SQLite.
  Strict, case-sensitive acceptance, evidence, and QA JSON contracts reject unknown fields, invalid
  marker placement, bad hashes, missing criteria, and oversized documents.
- Release preparation before QA and the customer gate is local-only: intended changes must be
  committed, every repository must be clean, and preview files are byte-hashed into a deterministic
  multi-repository candidate fingerprint together with the repository/remote mapping snapshotted
  by workspace creation. For project workspaces, the exact trusted top-level scaffold path set,
  lengths, and source digests are also part of the manifest. Added repositories, deleted scaffold,
  changed scaffold, or changed live origins invalidate the candidate;
  mutable Git config never authorizes publication. Customer-visible criteria require a servable
  `.customer-preview\<variant>\index.html` (or `browser\index.html`). `.ai-harness\outcome-verification` contains derived,
  read-only QA context and is excluded from candidate identity and commits.
- Ignored candidate paths are rejected unless they are derived QA/preview content or documented
  transient output. The transient directory policy is: `bin`, `obj`, `node_modules`, `dist`,
  `build`, `coverage`, `TestResults`, `.next`, `.vite`, `.vs`, `.idea`, `.venv`, `venv`,
  `__pycache__`, `packages`, and generated `wwwroot`; `.log`, `.suo`, `.user`, `.DS_Store`, and
  `Thumbs.db` are the only ignored transient files allowed outside those directories.
- Quality Engineer receives the confirmed brief, normalized plan and evidence, prior findings,
  workspace locations, and exact candidate identity from SQLite. Only a current all-criteria PASS
  creates the customer Release gate. PASS checks are bound to persisted host observations containing
  tool type, normalized safe arguments/command, working directory, exit status, and a bounded result
  summary plus digest. Commands must be real test/assertion/inspection commands executed from the
  candidate workspace; reads of QA context/evidence or paths outside candidate repositories and
  previews cannot authorize PASS. One real suite may support several criteria. Runtime retries do
  not consume QA rounds.
- Governed turns keep credentials and MCP integrations disabled, expose read-only Git inspection,
  and route each repository through disposable shadow metadata. Before the child process starts, the
  host durably journals and removes each authoritative worktree `.git` pointer, installs the shadow
  `.git`, and restores the original pointer after validating and discarding every shadow mutation.
  A restart recovery pass restores interrupted swaps before workspace or candidate inspection.
  This is deterministic same-user process isolation, not a kernel security boundary: on platforms
  without a separate sandbox identity, a hostile process that already knows an authoritative
  metadata path could still address it directly, but that path and credentials are not exposed
  through the governed cwd, prompt, Git environment, or normal Git discovery.
- Failed criteria resume only their responsible original role sessions, corrections run serially in
  Team Lead order, and each expected correction is bound to one plan hash, QA round, role, and
  semantic root and is applied once. Release Engineer refreshes the candidate, and the same QA
  session continues with a fresh complete context. Exhaustion creates an `OutcomeResolution` gate with Continue (one extra
  round, at most ten), Replan, or the existing Abandon path; there is no force-pass or waiver.
- Customer approval rechecks the fingerprint before queuing publication. A stale candidate
  supersedes the gate and publishes nothing. For governed flows the Release Engineer publication
  turn remains push-guarded; afterward the host publishes the exact immutable manifest SHAs,
  journals each repository side effect for restart recovery, and verifies every resulting PR
  commit/tree before approval becomes terminal.
- Release decisions carry the exact unresolved gate ID and full reviewed candidate fingerprint.
  Stale-tab conflicts return HTTP 409 without resolving the current gate or queuing publication;
  detected on-disk drift returns `RefreshQueued`, never an approval-shaped response. Rejection
  feedback is persisted atomically with the identity-bound decision.
- Existing flows without an outcome ledger remain visible as **Legacy unverified** and keep their
  unresolved legacy release gates. No historical PASS or acceptance criteria are inferred from prose.
- **Abandon** is the third customer decision. It cancels active execution, stops workspace-owned applications and listening ports, removes Copilot sessions, preview artifacts, worktrees, and local/remote flow branches, then marks the flow abandoned. The durable execution ledger, harness learnings, and model-routing evidence remain available for future decisions.
- Each step is routed immediately before execution across the currently discovered model + reasoning
  effort candidates. The step detail explains predictions, confidence, exploration, and rejected
  alternatives. A model-unavailable error is classified separately; automatic rerouting fails closed
  when the CLI result cannot prove that neither session nor tool activity began.
- A failed flow can be manually restarted from its detail page. The failed attempt remains in history, while a new retry resumes its Copilot session when available and re-queues downstream work in the same workspace.
- The shared correction limit is persisted in Settings (`0-10`, default `2`). It caps upstream retries per blocked handoff and total sceptic runs per Team Lead checkpoint. Pushback exhaustion stops the flow; pre-mortem exhaustion advances with the latest complete adjusted result.
- Multiple flows can run in parallel; every flow receives an independent project workspace and a matching branch in each discovered repository.
- No cloud credentials, private endpoints, or provider-specific work-item integrations are included.

See [`docs\ARCHITECTURE.md`](docs\ARCHITECTURE.md) for the flow and component model.
See [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) for Symphony attribution.

## Browser client

The browser dashboard is a React + TypeScript + Vite app in `src\AiHarnessDemo\ClientApp`. Vite builds
directly into `src\AiHarnessDemo\wwwroot`, so `dotnet build`/`dotnet run`/`dotnet publish` keep producing
a single deployable ASP.NET Core host — there is no separately deployed frontend. To iterate on the
client with hot reload against a running API:

```powershell
cd src\AiHarnessDemo
dotnet run
# in a second terminal
cd src\AiHarnessDemo\ClientApp
npm ci
npm run dev
```

`npm run typecheck`, `npm test`, and `npm run build` validate the client in isolation.
