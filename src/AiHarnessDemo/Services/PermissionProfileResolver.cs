using System.Collections.Immutable;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Security;
using AiHarnessDemo.Core.Workflow;

namespace AiHarnessDemo.Services;

internal sealed record DeferredPermissionSnapshot(
    int Iteration,
    string PlanStepKey,
    string WorkflowRevision,
    EffectiveExecutionPermission Permission);

public sealed class PermissionProfileResolver
{
    private static readonly ImmutableArray<string> ReadOnlyTools =
        ["view", "grep", "glob", "web_fetch"];

    private static readonly ImmutableArray<string> WorkspaceTools =
        OperatingSystem.IsWindows()
            ? ["view", "grep", "glob", "create", "edit", "powershell"]
            : ["view", "grep", "glob", "create", "edit", "bash"];

    private static readonly ImmutableArray<string> HostControlledPublishTools =
        OperatingSystem.IsWindows()
            ? ["view", "grep", "glob", "powershell"]
            : ["view", "grep", "glob", "bash"];

    private static readonly ImmutableArray<string> PublicationDenials =
    [
        "shell(git push)",
        "shell(git send-pack)",
        "shell(gh:*)",
        "shell(ssh:*)",
        "shell(scp:*)",
        "shell(curl:*)",
        "shell(wget:*)",
        "shell(Invoke-WebRequest:*)",
        "shell(Invoke-RestMethod:*)"
    ];

    private static readonly ImmutableArray<string> PublicationUrls =
        ["github.com", "api.github.com"];

    private static readonly ImmutableArray<string> ReviewedSourceMutationDenials =
    [
        "shell(git add)",
        "shell(git am)",
        "shell(git apply)",
        "shell(git branch)",
        "shell(git checkout)",
        "shell(git cherry-pick)",
        "shell(git clean)",
        "shell(git commit)",
        "shell(git commit-tree)",
        "shell(git config)",
        "shell(git fetch)",
        "shell(git merge)",
        "shell(git mv)",
        "shell(git pull)",
        "shell(git rebase)",
        "shell(git reset)",
        "shell(git restore)",
        "shell(git revert)",
        "shell(git rm)",
        "shell(git stash)",
        "shell(git switch)",
        "shell(git tag)",
        "shell(git update-index)",
        "shell(git update-ref)",
        "shell(git worktree)",
        "shell(git.exe:*)",
        "shell(Set-Content:*)",
        "shell(Add-Content:*)",
        "shell(Clear-Content:*)",
        "shell(Out-File:*)",
        "shell(New-Item:*)",
        "shell(Remove-Item:*)",
        "shell(Move-Item:*)",
        "shell(Copy-Item:*)",
        "shell(Rename-Item:*)",
        "shell(rm:*)",
        "shell(mv:*)",
        "shell(cp:*)",
        "shell(install:*)",
        "shell(mkdir:*)",
        "shell(rmdir:*)",
        "shell(ln:*)",
        "shell(touch:*)",
        "shell(truncate:*)",
        "shell(tee:*)",
        "shell(sed:*)",
        "shell(perl:*)",
        "shell(python:*)",
        "shell(python3:*)",
        "shell(node:*)"
    ];

