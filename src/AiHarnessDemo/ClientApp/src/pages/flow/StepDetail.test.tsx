import { cleanup, render, screen, within } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import type { FlowStepDto } from "../../api/types";
import { StepDetail } from "./StepDetail";

const step: FlowStepDto = {
  id: "11111111-1111-1111-1111-111111111111",
  iteration: 1,
  sequence: 1,
  agentId: "team-lead",
  agentName: "Team Lead",
  agentRole: "team-lead",
  label: "Plan the delivery system",
  planStepKey: "team-plan",
  duties: ["Analyze", "Design"],
  stage: "BeforeReview",
  isOutcomeOwner: false,
  permissionProfile: "ReadOnlySource",
  effectivePermissionJson: "{\"profile\":\"ReadOnlySource\"}",
  workflowRevision: "workflow-test",
  dependencyStepIds: [],
  dependencyPlanStepKeys: [],
  model: "claude-sonnet-5",
  modelEffort: "high",
  modelReason: "Selected for cross-cutting planning.",
  taskProfile: {
    role: "team-lead",
    planStepKey: "team-plan",
    agentId: "team-lead",
    complexity: 8,
    reasoningDepth: 9,
    contextDemand: 7,
    toolIntensity: 2,
    taskTypeTags: ["Planning"],
    risk: "High",
    riskReason: "Cross-cutting delivery risk.",
    confidence: 0.8,
    rationales: ["Multiple boundaries."]
  },
  routing: {
    strategy: "MaximumQuality",
    selectedModel: "claude-sonnet-5",
    selectedEffort: "high",
    predictedQuality: 0.95,
    predictedAcceptedTimeSeconds: 70,
    predictedPremiumRequests: 1.2,
    premiumUseEstimated: true,
    confidence: 0.8,
    uncertainty: 0.2,
    exploration: false,
    reason: "Highest conservative quality.",
    rerouteCount: 0,
    alternatives: [
      {
        model: "gpt-fast",
        effort: "medium",
        rank: 1,
        predictedQuality: 0.9,
        predictedAcceptedTimeSeconds: 40,
        predictedPremiumRequests: 0.5,
        confidence: 0.6,
        reason: "Ranked behind the selected candidate."
      }
    ]
  },
  status: "Completed",
  phase: "Succeeded",
  attempt: 1,
  executionAttempts: 1,
  inputSummary: "The change crosses technical boundaries.",
  executionPrompt: "Review `WORKFLOW.md` and **plan the delivery**.",
  copilotSessionId: "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee",
  outputSummary: "## Next owner\n\n**Architect** should define the boundaries.\n\n- Document decisions\n- Hand off",
  pushbackReason: "",
  startedAt: "2026-09-02T12:00:00Z",
  completedAt: "2026-09-02T12:01:00Z",
  durationMilliseconds: 60_000,
  toolCalls: []
};

afterEach(cleanup);

