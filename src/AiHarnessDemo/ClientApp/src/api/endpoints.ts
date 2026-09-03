// One typed function per DemoApi endpoint (see Api/DemoApi.cs). Kept separate from
// api/queries.ts so the raw HTTP surface and the React Query bindings can be tested
// and reasoned about independently.
import { request } from "./client";
import type {
  AnalyzeRepositoryRequest,
  AnalyzeRepositoryResponse,
  BootstrapDto,
  DirectoryListingDto,
  FeedbackRequest,
  FeedbackResponse,
  FlowDetailDto,
  FlowSummaryDto,
  HistoryItemDto,
  IntakeRequest,
  IntakeResponse,
  LearningDto,
  PreviewDto,
  SaveSettingsRequest,
  SettingsDto,
  ToggleAgentRequest
} from "./types";

export const api = {
  bootstrap: () => request<BootstrapDto>("/api/bootstrap"),
  settings: () => request<SettingsDto>("/api/settings"),
  saveSettings: (body: SaveSettingsRequest) =>
    request<SettingsDto>("/api/settings", { method: "PUT", body }),
  toggleAgent: (agentId: string, body: ToggleAgentRequest) =>
    request(`/api/agents/${encodeURIComponent(agentId)}`, { method: "PUT", body }),
  listDirectories: (path: string) => {
    const query = path ? `?path=${encodeURIComponent(path)}` : "";
    return request<DirectoryListingDto>(`/api/directories${query}`);
  },
  analyzeRepository: (body: AnalyzeRepositoryRequest) =>
    request<AnalyzeRepositoryResponse>("/api/repositories/analyze", { method: "POST", body }),
  continueIntake: (body: IntakeRequest) =>
    request<IntakeResponse>("/api/intake", { method: "POST", body }),
  flows: () => request<FlowSummaryDto[]>("/api/flows"),
  flow: (flowId: string) => request<FlowDetailDto>(`/api/flows/${flowId}`),
  startFlow: (flowId: string) =>
    request<FlowDetailDto>(`/api/flows/${flowId}/start`, { method: "POST", body: {} }),
  restartFlow: (flowId: string) =>
    request<FlowDetailDto>(`/api/flows/${flowId}/restart`, { method: "POST", body: {} }),
  sendFeedback: (flowId: string, body: FeedbackRequest) =>
    request<FeedbackResponse>(`/api/flows/${flowId}/feedback`, { method: "POST", body }),
  decideFlow: (flowId: string, approve: boolean) =>
    request<FlowDetailDto>(`/api/flows/${flowId}/decision`, {
      method: "POST",
      body: { approve }
    }),
  history: () => request<HistoryItemDto[]>("/api/history"),
  learnings: () => request<LearningDto[]>("/api/learnings"),
  preview: (flowId: string) => request<PreviewDto>(`/api/previews/${flowId}`)
};
