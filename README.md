# AI Harness Studio

AI Harness Studio is a standalone .NET 10 conference demo that turns a spoken product idea into an observable multi-agent engineering flow.

It is intentionally generic and suitable for an external audience. Its core is a C# implementation of the [OpenAI Symphony service specification](https://github.com/openai/symphony/blob/main/SPEC.md). It retains repository-owned workflow policy, durable orchestration, explicit gates, isolated workspaces, lifecycle hooks, Copilot CLI execution, retry and reconciliation, and a complete ledger without provider-specific operational dependencies.

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
5. Open the customer preview, speak feedback, then approve the result or start another iteration.
6. Show **Execution history** and **Harness memory** to explain model routing and cross-flow learning.

The AI Factory and every new-assignment control remain locked until Settings contains a studied project folder with at least one Git repository.

## Safety and persistence

- All application state is stored in `data\ai-harness.db`.
- Model catalogs, normalized task profiles, routing decisions/alternatives, and normalized outcome
  evidence are stored in SQLite. Prompts and repository content are not copied into routing evidence.
- `WORKFLOW.md` is the hot-reloadable, version-controlled Symphony policy and prompt contract.
- Agent prompts lead with the role-specific assignment and include only compact repository facts, relevant immediate handoffs, applicable learned constraints, and the completion contract; retry diagnostics remain in the execution ledger.
- Agent definitions are loaded from `.github\agents\*.agent.md`.
- Copilot CLI receives explicit project access to `data\worktrees\<flow-id>`; each Git repository discovered in the selected project is materialized there as an isolated worktree, and source-folder paths remain intentionally inaccessible.
- An explicit agent `PUSHBACK` is persisted as a rejected handoff, resumes the responsible upstream agent's Copilot session, then retries the blocked agent in its existing session.
- Copilot session IDs are persisted per step. After a host restart, completed CLI turns are recovered from the session journal; interrupted turns resume the same session and preserved workspace instead of starting over.
- Each step is routed immediately before execution across the currently discovered model + reasoning
  effort candidates. The step detail explains predictions, confidence, exploration, and rejected
  alternatives. A model-unavailable error is classified separately; automatic rerouting fails closed
  when the CLI result cannot prove that neither session nor tool activity began.
- A failed flow can be manually restarted from its detail page. The failed attempt remains in history, while a new retry resumes its Copilot session when available and re-queues downstream work in the same workspace.
- The handoff retry limit is persisted in Settings (`0-10`, default `2`); only an exhausted limit or an unroutable pushback stops the flow and skips downstream steps.
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
