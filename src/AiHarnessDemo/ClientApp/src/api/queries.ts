import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "./endpoints";
import { flowDetailPollIntervalMs } from "../lib/pollInterval";
import type {
  AnalyzeRepositoryRequest,
  DirectReviewRequest,
  IntakeRequest,
  QualificationResolutionRequest,
  SaveSettingsRequest,
  ToggleAgentRequest
} from "./types";

/**
 * TanStack Query bindings for every DemoApi endpoint the UI consumes. Server state lives
 * here exclusively; component-local UI state (selected step, modal open/closed, textarea
 * drafts) stays in the components themselves. See lib/pollInterval.ts for the active-flow
 * refresh cadence, which reproduces the original `scheduleFlowPoll` behavior.
 */

export const queryKeys = {
  bootstrap: ["bootstrap"] as const,
  settings: ["settings"] as const,
  agentCatalog: ["agent-catalog"] as const,
  flows: ["flows"] as const,
  flow: (flowId: string) => ["flow", flowId] as const,
  reviewResult: (flowId: string) => ["review-result", flowId] as const,
  history: ["history"] as const,
  learnings: ["learnings"] as const,
  preview: (flowId: string) => ["preview", flowId] as const,
  directories: (path: string) => ["directories", path] as const
};

export function useBootstrapQuery() {
  return useQuery({
    queryKey: queryKeys.bootstrap,
    queryFn: api.bootstrap,
    staleTime: 0
  });
}

export function useSettingsQuery() {
  return useQuery({
    queryKey: queryKeys.settings,
    queryFn: api.settings,
    staleTime: 0
  });
}

export function useAgentCatalogQuery() {
  return useQuery({
    queryKey: queryKeys.agentCatalog,
    queryFn: api.agentCatalog,
    staleTime: 0
  });
}

export function useFlowsQuery() {
  return useQuery({
    queryKey: queryKeys.flows,
    queryFn: api.flows,
    staleTime: 0
  });
}

export function useFlowQuery(flowId: string | undefined) {
  return useQuery({
    queryKey: queryKeys.flow(flowId ?? ""),
    queryFn: () => api.flow(flowId!),
    enabled: Boolean(flowId),
    staleTime: 0,
    refetchInterval: query =>
      flowDetailPollIntervalMs(
        query.state.data?.status,
        query.state.data?.kind,
        query.state.data?.reviewedPreviewUrl
      ),
    refetchIntervalInBackground: true
  });
}

export function useReviewResultQuery(flowId: string | undefined, enabled = true) {
  return useQuery({
    queryKey: queryKeys.reviewResult(flowId ?? ""),
    queryFn: () => api.reviewResult(flowId!),
    enabled: Boolean(flowId) && enabled,
    staleTime: 0,
    retry: false
  });
}

export function useHistoryQuery() {
  return useQuery({ queryKey: queryKeys.history, queryFn: api.history, staleTime: 0 });
}

export function useLearningsQuery() {
  return useQuery({ queryKey: queryKeys.learnings, queryFn: api.learnings, staleTime: 0 });
}

export function usePreviewQuery(flowId: string | undefined) {
  return useQuery({
    queryKey: queryKeys.preview(flowId ?? ""),
    queryFn: () => api.preview(flowId!),
    enabled: Boolean(flowId),
    staleTime: 0,
    retry: false
  });
}

export function useDirectoryListingQuery(path: string, enabled: boolean) {
  return useQuery({
    queryKey: queryKeys.directories(path),
    queryFn: () => api.listDirectories(path),
    enabled,
    staleTime: 0,
    retry: false
  });
}

function useInvalidateProjection() {
  const queryClient = useQueryClient();
  return async (...flowIds: string[]) => {
    const invalidations = [
      queryClient.invalidateQueries({ queryKey: queryKeys.bootstrap }),
      queryClient.invalidateQueries({ queryKey: queryKeys.settings }),
      queryClient.invalidateQueries({ queryKey: queryKeys.agentCatalog }),
      queryClient.invalidateQueries({ queryKey: queryKeys.flows }),
      queryClient.invalidateQueries({ queryKey: queryKeys.history })
    ];
    if (flowIds.length > 0) {
      for (const flowId of new Set(flowIds)) {
        invalidations.push(
          queryClient.invalidateQueries({ queryKey: queryKeys.flow(flowId) }),
          queryClient.invalidateQueries({ queryKey: queryKeys.reviewResult(flowId) }),
          queryClient.invalidateQueries({ queryKey: queryKeys.preview(flowId) })
        );
      }
    } else {
      invalidations.push(
        queryClient.invalidateQueries({ queryKey: ["flow"] }),
        queryClient.invalidateQueries({ queryKey: ["review-result"] }),
        queryClient.invalidateQueries({ queryKey: ["preview"] })
      );
    }
    await Promise.all(invalidations);
  };
}

