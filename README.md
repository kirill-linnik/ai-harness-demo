# AI Harness Studio

AI Harness Studio is a standalone .NET 10 conference demo that turns a spoken product idea into an observable multi-agent engineering flow.

It is intentionally generic and suitable for an external audience. Its core is a C# implementation of the [OpenAI Symphony service specification](https://github.com/openai/symphony/blob/main/SPEC.md). It retains repository-owned workflow policy, durable orchestration, explicit gates, isolated workspaces, lifecycle hooks, Copilot CLI execution, retry and reconciliation, and a complete ledger without provider-specific operational dependencies.

## Run

Prerequisites:

- .NET 10 SDK
- GitHub Copilot CLI
- Git
- Edge or Chrome for browser speech recognition

```powershell
cd <clone-directory>
dotnet run --project .\src\AiHarnessDemo
```

Or:

```powershell
.\Start-Demo.ps1
```

Open `http://localhost:5283`.

## Rehearsal path

1. Open **Settings**, enable the agents you want Team Lead to consider, and choose a local source repository.
2. Leave **Run Copilot init** selected, then choose **Initialize and study repository**. Review and edit the generated shared knowledge.
3. Open **AI Factory**, click **Listen to the next task**, and speak the idea.
4. Answer Account Manager's clarification, send the brief to Team Lead, and watch the execution graph.
5. Open the customer preview, speak feedback, then approve the result or start another iteration.
6. Show **Execution history** and **Harness memory** to explain model routing and cross-flow learning.

The AI Factory and every new-assignment control remain locked until Settings contains a studied Git repository.

## Safety and persistence

- All application state is stored in `data\ai-harness.db`.
- `WORKFLOW.md` is the hot-reloadable, version-controlled Symphony policy and prompt contract.
- Agent definitions are loaded from `.github\agents\*.agent.md`.
- Copilot CLI receives tool access only inside `data\worktrees\<flow-id>`; the selected source must be a Git repository.
- Multiple flows can run in parallel; every flow receives an independent worktree and branch.
- No cloud credentials, private endpoints, or provider-specific work-item integrations are included.

See [`docs\ARCHITECTURE.md`](docs\ARCHITECTURE.md) for the flow and component model.
See [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) for Symphony attribution.