    public EffectiveExecutionPermission Resolve(
        PermissionResolutionRequest request,
        WorkflowPermissionRestrictions workflow)
    {
        if (!Enum.IsDefined(request.FlowKind) ||
            !Enum.IsDefined(request.InvocationKind) ||
            !Enum.IsDefined(request.PlanStage) ||
            request.PlanDuties.IsDefault ||
            request.PlanDuties.Any(duty => !Enum.IsDefined(duty)))
        {
            throw new InvalidOperationException(
                "The execution permission inputs contain an unknown value.");
        }

        var requested = ResolveRequestedProfile(request);
        var ceiling = ResolveCeiling(request, workflow);
        var profile = PermissionRank(requested) <= PermissionRank(ceiling)
            ? requested
            : ceiling;
        if (!Enum.IsDefined(profile))
        {
            throw new InvalidOperationException(
                $"Unknown execution permission profile '{profile}'.");
        }

        var readOnly =
            profile is ExecutionPermissionProfile.ReadOnlySource or
                ExecutionPermissionProfile.PreMortemReadOnly;
        var publish = profile == ExecutionPermissionProfile.Publish;
        var hostControlledPublish =
            request.FlowKind == FlowKind.Delivery &&
            request.PlanStage == PlanStage.AfterApproval &&
            request.PlanDuties.Length == 1 &&
            request.PlanDuties[0] == PlanDuty.Publish &&
            request.IsOnlyPlannedPublishStep &&
            request.DurableApproval &&
            request.DurableReviewDecision == ReviewDecision.Accepted;
        var additionalDenied = workflow.AdditionalDeniedTools.TryGetValue(
            profile,
            out var configured)
            ? configured
            : [];
        var deniedTools = (readOnly
                ? ImmutableArray.Create("write", "shell")
                : hostControlledPublish
                    ? ImmutableArray.Create("write")
                    : ImmutableArray<string>.Empty)
            .AddRange(hostControlledPublish
                ? ReviewedSourceMutationDenials
                : [])
            .AddRange(publish && !hostControlledPublish
                ? []
                : PublicationDenials)
            .AddRange(additionalDenied)
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray();

        return new EffectiveExecutionPermission(
            profile,
            readOnly
                ? ReadOnlyTools
                : hostControlledPublish
                    ? HostControlledPublishTools
                    : WorkspaceTools,
            deniedTools,
            publish && !hostControlledPublish ? [] : PublicationUrls,
            DisableBuiltinMcps: true,
            DisableCustomInstructions: true,
            DisallowTemporaryDirectory: readOnly || hostControlledPublish,
            GuardPublicationCredentials: !publish || hostControlledPublish,
            AllowRemotePublication: publish,
            GovernedGitMetadataIsolation: hostControlledPublish);
    }

    public static WorkflowPermissionRestrictions FromWorkflow(
        WorkflowDefinition workflow)
    {
        var permissions = workflow.Config.Studio.Permissions;
        return new WorkflowPermissionRestrictions(
            workflow.Config.Studio.FlowKinds.Advisory.MaximumPermission,
            workflow.Config.Studio.FlowKinds.Delivery.PreReviewMaximumPermission,
            workflow.Config.Studio.FlowKinds.Delivery.PostApprovalMaximumPermission,
            new Dictionary<ExecutionPermissionProfile, ImmutableArray<string>>
            {
                [ExecutionPermissionProfile.ReadOnlySource] =
                    Clean(permissions.ReadOnlySource.AdditionalDeniedTools),
                [ExecutionPermissionProfile.WorkspaceWrite] =
                    Clean(permissions.WorkspaceWrite.AdditionalDeniedTools),
                [ExecutionPermissionProfile.Publish] =
                    Clean(permissions.Publish.AdditionalDeniedTools),
                [ExecutionPermissionProfile.PreMortemReadOnly] =
                    Clean(permissions.PreMortemReadOnly.AdditionalDeniedTools)
            }.ToImmutableDictionary());
    }

