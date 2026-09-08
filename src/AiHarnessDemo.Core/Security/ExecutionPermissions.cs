using System.Collections.Immutable;
using AiHarnessDemo.Core.Domain;

namespace AiHarnessDemo.Core.Security;

public sealed record PermissionResolutionRequest(
    FlowKind FlowKind,
    ExecutionInvocationKind InvocationKind,
    PlanStage PlanStage,
    ImmutableArray<PlanDuty> PlanDuties,
    ReviewDecision? DurableReviewDecision,
    bool DurableApproval,
    bool IsOnlyPlannedPublishStep,
    string ContractVersion,
    bool LegacyPublicationAuthorized,
    bool IsGovernedOutcomeVerification);

public sealed record WorkflowPermissionRestrictions(
    ExecutionPermissionProfile AdvisoryMaximum,
    ExecutionPermissionProfile DeliveryPreReviewMaximum,
    ExecutionPermissionProfile DeliveryPostApprovalMaximum,
    ImmutableDictionary<ExecutionPermissionProfile, ImmutableArray<string>>
        AdditionalDeniedTools);

public sealed record EffectiveExecutionPermission(
    ExecutionPermissionProfile Profile,
    ImmutableArray<string> AllowedTools,
    ImmutableArray<string> DeniedTools,
    ImmutableArray<string> DeniedUrls,
    bool DisableBuiltinMcps,
    bool DisableCustomInstructions,
    bool DisallowTemporaryDirectory,
    bool GuardPublicationCredentials,
    bool AllowRemotePublication,
    bool GovernedGitMetadataIsolation);
