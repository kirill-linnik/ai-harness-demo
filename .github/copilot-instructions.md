# AI Harness Studio

- Target .NET 10 and keep nullable reference types enabled.
- Keep the product generic and suitable for an external audience; do not add internal service, tenant, telemetry, or work-item dependencies.
- Agent definitions belong in `.github\agents` and must remain valid GitHub Copilot custom-agent Markdown.
- `WORKFLOW.md` is the strict Studio extension of the Symphony-style policy and shared prompt
  contract. Keep variables strict, reload atomically, block new work when the current file is
  invalid, and preserve each attempt's persisted revision and effective permission ceiling.
- Persist runtime state through `HarnessDbContext`; do not introduce file-shaped success fallbacks.
- Every agent execution must run through Copilot CLI.
- Execution must stay isolated per flow through `WorkspaceManager`.
- Every agent transition must remain visible in `FlowStep` and `FlowEvent`.
- Route `FlowRun.Status` changes through `FlowLifecycleCoordinator`; invalid API transitions are
  typed conflicts, and restart reconciliation must remain idempotent.
- Add focused xUnit coverage when orchestration, model selection, intake, persistence, or handoff behavior changes.
- Use Windows-style paths in documentation and PowerShell scripts.
