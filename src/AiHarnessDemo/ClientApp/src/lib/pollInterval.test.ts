import { describe, expect, it } from "vitest";
import { flowPollIntervalMs } from "./pollInterval";

describe("flowPollIntervalMs", () => {
  it("polls every second while a flow is queued, running, or reworking", () => {
    expect(flowPollIntervalMs("Queued")).toBe(1000);
    expect(flowPollIntervalMs("Running")).toBe(1000);
    expect(flowPollIntervalMs("Reworking")).toBe(1000);
    expect(flowPollIntervalMs("Abandoning")).toBe(1000);
  });

  it("stops polling for terminal or feedback-waiting statuses", () => {
    expect(flowPollIntervalMs("Intake")).toBe(false);
    expect(flowPollIntervalMs("WaitingForFeedback")).toBe(false);
    expect(flowPollIntervalMs("Approved")).toBe(false);
    expect(flowPollIntervalMs("Abandoned")).toBe(false);
    expect(flowPollIntervalMs("Failed")).toBe(false);
  });

  it("stops polling when there is no flow yet", () => {
    expect(flowPollIntervalMs(undefined)).toBe(false);
  });
});
