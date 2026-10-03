import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import type { FlowMessageDto } from "../../api/types";
import { CustomerDialogue } from "./CustomerDialogue";

const timestamp = "2026-10-03T14:26:58Z";

function message(overrides: Partial<FlowMessageDto> = {}): FlowMessageDto {
  return {
    id: "customer-message",
    role: "Customer",
    content: "Make the site easier to navigate.",
    isQuestion: false,
    createdAt: timestamp,
    ...overrides
  };
}

afterEach(cleanup);

describe("CustomerDialogue", () => {
  it("shows the original submission only once within the read-only dialogue", () => {
    const originalRequest = "  Original request:\nKeep the existing content.\n\nImprove navigation.  ";
    render(
      <CustomerDialogue
        flow={{
          messages: [message({ content: originalRequest })]
        }}
      />
    );

    const section = screen.getByRole("region", { name: "Customer dialogue" });
    expect(within(section).queryByText("Original customer request")).not.toBeInTheDocument();
    fireEvent.click(within(section).getByText("Recorded dialogue (1 message)"));
    const request = within(section).getByText(/Original request:/);
    expect(
      [...request.childNodes]
        .filter(node => node.nodeType === Node.TEXT_NODE)
        .map(node => node.textContent)
        .join("")
    ).toBe(originalRequest);
    expect(request).toBeVisible();
    expect(within(section).getAllByText(/Original request:/)).toHaveLength(1);
    expect(within(section).getByText("Recorded dialogue (1 message)")).toBeInTheDocument();
    expect(screen.queryByRole("textbox")).not.toBeInTheDocument();
    expect(screen.queryByRole("button")).not.toBeInTheDocument();
  });

  it("expands saved messages chronologically with roles, timestamps, and attachments", () => {
    const messages = [
      message({
        id: "confirmation",
        content: "Yes, I confirm.",
        createdAt: "2026-10-03T14:29:00Z"
      }),
      message({
        id: "account-manager",
        role: "AccountManager",
        content: "Please confirm the proposed brief.",
        isQuestion: true,
        createdAt: "2026-10-03T14:28:00Z"
      }),
      message({
        content: "First line.\nSecond line.",
        attachments: [{
          id: "photo",
          fileName: "reference-photo.jpg",
          contentType: "image/jpeg",
          length: 2048
        }]
      })
    ];
    render(<CustomerDialogue flow={{ messages }} />);

    const summary = screen.getByText("Recorded dialogue (3 messages)");
    expect(summary.parentElement).not.toHaveAttribute("open");
    fireEvent.click(summary);
    expect(summary.parentElement).toHaveAttribute("open");

    const dialogue = screen.getByRole("list", { name: "Recorded dialogue" });
    const turns = within(dialogue).getAllByRole("listitem").filter(
      item => item.classList.contains("dialogue-turn")
    );
    expect(turns).toHaveLength(3);
    expect(turns[0]).toHaveTextContent("First line. Second line.");
    expect(turns[1]).toHaveTextContent("Account Manager");
    expect(turns[1]).toHaveTextContent("Please confirm the proposed brief.");
    expect(turns[2]).toHaveTextContent("Customer");
    expect(turns[2]).toHaveTextContent("Yes, I confirm.");
    expect(within(dialogue).getByText("reference-photo.jpg (2 KB)")).toBeVisible();
    expect(turns[0]?.querySelector("time")).toHaveAttribute("datetime", timestamp);
    expect(turns[1]?.querySelector("time")).toHaveAttribute(
      "datetime", "2026-10-03T14:28:00Z"
    );
    expect(messages[0]?.id).toBe("confirmation");
  });

  it("preserves labeled later feedback and harness messages", () => {
    render(
      <CustomerDialogue
        flow={{
          messages: [
            message({
              id: "product-feedback",
              role: "ProductManager",
              content: "Please refine the approved direction."
            }),
            message({
              id: "harness",
              role: "Harness",
              content: "The first intake attempt failed."
            })
          ]
        }}
      />
    );
    fireEvent.click(screen.getByText("Recorded dialogue (2 messages)"));

    const dialogue = screen.getByRole("list", { name: "Recorded dialogue" });
    expect(within(dialogue).getByText("Product Manager")).toBeVisible();
    expect(within(dialogue).getByText("Harness")).toBeVisible();
    expect(within(dialogue).getByText("Please refine the approved direction.")).toBeVisible();
    expect(within(dialogue).getByText("The first intake attempt failed.")).toBeVisible();
  });

  it("does not invent missing messages", () => {
    render(<CustomerDialogue flow={{ messages: [] }} />);
    fireEvent.click(screen.getByText("Recorded dialogue (0 messages)"));
    expect(screen.getByText("No customer dialogue was recorded for this flow.")).toBeVisible();
    expect(screen.queryByRole("list")).not.toBeInTheDocument();
  });

  it("renders submitted markup as text rather than executable HTML", () => {
    const content = '<img src="missing" onerror="alert(1)">\n<script>alert(1)</script>';
    const { container } = render(
      <CustomerDialogue
        flow={{ messages: [message({ content })] }}
      />
    );
    fireEvent.click(screen.getByText("Recorded dialogue (1 message)"));
    expect(screen.getAllByText(/<img src="missing"/)).toHaveLength(1);
    expect(container.querySelector("img, script")).toBeNull();
  });
});
