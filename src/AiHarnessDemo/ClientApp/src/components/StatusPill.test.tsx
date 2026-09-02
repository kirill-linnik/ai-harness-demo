import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import { StatusPill } from "./StatusPill";

describe("StatusPill", () => {
  it("renders a humanized label and a css-safe status class", () => {
    render(<StatusPill status="WaitingForFeedback" />);
    const pill = screen.getByText("Awaiting feedback");
    expect(pill).toHaveClass("status-pill", "waitingforfeedback");
  });

  it("falls back to split words for statuses without a custom label", () => {
    render(<StatusPill status="Approved" />);
    expect(screen.getByText("Approved")).toHaveClass("status-pill", "approved");
  });
});