export function useSaveSettingsMutation() {
  const invalidateProjection = useInvalidateProjection();
  return useMutation({
    mutationFn: (body: SaveSettingsRequest) => api.saveSettings(body),
    onSuccess: () => invalidateProjection()
  });
}

export function useToggleAgentMutation() {
  const invalidateProjection = useInvalidateProjection();
  return useMutation({
    mutationFn: ({ agentId, body }: { agentId: string; body: ToggleAgentRequest }) =>
      api.toggleAgent(agentId, body),
    onSuccess: () => invalidateProjection()
  });
}

export function useReloadAgentCatalogMutation() {
  const invalidateProjection = useInvalidateProjection();
  return useMutation({
    mutationFn: api.reloadAgentCatalog,
    onSuccess: () => invalidateProjection()
  });
}

export function useAnalyzeRepositoryMutation() {
  const invalidateProjection = useInvalidateProjection();
  return useMutation({
    mutationFn: (body: AnalyzeRepositoryRequest) => api.analyzeRepository(body),
    onSuccess: () => invalidateProjection()
  });
}

export function useContinueIntakeMutation() {
  const queryClient = useQueryClient();
  const invalidateProjection = useInvalidateProjection();
  return useMutation({
    mutationFn: (body: IntakeRequest) => api.continueIntake(body),
    onSuccess: response => {
      queryClient.setQueryData(queryKeys.flow(response.flow.id), response.flow);
      return invalidateProjection(response.flow.id);
    }
  });
}

export function useStartFlowMutation() {
  const queryClient = useQueryClient();
  const invalidateProjection = useInvalidateProjection();
  return useMutation({
    mutationFn: (flowId: string) => api.startFlow(flowId),
    onSuccess: flow => {
      queryClient.setQueryData(queryKeys.flow(flow.id), flow);
      return invalidateProjection(flow.id);
    }
  });
}

export function useRestartFlowMutation() {
  const queryClient = useQueryClient();
  const invalidateProjection = useInvalidateProjection();
  return useMutation({
    mutationFn: (flowId: string) => api.restartFlow(flowId),
    onSuccess: flow => {
      queryClient.setQueryData(queryKeys.flow(flow.id), flow);
      return invalidateProjection(flow.id);
    }
  });
}

export function useRecoverFlowMutation() {
  const queryClient = useQueryClient();
  const invalidateProjection = useInvalidateProjection();
  return useMutation({
    mutationFn: (flowId: string) => api.recoverFlow(flowId),
    onSuccess: flow => {
      queryClient.setQueryData(queryKeys.flow(flow.id), flow);
      return invalidateProjection(flow.id);
    }
  });
}

export function useReviewFlowMutation() {
  const invalidateProjection = useInvalidateProjection();
  return useMutation({
    mutationFn: ({ flowId, body }: { flowId: string; body: DirectReviewRequest }) =>
      api.reviewFlow(flowId, body),
    onSuccess: result => invalidateProjection(result.flowId)
  });
}

export function useResolveQualificationMutation() {
  const invalidateProjection = useInvalidateProjection();
  return useMutation({
    mutationFn: ({
      flowId,
      body
    }: {
      flowId: string;
      body: QualificationResolutionRequest;
    }) => api.resolveQualification(flowId, body),
    onSuccess: result =>
      invalidateProjection(result.parentFlowId, result.successorFlowId)
  });
}

export function useAbandonFlowMutation() {
  const queryClient = useQueryClient();
  const invalidateProjection = useInvalidateProjection();
  return useMutation({
    mutationFn: (flowId: string) => api.abandonFlow(flowId),
    onSuccess: result => {
      queryClient.invalidateQueries({ queryKey: queryKeys.flow(result.flowId) });
      return invalidateProjection(result.flowId);
    }
  });
}
