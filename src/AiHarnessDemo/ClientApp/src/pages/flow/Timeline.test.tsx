import { cleanup, render, screen, within } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import type { FlowEventDto } from "../../api/types";
import { Timeline } from "./Timeline";

const stepId = "11111111-1111-4111-8111-111111111111";

afterEach(cleanup);

function event(
  id: string,
  createdAt: string,
  type: string,
  message: string,
  flowStepId: string | null = stepId
): FlowEventDto {
  return { id, flowStepId, type, message, dataJson: null, createdAt };
}

describe("Timeline", () => {
  it("renders causal order, attempt context, and grouped runtime detail", () => {
    const events = [
      event(
        "44444444-4444-4444-8444-444444444444",
        "2026-09-12T18:51:32Z",
        "flow.failed",
        "Quality Engineer could not continue because the acceptance criteria were truncated.",
        null
      ),
      event(
        "33333333-3333-4333-8333-333333333333",
        "2026-09-12T18:51:31Z",
        "learning.pushback-recorded",
        "The harness recorded a future prompt refinement."
      ),
      event(
        "22222222-2222-4222-8222-222222222222",
        "2026-09-12T18:51:01Z",
        "agent.StreamingTurn",
        "powershell completed."
      ),
      event(
        "21111111-1111-4111-8111-111111111111",
        "2026-09-12T18:51:00Z",
        "agent.StreamingTurn",
        "Using powershell."
      ),
      event(
        "11111111-1111-4111-8111-111111111111",
        "2026-09-12T18:46:40Z",
        "step.started",
        "Quality Engineer started."
      )
    ];

    render(
      <Timeline
        events={events}
        steps={[{ id: stepId, agentName: "Quality Engineer", attempt: 3 }]}
      />
    );

    const ledgerItems = within(
      screen.getByRole("list", { name: "Execution ledger, oldest to newest" })
    ).getAllByRole("listitem");
    expect(ledgerItems).toHaveLength(4);
    expect(within(ledgerItems[0]!).getByText("Agent step started")).toBeInTheDocument();
    expect(within(ledgerItems[3]!).getByText("Flow stopped")).toBeInTheDocument();

    const runtime = screen
      .getByText("Quality Engineer runtime activity")
      .closest("details");
    expect(runtime).not.toHaveAttribute("open");
    expect(within(runtime!).getByText("attempt 3")).toBeInTheDocument();
    expect(within(runtime!).getByText("2 runtime updates")).toBeInTheDocument();

    const learning = screen.getByText("Learning recorded").closest("li");
    expect(learning).toHaveClass("learning");
    expect(learning).not.toHaveClass("failed");
    expect(screen.getAllByText("Quality Engineer · attempt 3")).toHaveLength(2);
  });

  it("collapses long event details behind a concise summary", () => {
    const longReason = `The verification context was incomplete. ${"Missing evidence. ".repeat(30)}`;
    const { container } = render(
      <Timeline
        events={[
          event(
            "55555555-5555-4555-8555-555555555555",
            "2026-09-12T18:51:32Z",
            "flow.failed",
            longReason,
            null
          )
        ]}
        steps={[]}
      />
    );

    const detail = container.querySelector("details");
    expect(detail).not.toBeNull();
    expect(detail).not.toHaveAttribute("open");
    expect(detail).toHaveTextContent("The verification context was incomplete.");
    expect(detail).toHaveTextContent("Missing evidence.");
  });
});
