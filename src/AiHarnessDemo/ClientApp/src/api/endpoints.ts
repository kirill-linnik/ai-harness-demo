// One typed function per DemoApi endpoint (see Api/DemoApi.cs). Kept separate from
// api/queries.ts so the raw HTTP surface and the React Query bindings can be tested
// and reasoned about independently.
import { request } from "./client";
import type {
  AnalyzeRepositoryRequest,
  AnalyzeRepositoryResponse,
  AbandonFlowResponse,
  AgentDto,
  AgentCatalogDto,
  BootstrapDto,
  DirectoryListingDto,
  DirectReviewRequest,
  DirectReviewResponse,
  DemoMutationRequest,
  DemoRuntimeStatus,
  FlowDetailDto,
  FlowReviewResultResponse,
  FlowSummaryDto,
  HistoryItemDto,
  IntakeRequest,
  IntakeResponse,
  LearningDto,
  PreviewDto,
  QualificationResolutionRequest,
  QualificationResolutionResponse,
  ReadinessResolutionRequest,
  ReadinessResolutionResponse,
  ReadinessWaiverRequest,
  ReadinessWaiverResponse,
  SaveSettingsRequest,
  SettingsDto,
  ToggleAgentRequest
} from "./types";

export const api = {
  bootstrap: () => request<BootstrapDto>("/api/bootstrap"),
  settings: () => request<SettingsDto>("/api/settings"),
  agentCatalog: () => request<AgentCatalogDto>("/api/agent-catalog"),
  saveSettings: (body: SaveSettingsRequest) =>
    request<SettingsDto>("/api/settings", { method: "PUT", body }),
  toggleAgent: (agentId: string, body: ToggleAgentRequest) =>
    request<AgentDto>(`/api/agents/${encodeURIComponent(agentId)}`, { method: "PUT", body }),
  reloadAgentCatalog: () =>
    request<AgentCatalogDto>("/api/agent-catalog/reload", { method: "POST" }),
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
  recoverFlow: (flowId: string) =>
    request<FlowDetailDto>(`/api/flows/${flowId}/recover`, { method: "POST", body: {} }),
  reviewFlow: (flowId: string, body: DirectReviewRequest) =>
    request<DirectReviewResponse>(`/api/flows/${flowId}/review`, {
      method: "POST",
      body
    }),
  reviewResult: (flowId: string) =>
    request<FlowReviewResultResponse>(`/api/flows/${flowId}/review-result`),
  grantReadinessWaiver: (flowId: string, body: ReadinessWaiverRequest) =>
    request<ReadinessWaiverResponse>(`/api/flows/${flowId}/readiness-waiver`, {
      method: "POST",
      body
    }),
  resolveReadiness: (flowId: string, body: ReadinessResolutionRequest) =>
    request<ReadinessResolutionResponse>(`/api/flows/${flowId}/readiness-resolution`, {
      method: "POST",
      body
    }),
  resolveQualification: (flowId: string, body: QualificationResolutionRequest) =>
    request<QualificationResolutionResponse>(
      `/api/flows/${flowId}/qualification-resolution`,
      { method: "POST", body }
    ),
  abandonFlow: (flowId: string) =>
    request<AbandonFlowResponse>(`/api/flows/${flowId}/abandon`, {
      method: "POST"
    }),
  history: () => request<HistoryItemDto[]>("/api/history"),
  learnings: () => request<LearningDto[]>("/api/learnings"),
  preview: (flowId: string) => request<PreviewDto>(`/api/previews/${flowId}`),
  demoStatus: (flowId: string, artifactId: string) =>
    request<DemoRuntimeStatus>(
      `/api/previews/${flowId}/artifacts/${encodeURIComponent(artifactId)}/demo`
    ),
  startDemo: (flowId: string, artifactId: string, body: DemoMutationRequest) =>
    request<DemoRuntimeStatus>(
      `/api/previews/${flowId}/artifacts/${encodeURIComponent(artifactId)}/demo/start`,
      { method: "POST", body }
    ),
  restartDemo: (flowId: string, artifactId: string, body: DemoMutationRequest) =>
    request<DemoRuntimeStatus>(
      `/api/previews/${flowId}/artifacts/${encodeURIComponent(artifactId)}/demo/restart`,
      { method: "POST", body }
    ),
  stopDemo: (flowId: string, artifactId: string, body: DemoMutationRequest) =>
    request<DemoRuntimeStatus>(
      `/api/previews/${flowId}/artifacts/${encodeURIComponent(artifactId)}/demo/stop`,
      { method: "POST", body }
    )
};
