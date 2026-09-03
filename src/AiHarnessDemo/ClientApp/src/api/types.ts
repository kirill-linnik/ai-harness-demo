// Typed mirror of src/AiHarnessDemo/Contracts/ApiModels.cs.
// Enum members are serialized as their C# name strings by JsonStringEnumConverter.

export type OutcomeType = "Commit" | "PullRequest";

export type FlowStatus =
  | "Intake"
  | "Queued"
  | "Running"
  | "WaitingForFeedback"
  | "Reworking"
  | "Approved"
  | "Failed";

export type StepStatus =
  | "Pending"
  | "Running"
  | "Completed"
  | "Pushback"
  | "Failed"
  | "Skipped";

export type ConversationRole =
  | "Customer"
  | "AccountManager"
  | "ProductManager"
  | "Harness";

export type AgentRunPhase =
  | "PreparingWorkspace"
  | "BuildingPrompt"
  | "LaunchingAgentProcess"
  | "InitializingSession"
  | "StreamingTurn"
  | "Finishing"
  | "Succeeded"
  | "Failed"
  | "TimedOut"
  | "Stalled"
  | "CanceledByReconciliation";

export type HandoffActionType = "Advance" | "RequestRevision" | "Release";

export type HandoffGateDecision =
  | "BlockedKillSwitch"
  | "LoggedShadow"
  | "AwaitingHumanApproval"
  | "AutoApproved";

export type HandoffTrustLevel = "Shadow" | "Gated" | "Auto";

export interface SettingsDto {
  repositoryPath: string;
  repositoryKnowledge: string;
  outcome: OutcomeType;
  maxHandoffRetries: number;
  updatedAt: string;
}

export interface AgentDto {
  id: string;
  name: string;
  description: string;
  role: string;
  accent: string;
  enabled: boolean;
  sortOrder: number;
}

export interface AgentToolCallDto {
  id: string;
  toolName: string;
  argumentsSummary: string;
  succeeded: boolean;
}

export interface FlowSummaryDto {
  id: string;
  title: string;
  status: FlowStatus;
  iteration: number;
  repositoryPath: string;
  outcomeLabel: string;
  outcomeUrl: string;
  createdAt: string;
  updatedAt: string;
}

export interface FlowStepDto {
  id: string;
  iteration: number;
  sequence: number;
  agentId: string;
  agentName: string;
  agentRole: string;
  label: string;
  model: string;
  modelReason: string;
  status: StepStatus;
  phase: AgentRunPhase;
  attempt: number;
  executionAttempts: number;
  inputSummary: string;
  executionPrompt: string;
  copilotSessionId: string;
  outputSummary: string;
  pushbackReason: string;
  startedAt: string | null;
  completedAt: string | null;
  durationMilliseconds: number;
  toolCalls: AgentToolCallDto[];
}

export interface FlowMessageDto {
  id: string;
  role: ConversationRole;
  content: string;
  isQuestion: boolean;
  createdAt: string;
}

export interface FlowEventDto {
  id: string;
  flowStepId: string | null;
  type: string;
  message: string;
  createdAt: string;
}

export interface HandoffGateRecordDto {
  id: string;
  flowStepId: string;
  actionType: HandoffActionType;
  decision: HandoffGateDecision;
  trustLevelAtDecision: HandoffTrustLevel;
  summary: string;
  evidence: string;
  reason: string;
  decidedAt: string;
  resolved: boolean;
  approved: boolean | null;
  resolvedBy: string | null;
  resolutionNote: string | null;
  resolvedAt: string | null;
}

export interface FlowDetailDto {
  id: string;
  title: string;
  originalRequest: string;
  consolidatedRequest: string;
  status: FlowStatus;
  iteration: number;
  repositoryPath: string;
  repositoryKnowledge: string;
  outcome: OutcomeType;
  workspacePath: string;
  branchName: string;
  outcomeUrl: string;
  outcomeLabel: string;
  failureReason: string;
  createdAt: string;
  updatedAt: string;
  completedAt: string | null;
  steps: FlowStepDto[];
  messages: FlowMessageDto[];
  events: FlowEventDto[];
  gateRecords: HandoffGateRecordDto[];
}

export interface LearningDto {
  id: string;
  sourceFlowId: string | null;
  agentId: string;
  category: string;
  trigger: string;
  lesson: string;
  promptRefinement: string;
  timesApplied: number;
  timesObserved: number;
  createdAt: string;
}

export interface HistoryItemDto {
  flowId: string;
  flowTitle: string;
  iteration: number;
  agentName: string;
  agentRole: string;
  model: string;
  status: StepStatus;
  durationMilliseconds: number;
  startedAt: string | null;
  pushbackReason: string;
}

export interface HarnessStatsDto {
  totalFlows: number;
  activeFlows: number;
  completedFlows: number;
  learnedRefinements: number;
  totalAgentMinutes: number;
}

export interface WorkflowStatusDto {
  ready: boolean;
  sourcePath: string;
  loadedAt: string | null;
  lastError: string | null;
  maxConcurrentAgents: number | null;
  maxAttempts: number | null;
  workspaceRoot: string | null;
}

export interface CopilotCliStatusDto {
  ready: boolean;
  command: string;
  resolvedPath: string;
  version: string;
  detail: string;
  checkedAt: string;
}

export interface BootstrapDto {
  settings: SettingsDto;
  agents: AgentDto[];
  flows: FlowSummaryDto[];
  stats: HarnessStatsDto;
  copilotCliAvailable: boolean;
  copilotCli: CopilotCliStatusDto;
  workflow: WorkflowStatusDto;
  factoryEnabled: boolean;
  factoryDisabledReason: string;
}

export interface SaveSettingsRequest {
  repositoryPath: string;
  repositoryKnowledge: string;
  outcome: OutcomeType;
  maxHandoffRetries: number;
}

export interface ToggleAgentRequest {
  enabled: boolean;
}

export interface AnalyzeRepositoryRequest {
  path: string;
  runCopilotInit: boolean;
}

export interface AnalyzeRepositoryResponse {
  repositoryPath: string;
  knowledge: string;
  copilotInitSucceeded: boolean;
  copilotInitMessage: string;
}

export interface DirectoryEntryDto {
  name: string;
  path: string;
}

export interface DirectoryListingDto {
  currentPath: string | null;
  parentPath: string | null;
  directories: DirectoryEntryDto[];
  locations: DirectoryEntryDto[];
}

export interface IntakeRequest {
  flowId: string | null;
  message: string;
}

export interface IntakeResponse {
  flow: FlowDetailDto;
  reply: string;
  readyToStart: boolean;
  shouldSpeak: boolean;
}

export interface FeedbackRequest {
  message: string;
}

export interface FeedbackResponse {
  flow: FlowDetailDto;
  reply: string;
  shouldSpeak: boolean;
}

export interface FlowDecisionRequest {
  approve: boolean;
}

export interface PreviewDto {
  flowId: string;
  title: string;
  request: string;
  repositoryName: string;
  iteration: number;
  outcomeLabel: string;
  deliveredBy: FlowStepDto[];
  generatedAt: string;
}
