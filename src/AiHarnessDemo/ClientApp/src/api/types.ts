// Typed mirror of src/AiHarnessDemo/Contracts/ApiModels.cs.
// Enum members are serialized as their C# name strings by JsonStringEnumConverter.

export type OutcomeType = "Commit" | "PullRequest" | "None";
export type DeliveryOutcomeType = Exclude<OutcomeType, "None">;
export type FlowKind = "Advisory" | "Delivery";
export type FlowLinkKind =
  | "AdvisoryPromotion"
  | "QualificationRosterRepair"
  | "QualificationScopeRevision";
export type AgentDefinitionStatus = "Valid" | "Invalid";
export type PlanDuty =
  | "Analyze"
  | "Design"
  | "Implement"
  | "Verify"
  | "PrepareOutcome"
  | "Publish";
export type PlanStage = "BeforeReview" | "AfterApproval";
export type ExecutionPermissionProfile =
  | "ReadOnlySource"
  | "WorkspaceWrite"
  | "Publish"
  | "PreMortemReadOnly";
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
  | "Failed"
  | "Blocked";

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

export type HandoffActionType =
  | "Advance"
  | "RequestRevision"
  | "CustomerReview"
  | "CustomerWaiver";

export type DeliveryReadinessState =
  | "ReadyToApprove"
  | "NeedsCustomerWaiver"
  | "NeedsRefinement"
  | "Blocked";

export type DeliveryCriterionOutcome = "Verified" | "Failed" | "Blocked";

export type DeliveryRiskClassification =
  | "NonBlockingDisclosure"
  | "WaiverRequired"
  | "Blocking";

export type DeliveryRiskSeverity = "Low" | "Medium" | "High" | "Critical";

export type DeliveryReadinessAction =
  | "None"
  | "Accept"
  | "RequestRefinement"
  | "GrantWaiver"
  | "Continue"
  | "Replan"
  | "Abandon";

export interface DeliveryReadinessCriterionDto {
  criterionId: string;
  requirement: string;
  outcome: DeliveryCriterionOutcome;
  rationale: string;
  remediation: string | null;
  evidenceIds: string[];
  responsibleRoles: string[];
  customerVisible: boolean;
}

export interface DeliveryReadinessRiskDto {
  riskId: string;
  classification: DeliveryRiskClassification;
  severity: DeliveryRiskSeverity;
  statement: string;
  impact: string;
  evidenceIds: string[];
  criterionIds: string[];
  sourceRole: string;
  sourceStepId: string;
  preMortemFindingId: string | null;
  waived: boolean;
}

export interface DeliveryReadinessDto {
  state: DeliveryReadinessState;
  revision: number;
  contractHash: string;
  reviewedCandidateId: string;
  candidateFingerprintPrefix: string;
  criteria: DeliveryReadinessCriterionDto[];
  risks: DeliveryReadinessRiskDto[];
  requiredWaiverRiskIds: string[];
  grantedWaiverRiskIds: string[];
  diagnostics: string[];
  allowedActions: DeliveryReadinessAction[];
  reviewGateId: string | null;
  waiverGateId: string | null;
  publicationAssurance: string;
  label: string;
}

export type ReviewDecision =
  | "Accepted"
  | "RefinementRequested"
  | "PromotedToDelivery";

export type ReviewIntent =
  | "Accept"
  | "RequestRefinement"
  | "PromoteToDelivery";

export type ReviewPublicationStatus =
  | "NotApplicable"
  | "AwaitingApproval"
  | "Queued"
  | "Running"
  | "Failed"
  | "Published";

export type HandoffGateDecision =
  | "BlockedKillSwitch"
  | "LoggedShadow"
  | "AwaitingHumanApproval"
  | "AutoApproved";

export type HandoffTrustLevel = "Shadow" | "Gated" | "Auto";

export interface SettingsDto {
  repositoryPath: string;
  repositoryKnowledge: string;
  outcome: DeliveryOutcomeType;
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
  definitionStatus: AgentDefinitionStatus;
  validationError: string;
  required: boolean;
  switchable: boolean;
  definitionHash: string;
  loadedAt: string;
}

export interface AgentToolCallDto {
  id: string;
  toolName: string
  argumentsSummary: string
  succeeded: boolean
}

export interface FlowSummaryDto {  id: string;
title: string;
kind: FlowKind;
parentFlowRunId: string | null;
  parentIteration: number | null;
  linkKind: FlowLinkKind | null;
  currentBlockerCode: string | null;
  customerBlockerMessage: string | null;
  review: FlowReviewSummaryDto;
  linkedFlows: LinkedFlowDto[];
  status: FlowStatus;
  iteration: number;
  agentCatalogRevision: string;
  repositoryPath: string;
  outcomeLabel: string;
  outcomeUrl: string;
  createdAt: string;
  updatedAt: string;
  readinessState?: DeliveryReadinessState | null;
  readinessLabel?: string | null;
}

