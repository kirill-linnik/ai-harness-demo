using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Core.Verification;
using AiHarnessDemo.Core.Workflow;
using System.Text.RegularExpressions;

namespace AiHarnessDemo.Core.Orchestration;

public sealed record TeamPlanValidationContext(
    FlowKind FlowKind,
    IReadOnlyCollection<FlowAgentSnapshot> AgentSnapshots,
    bool PreMortemEnabled,
    int MaximumSteps,
    int MaximumDependenciesPerStep,
    int MaximumAssignmentCharacters,
    IReadOnlyCollection<PlanDuty> RequiredDuties)
{
    public static TeamPlanValidationContext FromWorkflow(
        FlowKind flowKind,
        IReadOnlyCollection<FlowAgentSnapshot> snapshots,
        bool preMortemEnabled,
        WorkflowDefinition workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        var planning = workflow.Config.Studio.Planning;
        var required = flowKind == FlowKind.Advisory
            ? workflow.Config.Studio.FlowKinds.Advisory.RequiredDuties
            : workflow.Config.Studio.FlowKinds.Delivery.RequiredDuties;
        return new TeamPlanValidationContext(
            flowKind,
            snapshots,
            preMortemEnabled,
            planning.MaxSteps,
            planning.MaxDependenciesPerStep,
            planning.MaxAssignmentCharacters,
            required);
    }

    public static TeamPlanValidationContext ForPersistedPlan(
        FlowKind flowKind,
        IReadOnlyCollection<FlowAgentSnapshot> snapshots,
        bool preMortemEnabled) =>
        new(
            flowKind,
            snapshots,
            preMortemEnabled,
            MaximumSteps: 24,
            MaximumDependenciesPerStep: 8,
            MaximumAssignmentCharacters: 4_000,
            RequiredDuties: flowKind == FlowKind.Advisory
                ? [PlanDuty.PrepareOutcome]
                :
                [
                    PlanDuty.Implement,
                    PlanDuty.Verify,
                    PlanDuty.PrepareOutcome,
                    PlanDuty.Publish
                ]);
}

public sealed record ValidatedTeamPlan(
    TeamPlanDocument Document,
    IReadOnlyList<TeamPlanStep> OrderedSteps,
    TeamPlanStep? OutcomeOwner,
    TeamPlanStep? PublicationStep)
{
    public bool IsMissingQualification =>
        Document.Disposition == TeamPlanDisposition.MissingQualification;
}

public sealed class TeamPlanValidator
{
    public const int MaximumIdentifierCharacters = 120;
    public const int MaximumJustificationCharacters = 4_000;
    public const int MaximumMissingQualifications = 8;
    public const int MaximumQualificationTextCharacters = 1_200;
    public const int MaximumSuggestedNameCharacters = 200;
    public const int MaximumSuggestedDescriptionCharacters = 1_200;
    public const int MaximumOrder = 1_000_000;

