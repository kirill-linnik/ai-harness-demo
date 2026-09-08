import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it } from "vitest";
import type { FlowSummaryDto } from "../../api/types";
import { FlowCard } from "./FlowCard";

const timestamp = "2026-09-06T12:00:00Z";

function summary(overrides: Partial<FlowSummaryDto> = {}): FlowSummaryDto {
  return {
    id: "11111111-1111-4111-8111-111111111111",
    title: "Assess checkout resilience",
    kind: "Advisory",
    contractVersion: "studio-v2",
    parentFlowRunId: "22222222-2222-4222-8222-222222222222",
    parentIteration: 2,
    linkKind: "QualificationScopeRevision",
    currentBlockerCode: "missing-qualification",
    customerBlockerMessage: "A qualified specialist is not currently available.",
    review: {
      gateId: null,
      available: false,
      resolved: false,
      approved: null,
      decision: null,
      publicationStatus: "NotApplicable"
    },
    linkedFlows: [],
    status: "Blocked",
    iteration: 1,
    agentCatalogRevision: "catalog-test",
    repositoryPath: "E:\\projects\\demo",
    outcomeLabel: "Qualification needed",
    outcomeUrl: "",
    createdAt: timestamp,
    updatedAt: timestamp,
    ...overrides
  };
}

afterEach(cleanup);

describe("FlowCard", () => {
  it("links an Intake flow to its conversation", () => {
    render(
      <MemoryRouter>
        <FlowCard
          flow={summary({
            status: "Intake",
            currentBlockerCode: null,
            customerBlockerMessage: null
          })}
        />
      </MemoryRouter>
    );

    expect(screen.getByRole("link")).toHaveAttribute(
      "href",
      "/intake/11111111-1111-4111-8111-111111111111"
    );
  });

  it("shows kind, Blocked state, and parent relationship", () => {
    render(
      <MemoryRouter>
        <FlowCard flow={summary()} />
      </MemoryRouter>
    );

    expect(screen.getByText("Advisory")).toBeInTheDocument();
    expect(screen.getByText("Blocked")).toBeInTheDocument();
    expect(screen.getByText(/Qualification Scope Revision from 22222222/)).toBeInTheDocument();
    expect(screen.getByText("A qualified specialist is not currently available.")).toBeInTheDocument();
  });

  it("shows accepted and published Delivery state without inventing authority", () => {
    render(
      <MemoryRouter>
        <FlowCard
          flow={summary({
            kind: "Delivery",
            status: "Approved",
            parentFlowRunId: null,
            parentIteration: null,
            linkKind: null,
            currentBlockerCode: null,
            customerBlockerMessage: null,
            outcomeLabel: "Published pull request",
            review: {
              gateId: "33333333-3333-4333-8333-333333333333",
              available: true,
              resolved: true,
              approved: true,
              decision: "Accepted",
              publicationStatus: "Published"
            }
          })}
        />
      </MemoryRouter>
    );

    expect(screen.getByText("Delivery")).toBeInTheDocument();
    expect(screen.getByText("Accepted")).toBeInTheDocument();
    expect(screen.getByText("Publication published")).toBeInTheDocument();
  });
});
