import type { FlowStatus, OutcomeType, StepStatus } from "../api/types";

/** Ports the small pure helpers from the original wwwroot/app.js verbatim. */

export function initials(name: string | null | undefined): string {
  return String(name || "AI")
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map(word => word[0]!.toUpperCase())
    .join("");
}

export function splitWords(value: string | null | undefined): string[] {
  return String(value || "")
    .replace(/([a-z])([A-Z])/g, "$1 $2")
    .replaceAll("-", " ")
    .split(/\s+/)
    .filter(Boolean);
}

export function lastPathPart(path: string | null | undefined): string {
  const parts = String(path || "").split(/[\\/]/).filter(Boolean);
  return parts.at(-1) || path || "Repository";
}

export function groupBy<T, K extends PropertyKey>(
  items: readonly T[],
  selector: (item: T) => K
): Record<K, T[]> {
  return items.reduce(
    (groups, item) => {
      const key = selector(item);
      (groups[key] ??= []).push(item);
      return groups;
    },
    {} as Record<K, T[]>
  );
}

export function formatDuration(milliseconds: number | null | undefined): string {
  const value = Number(milliseconds || 0);
  if (value <= 0) return "—";
  if (value < 60_000) return `${Math.max(0.1, value / 1000).toFixed(1)} sec`;
  return `${(value / 60_000).toFixed(1)} min`;
}

export function timeAgo(value: string | number | Date): string {
  const date = new Date(value);
  const seconds = Math.round((Date.now() - date.getTime()) / 1000);
  if (Math.abs(seconds) < 45) return "just now";
  const units: [Intl.RelativeTimeFormatUnit, number][] = [
    ["year", 31_536_000],
    ["month", 2_592_000],
    ["day", 86_400],
    ["hour", 3_600],
    ["minute", 60]
  ];
  const formatter = new Intl.RelativeTimeFormat("en", { numeric: "auto" });
  for (const [unit, size] of units) {
    if (Math.abs(seconds) >= size) return formatter.format(-Math.round(seconds / size), unit);
  }
  return formatter.format(-seconds, "second");
}

const STATUS_LABELS: Partial<Record<FlowStatus | StepStatus | OutcomeType, string>> = {
  WaitingForFeedback: "Awaiting feedback",
  PullRequest: "Pull request"
};

export function statusLabel(status: string | null | undefined): string {
  const known = status ? STATUS_LABELS[status as FlowStatus | StepStatus | OutcomeType] : undefined;
  return known ?? splitWords(status).join(" ");
}

export function statusClass(status: string | null | undefined): string {
  return String(status || "").toLowerCase().replaceAll("_", "");
}
