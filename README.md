# AI Harness Studio

AI Harness Studio is a standalone .NET 10 application that turns a customer request into one
observable, durable, dynamic multi-agent flow executed through GitHub Copilot CLI. It is an
independent adaptation of selected OpenAI Symphony ideas, not a port, drop-in replacement, or claim
of full Symphony conformance.

The host is ASP.NET Core with SQLite persistence; the dashboard is React and Vite.

## Key capabilities

- **Repository-grounded work** — Studio performs a read-only, evidence-based repository study,
  persists the resulting project knowledge, and keeps publication recaps flow-scoped until the
  published branch is integrated and the source is re-analyzed.
- **Dynamic, duty-based teams** — an Account Manager confirms the brief, then a Team Lead selects
  enabled snapshotted agents and assigns duties, dependencies, acceptance criteria, and an outcome
  owner. Duties, not agent names, define the required work.
- **Advisory and Delivery flows** — Advisory produces reviewable recommendations without source
  writes and lets the host materialize declared artifacts; Delivery works in an isolated Git
  worktree and can publish a commit or pull request only after customer acceptance.
- **Evidence-based readiness** — Delivery acceptance criteria, host-observed execution evidence, QA
  results, residual risks, waivers, and the exact sealed candidate are bound together before review,
  approval, or publication.
- **Reviewable outcomes** — customers can inspect Advisory artifacts or an immutable,
  network-disabled Delivery preview. Browser previews are sealed only from workspace-root
  `.customer-preview\<variant>` artifacts. The flow page restores **Open reviewed preview**
  immediately from the durable seal after restart, while the preview endpoint revalidates the
  exact candidate bytes before serving them. An optional loopback-only live demo is available for
  convenience but never counts as readiness evidence.
- **Durable orchestration** — plans, attempts, sessions, permissions, events, reviews, links,
  learnings, readiness, and publication state survive restart and remain visible in the dashboard.
- **Bounded agent context** — working prompts stay within 32 KiB of UTF-8; complete larger
  handoffs and evidence remain available through durable, searchable context documents. Invalid
  QA responses receive a read-only correction without discarding completed work.

## How a flow runs

1. In **Settings**, choose a **Source project** and select **Initialize and study repository**.
2. Submit a request. Creating the flow captures immutable agent definitions and enabled state.
3. The Account Manager returns a strict `intake` brief for explicit customer confirmation as
   Advisory or Delivery.
4. The Team Lead creates a validated `team plan` from that snapshot. Worker identities are optional,
   but required duties and dependency rules are enforced.
5. Agents execute sequentially in the flow workspace. Dependencies carry results forward, optional
   pre-mortem checks can challenge work, and bounded pushback can return work to an earlier
   dependency.
6. For Delivery, the final `BeforeReview` outcome owner is the sole `Verify` step and also owns
   `PrepareOutcome`. During that task, dedicated verification-preview URLs expose unreviewed
   artifacts through the real sandbox and network policy. The host then validates criterion
   results, seals the candidate, and derives Delivery readiness.
7. Customers can refine either flow kind, accept an Advisory, promote it to a fresh Delivery, or
   accept a ready Delivery. Only accepted Delivery work materializes the planned `AfterApproval`
   Publish step.

Different flows may run concurrently; steps inside one flow remain sequential.

## Runtime guarantees

- Root `WORKFLOW.md` is a strict, atomically reloaded policy and prompt definition. An invalid current
  file blocks new work without changing the last-known-good definition used by existing flows.
- Agent definitions live in `.github\agents\*.agent.md`. Each flow captures immutable definitions
  and enabled state, so catalog changes affect only future flows.
- Every agent execution runs through Copilot CLI with a host-derived permission profile:
  `ReadOnlySource`, `WorkspaceWrite`, `Publish`, or `PreMortemReadOnly`.
- `WorkspaceManager` isolates each flow. `FlowLifecycleCoordinator` guards status changes, while
  `FlowStep` and `FlowEvent` make attempts, retries, recovery, and handoffs auditable.
- Delivery publication uses sealed, reviewed Git and preview identities. The host revalidates those
  identities and readiness state before and after publishing.

These are strong controls for a trusted host, not kernel-level sandboxing against a hostile
same-user process.

## Agents

| Definition | Runtime rule |
| --- | --- |
| Account Manager | Required and always enabled for intake, refinement, and customer-safe blocker explanations. |
| Team Lead | Required and always enabled for dynamic planning. |
| Pre-mortem Sceptic | Required definition; execution can be disabled. |
| Quality Engineer | Optional and switchable. Any enabled suitable agent may carry `Verify`; selecting an independent Quality Engineer provides stronger separation from implementation. |
| All other definitions | Optional, switchable, and selected by exact snapshot ID. |

Use **Reload catalog** after editing definitions. Invalid optional definitions remain visible for
diagnostics but cannot be selected; invalid required definitions block new work.

## Run locally

Prerequisites:

- .NET 10 SDK
- Node.js 20.19+ or 22.12+
- Git
- Authenticated GitHub Copilot CLI
- Edge or Chrome only if browser speech recognition is wanted

```powershell
winget install GitHub.Copilot
copilot --version
copilot login

Set-Location <clone-directory>
dotnet run --project .\src\AiHarnessDemo
# Or:
.\Start-Demo.ps1
```

Open `http://localhost:5283`. Runtime state is stored in `data\ai-harness.db`; isolated flow
workspaces are created under `data\worktrees`.

Startup uses `Database.EnsureCreatedAsync()` to create a fresh schema when the database file is
absent. It does not update an existing schema. After entity-shape changes, stop Studio and recreate
the development database:

```powershell
Remove-Item .\data\ai-harness.db, .\data\ai-harness.db-wal, .\data\ai-harness.db-shm `
  -Force -ErrorAction SilentlyContinue
.\Start-Demo.ps1
```

This deletes local settings, flow history, reviews, and readiness records.

## Validate

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

See [`docs\ARCHITECTURE.md`](docs/ARCHITECTURE.md) for plan rules, lifecycle details, persistence,
recovery, and trust boundaries. See
[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) for Symphony attribution.
