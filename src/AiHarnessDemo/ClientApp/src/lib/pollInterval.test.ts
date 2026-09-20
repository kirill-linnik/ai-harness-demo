import { describe, expect, it } from "vitest";
import { flowDetailPollIntervalMs, flowPollIntervalMs } from "./pollInterval";

describe("flowPollIntervalMs", () => {
  it("polls every second only while a flow is machine-active", () => {
    expect(flowPollIntervalMs("Intake")).toBe(1000);
    expect(flowPollIntervalMs("Queued")).toBe(1000);
    expect(flowPollIntervalMs("Running")).toBe(1000);
    expect(flowPollIntervalMs("Reworking")).toBe(1000);
    expect(flowPollIntervalMs("Abandoning")).toBe(1000);
  });

  describe("flowDetailPollIntervalMs", () => {
    it("keeps polling a Delivery review until its verified preview link is ready", () => {
      expect(flowDetailPollIntervalMs("WaitingForFeedback", "Delivery", null)).toBe(5000);
      expect(
        flowDetailPollIntervalMs(
          "WaitingForFeedback",
          "Delivery",
          "#/preview/flow-id"
        )
      ).toBe(false);
    });

    it("does not poll settled Advisory flows for a preview", () => {
      expect(flowDetailPollIntervalMs("WaitingForFeedback", "Advisory", null)).toBe(false);
    });

    it("retains the active flow cadence", () => {
      expect(flowDetailPollIntervalMs("Running", "Delivery", null)).toBe(1000);
    });
  });

  it("stops polling for durable human and terminal statuses", () => {
    expect(flowPollIntervalMs("WaitingForFeedback")).toBe(false);
    expect(flowPollIntervalMs("Blocked")).toBe(false);
    expect(flowPollIntervalMs("Approved")).toBe(false);
    expect(flowPollIntervalMs("Abandoned")).toBe(false);
    expect(flowPollIntervalMs("Failed")).toBe(false);
  });

  it("stops polling when there is no flow yet", () => {
    expect(flowPollIntervalMs(undefined)).toBe(false);
  });
});
