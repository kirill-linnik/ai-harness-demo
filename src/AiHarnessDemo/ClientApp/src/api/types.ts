// Typed mirror of src/AiHarnessDemo/Contracts/ApiModels.cs.
// Enum members are serialized as their C# name strings by JsonStringEnumConverter.

export type OutcomeType = "Commit" | "PullRequest";
export type ModelSelectionStrategy = "MaximumQuality" | "FastestResponse" | "LowestCost";
export type TaskRisk = "Low" | "Medium" | "High" | "Critical";

export type FlowStatus =
  | "Intake"
  | "Queued"
  | "Running"
  | "WaitingForFeedback"
  | "Reworking"
  | "Abandoning"
  | "Approved"
  | "Abandoned"
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
  | "Retrying"
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
  modelSelectionStrategy: ModelSelectionStrategy;
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
  modelEffort: string;
  modelReason: string;
  taskProfile: TaskProfileDto | null;
  routing: RoutingDecisionDto | null;
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
  modelSelectionStrategy: ModelSelectionStrategy;
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

export interface ModelCatalogStatusDto {
  ready: boolean;
  catalogVersion: string;
  candidateCount: number;
  detail: string;
  checkedAt: string;
}

export interface TaskProfileDto {
  version: string;
  role: string;
  complexity: number;
  reasoningDepth: number;
  contextDemand: number;
  toolIntensity: number;
  taskTypeTags: string[];
  risk: TaskRisk;
  riskReason: string;
  confidence: number;
  rationales: string[];
}

export interface RoutingAlternativeDto {
  model: string;
  effort: string;
  rank: number;
  predictedQuality: number;
  predictedAcceptedTimeSeconds: number;
  predictedPremiumRequests: number;
  confidence: number;
  reason: string;
}

export interface RoutingDecisionDto {
  strategy: ModelSelectionStrategy;
  selectedModel: string;
  selectedEffort: string;
  predictedQuality: number;
  predictedAcceptedTimeSeconds: number;
  predictedPremiumRequests: number;
  premiumUseEstimated: boolean;
  confidence: number;
  uncertainty: number;
  exploration: boolean;
  reason: string;
  algorithmVersion: string;
  rerouteCount: number;
  alternatives: RoutingAlternativeDto[];
}

export interface BootstrapDto {
  settings: SettingsDto;
  agents: AgentDto[];
  flows: FlowSummaryDto[];
  stats: HarnessStatsDto;
  copilotCliAvailable: boolean;
  copilotCli: CopilotCliStatusDto;
  modelCatalog: ModelCatalogStatusDto;
  workflow: WorkflowStatusDto;
  factoryEnabled: boolean;
  factoryDisabledReason: string;
}

export interface SaveSettingsRequest {
  repositoryPath: string;
  repositoryKnowledge: string;
  outcome: OutcomeType;
  maxHandoffRetries: number;
  modelSelectionStrategy: ModelSelectionStrategy;
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

export interface AbandonFlowResponse {
  flowId: string;
  status: "Abandoned";
  processesStopped: number;
  listeningPortsReleased: number[];
  copilotSessionsDeleted: number;
  worktreesRemoved: number;
  localBranchesDeleted: number;
  remoteBranchesDeleted: number;
}

export interface PreviewDto {
  flowId: string;
  title: string;
  request: string;
  repositoryName: string;
  iteration: number;
  status: FlowStatus;
  outcomeLabel: string;
  artifacts: PreviewArtifactDto[];
  deliveredBy: FlowStepDto[];
  generatedAt: string;
}

export interface PreviewArtifactDto {
  id: string;
  label: string;
  url: string;
}