    public static EffectiveExecutionPermission Tighten(
        EffectiveExecutionPermission persisted,
        EffectiveExecutionPermission current)
    {
        ArgumentNullException.ThrowIfNull(persisted);
        ArgumentNullException.ThrowIfNull(current);
        ValidateShape(persisted);
        ValidateShape(current);

        var profile = PermissionRank(current.Profile) <
                      PermissionRank(persisted.Profile)
            ? current.Profile
            : persisted.Profile;
        var currentAllowed = current.AllowedTools.ToHashSet(StringComparer.Ordinal);
        var allowed = persisted.AllowedTools
            .Where(currentAllowed.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray();
        var denied = persisted.DeniedTools
            .Concat(current.DeniedTools)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        var deniedUrls = persisted.DeniedUrls
            .Concat(current.DeniedUrls)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        return new EffectiveExecutionPermission(
            profile,
            allowed,
            denied,
            deniedUrls,
            DisableBuiltinMcps:
                persisted.DisableBuiltinMcps || current.DisableBuiltinMcps,
            DisableCustomInstructions:
                persisted.DisableCustomInstructions ||
                current.DisableCustomInstructions,
            DisallowTemporaryDirectory:
                persisted.DisallowTemporaryDirectory ||
                current.DisallowTemporaryDirectory,
            GuardPublicationCredentials:
                persisted.GuardPublicationCredentials ||
                current.GuardPublicationCredentials,
            AllowRemotePublication:
                persisted.AllowRemotePublication &&
                current.AllowRemotePublication,
            GovernedGitMetadataIsolation:
                persisted.GovernedGitMetadataIsolation ||
                current.GovernedGitMetadataIsolation);
    }

    public static bool Equivalent(
        EffectiveExecutionPermission left,
        EffectiveExecutionPermission right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return left.Profile == right.Profile &&
               left.AllowedTools.ToHashSet(StringComparer.Ordinal)
                   .SetEquals(right.AllowedTools) &&
               left.DeniedTools.ToHashSet(StringComparer.Ordinal)
                   .SetEquals(right.DeniedTools) &&
               left.DeniedUrls.ToHashSet(StringComparer.OrdinalIgnoreCase)
                   .SetEquals(right.DeniedUrls) &&
               left.DisableBuiltinMcps == right.DisableBuiltinMcps &&
               left.DisableCustomInstructions == right.DisableCustomInstructions &&
               left.DisallowTemporaryDirectory == right.DisallowTemporaryDirectory &&
               left.GuardPublicationCredentials == right.GuardPublicationCredentials &&
               left.AllowRemotePublication == right.AllowRemotePublication &&
               left.GovernedGitMetadataIsolation ==
               right.GovernedGitMetadataIsolation;
    }

    public static void ValidatePersisted(
        EffectiveExecutionPermission permission,
        ExecutionPermissionProfile recordedProfile,
        PermissionResolutionRequest? request = null)
    {
        ArgumentNullException.ThrowIfNull(permission);
        ValidateShape(permission);
        if (permission.Profile != recordedProfile)
        {
            throw new InvalidOperationException(
                "The persisted effective permission does not match the durable step profile.");
        }
        if (permission.AllowRemotePublication &&
            permission.Profile != ExecutionPermissionProfile.Publish)
        {
            throw new InvalidOperationException(
                "The persisted effective permission contains inconsistent publication authority.");
        }
        if (!permission.AllowRemotePublication &&
            !permission.GuardPublicationCredentials)
        {
            throw new InvalidOperationException(
                "A non-publication permission must guard publication credentials.");
        }
        if (request is not null)
        {
            var publicationShape =
                request.FlowKind == FlowKind.Delivery &&
                request.PlanStage == PlanStage.AfterApproval &&
                request.PlanDuties.Length == 1 &&
                request.PlanDuties[0] == PlanDuty.Publish &&
                request.IsOnlyPlannedPublishStep &&
                request.DurableApproval &&
                request.DurableReviewDecision == ReviewDecision.Accepted;
            if (permission.Profile == ExecutionPermissionProfile.Publish &&
                !publicationShape)
            {
                throw new InvalidOperationException(
                    "The persisted permission grants publication outside the durable approved publication shape.");
            }
            if (publicationShape &&
                (!permission.GuardPublicationCredentials ||
                 permission.AllowedTools.Contains("create") ||
                 permission.AllowedTools.Contains("edit") ||
                 !permission.DeniedTools.Contains("write") ||
                 !permission.DeniedTools.Contains("shell(git commit)") ||
                 !permission.DeniedTools.Contains("shell(git push)") ||
                 permission.DeniedUrls.Length == 0 ||
                 !permission.GovernedGitMetadataIsolation))
            {
                throw new InvalidOperationException(
                    "The persisted Publish policy is not host-controlled and source-sealed.");
            }
            if ((request.FlowKind == FlowKind.Advisory ||
                 IsReadOnlyLifecycleInvocation(request.InvocationKind)) &&
                PermissionRank(permission.Profile) >
                PermissionRank(ExecutionPermissionProfile.ReadOnlySource))
            {
                throw new InvalidOperationException(
                    "The persisted permission exceeds the flow or lifecycle read-only boundary.");
            }
        }
    }

    internal static bool GrantsRemotePublication(
        EffectiveExecutionPermission permission) =>
        permission.Profile == ExecutionPermissionProfile.Publish &&
        permission.AllowRemotePublication;

    private static ExecutionPermissionProfile ResolveRequestedProfile(
        PermissionResolutionRequest request)
    {
        if (request.InvocationKind == ExecutionInvocationKind.PreMortem)
        {
            if (request.PlanStage != PlanStage.BeforeReview ||
                request.PlanDuties.Contains(PlanDuty.Publish))
            {
                throw new InvalidOperationException(
                    "Pre-mortem execution must be a pre-review, non-publication step.");
            }
            return ExecutionPermissionProfile.PreMortemReadOnly;
        }

        if (IsReadOnlyLifecycleInvocation(request.InvocationKind))
        {
            if (request.PlanStage != PlanStage.BeforeReview ||
                request.PlanDuties.Any(
                    duty => duty is
                        PlanDuty.Implement or
                        PlanDuty.Verify or
                        PlanDuty.Publish))
            {
                throw new InvalidOperationException(
                    "Intake, planning, review classification, and blocker explanation must remain read-only before review.");
            }
            return ExecutionPermissionProfile.ReadOnlySource;
        }

        if (request.FlowKind == FlowKind.Advisory)
        {
            if (request.PlanStage != PlanStage.BeforeReview ||
                request.PlanDuties.Any(
                    duty => duty is PlanDuty.Implement or PlanDuty.Publish))
            {
                throw new InvalidOperationException(
                    "Advisory execution cannot implement, publish, or run after approval.");
            }
            return ExecutionPermissionProfile.ReadOnlySource;
        }

        if (request.PlanStage == PlanStage.AfterApproval)
        {
            if (request.InvocationKind != ExecutionInvocationKind.Publication ||
                request.PlanDuties.Length != 1 ||
                request.PlanDuties[0] != PlanDuty.Publish ||
                !request.IsOnlyPlannedPublishStep ||
                !request.DurableApproval ||
                request.DurableReviewDecision != ReviewDecision.Accepted)
            {
                throw new InvalidOperationException(
                    "Publish requires the sole planned Publish duty after durable Delivery acceptance.");
            }
            return ExecutionPermissionProfile.Publish;
        }

        if (request.InvocationKind == ExecutionInvocationKind.Publication)
        {
            throw new InvalidOperationException(
                "Publication invocation is valid only after durable Delivery acceptance.");
        }
        if (request.PlanDuties.Contains(PlanDuty.Publish))
        {
            throw new InvalidOperationException(
                "Publish duty is valid only in the planned after-approval step.");
        }
        if (request.DurableApproval)
        {
            throw new InvalidOperationException(
                "A pre-review execution cannot consume publication approval.");
        }

        return request.PlanDuties.Any(
            duty => duty is
                PlanDuty.Implement or
                PlanDuty.Verify or
                PlanDuty.PrepareOutcome)
            ? ExecutionPermissionProfile.WorkspaceWrite
            : ExecutionPermissionProfile.ReadOnlySource;
    }

    private static ExecutionPermissionProfile ResolveCeiling(
        PermissionResolutionRequest request,
        WorkflowPermissionRestrictions workflow) =>
        request.InvocationKind == ExecutionInvocationKind.PreMortem
            ? ExecutionPermissionProfile.PreMortemReadOnly
            : IsReadOnlyLifecycleInvocation(request.InvocationKind)
                ? ExecutionPermissionProfile.ReadOnlySource
            : request.FlowKind == FlowKind.Advisory
                ? workflow.AdvisoryMaximum
                : request.PlanStage == PlanStage.AfterApproval
                    ? workflow.DeliveryPostApprovalMaximum
                    : workflow.DeliveryPreReviewMaximum;

    private static bool IsReadOnlyLifecycleInvocation(
        ExecutionInvocationKind invocationKind) =>
        invocationKind is
            ExecutionInvocationKind.Intake or
            ExecutionInvocationKind.Planning or
            ExecutionInvocationKind.PreMortem or
            ExecutionInvocationKind.BlockerExplanation;

    private static int PermissionRank(ExecutionPermissionProfile profile) => profile switch
    {
        ExecutionPermissionProfile.ReadOnlySource => 0,
        ExecutionPermissionProfile.PreMortemReadOnly => 0,
        ExecutionPermissionProfile.WorkspaceWrite => 1,
        ExecutionPermissionProfile.Publish => 2,
        _ => throw new InvalidOperationException(
            $"Unknown execution permission profile '{profile}'.")
    };

    private static void ValidateShape(EffectiveExecutionPermission permission)
    {
        if (!Enum.IsDefined(permission.Profile) ||
            permission.AllowedTools.IsDefault ||
            permission.DeniedTools.IsDefault ||
            permission.DeniedUrls.IsDefault ||
            permission.AllowedTools.Any(string.IsNullOrWhiteSpace) ||
            permission.DeniedTools.Any(string.IsNullOrWhiteSpace) ||
            permission.DeniedUrls.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException(
                "The effective permission document is invalid.");
        }
    }

    private static ImmutableArray<string> Clean(IEnumerable<string> values) =>
        values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray();
}