export interface FlowStepDto {
  id: string;
  iteration: number;
  sequence: number;
  agentId: string;
  agentName: string;
  agentRole: string;
  label: string;
  planStepKey: string;
  duties: PlanDuty[];
  stage: PlanStage;
  isOutcomeOwner: boolean;
  permissionProfile: ExecutionPermissionProfile;
  effectivePermissionJson: string;
  workflowRevision: string;
  dependencyStepIds: string[];
  dependencyPlanStepKeys: string[];
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

export interface LinkedFlowDto {
  id: string;
  title: string;
  kind: FlowKind;
  status: FlowStatus;
  iteration: number;
  parentIteration: number | null;
  linkKind: FlowLinkKind | null;
  outcomeLabel: string;
  review: FlowReviewSummaryDto;
  createdAt: string;
}

export interface FlowReviewSummaryDto {
  gateId: string | null;
  available: boolean;
  resolved: boolean;
  approved: boolean | null;
  decision: ReviewDecision | null;
  publicationStatus: ReviewPublicationStatus;
}

export interface FlowMessageDto {
  id: string;
  role: ConversationRole;
  content: string;
  isQuestion: boolean;
  createdAt: string;
  attachments?: FlowAttachmentDto[];
}

export interface FlowAttachmentDto {
  id: string;
  fileName: string;
  contentType: string;
  length: number;
}

export interface FlowEventDto {
  id: string;
  flowStepId: string | null;
  type: string;
  message: string;
  dataJson?: string | null;
  createdAt: string;
}

export interface HandoffGateRecordDto {
  id: string;
  flowStepId: string;
  actionType: HandoffActionType;
  decision: HandoffGateDecision;
  reviewDecision?: ReviewDecision | null;
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
  kind: FlowKind;
  parentFlowRunId: string | null;
  parentIteration: number | null;
  linkKind: FlowLinkKind | null;
  linkedFlows: LinkedFlowDto[];
  agentCatalogRevision: string;
  outcomeOwnerPlanStepKey: string | null;
  publicationPlanStepKey: string | null;
  currentBlockerCode: string | null;
  currentBlockerSummary: string | null;
  currentBlockerDataJson: string | null;
  customerBlockerMessage: string | null;
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
  reviewedPreviewUrl: string | null;
  outcomeResult: FlowOutcomeDto | null;
  failureReason: string;
  createdAt: string;
  updatedAt: string;
  completedAt: string | null;
  steps: FlowStepDto[];
  messages: FlowMessageDto[];
  events: FlowEventDto[];
  gateRecords: HandoffGateRecordDto[];
  review: FlowReviewSummaryDto;
  publicationStatus: ReviewPublicationStatus;
  deliveryReadiness?: DeliveryReadinessDto | null;
}

export interface AdvisoryArtifactDto {
  id: string;
  path: string;
  mediaType: string;
  byteLength: number;
  url: string;
  downloadUrl: string;
}

export interface FlowOutcomeDto {
  goal: string;
  summary: string;
  implementationDetails: string[];
  artifacts: AdvisoryArtifactDto[];
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
  flowKind: FlowKind;
  flowStatus: FlowStatus;
  parentFlowRunId: string | null;
  parentIteration: number | null;
  linkKind: FlowLinkKind | null;
  currentBlockerCode: string | null;
  customerBlockerMessage: string | null;
  outcomeLabel: string;
  review: FlowReviewSummaryDto;
  iteration: number;
  agentName: string;
  agentRole: string;
  model: string;
  status: StepStatus;
  durationMilliseconds: number;
  startedAt: string | null;
  pushbackReason: string;
  readinessState?: DeliveryReadinessState | null;
  readinessLabel?: string | null;
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
  currentFileValid: boolean;
  hasEffectiveDefinition: boolean;
  sourcePath: string;
  effectiveLoadedAt: string | null;
  effectiveRevision: string | null;
  currentFileError: string | null;
  loadedAt: string | null;
  lastError: string | null;
  maxConcurrentAgents: number | null;
  maxAttempts: number | null;
  workspaceRoot: string | null;
}

export interface AgentCatalogStatusDto {
  ready: boolean;
  hasEffectiveCatalog: boolean;
  effectiveRevision: string | null;
  loadedAt: string | null;
  lastError: string | null;
  validDefinitionCount: number;
  invalidDefinitionCount: number;
}

export interface AgentCatalogDto {
  status: AgentCatalogStatusDto;
  agents: AgentDto[];
}

export interface NewWorkAdmissionStatusDto {
  ready: boolean;
  failures: string[];
  checkedAt: string;
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
  role: string;
  planStepKey: string;
  agentId: string;
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
  agentCatalog: AgentCatalogStatusDto;
  admission: NewWorkAdmissionStatusDto;
  factoryEnabled: boolean;
  factoryDisabledReason: string;
  githubCliAvailable: boolean;
  githubCliAuthenticated: boolean;
}

export interface SaveSettingsRequest {
  repositoryPath: string;
  repositoryKnowledge: string;
  outcome: DeliveryOutcomeType;
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
  files?: File[];
}

export interface IntakeResponse {
  flow: FlowDetailDto;
  reply: string;
  readyToStart: boolean;
  shouldSpeak: boolean;
}

export interface DirectReviewRequest {
  gateId: string;
  intent: ReviewIntent;
  refinement?: {
    goal?: string | null;
    requestedChanges: string[];
  } | null;
  reviewedCandidateId?: string | null;
  readinessRevision?: number | null;
  readinessContractHash?: string | null;
}

export interface ReadinessWaiverRequest {
  gateId: string;
  reviewedCandidateId: string;
  readinessRevision: number;
  readinessContractHash: string;
  riskIds: string[];
  acknowledgement: string;
}

export interface ReadinessWaiverResponse {
  flowId: string;
  gateId: string;
  reviewedCandidateId: string;
  readinessRevision: number;
  readinessContractHash: string;
  waivedRiskIds: string[];
  message: string;
}

export type ReadinessResolutionAction =
  | "RequestRefinement"
  | "Continue"
  | "Replan"
  | "Abandon";

export interface ReadinessResolutionRequest {
  reviewedCandidateId: string;
  readinessRevision: number;
  readinessContractHash: string;
  action: ReadinessResolutionAction;
  refinement?: {
    goal?: string | null;
    requestedChanges: string[];
  } | null;
}

export interface ReadinessResolutionResponse {
  flowId: string;
  action: ReadinessResolutionAction;
  resolvedFrom: DeliveryReadinessState;
  status: FlowStatus;
  iteration: number;
  message: string;
}

export interface DirectReviewResponse {
  flowId: string;
  gateId: string;
  intent: ReviewIntent;
  decision: ReviewDecision | null;
  status: FlowStatus;
  iteration: number;
  publicationStepId: string | null;
  linkedFlowId?: string | null;
  publicationStatus: ReviewPublicationStatus;
  message: string;
}

export type QualificationResolutionAction = "RosterRepair" | "ScopeRevision";

export interface QualificationResolutionRequest {
  action: QualificationResolutionAction;
  scopeRevision?: {
    goal: string;
    scope: string[];
  } | null;
  revisedGoal?: string | null;
  revisedScope?: string[] | null;
}

export interface QualificationResolutionResponse {
  parentFlowId: string;
  parentIteration: number;
  successorFlowId: string;
  linkKind: FlowLinkKind;
  parentStatus: FlowStatus;
  successorStatus: FlowStatus;
  existingSuccessor: boolean;
  accountManagerReply: string;
}

export interface FlowReviewResultResponse {
  flowId: string;
  gateId: string;
  status: FlowStatus;
  iteration: number;
  resolved: boolean;
  approved: boolean | null;
  decision: ReviewDecision | null;
  publicationStatus: ReviewPublicationStatus;
  linkedFlowId: string | null;
  linkKind: FlowLinkKind | null;
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
  kind: FlowKind;
  iteration: number;
  status: FlowStatus;
  outcomeLabel: string;
  outcomeResult: FlowOutcomeDto | null;
  artifacts: PreviewArtifactDto[];
  deliveredBy: PreviewContributorDto[];
  review: FlowReviewSummaryDto;
  publicationStatus: ReviewPublicationStatus;
  deliveryReadiness: DeliveryReadinessDto | null;
  generatedAt: string;
}

export interface PreviewContributorDto {
  id: string;
  agentName: string;
  label: string;
  status: StepStatus;
  durationMilliseconds: number;
}

export type DemoCapability = "Available" | "OfflineOnly";
export type DemoInstanceState =
  | "Stopped"
  | "Starting"
  | "Running"
  | "Unhealthy"
  | "Failed";

export interface PreviewArtifactDto {
  id: string;
  label: string;
  url: string;
  openUrl: string;
  mediaType: string | null;
  byteLength: number | null;
  downloadUrl: string | null;
  interactive: boolean;
  demoCapability: DemoCapability;
  demoInstanceId: string | null;
  demoState: DemoInstanceState;
  demoUrl: string | null;
  demoFailureDetail: string | null;
  demoCandidateFingerprint: string | null;
  demoManifestHash: string | null;
}


export interface DemoMutationRequest {
  candidateFingerprint: string;
  manifestHash: string;
}

export interface DemoRuntimeStatus {
  capability: DemoCapability;
  instanceId: string | null;
  state: DemoInstanceState;
  stableUrl: string | null;
  failureDetail: string | null;
  candidateFingerprint: string | null;
  manifestHash: string | null;
}
