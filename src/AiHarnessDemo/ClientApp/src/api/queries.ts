import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "./endpoints";
import { flowPollIntervalMs } from "../lib/pollInterval";
import type {
  AnalyzeRepositoryRequest,
  FeedbackRequest,
  FlowDecisionRequest,
  IntakeRequest,
  OutcomeResolutionRequest,
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
  flow: (flowId: string) => ["flow", flowId] as const,
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

export function useFlowQuery(flowId: string | undefined) {
  return useQuery({
    queryKey: queryKeys.flow(flowId ?? ""),
    queryFn: () => api.flow(flowId!),
    enabled: Boolean(flowId),
    staleTime: 0,
    refetchInterval: query => flowPollIntervalMs(query.state.data?.status),
    refetchIntervalInBackground: true
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

function useInvalidateBootstrap() {
  const queryClient = useQueryClient();
  return () => queryClient.invalidateQueries({ queryKey: queryKeys.bootstrap });
}

export function useSaveSettingsMutation() {
  const invalidateBootstrap = useInvalidateBootstrap();
  return useMutation({
    mutationFn: (body: SaveSettingsRequest) => api.saveSettings(body),
    onSuccess: () => invalidateBootstrap()
  });
}

export function useToggleAgentMutation() {
  const invalidateBootstrap = useInvalidateBootstrap();
  return useMutation({
    mutationFn: ({ agentId, body }: { agentId: string; body: ToggleAgentRequest }) =>
      api.toggleAgent(agentId, body),
    onSuccess: () => invalidateBootstrap()
  });
}

export function useAnalyzeRepositoryMutation() {
  const invalidateBootstrap = useInvalidateBootstrap();
  return useMutation({
    mutationFn: (body: AnalyzeRepositoryRequest) => api.analyzeRepository(body),
    onSuccess: () => invalidateBootstrap()
  });
}

export function useContinueIntakeMutation() {
  const invalidateBootstrap = useInvalidateBootstrap();
  return useMutation({
    mutationFn: (body: IntakeRequest) => api.continueIntake(body),
    onSuccess: () => invalidateBootstrap()
  });
}

export function useStartFlowMutation() {
  const invalidateBootstrap = useInvalidateBootstrap();
  return useMutation({
    mutationFn: (flowId: string) => api.startFlow(flowId),
    onSuccess: () => invalidateBootstrap()
  });
}

export function useRestartFlowMutation() {
  const queryClient = useQueryClient();
  const invalidateBootstrap = useInvalidateBootstrap();
  return useMutation({
    mutationFn: (flowId: string) => api.restartFlow(flowId),
    onSuccess: flow => {
      queryClient.setQueryData(queryKeys.flow(flow.id), flow);
      invalidateBootstrap();
    }
  });
}

export function useSendFeedbackMutation() {
  return useMutation({
    mutationFn: ({ flowId, body }: { flowId: string; body: FeedbackRequest }) =>
      api.sendFeedback(flowId, body)
  });
}

export function useDecideFlowMutation() {
  const queryClient = useQueryClient();
  const invalidateBootstrap = useInvalidateBootstrap();
  return useMutation({
    mutationFn: ({ flowId, body }: { flowId: string; body: FlowDecisionRequest }) =>
      api.decideFlow(flowId, body),
    onSuccess: result => {
      queryClient.setQueryData(queryKeys.flow(result.flow.id), result.flow);
      invalidateBootstrap();
    }
  });
}

export function useResolveOutcomeMutation() {
  const queryClient = useQueryClient();
  const invalidateBootstrap = useInvalidateBootstrap();
  return useMutation({
    mutationFn: ({
      flowId,
      body
    }: {
      flowId: string;
      body: OutcomeResolutionRequest;
    }) => api.resolveOutcome(flowId, body),
    onSuccess: flow => {
      queryClient.setQueryData(queryKeys.flow(flow.id), flow);
      invalidateBootstrap();
    }
  });
}

export function useAbandonFlowMutation() {
  const queryClient = useQueryClient();
  const invalidateBootstrap = useInvalidateBootstrap();
  return useMutation({
    mutationFn: (flowId: string) => api.abandonFlow(flowId),
    onSuccess: result => {
      queryClient.invalidateQueries({ queryKey: queryKeys.flow(result.flowId) });
      invalidateBootstrap();
    }
  });
}
