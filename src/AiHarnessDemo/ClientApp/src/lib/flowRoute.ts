import type { FlowStatus } from "../api/types";

export function flowRoute(id: string, status?: FlowStatus): string {
  return status === "Intake" || status === undefined
    ? `/intake/${id}`
    : `/factory/${id}`;
}