    private static readonly HashSet<string> CoreAgentIds =
        new(StringComparer.Ordinal)
        {
            "account-manager",
            "team-lead",
            "pre-mortem-sceptic"
        };
    private static readonly Regex CanonicalExternalStepIdPattern = new(
        "^[a-z0-9]+(?:-[a-z0-9]+)*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public ValidatedTeamPlan Validate(
        TeamPlanDocument document,
        TeamPlanValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(context);

        var errors = new List<string>();
        ValidateContext(context, errors);

        if (document.Steps?.Any(step => step is null) == true)
        {
            errors.Add("steps must not contain null entries");
        }
        var steps = document.Steps?.OfType<TeamPlanStep>().ToList() ?? [];
        var checkpoints = document.PreMortemCheckpoints?.ToList() ?? [];
        if (document.Steps is null)
        {
            errors.Add("steps is required");
        }
        if (document.PreMortemCheckpoints is null)
        {
            errors.Add("preMortemCheckpoints is required");
        }

        if (document.Disposition == TeamPlanDisposition.MissingQualification)
        {
            if (steps.Count != 0)
            {
                errors.Add("missing qualification must contain no steps");
            }
            if (checkpoints.Count != 0)
            {
                errors.Add("missing qualification must contain no pre-mortem checkpoints");
            }
            ValidateMissingQualification(document.MissingQualification, errors);
            ThrowIfInvalid(errors);
            return new ValidatedTeamPlan(document, [], null, null);
        }

        if (document.Disposition != TeamPlanDisposition.Planned)
        {
            errors.Add("disposition must be Planned or MissingQualification");
        }
        if (document.MissingQualification is not null)
        {
            errors.Add("a planned result must set missingQualification to null");
        }
        if (steps.Count is < 1 || steps.Count > context.MaximumSteps)
        {
            errors.Add($"steps must contain 1-{context.MaximumSteps} entries");
        }

        var enabledSnapshots = context.AgentSnapshots
            .Where(snapshot => snapshot.EnabledAtSnapshot)
            .GroupBy(snapshot => snapshot.AgentId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var indexedSteps = steps
            .Select((step, index) => new IndexedStep(step, index))
            .OrderBy(item => item.Step.Order)
            .ThenBy(item => item.Index)
            .ToList();
        var stepById = new Dictionary<string, TeamPlanStep>(
            StringComparer.OrdinalIgnoreCase);
        var orderById = new Dictionary<string, int>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var (step, index) in steps.Select((step, index) => (step, index)))
        {
            var prefix = $"step {index + 1}";
            if (!ValidateBoundedText(
                    step.Id,
                    1,
                    MaximumIdentifierCharacters,
                    $"{prefix} id",
                    errors))
            {
                continue;
            }
            if (!CanonicalExternalStepIdPattern.IsMatch(step.Id))
            {
                errors.Add(
                    $"{prefix} id must use canonical lowercase kebab-case");
            }
            if (IsReservedHostStepId(step.Id))
            {
                errors.Add(
                    $"{prefix} id '{step.Id}' is reserved for host lifecycle steps");
            }
            if (!stepById.TryAdd(step.Id, step))
            {
                errors.Add($"plan-step id '{step.Id}' must be unique");
            }
            else
            {
                orderById[step.Id] = step.Order;
            }
        }

        foreach (var (step, index) in steps.Select((step, index) => (step, index)))
        {
            ValidateStep(
                step,
                index,
                context,
                enabledSnapshots,
                stepById,
                orderById,
                errors);
        }

        ValidateAcyclic(stepById, errors);
        ValidateCheckpoints(
            checkpoints,
            stepById,
            context.PreMortemEnabled,
            context.MaximumSteps,
            errors);
        ValidateOutcomeAndFlowRules(steps, context, errors);
        ValidateAcceptanceCriteria(document, context, errors);

        ThrowIfInvalid(errors);
        var ordered = indexedSteps.Select(item => item.Step).ToArray();
        return new ValidatedTeamPlan(
            document,
            ordered,
            ordered.Single(step => step.OutcomeOwner),
            ordered.SingleOrDefault(step =>
                step.Stage == PlanStage.AfterApproval &&
                step.Duties?.SequenceEqual([PlanDuty.Publish]) == true));
    }

    /// <summary>
    /// A Delivery plan must publish typed acceptance criteria. They are the only criterion
    /// namespace that later verification may use, so a missing or malformed plan is rejected here
    /// rather than being reconstructed from prose later.
    /// </summary>
    private static void ValidateAcceptanceCriteria(
        TeamPlanDocument document,
        TeamPlanValidationContext context,
        ICollection<string> errors)
    {
        if (context.FlowKind != FlowKind.Delivery)
        {
            if (document.AcceptanceCriteria is not null)
            {
                errors.Add(
                    "acceptanceCriteria is accepted only for Delivery plans");
            }
            return;
        }
        if (document.AcceptanceCriteria is null)
        {
            errors.Add(
                "acceptanceCriteria is required for a Delivery plan and must define every " +
                "customer-visible success criterion with a stable AC-000 identifier");
            return;
        }
        foreach (var error in DeliveryReadinessPolicy.ValidateAcceptancePlan(
                     new DeliveryAcceptancePlan(document.AcceptanceCriteria)))
        {
            errors.Add(error);
        }
    }

