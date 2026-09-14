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

    expect(screen.getByText("Agent is reasoning, acting, and observing...")).toBeInTheDocument();
  });

  it("does not present an older input summary as the exact execution prompt", () => {
    render(<StepDetail step={{ ...step, executionPrompt: "" }} gate={undefined} />);

    const prompt = screen.getByRole("region", { name: "Prompt sent to Copilot CLI" });
    expect(
      within(prompt).getByText("The exact prompt was not captured for this earlier handoff.")
    ).toBeInTheDocument();
    expect(within(prompt).queryByText("The change crosses technical boundaries.")).not.toBeInTheDocument();
  });
});
