# AI Harness Studio

AI Harness Studio is a standalone .NET 10 application that turns a customer request into an
observable, durable multi-agent flow executed through GitHub Copilot CLI. It is an independent
adaptation of selected OpenAI Symphony ideas, not a port, drop-in replacement, or claim of full
Symphony conformance.

The host is ASP.NET Core with SQLite persistence; the dashboard is React and Vite.

## Key capabilities

- **Repository-grounded work** — Studio performs a read-only, evidence-based repository study,
  persists the resulting project knowledge, and refreshes it after an accepted Delivery when the
  implementation changes that knowledge.
- **Dynamic agent teams** — an Account Manager confirms the brief, then a Team Lead selects enabled
  repository-defined agents, duties, dependencies, acceptance criteria, and an outcome owner.
- **Advisory and Delivery flows** — Advisory produces reviewable recommendations without source
  writes; Delivery works in an isolated Git worktree and can publish a commit or pull request only
  after customer acceptance.
- **Evidence-based readiness** — Delivery acceptance criteria, host-observed execution evidence, QA
  results, residual risks, waivers, and the exact reviewed candidate are bound together before
  approval or publication.
- **Reviewable outcomes** — customers can inspect Advisory artifacts or an immutable,
  network-disabled Delivery preview. An optional loopback-only live demo is available for
  convenience but never counts as readiness evidence.
- **Durable orchestration** — plans, attempts, sessions, permissions, events, reviews, links,
  learnings, readiness, and publication state survive restart and remain visible in the dashboard.

## How a flow runs

1. Select and study a source repository, then submit a request.
2. Account Manager returns a strict `intake-v2` brief for customer confirmation as Advisory or
   Delivery.
3. Team Lead creates a validated `team-plan-v1` from the flow's immutable agent snapshot.
4. Agents execute sequentially in the flow's isolated workspace. Dependencies carry results
   forward, optional pre-mortem checks can challenge work, and every transition is persisted.
5. The outcome owner returns `flow-outcome-v1`. Customers can refine either flow kind, accept an
   Advisory, promote it to a fresh Delivery, or approve a ready Delivery for host-controlled
   publication.

Different flows may run concurrently; steps inside one flow remain sequential.

## Runtime guarantees

- Root `WORKFLOW.md` is a strict, atomically reloaded policy and prompt contract. An invalid current
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

See [`docs\ARCHITECTURE.md`](docs/ARCHITECTURE.md) for contracts, lifecycle details, persistence,
recovery, and trust boundaries. See
[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) for Symphony attribution.
