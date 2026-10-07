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
  `.customer-preview\<variant>` artifacts. Before review opens, the host copies the verified files
  into content-addressed SQLite records bound to the candidate fingerprint. The flow page and
  preview endpoint serve those immutable bytes after restart without rescanning the worktree. An
  optional loopback-only live demo is available for convenience but never counts as readiness
  evidence.
- **Repairable preview handoffs** — before substantive browser QA, Studio prepares an unambiguous
  repository-local preview at the canonical workspace root, recording its origin and hashes before
  copying. Missing or ambiguous output is sent back to its upstream implementation owner through
  bounded pushback, rather than discovered only after a long verification run. Changed preview
  inputs invalidate prior evidence and require fresh verification through the actual Studio sandbox.
- **Durable orchestration** — plans, attempts, sessions, permissions, events, reviews, links,
  learnings, readiness, and publication state survive restart and remain visible in the dashboard.
- **Separate execution artifacts** — in a project containing multiple repositories, new files
  outside the registered product repositories and trusted scaffold are tool/agent artifacts, not
  deliverables. Studio catalogs their paths and digests in SQLite, copies files up to 8 MiB into
  durable storage, and retains workspace files for downstream evidence references. Larger files
  remain integrity-checked workspace references rather than bloating the database or stopping
  the flow. No tool-name, filename, or extension allowlist is needed; artifacts cannot block QA
  or enter the reviewed candidate. Product additions belong inside a registered repository;
  repository-local generated output follows that repository's Git ignore rules.
- **Recorded customer dialogue** — every flow page includes a read-only **Customer dialogue**
  section with expandable, timestamped text messages, including the original customer message,
  including Account Manager clarifications, customer confirmation, later refinements, and uploaded
  filenames. The conversation remains available after intake finishes; it does not restart intake
  or reconstruct missing history from agent handoffs.
- **Bounded agent context** — working prompts stay within 32 KiB of UTF-8; complete larger
  handoffs and evidence remain available through durable, searchable context documents. Invalid
  QA responses receive a read-only correction without discarding completed work.
- **Customer files without folder access** — intake messages can upload files (up to eight per
  message, 8 MiB per file, 16 MiB per flow). Studio stores their bytes in SQLite, shows the
  filenames in the conversation, and stages immutable per-attempt copies for the Account Manager
  and downstream agents. An outside-project path alone prompts an upload, not access to a whole
  personal folder. Incidental fields in source material are not public requirements without
  explicit customer authorization.

## How a flow runs

1. In **Settings**, choose a **Source project** and select **Initialize and study repository**.
2. Submit a request. Creating the flow captures immutable agent definitions and enabled state.
3. The Account Manager returns a strict `intake` brief for explicit customer confirmation as
   Advisory or Delivery. Attach outside-project source files to the intake conversation instead
   of relying on a pathname; the exact submitted text and uploaded bytes remain available to
   downstream agents even when the brief is concise.
4. The Team Lead creates a validated `team plan` from that snapshot. Worker identities are optional,
   but required duties and dependency rules are enforced.
5. Agents execute sequentially in the flow workspace. Dependencies carry results forward, optional
   pre-mortem checks can challenge requirements before design or implementation, and bounded
   pushback can return work to an earlier dependency. Medium-or-higher-risk design or
   implementation, or new Delivery using uploaded customer files, gets a requirements
   checkpoint when the sceptic is available; routine Low-risk work without uploads can skip it.
6. For Delivery, the final `BeforeReview` outcome owner is the sole `Verify` step and also owns
   `PrepareOutcome`. During that task, dedicated verification-preview URLs expose unreviewed
   artifacts through the real sandbox and network policy. The host then validates criterion
   results, seals the candidate, and derives Delivery readiness. Browser-visible changes must
   also work in the production renderer; a standalone preview is not a substitute. Pre-mortem
   advice never substitutes for this final QA.
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
- A source project may be a Git repository, a folder containing several repositories, a folder
  inside a larger repository, or a folder without source control. Studio fetches each available
  `origin` `main` or `master` (preferring the remote default when it is one of those branches).
  New Delivery branches and guarded intake/Advisory snapshots use those commits without pulling
  into source checkouts. Repositories without a remote use local `main` or `master`. For a selected
  folder inside a larger repository, Delivery retains the parent Git history but limits reviewed
  changes to that folder. A folder without Git uses its present files; Delivery initializes and
  commits a baseline **only in its isolated workspace**, with commit rather than PR publication.
  If a remote cannot be checked or fetched, new preparation stops. Existing Delivery work resumes
  on its owned flow branch without fetching, merging, or rebasing advancing upstream changes;
  branch and Git common-directory identity are revalidated. Selected-folder baselines and guarded
  snapshots remain fixed to their creation-time source. Publication still requires the exact
  sealed and reviewed candidate.
- Delivery publication uses sealed, reviewed Git and preview identities. The host revalidates those
  identities and readiness state before and after publishing. For a pull-request outcome, a
  repository whose reviewed commit already matches the remote default branch, or whose verified
  comparison has no file changes, is recorded without opening an empty or unrelated pull request.
  New pull requests describe the confirmed outcome in readable Markdown rather than pasting the
  brief JSON; verification identifiers are available in a collapsible section.

These are strong controls for a trusted host, not kernel-level sandboxing against a hostile
same-user process.

