import { describe, expect, it } from "vitest";
import {
  formatDuration,
  groupBy,
  initials,
  lastPathPart,
  splitWords,
  statusClass,
  statusLabel,
  timeAgo
} from "./format";

describe("initials", () => {
  it("takes the first letter of up to two words", () => {
    expect(initials("Account Manager")).toBe("AM");
    expect(initials("Harness")).toBe("H");
  });

  it("falls back to the default seed for empty input, taking only its first letter", () => {
    // Matches the original app.js behavior exactly: `String(name || "AI")` treats "AI" as a
    // single word, so only its first letter survives the two-word-initial slice.
    expect(initials("")).toBe("A");
    expect(initials(null)).toBe("A");
  });
});

describe("splitWords", () => {
  it("splits camelCase and kebab-case", () => {
    expect(splitWords("WaitingForFeedback")).toEqual(["Waiting", "For", "Feedback"]);
    expect(splitWords("role-based-access")).toEqual(["role", "based", "access"]);
  });
});

describe("lastPathPart", () => {
  it("returns the last segment for both separators", () => {
    expect(lastPathPart("C:\\repos\\demo")).toBe("demo");
    expect(lastPathPart("/home/user/project")).toBe("project");
  });

  it("falls back to Repository when empty", () => {
    expect(lastPathPart("")).toBe("Repository");
    expect(lastPathPart(null)).toBe("Repository");
  });
});

describe("groupBy", () => {
  it("groups items by the selector key", () => {
    const items = [
      { iteration: 1, id: "a" },
      { iteration: 2, id: "b" },
      { iteration: 1, id: "c" }
    ];
    const grouped = groupBy(items, item => item.iteration);
    expect(grouped[1].map(item => item.id)).toEqual(["a", "c"]);
    expect(grouped[2].map(item => item.id)).toEqual(["b"]);
  });
});

describe("formatDuration", () => {
  it("renders an em dash for zero or missing durations", () => {
    expect(formatDuration(0)).toBe("—");
    expect(formatDuration(null)).toBe("—");
  });

  it("renders seconds under a minute", () => {
    expect(formatDuration(1500)).toBe("1.5 sec");
  });

  it("renders minutes at or above a minute", () => {
    expect(formatDuration(125_000)).toBe("2.1 min");
  });
});

describe("timeAgo", () => {
  it("reports 'just now' for very recent timestamps", () => {
    expect(timeAgo(new Date().toISOString())).toBe("just now");
  });

  it("reports relative minutes in the past", () => {
    const fiveMinutesAgo = new Date(Date.now() - 5 * 60 * 1000).toISOString();
    expect(timeAgo(fiveMinutesAgo)).toBe("5 minutes ago");
  });
});

describe("statusClass", () => {
  it("lowercases and strips underscores", () => {
    expect(statusClass("WaitingForFeedback")).toBe("waitingforfeedback");
    expect(statusClass(null)).toBe("");
  });
});

describe("statusLabel", () => {
  it("uses the known label map first", () => {
    expect(statusLabel("WaitingForFeedback")).toBe("Awaiting feedback");
    expect(statusLabel("PullRequest")).toBe("Pull request");
  });

  it("falls back to split words for unknown statuses", () => {
    expect(statusLabel("Completed")).toBe("Completed");
    expect(statusLabel("Pushback")).toBe("Pushback");
  });
});