    public static bool IsDependencyAncestor(
        TeamPlanDocument document,
        string ancestorStepId,
        string descendantStepId)
    {
        var steps = (document.Steps ?? [])
            .ToDictionary(step => step.Id, StringComparer.OrdinalIgnoreCase);
        if (!steps.ContainsKey(ancestorStepId) ||
            !steps.TryGetValue(descendantStepId, out var descendant) ||
            string.Equals(ancestorStepId, descendantStepId, StringComparison.Ordinal))
        {
            return false;
        }

        var pending = new Stack<string>(descendant.DependsOn ?? []);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.TryPop(out var candidate))
        {
            if (!visited.Add(candidate))
            {
                continue;
            }
            if (string.Equals(
                    candidate,
                    ancestorStepId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (steps.TryGetValue(candidate, out var step))
            {
                foreach (var dependency in step.DependsOn ?? [])
                {
                    pending.Push(dependency);
                }
            }
        }
        return false;
    }

    private static void ValidateContext(
        TeamPlanValidationContext context,
        ICollection<string> errors)
    {
        if (!Enum.IsDefined(context.FlowKind))
        {
            errors.Add("flow kind is invalid");
        }
        if (context.MaximumSteps is < 1 or > 24)
        {
            errors.Add("maximum steps must be from 1 through 24");
        }
        if (context.MaximumDependenciesPerStep is < 0 or > 8)
        {
            errors.Add("maximum dependencies per step must be from 0 through 8");
        }
        if (context.MaximumAssignmentCharacters is < 1 or > 4_000)
        {
            errors.Add("maximum assignment characters must be from 1 through 4000");
        }
        if (context.RequiredDuties is null ||
            context.RequiredDuties.Any(duty => !Enum.IsDefined(duty)))
        {
            errors.Add("configured required duties contain an invalid value");
        }
    }

    private static void ValidateStep(
        TeamPlanStep step,
        int index,
        TeamPlanValidationContext context,
        IReadOnlyDictionary<string, FlowAgentSnapshot> enabledSnapshots,
        IReadOnlyDictionary<string, TeamPlanStep> stepById,
        IReadOnlyDictionary<string, int> orderById,
        ICollection<string> errors)
    {
        var prefix = string.IsNullOrWhiteSpace(step.Id)
            ? $"step {index + 1}"
            : $"step '{step.Id}'";
        ValidateBoundedText(
            step.AgentId,
            1,
            MaximumIdentifierCharacters,
            $"{prefix} agentId",
            errors);
        if (CoreAgentIds.Contains(step.AgentId))
        {
            errors.Add($"{prefix} cannot select core agent '{step.AgentId}' as a worker");
        }
        if (!enabledSnapshots.TryGetValue(step.AgentId, out var snapshot))
        {
            errors.Add(
                $"{prefix} selects '{step.AgentId}', which is not enabled in the immutable flow snapshot");
        }

        if (step.Order is < 1 or > MaximumOrder)
        {
            errors.Add($"{prefix} order must be from 1 through {MaximumOrder}");
        }
        if (!Enum.IsDefined(step.Stage))
        {
            errors.Add($"{prefix} stage is invalid");
        }
        ValidateBoundedText(
            step.Assignment,
            1,
            context.MaximumAssignmentCharacters,
            $"{prefix} assignment",
            errors);
        ValidateBoundedText(
            step.Justification,
            1,
            MaximumJustificationCharacters,
            $"{prefix} justification",
            errors);

        var dependencies = step.DependsOn?.ToList() ?? [];
        if (step.DependsOn is null)
        {
            errors.Add($"{prefix} dependsOn is required");
        }
        else
        {
            if (dependencies.Count > context.MaximumDependenciesPerStep)
            {
                errors.Add(
                    $"{prefix} dependsOn must contain at most " +
                    $"{context.MaximumDependenciesPerStep} entries");
            }
            if (dependencies.Count !=
                dependencies.Distinct(StringComparer.OrdinalIgnoreCase).Count())
            {
                errors.Add($"{prefix} dependsOn must not contain duplicates");
            }
            foreach (var dependency in dependencies)
            {
                if (!ValidateBoundedText(
                    dependency,
                    1,
                    MaximumIdentifierCharacters,
                    $"{prefix} dependency",
                    errors))
                {
                    continue;
                }
                if (!CanonicalExternalStepIdPattern.IsMatch(dependency) ||
                    IsReservedHostStepId(dependency))
                {
                    errors.Add(
                        $"{prefix} dependency '{dependency}' must use a canonical external plan-step ID");
                }
                if (!stepById.TryGetValue(
                        dependency,
                        out var dependencyStep))
                {
                    errors.Add($"{prefix} dependency '{dependency}' does not name a plan step");
                    continue;
                }
                if (!string.Equals(
                        dependency,
                        dependencyStep.Id,
                        StringComparison.Ordinal))
                {
                    errors.Add(
                        $"{prefix} dependency '{dependency}' must use the exact canonical plan-step ID '{dependencyStep.Id}'");
                }
                if (string.Equals(
                        dependency,
                        step.Id,
                        StringComparison.OrdinalIgnoreCase) ||
                    orderById.GetValueOrDefault(dependency) >= step.Order)
                {
                    errors.Add(
                        $"{prefix} dependency '{dependency}' must refer to a step with an earlier order");
                }
            }
        }

        var duties = step.Duties?.ToList() ?? [];
        if (step.Duties is null || duties.Count == 0)
        {
            errors.Add($"{prefix} duties must contain at least one duty");
        }
        else
        {
            if (duties.Count > Enum.GetValues<PlanDuty>().Length)
            {
                errors.Add(
                    $"{prefix} duties must contain at most {Enum.GetValues<PlanDuty>().Length} entries");
            }
            if (duties.Any(duty => !Enum.IsDefined(duty)))
            {
                errors.Add($"{prefix} duties contain an invalid value");
            }
            if (duties.Count != duties.Distinct().Count())
            {
                errors.Add($"{prefix} duties must not contain duplicates");
            }
        }

        ValidateProfile(step, snapshot, errors);
    }

    private static void ValidateProfile(
        TeamPlanStep step,
        FlowAgentSnapshot? snapshot,
        ICollection<string> errors)
    {
        var prefix = $"step '{step.Id}' taskProfile";
        if (step.TaskProfile is null)
        {
            errors.Add($"{prefix} is required");
            return;
        }

        var isPublishOnly =
            step.Stage == PlanStage.AfterApproval &&
            step.Duties?.SequenceEqual([PlanDuty.Publish]) == true;
        if (step.TaskProfile.IsEmpty && isPublishOnly)
        {
            return;
        }
        if (step.TaskProfile.IsEmpty)
        {
            errors.Add($"{prefix} must contain a complete bounded profile");
            return;
        }
        if (step.TaskProfile.Complexity is null ||
            step.TaskProfile.ReasoningDepth is null ||
            step.TaskProfile.ContextDemand is null ||
            step.TaskProfile.ToolIntensity is null ||
            step.TaskProfile.TaskTypeTags is null ||
            step.TaskProfile.Risk is null ||
            step.TaskProfile.RiskReason is null ||
            step.TaskProfile.Confidence is null ||
            step.TaskProfile.Rationales is null)
        {
            errors.Add($"{prefix} must either be empty for Publish or contain every profile property");
            return;
        }

        var input = new TaskProfileInput(
            snapshot?.Role ?? step.AgentId,
            step.TaskProfile.Complexity.Value,
            step.TaskProfile.ReasoningDepth.Value,
            step.TaskProfile.ContextDemand.Value,
            step.TaskProfile.ToolIntensity.Value,
            step.TaskProfile.TaskTypeTags.Select(tag => tag.ToString()).ToArray(),
            step.TaskProfile.Risk.Value.ToString(),
            step.TaskProfile.RiskReason,
            step.TaskProfile.Confidence.Value,
            step.TaskProfile.Rationales);
        foreach (var error in TaskProfileRules.Validate(input))
        {
            errors.Add($"{prefix}: {error}");
        }
    }

    private static void ValidateAcyclic(
        IReadOnlyDictionary<string, TeamPlanStep> stepById,
        ICollection<string> errors)
    {
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool Visit(string id)
        {
            if (visited.Contains(id))
            {
                return false;
            }
            if (!visiting.Add(id))
            {
                return true;
            }
            if (stepById.TryGetValue(id, out var step))
            {
                foreach (var dependency in step.DependsOn ?? [])
                {
                    if (stepById.ContainsKey(dependency) && Visit(dependency))
                    {
                        return true;
                    }
                }
            }
            visiting.Remove(id);
            visited.Add(id);
            return false;
        }

        if (stepById.Keys.Any(Visit))
        {
            errors.Add("step dependencies must be acyclic");
        }
    }

    private static void ValidateCheckpoints(
        IReadOnlyList<string> checkpoints,
        IReadOnlyDictionary<string, TeamPlanStep> stepById,
        bool preMortemEnabled,
        int maximumCheckpoints,
        ICollection<string> errors)
    {
        if (checkpoints.Count !=
            checkpoints.Distinct(StringComparer.OrdinalIgnoreCase).Count())
        {
            errors.Add("preMortemCheckpoints must not contain duplicates");
        }
        if (checkpoints.Count > maximumCheckpoints)
        {
            errors.Add(
                $"preMortemCheckpoints must contain at most {maximumCheckpoints} entries");
        }
        if (!preMortemEnabled && checkpoints.Count > 0)
        {
            errors.Add(
                "preMortemCheckpoints must be empty because the Pre-mortem Sceptic is disabled in the flow snapshot");
        }
        foreach (var checkpoint in checkpoints)
        {
            if (!ValidateBoundedText(
                checkpoint,
                1,
                MaximumIdentifierCharacters,
                "pre-mortem checkpoint",
                errors))
            {
                continue;
            }
            if (!CanonicalExternalStepIdPattern.IsMatch(checkpoint) ||
                IsReservedHostStepId(checkpoint))
            {
                errors.Add(
                    $"pre-mortem checkpoint '{checkpoint}' must use a canonical external plan-step ID");
            }
            if (!stepById.TryGetValue(checkpoint, out var step))
            {
                errors.Add(
                    $"pre-mortem checkpoint '{checkpoint}' does not name a plan step");
            }
            else if (!string.Equals(
                         checkpoint,
                         step.Id,
                         StringComparison.Ordinal))
            {
                errors.Add(
                    $"pre-mortem checkpoint '{checkpoint}' must use the exact canonical plan-step ID '{step.Id}'");
            }
            else if (step.Stage != PlanStage.BeforeReview)
            {
                errors.Add(
                    $"pre-mortem checkpoint '{checkpoint}' must name a BeforeReview step");
            }
        }
    }

    private static void ValidateOutcomeAndFlowRules(
        IReadOnlyList<TeamPlanStep> steps,
        TeamPlanValidationContext context,
        ICollection<string> errors)
    {
        var preReview = steps
            .Where(step => step.Stage == PlanStage.BeforeReview)
            .OrderBy(step => step.Order)
            .ToList();
        var owners = preReview.Where(step => step.OutcomeOwner).ToList();
        if (owners.Count != 1)
        {
            errors.Add("exactly one BeforeReview step must be the outcome owner");
        }
        if (steps.Any(step =>
                step.Stage == PlanStage.AfterApproval && step.OutcomeOwner))
        {
            errors.Add("an AfterApproval step cannot be the outcome owner");
        }
        if (owners.Count == 1)
        {
            var owner = owners[0];
            if (owner.Duties?.Contains(PlanDuty.PrepareOutcome) != true)
            {
                errors.Add("the outcome owner must contain PrepareOutcome");
            }
            if (preReview.Count == 0 ||
                !ReferenceEquals(owner, preReview[^1]))
            {
                errors.Add("the outcome owner must be the final BeforeReview worker");
            }
        }

        var allDuties = steps
            .SelectMany(step => step.Duties ?? [])
            .ToHashSet();
        foreach (var requiredDuty in context.RequiredDuties.Distinct())
        {
            if (!allDuties.Contains(requiredDuty))
            {
                errors.Add(
                    $"configured required duty '{requiredDuty}' is missing from the plan");
            }
        }

        if (context.FlowKind == FlowKind.Advisory)
        {
            if (preReview.Count == 0)
            {
                errors.Add("an Advisory plan requires at least one worker");
            }
            if (!allDuties.Contains(PlanDuty.PrepareOutcome))
            {
                errors.Add("an Advisory plan requires PrepareOutcome");
            }
            if (steps.Any(step => step.Stage == PlanStage.AfterApproval))
            {
                errors.Add("an Advisory plan cannot contain AfterApproval steps");
            }
            if (allDuties.Contains(PlanDuty.Implement) ||
                allDuties.Contains(PlanDuty.Publish))
            {
                errors.Add("an Advisory plan cannot contain Implement or Publish");
            }
            return;
        }

        foreach (var duty in new[]
                 {
                     PlanDuty.Implement,
                     PlanDuty.Verify,
                     PlanDuty.PrepareOutcome,
                     PlanDuty.Publish
                 })
        {
            if (!allDuties.Contains(duty))
            {
                errors.Add($"a Delivery plan requires {duty}");
            }
        }
        if (preReview.Any(step => step.Duties?.Contains(PlanDuty.Publish) == true))
        {
            errors.Add("a Delivery plan cannot Publish before review");
        }
        var verificationSteps = preReview
            .Where(step => step.Duties?.Contains(PlanDuty.Verify) == true)
            .ToList();
        if (verificationSteps.Count != 1 ||
            owners.Count != 1 ||
            !ReferenceEquals(verificationSteps.SingleOrDefault(), owners[0]))
        {
            errors.Add(
                "a Delivery plan requires its final outcome owner to be the sole BeforeReview Verify-duty step");
        }
        var afterApproval = steps
            .Where(step => step.Stage == PlanStage.AfterApproval)
            .ToList();
        if (afterApproval.Count != 1 ||
            afterApproval[0].Duties is null ||
            afterApproval[0].Duties!.Count != 1 ||
            afterApproval[0].Duties![0] != PlanDuty.Publish)
        {
            errors.Add(
                "a Delivery plan requires exactly one AfterApproval Publish-only step");
        }
        if (steps.Count(step =>
                step.Duties?.Contains(PlanDuty.Publish) == true) != 1)
        {
            errors.Add("a Delivery plan must contain exactly one publication step");
        }
    }

    private static void ValidateMissingQualification(
        MissingQualification? qualification,
        ICollection<string> errors)
    {
        if (qualification is null)
        {
            errors.Add("missingQualification is required for MissingQualification disposition");
            return;
        }
        ValidateBoundedText(
            qualification.Summary,
            1,
            MaximumQualificationTextCharacters,
            "missingQualification summary",
            errors);
        if (qualification.Missing is null ||
            qualification.Missing.Count is < 1 or > MaximumMissingQualifications)
        {
            errors.Add(
                $"missingQualification missing must contain 1-{MaximumMissingQualifications} entries");
        }
        else
        {
            foreach (var (item, index) in qualification.Missing.Select(
                         (item, index) => (item, index)))
            {
                ValidateBoundedText(
                    item,
                    1,
                    MaximumQualificationTextCharacters,
                    $"missingQualification missing item {index + 1}",
                    errors);
            }
        }
        ValidateBoundedText(
            qualification.WhyRequired,
            1,
            MaximumQualificationTextCharacters,
            "missingQualification whyRequired",
            errors);
        if (qualification.SuggestedAgent is null)
        {
            errors.Add("missingQualification suggestedAgent is required");
            return;
        }
        ValidateBoundedText(
            qualification.SuggestedAgent.Id,
            1,
            MaximumIdentifierCharacters,
            "suggestedAgent id",
            errors);
        ValidateBoundedText(
            qualification.SuggestedAgent.Name,
            1,
            MaximumSuggestedNameCharacters,
            "suggestedAgent name",
            errors);
        ValidateBoundedText(
            qualification.SuggestedAgent.Description,
            1,
            MaximumSuggestedDescriptionCharacters,
            "suggestedAgent description",
            errors);
    }

    private static bool ValidateBoundedText(
        string? value,
        int minimum,
        int maximum,
        string label,
        ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length < minimum ||
            value.Length > maximum)
        {
            errors.Add($"{label} must contain {minimum}-{maximum} characters");
            return false;
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            errors.Add($"{label} must be trimmed and contain no control characters");
            return false;
        }
        return true;
    }

    private static bool IsReservedHostStepId(string value) =>
        string.Equals(value, "team-plan", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith(
            "account-manager:",
            StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith(
            "pre-mortem:",
            StringComparison.OrdinalIgnoreCase);

    private static void ThrowIfInvalid(IReadOnlyList<string> errors)
    {
        if (errors.Count > 0)
        {
            throw new TeamPlanContractException(errors);
        }
    }

    private sealed record IndexedStep(TeamPlanStep Step, int Index);
}