describe("StepDetail", () => {
  it("uses the stacked detail layout for all multi-content callouts", () => {
    render(<StepDetail step={step} gate={undefined} />);

    for (const name of ["Execution activity", "Routing decision", "Task profile"]) {
      expect(screen.getByRole("region", { name })).toHaveClass("callout", "detail-callout");
    }
  });

  it("shows the prompt and renders the current output as Markdown", () => {
    render(<StepDetail step={step} gate={undefined} />);

    expect(screen.getByText("Prompt sent to Copilot CLI")).toBeInTheDocument();
    expect(screen.getByText("session aaaaaaaa")).toHaveAttribute(
      "title",
      "Copilot session aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"
    );
    expect(screen.getByText("WORKFLOW.md")).toHaveProperty("tagName", "CODE");
    expect(screen.getByText("plan the delivery")).toHaveProperty("tagName", "STRONG");
    expect(screen.getByRole("heading", { name: "Next owner", level: 2 })).toBeInTheDocument();
    expect(screen.getByText("Architect")).toHaveProperty("tagName", "STRONG");
    expect(screen.getByRole("list")).toBeInTheDocument();
    expect(screen.getByRole("region", { name: "Routing decision" })).toHaveTextContent(
      "95.0% conservative quality"
    );
    expect(screen.getByRole("region", { name: "Task profile" })).toHaveTextContent("High risk");
    expect(screen.getByRole("region", { name: "Task profile" })).toHaveTextContent("Multiple boundaries.");
    expect(screen.getByText("Adaptive")).toBeInTheDocument();
    expect(screen.getByText(/0.50 premium requests/)).toBeInTheDocument();
  });

  it("keeps an explicit placeholder while a running step has no output yet", () => {
    render(
      <StepDetail
        step={{ ...step, status: "Running", phase: "StreamingTurn", outputSummary: "" }}
        gate={undefined}
      />
    );

    expect(screen.getByText("Awaiting an observed response; see execution activity.")).toBeInTheDocument();
    expect(screen.getByText("Runtime activity is unavailable for this attempt.")).toBeInTheDocument();
  });

  it("shows safe structured activity, budgets, and a non-terminal soft warning", () => {
    render(<StepDetail step={{
      ...step, status: "Running", phase: "StreamingTurn", runtimeActivity: {
        observedAt: "2026-10-03T13:10:00Z",
        lastRawOutputAt: "2026-10-03T13:10:00Z",
        lastStructuredEvent: "tool.execution_start",
        lastStructuredActivityAt: "2026-10-03T13:00:00Z",
        activeTool: "powershell",
        activeToolStartedAt: "2026-10-03T13:00:00Z",
        activeToolElapsedMilliseconds: 600_000,
        completedToolCount: 12,
        totalElapsedMilliseconds: 4_200_000,
        remainingBudgetMilliseconds: 10_200_000,
        budgetDeadlineAt: "2026-10-03T16:00:00Z",
        softWarning: true,
        terminationReason: null
      }
    }} gate={undefined} />);
    const runtime = within(screen.getByRole("region", { name: "Execution activity" }));
    expect(screen.getByRole("region", { name: "Execution activity" })).toHaveClass("detail-callout");
    expect(runtime.getByRole("status")).toHaveTextContent("Execution continues");
    expect(runtime.getByText(/tool.execution_start/)).toBeInTheDocument();
    expect(runtime.getByText(/powershell/)).toBeInTheDocument();
    expect(runtime.getByText("12")).toBeInTheDocument();
    expect(runtime.getByText("Remaining absolute budget")).toBeInTheDocument();
    expect(runtime.getByText("Raw stdout/stderr activity (not progress)")).toBeInTheDocument();
  });

  it("keeps unavailable lifecycle fields explicit and shows budget exhaustion", () => {
    render(<StepDetail step={{
      ...step, phase: "BudgetExhausted", status: "Failed", runtimeActivity: {
        observedAt: "2026-10-03T16:00:00Z", lastRawOutputAt: null,
        lastStructuredEvent: null, lastStructuredActivityAt: null,
        activeTool: null, activeToolStartedAt: null, activeToolElapsedMilliseconds: null,
        completedToolCount: null, totalElapsedMilliseconds: 14_400_000,
        remainingBudgetMilliseconds: 0, budgetDeadlineAt: "2026-10-03T16:00:00Z",
        softWarning: true, terminationReason: "BudgetExhausted"
      }
    }} gate={undefined} />);
    const runtime = within(screen.getByRole("region", { name: "Execution activity" }));
    expect(runtime.getAllByText("Unavailable")).toHaveLength(4);
    expect(runtime.getByText(/Termination reason: Budget Exhausted/)).toBeInTheDocument();
    expect(runtime.getByText("0s")).toBeInTheDocument();
    expect(runtime.getByRole("status")).not.toHaveTextContent("Execution continues");
  });

  it("renders the formatted assignment as sections and lists without hiding literal brief text", () => {
    const executionPrompt = String.raw`## Assignment

### Goal

Give both sites a fresh look.

### Details

- Redesign the visual style.
- Keep \<main\> \& \*\*literal\*\* text.

### Success criteria

- Both sites use the new design.

### Constraints

- Preserve C:\\work\\logo\_v2.svg.

### Assumptions

None specified.`;
    render(<StepDetail step={{ ...step, executionPrompt }} gate={undefined} />);

    const prompt = within(screen.getByRole("region", { name: "Prompt sent to Copilot CLI" }));
    for (const name of ["Goal", "Details", "Success criteria", "Constraints", "Assumptions"]) {
      expect(prompt.getByRole("heading", { name, level: 3 })).toBeInTheDocument();
    }
    expect(prompt.getAllByRole("list")).toHaveLength(3);
    expect(prompt.getAllByRole("listitem")).toHaveLength(4);
    expect(prompt.getByText("Give both sites a fresh look.")).toBeInTheDocument();
    expect(prompt.getByText("Keep <main> & **literal** text.")).toBeInTheDocument();
    expect(prompt.getByText(String.raw`Preserve C:\work\logo_v2.svg.`)).toBeInTheDocument();
    expect(prompt.getByText("None specified.")).toBeInTheDocument();
  });

  it("does not present an older input summary as the exact execution prompt", () => {
    render(<StepDetail step={{ ...step, executionPrompt: "" }} gate={undefined} />);

    const prompt = screen.getByRole("region", { name: "Prompt sent to Copilot CLI" });
    expect(
      within(prompt).getByText("The exact prompt was not captured for this earlier handoff.")
    ).toBeInTheDocument();
    expect(within(prompt).queryByText("The change crosses technical boundaries.")).not.toBeInTheDocument();
  });

  it.each(["Failed", "Pushback"] as const)("labels a %s outcome without blaming the wrong source", status => {
    render(
      <StepDetail
        step={{ ...step, status, pushbackReason: "The evidence reference needs correction." }}
        gate={undefined}
      />
    );

    expect(screen.getByText(status === "Pushback" ? "Pushback:" : "Execution failure:")).toBeInTheDocument();
    expect(screen.queryByText(status === "Pushback" ? "Execution failure:" : "Pushback:")).not.toBeInTheDocument();
  });
});
