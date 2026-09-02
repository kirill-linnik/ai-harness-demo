# AI Harness Studio

- Target .NET 10 and keep nullable reference types enabled.
- Keep the product generic and suitable for an external audience; do not add internal service, tenant, telemetry, or work-item dependencies.
- Agent definitions belong in `.github\agents` and must remain valid GitHub Copilot custom-agent Markdown.
- `WORKFLOW.md` is the Symphony policy and shared prompt contract; keep its variables strict and its runtime values hot-reloadable.
- Persist runtime state through `HarnessDbContext`; do not introduce file-shaped success fallbacks.
- Every agent execution must run through Copilot CLI.
- Execution must stay isolated per flow through `WorkspaceManager`.
- Every agent transition must remain visible in `FlowStep` and `FlowEvent`.
- Add focused xUnit coverage when orchestration, model selection, intake, persistence, or handoff behavior changes.
- Use Windows-style paths in documentation and PowerShell scripts.