Execution now separates structured inactivity (five minutes), bounded silent tools (30 minutes),
a non-terminating one-hour warning, and a finite four-hour assignment budget shared across retries
and restarts. The flow page exposes safe observed activity, remaining budget, and termination
reasons. Legacy timeout keys require explicit migration; see
[`docs\ARCHITECTURE.md`](docs/ARCHITECTURE.md#execution-timeout-policy-and-activity).

Recoverable pre-review runtime failures continue automatically from preserved work after bounded
backoff, with durable scheduling across restarts. The existing **Max handoff retries** setting also
caps factory continuations per assignment; repeated failures without new host-observed evidence
stop earlier. Continuations share the original absolute deadline and cannot gain permissions.
Publication, unsafe or unresumable interruptions, unavailable models/dependencies, and exhausted
budgets are not blindly retried. Missing preview handoffs route back to the accepted implementation
owner even if the verifier ignores the blocker. Required behavior must be exercised and its effect
asserted; screenshots prove rendering, not interaction. Unresolved work remains preserved with an
explicit blocker, never presented as a successful customer result.

After a format-only QA correction fails, a complete, scope-valid defect report can still seed
bounded implementation-owner repair. Its verification claims remain rejected; fresh QA must pass
the full evidence contract before review. Known failed criteria are repaired even when other
criteria are blocked, while external-only blockers still require resolution.
Verification after host-directed implementation repair is a distinct, finite assignment for the
repaired candidate. Its budget starts when verification launches, rather than expiring while the
owner repairs the product. Old deadlines remain unchanged, and retries of the new verification
share its deadline; neither permissions nor repair limits increase.
An operator can explicitly scope distinct remaining work after an exhausted pre-review worker
through `POST /api/flows/{id}/remaining-work` with an `Assignment` of at most 2,000 characters.
This does not restart the old task: it preserves its deadline, records the new scope, starts a
fresh CLI session, caps the new budget at 45 minutes and the original policy ceiling, and rejects
duplicate scopes or assignments beyond the configured handoff limit. Approval and publication
cannot be bypassed.

## Agents

| Definition | Runtime rule |
| --- | --- |
| Account Manager | Required and always enabled for intake, refinement, and customer-safe blocker explanations. |
| Team Lead | Required and always enabled for dynamic planning. |
| Pre-mortem Sceptic | Required definition; execution can be disabled. |
| Quality Engineer | Optional and switchable. Any enabled suitable agent may carry `Verify`; selecting an independent Quality Engineer provides stronger separation from implementation. |
| All other definitions | Optional, switchable, and selected by exact snapshot ID. |

Agent definitions in `.github\agents` are project-agnostic role contracts, not a place for facts
from one customer or repository. Each execution can consult its complete, durable flow input,
but should use and hand off only what its assigned duty and confirmed outcome require.
Response schemas, markers, budgets, evidence identifiers, artifact paths, and lifecycle rules
are supplied by the harness, not embedded in editable agents. Planning corrections repeat the
complete host-owned contract instead of depending on role prose or a partial earlier reply.

Use **Reload catalog** after editing definitions. Invalid optional definitions remain visible for
diagnostics but cannot be selected; invalid required definitions block new work.

The [agent quality contracts](docs/AGENT-QUALITY.md) describe the team's outcome-first standards,
research basis, and limitations. Visual delivery requires baseline comparison, explicit content
consolidation, concrete design decisions, and independent checks of the actual customer experience;
successful builds and overflow checks alone are not proof of a good result.

## Run locally

Prerequisites:

- .NET 10 SDK
- Node.js 20.19+ or 22.12+
- Git
- Authenticated GitHub Copilot CLI
- For **Pull request** Delivery only: GitHub CLI (`gh`) installed on the Studio server's `PATH`
  and authenticated **for that process** (`gh auth login -h github.com` as the Studio user, or
  `GH_TOKEN` in its environment). A token visible in a separate terminal does not necessarily
  reach Studio. Restart Studio after installing `gh` or changing its `PATH` or environment;
  Studio checks both CLI availability and authentication before confirming a Delivery brief and
  again before customer acceptance. Advisory and local **Commit** outcomes do not need `gh`.
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

### MCP tools

Configure MCP servers if you want additional tools for your agents. Studio uses the standard
[Copilot CLI MCP configuration](https://docs.github.com/copilot/how-tos/copilot-cli)
and is MCP-provider agnostic. Set `copilot.mcp_config_file` in `WORKFLOW.md` to your
repository-owned JSON file; omit the setting to run without additional configured servers.
Choose whichever MCP servers suit your project. Keep credentials out of committed configuration.
List each server's native tool names explicitly; wildcard tool lists are not supported.

Copilot CLI starts and operates the configured servers. Studio preserves its execution restrictions,
records tool calls, and snapshots effective configuration for each attempt. Configuration changes
apply to new attempts; invalid configuration blocks new work rather than silently enabling tools.
MCP calls use the same persisted per-step tool history and success/failure chips as other agent
tools, retaining their native names and redacted arguments. Structured MCP errors remain failures.

Startup uses `Database.EnsureCreatedAsync()` to create a fresh schema when the database file is
absent. The additive reviewed-preview and customer-upload tables are created automatically for
existing databases; other entity-shape changes are not migrated. After other shape changes, stop
Studio and recreate the development database:

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
