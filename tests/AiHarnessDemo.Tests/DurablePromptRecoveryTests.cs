using AiHarnessDemo.Contracts;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Orchestration;
using System.Text;
using AiHarnessDemo.Core.Reasoning;
using AiHarnessDemo.Core.Verification;
using AiHarnessDemo.Core.Workflow;
using AiHarnessDemo.Data;
using AiHarnessDemo.Infrastructure;
using AiHarnessDemo.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiHarnessDemo.Tests;

public sealed class DurablePromptRecoveryTests
{
    [Fact]
    public async Task ResponseCorrection_LeadsWithTheErrorAndStagesOnlyCorrectionInputs()
    {
        await using var fixture = await DurablePromptFixture.CreateAsync(executionPrompt: string.Empty);
        var workflow = await fixture.ReloadWorkflowAsync("{{ task }}\n{{ agent.instructions }}\n{{ outcome.context }}");
        var stager = new AgentManifestStager();
        var manifest = await stager.StageAsync(fixture.CopilotHome, DurablePromptFixture.Manifest(), fixture.SessionId);
        const string error = "Validation error: AC-003 cited a SourceInspection where a Command is required.";
        var context = fixture.Context with
        {
            ResumeSession = true,
            Task = "Retained response to correct:\n" + new string('r', 12_000),
            RepositoryKnowledge = new string('k', 20_000),
            OutcomeContext = "Unchanged accepted criteria and exact evidence.\n" + new string('e', 12_000),
            OutcomeContract = "Return the exact unchanged response schema.",
            ResponseCorrectionInstructions = error,
            StudioDependencyOutputs = [new("implementation", "engineer", StudioDependencyKind.Direct, 1, 1, 10, new string('h', 20_000))]
        };
        var prepared = await AgentPromptContext.PrepareAsync(context, new string('i', 6_000),
            workflow.Revision, manifest, stager, fixture.Factory, CancellationToken.None);
        var instructions = await CopilotReasoningHost.RenderAndPersistExecutionInstructionsAsync(
            prepared.Context, workflow, prepared.AgentInstructions, fixture.WorkspacePath, null,
            new WorkflowPromptRenderer(), fixture.Factory, CancellationToken.None, prepared);

        Assert.StartsWith("## Correct the previous response in this session", instructions.Prompt);
        Assert.True(instructions.Prompt.IndexOf(error, StringComparison.Ordinal) < 200);
        Assert.True(Encoding.UTF8.GetByteCount(instructions.Prompt) < 8_000);
        Assert.All(prepared.DocumentPaths, path => Assert.Contains(path, instructions.Prompt));
        Assert.DoesNotContain(prepared.DocumentPaths, path => path.EndsWith("repository-knowledge.md", StringComparison.Ordinal));
        Assert.DoesNotContain(prepared.DocumentPaths, path => path.EndsWith("customer-input.md", StringComparison.Ordinal));
        Assert.DoesNotContain(prepared.DocumentPaths, path => path.EndsWith("handoff-01.md", StringComparison.Ordinal));
        Assert.Contains(prepared.DocumentPaths, path => path.EndsWith("assignment.md", StringComparison.Ordinal));
        Assert.Contains(prepared.DocumentPaths, path => path.EndsWith("response-contract.md", StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() => CopilotReasoningHost.BuildResponseCorrectionPrompt(
            context with { ResumeSession = false }, prepared.DocumentPaths));
    }

    [Fact]
    public async Task LongIntakeSubmission_StagesFullDialogueInsteadOfDroppingLaterTalks()
    {
        await using var fixture = await DurablePromptFixture.CreateAsync(
            executionPrompt: string.Empty,
            invocationKind: ExecutionInvocationKind.Intake);
        const string secondTalk =
            "SECOND_TALK_ABSTRACT: Keep the source-tracked project decisions in full.";
        var submission = "Create a meeting with two speakers.\n" +
            new string('a', 120_000) + "\n" + secondTalk;
        var dialogue = IntakeCoordinator.BuildDialogueTask(
            [
                new FlowMessage
                {
                    Role = ConversationRole.Customer,
                    Content = submission
                }
            ],
            OutcomeType.PullRequest);
        var workflow = await fixture.ReloadWorkflowAsync(
            "{{ task }}\n{{ agent.instructions }}");
        var stager = new AgentManifestStager();
        var manifest = await stager.StageAsync(
            fixture.CopilotHome, DurablePromptFixture.Manifest(), fixture.SessionId);

        var prepared = await AgentPromptContext.PrepareAsync(
            fixture.Context with
            {
                InvocationKind = ExecutionInvocationKind.Intake,
                Task = dialogue
            },
            "Capture all relevant speaker details.",
            workflow.Revision, manifest, stager, fixture.Factory,
            CancellationToken.None);
        var assignment = Assert.Single(
            prepared.DocumentPaths,
            path => path.EndsWith("assignment.md", StringComparison.Ordinal));
        Assert.Contains(secondTalk, await File.ReadAllTextAsync(assignment));
        var instructions = await CopilotReasoningHost.RenderAndPersistExecutionInstructionsAsync(
            prepared.Context, workflow, prepared.AgentInstructions, fixture.WorkspacePath,
            null, new WorkflowPromptRenderer(), fixture.Factory,
            CancellationToken.None, prepared);

        Assert.Contains(assignment, instructions.Prompt);
        Assert.DoesNotContain(secondTalk, instructions.Prompt);
        Assert.True(Encoding.UTF8.GetByteCount(instructions.Prompt) <=
            AgentPromptContext.MaximumWorkingPromptBytes);
    }

    [Fact]
    public async Task LargeCustomerSubmission_DoesNotExceedTheWorkingPromptOrLoseRelevantFacts()
    {
        await using var fixture = await DurablePromptFixture.CreateAsync(
            executionPrompt: string.Empty);
        const string essential = "SECOND_SPEAKER_TALK: Keep the entire supplied abstract.";
        var submitted = "Speaker submission form:\n" +
            new string('x', 120_000) + "\n" + essential;
        await using (var database = await fixture.Factory.CreateDbContextAsync())
        {
            var flow = await database.Flows.SingleAsync(item => item.Id == fixture.FlowId);
            flow.OriginalRequest = submitted;
            await database.SaveChangesAsync();
        }
        var workflow = await fixture.ReloadWorkflowAsync(
            "{{ task }}\n{{ agent.instructions }}\n{{ outcome.context }}");
        var stager = new AgentManifestStager();
        var manifest = await stager.StageAsync(
            fixture.CopilotHome, DurablePromptFixture.Manifest(), fixture.SessionId);

        var prepared = await AgentPromptContext.PrepareAsync(
            fixture.Context with { Task = "Create the meeting from the submitted talks." },
            "Use the relevant exact customer facts.",
            workflow.Revision, manifest, stager, fixture.Factory,
            CancellationToken.None);
        var customerPath = Assert.Single(
            prepared.DocumentPaths,
            path => path.EndsWith(
                AgentPromptContext.CustomerInputFileName,
                StringComparison.Ordinal));
        var contents = await File.ReadAllTextAsync(customerPath);
        Assert.Contains(submitted, contents);
        Assert.Contains(essential, contents);
        var instructions = await CopilotReasoningHost.RenderAndPersistExecutionInstructionsAsync(
            prepared.Context, workflow, prepared.AgentInstructions, fixture.WorkspacePath,
            null, new WorkflowPromptRenderer(), fixture.Factory,
            CancellationToken.None, prepared);

        Assert.True(Encoding.UTF8.GetByteCount(instructions.Prompt) <=
            AgentPromptContext.MaximumWorkingPromptBytes);
        Assert.Contains(customerPath, instructions.Prompt);
        Assert.DoesNotContain(essential, instructions.Prompt);
        Assert.Contains(essential, prepared.SnapshotJson);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Publication_StagedKnowledgeRetainsProjectNameForRecap(
        bool hasKnowledgeTitle)
    {
        await using var fixture = await DurablePromptFixture.CreateAsync(
            executionPrompt: string.Empty,
            invocationKind: ExecutionInvocationKind.Publication);
        var projectName = hasKnowledgeTitle
            ? "Example project"
            : Path.GetFileName(fixture.Root);
        var knowledge = (hasKnowledgeTitle ? "# Example project\n\n" : "") +
            new string('k', 6_000);
        var workflow = await fixture.ReloadWorkflowAsync(
            "{{ task }}\n{{ agent.instructions }}\n{{ role.context }}\n{{ response.contract }}");
        var stager = new AgentManifestStager();
        var manifest = await stager.StageAsync(
            fixture.CopilotHome, DurablePromptFixture.Manifest(), fixture.SessionId);

        var prepared = await AgentPromptContext.PrepareAsync(
            fixture.Context with { RepositoryKnowledge = knowledge },
            "Publish the reviewed candidate.",
            workflow.Revision, manifest, stager, fixture.Factory,
            CancellationToken.None);
        var knowledgePath = Assert.Single(
            prepared.DocumentPaths,
            path => path.EndsWith("repository-knowledge.md", StringComparison.Ordinal));
        Assert.Equal(knowledge, await File.ReadAllTextAsync(knowledgePath));
        Assert.Empty(prepared.Context.SourceProjectPath);

        var instructions = await CopilotReasoningHost.RenderAndPersistExecutionInstructionsAsync(
            prepared.Context, workflow, prepared.AgentInstructions, fixture.WorkspacePath,
            null, new WorkflowPromptRenderer(), fixture.Factory,
            CancellationToken.None, prepared);

        Assert.Contains(knowledgePath, instructions.Prompt);
        Assert.Contains(
            $"Project must remain exactly \"{projectName}\"",
            instructions.Prompt);
        Assert.True(Encoding.UTF8.GetByteCount(instructions.Prompt) <=
            AgentPromptContext.MaximumWorkingPromptBytes);
    }

    [Theory]
    [InlineData(ExecutionInvocationKind.Intake)]
    [InlineData(ExecutionInvocationKind.Planning)]
    [InlineData(ExecutionInvocationKind.Worker)]
    [InlineData(ExecutionInvocationKind.PreMortem)]
    public async Task UploadedFile_IsAvailableWithoutExposingItsBytesInThePrompt(
        ExecutionInvocationKind invocationKind)
    {
        await using var fixture = await DurablePromptFixture.CreateAsync(
            executionPrompt: string.Empty, invocationKind);
        var bytes = Enumerable.Range(0, 18_000)
            .Select(index => (byte)(index % 251))
            .ToArray();
        var stored = await AddCustomerPhotoAsync(fixture, bytes);
        var workflow = await fixture.ReloadWorkflowAsync(
            "{{ task }}\n{{ agent.instructions }}\n{{ outcome.context }}");
        var stager = new AgentManifestStager();
        var manifest = await stager.StageAsync(
            fixture.CopilotHome, DurablePromptFixture.Manifest(), fixture.SessionId);
        var prepared = await AgentPromptContext.PrepareAsync(
            fixture.Context with { InvocationKind = invocationKind },
            "Use only the files this assignment needs.",
            workflow.Revision, manifest, stager, fixture.Factory,
            CancellationToken.None);
        var indexPath = Assert.Single(
            prepared.DocumentPaths,
            path => path.EndsWith("customer-attachments.md", StringComparison.Ordinal));
        var stagedImagePath = Path.Combine(
            manifest.Root, "host-context", "inputs",
            fixture.FlowId.ToString("N"), fixture.StepId.ToString("N"),
            "attempt-1", CustomerAttachmentStore.StagedFileName(stored));

        Assert.Equal(bytes, await File.ReadAllBytesAsync(stagedImagePath));
        var index = await File.ReadAllTextAsync(indexPath);
        Assert.Contains("Photo_Speaker.jpg", index);
        Assert.Contains(stagedImagePath, index);
        Assert.Contains(stored.Digest, index);
        Assert.Contains(indexPath, prepared.Context.Task);
        Assert.DoesNotContain(Convert.ToBase64String(bytes), prepared.Context.Task);
        var instructions = await CopilotReasoningHost.RenderAndPersistExecutionInstructionsAsync(
            prepared.Context, workflow, prepared.AgentInstructions, fixture.WorkspacePath,
            null, new WorkflowPromptRenderer(), fixture.Factory,
            CancellationToken.None, prepared);
        Assert.Contains(indexPath, instructions.Prompt);
        Assert.DoesNotContain(Convert.ToBase64String(bytes), instructions.Prompt);
        Assert.DoesNotContain(Convert.ToBase64String(bytes), prepared.SnapshotJson);
        Assert.True(Encoding.UTF8.GetByteCount(instructions.Prompt) <=
            AgentPromptContext.MaximumWorkingPromptBytes);
    }

    [Fact]
    public async Task UploadedFile_RecoveryRestagesExactBytesAndRejectsDatabaseTampering()
    {
        await using var fixture = await DurablePromptFixture.CreateAsync(
            executionPrompt: string.Empty);
        byte[] bytes = [0xff, 0xd8, 0xff, 0xe0, 1, 2, 3];
        var stored = await AddCustomerPhotoAsync(fixture, bytes);
        var workflow = await fixture.ReloadWorkflowAsync(
            "{{ task }}\n{{ agent.instructions }}\n{{ outcome.context }}");
        var stager = new AgentManifestStager();
        var manifest = await stager.StageAsync(
            fixture.CopilotHome, DurablePromptFixture.Manifest(), fixture.SessionId);
        var prepared = await AgentPromptContext.PrepareAsync(
            fixture.Context, "Use the uploaded photo.", workflow.Revision,
            manifest, stager, fixture.Factory, CancellationToken.None);
        _ = await CopilotReasoningHost.RenderAndPersistExecutionInstructionsAsync(
            prepared.Context, workflow, prepared.AgentInstructions, fixture.WorkspacePath,
            null, new WorkflowPromptRenderer(), fixture.Factory,
            CancellationToken.None, prepared);
        var photoPath = Path.Combine(
            manifest.Root, "host-context", "inputs",
            fixture.FlowId.ToString("N"), fixture.StepId.ToString("N"),
            "attempt-1", CustomerAttachmentStore.StagedFileName(stored));
        File.Delete(photoPath);
        var recovered = fixture.Context with { RecoverInterruptedSession = true };

        var restored = await AgentPromptContext.PrepareAsync(
            recovered, "Changed instructions.", workflow.Revision,
            manifest, stager, fixture.Factory, CancellationToken.None);

        Assert.Equal(prepared.SnapshotJson, restored.SnapshotJson);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(photoPath));
        await using (var database = await fixture.Factory.CreateDbContextAsync())
        {
            var attachment = await database.FlowAttachments.SingleAsync(
                item => item.Id == stored.Id);
            var changed = (byte[])attachment.Content.Clone();
            changed[0] ^= 0xff;
            attachment.Content = changed;
            await database.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AgentPromptContext.PrepareAsync(
                recovered, "Changed instructions.", workflow.Revision,
                manifest, stager, fixture.Factory, CancellationToken.None));
    }

    [Fact]
    public async Task CustomerUpload_RemainsIsolatedToItsOwningFlow()
    {
        await using var fixture = await DurablePromptFixture.CreateAsync(
            executionPrompt: string.Empty);
        _ = await AddCustomerPhotoAsync(fixture, [1, 2, 3]);
        var other = new FlowRun
        {
            Title = "Independent flow",
            OriginalRequest = "Implement an unrelated change.",
            RepositoryPath = fixture.Root
        };
        await using (var database = await fixture.Factory.CreateDbContextAsync())
        {
            database.Flows.Add(other);
            await database.SaveChangesAsync();
        }
        var workflow = await fixture.ReloadWorkflowAsync(
            "{{ task }}\n{{ agent.instructions }}\n{{ outcome.context }}");
        var stager = new AgentManifestStager();
        var otherSession = Guid.NewGuid();
        var manifest = await stager.StageAsync(
            fixture.CopilotHome, DurablePromptFixture.Manifest(), otherSession);

        var context = await AgentPromptContext.PrepareAsync(
            fixture.Context with
            {
                FlowId = other.Id,
                FlowStepId = Guid.NewGuid(),
                CopilotSessionId = otherSession,
                Task = "Implement the unrelated change."
            },
            "Use only this flow's inputs.",
            workflow.Revision, manifest, stager, fixture.Factory,
            CancellationToken.None);

        Assert.DoesNotContain(context.DocumentPaths,
            path => path.EndsWith("customer-attachments.md", StringComparison.Ordinal));
        Assert.DoesNotContain("Customer-uploaded files", context.Context.Task);
        Assert.DoesNotContain("Photo_Speaker.jpg", context.SnapshotJson);
    }

    [Theory]
    [InlineData(ExecutionInvocationKind.Planning)]
    [InlineData(ExecutionInvocationKind.Worker)]
    [InlineData(ExecutionInvocationKind.PreMortem)]
    public async Task SummarizedBrief_PreservesExactCustomerDetailsForDownstreamAttempts(
        ExecutionInvocationKind invocationKind)
    {
        await using var fixture = await DurablePromptFixture.CreateAsync(
            executionPrompt: string.Empty,
            invocationKind);
        const string description =
            "The talk explains how source tracking and human ownership make the system reliable.";
        const string original = """
            Create a meeting with two speakers.
            First talk description: The talk explains how source tracking and human ownership make the system reliable.
            Speaker profile: https://example.test/speaker
            Photo: Downloads\speaker-photo.jpg
            """;
        const string correction =
            "Use 30 minutes for the second speaker instead of 40 minutes.";
        await using (var database = await fixture.Factory.CreateDbContextAsync())
        {
            var flow = await database.Flows.SingleAsync(item => item.Id == fixture.FlowId);
            flow.OriginalRequest = original;
            flow.ConsolidatedRequest = IntakeParser.SerializeBrief(new IntakeBrief
            {
                Goal = "Create a meeting with two speakers.",
                Details = ["Publish the submitted talks and supplied details."],
                SuccessCriteria = ["Both talks appear in the meeting."],
                Constraints = [],
                Assumptions = []
            });
            database.FlowMessages.AddRange(
                new FlowMessage
                {
                    FlowRunId = flow.Id,
                    Role = ConversationRole.Customer,
                    Content = original,
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-3)
                },
                new FlowMessage
                {
                    FlowRunId = flow.Id,
                    Role = ConversationRole.AccountManager,
                    Content = "Please confirm.",
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2)
                },
                new FlowMessage
                {
                    FlowRunId = flow.Id,
                    Role = ConversationRole.Customer,
                    Content = correction,
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
                });
            await database.SaveChangesAsync();
        }

        var workflow = await fixture.ReloadWorkflowAsync(
            "{{ task }}\n{{ agent.instructions }}\n{{ outcome.context }}");
        var manifest = await new AgentManifestStager().StageAsync(
            fixture.CopilotHome, DurablePromptFixture.Manifest(), fixture.SessionId);
        var stager = new AgentManifestStager();
        var prepared = await AgentPromptContext.PrepareAsync(
            fixture.Context with
            {
                InvocationKind = invocationKind,
                Task = "Publish the two submitted talks."
            },
            "Preserve supplied details.",
            workflow.Revision, manifest, stager, fixture.Factory,
            CancellationToken.None);
        var customerPath = Assert.Single(
            prepared.DocumentPaths,
            path => path.EndsWith(
                AgentPromptContext.CustomerInputFileName,
                StringComparison.Ordinal));
        var customerContent = await File.ReadAllTextAsync(customerPath);
        Assert.Equal(1, customerContent.Split(description).Length - 1);
        Assert.Contains(@"Downloads\speaker-photo.jpg", customerContent);
        Assert.Contains("https://example.test/speaker", customerContent);
        Assert.Contains(correction, customerContent);
        Assert.DoesNotContain("Please confirm.", customerContent);
        Assert.Contains(customerPath, prepared.Context.Task);
        Assert.DoesNotContain(description, prepared.Context.Task);

        var instructions = await CopilotReasoningHost.RenderAndPersistExecutionInstructionsAsync(
            prepared.Context, workflow, prepared.AgentInstructions, fixture.WorkspacePath,
            null, new WorkflowPromptRenderer(), fixture.Factory,
            CancellationToken.None, prepared);
        Assert.True(Encoding.UTF8.GetByteCount(instructions.Prompt) <=
            AgentPromptContext.MaximumWorkingPromptBytes);
        Assert.Contains(customerPath, instructions.Prompt);
        Assert.DoesNotContain(description, instructions.Prompt);

        await using (var database = await fixture.Factory.CreateDbContextAsync())
        {
            var flow = await database.Flows.SingleAsync(item => item.Id == fixture.FlowId);
            flow.OriginalRequest = "This later change must not rewrite the in-flight attempt.";
            await database.SaveChangesAsync();
        }
        File.Delete(customerPath);
        var restored = await AgentPromptContext.PrepareAsync(
            fixture.Context with { RecoverInterruptedSession = true },
            "Different instructions.", workflow.Revision,
            manifest, stager, fixture.Factory, CancellationToken.None);
        Assert.Equal(prepared.SnapshotJson, restored.SnapshotJson);
        Assert.Equal(customerContent, await File.ReadAllTextAsync(customerPath));
    }

    [Fact]
    public async Task LargeContext_IsPagedDurablyWithinA32KiBWorkingPromptAndRestoredExactly()
    {
        await using var fixture = await DurablePromptFixture.CreateAsync(executionPrompt: string.Empty);
        var workflow = await fixture.ReloadWorkflowAsync(
            "{{ task }}\n{{ agent.instructions }}\n{{ role.context }}\n{{ outcome.context }}\n{{ outcome.contract }}\n{{ response.contract }}");
        var design = "DESIGN-START\n" + new string('d', 23_139) + "\nSPEC-CORE: preserve the logo.";
        var evidence = string.Join(
            "\n",
            Enumerable.Range(1, 1_200).Select(index =>
                $"EV-{index:0000}: " + new string('e', 400)));
        var original = fixture.Context with
        {
            Task = "Implement the complete supplied design.",
            StudioDependencyOutputs =
            [
                new StudioDependencyOutput(
                    "design", "product-designer", StudioDependencyKind.Direct, 1, 1, 10, design)
            ],
            OutcomeContext = "Verify the exact acceptance plan.\n" + evidence,
            ContextDocuments = [new AgentContextDocument("evidence.jsonl", evidence)],
            RequiresDeliveryReadinessQa = true
        };
        var stager = new AgentManifestStager();
        var manifest = await stager.StageAsync(
            fixture.CopilotHome, DurablePromptFixture.Manifest(), fixture.SessionId);
        var prepared = await AgentPromptContext.PrepareAsync(
            original, "Use the supplied handoff.", workflow.Revision, manifest, stager,
            fixture.Factory, CancellationToken.None);
        var instructions = await CopilotReasoningHost.RenderAndPersistExecutionInstructionsAsync(
            prepared.Context, workflow, prepared.AgentInstructions, fixture.WorkspacePath,
            null, new WorkflowPromptRenderer(), fixture.Factory, CancellationToken.None, prepared);

        Assert.True(Encoding.UTF8.GetByteCount(instructions.Prompt) <= AgentPromptContext.MaximumWorkingPromptBytes);
        Assert.DoesNotContain(new string('d', 900), instructions.Prompt);
        Assert.DoesNotContain(new string('e', 400), instructions.Prompt);
        var handoffPath = Assert.Single(prepared.DocumentPaths, path => path.EndsWith("handoff-01.md"));
        var evidencePath = Assert.Single(prepared.DocumentPaths, path => path.EndsWith("evidence.jsonl"));
        Assert.Equal(design, await File.ReadAllTextAsync(handoffPath));
        Assert.Equal(evidence, await File.ReadAllTextAsync(evidencePath));
        Assert.Contains(handoffPath, instructions.Prompt);
        Assert.Contains(evidencePath, instructions.Prompt);
        await using (var database = await fixture.Factory.CreateDbContextAsync())
        {
            var snapshot = Assert.Single(await database.FlowEvents
                .Where(item => item.Type == AgentPromptContext.SnapshotEventType).ToListAsync());
            Assert.Contains("SPEC-CORE", snapshot.DataJson);
            Assert.Contains("EV-1200", snapshot.DataJson);
        }

        foreach (var path in prepared.DocumentPaths)
        {
            File.Delete(path);
        }
        await fixture.ReloadWorkflowAsync("CHANGED {{ task }}");
        var recoveredContext = original with
        {
            RecoverInterruptedSession = true,
            Task = "This newer task must not replace the durable instructions.",
            StudioDependencyOutputs = [],
            ContextDocuments = []
        };
        var recoveredInstructions = await CopilotReasoningHost.LoadPersistedExecutionInstructionsAsync(
            recoveredContext, fixture.Factory, CancellationToken.None);
        var restored = await AgentPromptContext.PrepareAsync(
            recoveredContext, "Changed instructions.", recoveredInstructions.WorkflowRevision,
            manifest, stager, fixture.Factory, CancellationToken.None);

        Assert.Equal(instructions.Prompt, recoveredInstructions.Prompt);
        Assert.Equal(workflow.Revision, recoveredInstructions.WorkflowRevision);
        Assert.Equal(prepared.SnapshotJson, restored.SnapshotJson);
        Assert.Equal(design, await File.ReadAllTextAsync(handoffPath));
        Assert.Equal(evidence, await File.ReadAllTextAsync(evidencePath));
        await File.WriteAllTextAsync(handoffPath, "Changed outside the host.");
        await Assert.ThrowsAsync<InvalidOperationException>(() => AgentPromptContext.PrepareAsync(
            recoveredContext, "Changed instructions.", recoveredInstructions.WorkflowRevision,
            manifest, stager, fixture.Factory, CancellationToken.None));
    }

    private static async Task<FlowAttachment> AddCustomerPhotoAsync(
        DurablePromptFixture fixture,
        byte[] bytes)
    {
        await using var database = await fixture.Factory.CreateDbContextAsync();
        var message = new FlowMessage
        {
            FlowRunId = fixture.FlowId,
            Role = ConversationRole.Customer,
            Content = "Please use my attached speaker photo."
        };
        var photo = Assert.Single(CustomerAttachmentStore.Prepare(
            fixture.FlowId, message.Id,
            [new IntakeAttachment("Photo_Speaker.jpg", "image/jpeg", bytes)],
            existingFlowBytes: 0,
            existingFlowFiles: 0));
        database.FlowMessages.Add(message);
        database.FlowAttachments.Add(photo);
        await database.SaveChangesAsync();
        return photo;
    }

    [Fact]
    public void WorkingPromptBudget_CountsUtf8BytesAndRejectsMissingReferences()
    {
        Assert.Throws<InvalidOperationException>(() =>
            AgentPromptContext.ValidateRenderedPrompt(new string('\u0416', 20_000), []));
        Assert.Throws<InvalidOperationException>(() =>
            AgentPromptContext.ValidateRenderedPrompt("Required handoff omitted.", [@"C:\context\handoff.md"]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("## Assignment\n\n{\"Goal\":\"Original brief.\",\"Details\":[],\"SuccessCriteria\":[],\"Constraints\":[],\"Assumptions\":[]}")]
    public async Task InterruptedAttempt_UsesExactPromptAndRevisionAfterWorkflowReload(
        string? capturedPrompt)
    {
        await using var fixture =
            await DurablePromptFixture.CreateAsync(executionPrompt: capturedPrompt);
        var originalPrompt = fixture.OriginalPrompt;
        var originalRevision = fixture.OriginalRevision;
        var current = await fixture.ReloadWorkflowAsync(
            "MATERIALLY CHANGED {{ task }} :: {{ agent.instructions }}");

        var recovered =
            await CopilotReasoningHost
                .RenderAndPersistExecutionInstructionsAsync(
                    fixture.Context with
                    {
                        Task = "A task that must not replace the original.",
                        RecoverInterruptedSession = true,
                        ResumeSession = true
                    },
                    current,
                    "Changed role instructions that must not be rendered.",
                    fixture.WorkspacePath,
                    stagedPromotion: null,
                    new WorkflowPromptRenderer(),
                    fixture.Factory,
                    CancellationToken.None);

        Assert.True(recovered.Recovered);
        Assert.Equal(originalPrompt, recovered.Prompt);
        Assert.Equal(originalRevision, recovered.WorkflowRevision);
        Assert.DoesNotContain(
            "MATERIALLY CHANGED",
            recovered.Prompt,
            StringComparison.Ordinal);

        var stored = await fixture.LoadStepAsync();
        Assert.Equal(originalPrompt, stored.ExecutionPrompt);
        Assert.Equal(originalRevision, stored.WorkflowRevision);
    }

    [Fact]
    public async Task FreshAttempt_PersistsAndStagesReadableBriefWithoutRewritingStoredJson()
    {
        const string brief =
            """{"Goal":"Refresh both sites.","Details":["Use the same design."],"SuccessCriteria":["Both sites look modern."],"Constraints":["Keep the logo unchanged."],"Assumptions":[]}""";
        await using var fixture =
            await DurablePromptFixture.CreateAsync(executionPrompt: string.Empty);
        await using (var database = await fixture.Factory.CreateDbContextAsync())
        {
            var flow = await database.Flows.SingleAsync(item => item.Id == fixture.FlowId);
            flow.ConsolidatedRequest = brief;
            await database.SaveChangesAsync();
        }
        var workflow = await fixture.ReloadWorkflowAsync(
            "## Assignment\n\n{{ task }}\n\n## Role contract\n\n{{ agent.instructions }}");
        var context = fixture.Context with
        {
            Task = WorkflowEngine.BuildStepTask(brief, "Implement the approved design.")
        };

        var instructions = await CopilotReasoningHost.RenderAndPersistExecutionInstructionsAsync(
            context,
            workflow,
            "Preserve the agreed scope.",
            fixture.WorkspacePath,
            stagedPromotion: null,
            new WorkflowPromptRenderer(),
            fixture.Factory,
            CancellationToken.None);
        var stager = new AgentManifestStager();
        var stagedAgent = await stager.StageAsync(
            fixture.CopilotHome,
            DurablePromptFixture.Manifest(),
            fixture.SessionId);
        var stagedPrompt = await stager.StagePromptAsync(
            stagedAgent,
            instructions.Prompt,
            fixture.SessionId,
            fixture.FlowId,
            fixture.StepId,
            attempt: 1);

        Assert.Contains("### Goal\n\nRefresh both sites.", instructions.Prompt, StringComparison.Ordinal);
        Assert.Contains(
            "### Constraints\n\n- Keep the logo unchanged.",
            instructions.Prompt,
            StringComparison.Ordinal);
        Assert.DoesNotContain(brief, instructions.Prompt, StringComparison.Ordinal);
        Assert.Equal(instructions.Prompt, await File.ReadAllTextAsync(stagedPrompt.Path));
        var stored = await fixture.LoadStepAsync();
        Assert.Equal(instructions.Prompt, stored.ExecutionPrompt);
        Assert.Equal(workflow.Revision, stored.WorkflowRevision);
        await using var persisted = await fixture.Factory.CreateDbContextAsync();
        Assert.Equal(
            brief,
            await persisted.Flows
                .Where(flow => flow.Id == fixture.FlowId)
                .Select(flow => flow.ConsolidatedRequest)
                .SingleAsync());
    }

    [Fact]
    public async Task InterruptedLongPrompt_ReusesOrRecreatesOriginalDigest()
    {
        var originalPrompt =
            "original-long-prompt:" +
            new string('p', 40_000) +
            ":original-end";
        await using var fixture =
            await DurablePromptFixture.CreateAsync(
                originalPrompt,
                ExecutionInvocationKind.Worker);
        var stager = new AgentManifestStager();
        var stagedAgent = await stager.StageAsync(
            fixture.CopilotHome,
            DurablePromptFixture.Manifest(),
            fixture.SessionId);
        var initial = await stager.StagePromptAsync(
            stagedAgent,
            originalPrompt,
            fixture.SessionId,
            fixture.FlowId,
            fixture.StepId,
            attempt: 1);
        var current = await fixture.ReloadWorkflowAsync(
            "CHANGED REVIEW TEMPLATE {{ task }}");
        var recovered =
            await CopilotReasoningHost
                .RenderAndPersistExecutionInstructionsAsync(
                    fixture.Context with
                    {
                        RecoverInterruptedSession = true,
                        ResumeSession = true,
                        RequiresDeliveryReadinessQa = true
                    },
                    current,
                    "Changed review instructions.",
                    fixture.WorkspacePath,
                    stagedPromotion: null,
                    new WorkflowPromptRenderer(),
                    fixture.Factory,
                    CancellationToken.None);
        var reused = await stager.StagePromptAsync(
            stagedAgent,
            recovered.Prompt,
            fixture.SessionId,
            fixture.FlowId,
            fixture.StepId,
            attempt: 1);

        Assert.True(reused.Reused);
        Assert.Equal(initial.Sha256, reused.Sha256);
        Assert.Equal(
            OutcomeVerificationRules.ComputeSha256(originalPrompt),
            reused.Sha256);

        stager.CleanupPrompt(reused);
        var recreated = await stager.StagePromptAsync(
            stagedAgent,
            recovered.Prompt,
            fixture.SessionId,
            fixture.FlowId,
            fixture.StepId,
            attempt: 1);
        Assert.False(recreated.Reused);
        Assert.Equal(initial.Sha256, recreated.Sha256);
        Assert.Equal(
            originalPrompt,
            await File.ReadAllTextAsync(recreated.Path));
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("Persisted prompt.", "not-a-workflow-revision")]
    public async Task InterruptedAttempt_InvalidPersistedInstructionsFailBeforeLaunch(
        string executionPrompt,
        string? invalidRevision)
    {
        await using var fixture =
            await DurablePromptFixture.CreateAsync(
                executionPrompt,
                ExecutionInvocationKind.Worker,
                invalidRevision);
        var current = await fixture.ReloadWorkflowAsync(
            "CHANGED TEMPLATE THAT MUST NOT LAUNCH {{ task }}");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CopilotReasoningHost
                .RenderAndPersistExecutionInstructionsAsync(
                    fixture.Context with
                    {
                        RecoverInterruptedSession = true,
                        ResumeSession = true
                    },
                    current,
                    "Changed instructions.",
                    fixture.WorkspacePath,
                    stagedPromotion: null,
                    new WorkflowPromptRenderer(),
                    fixture.Factory,
                    CancellationToken.None));

        Assert.Contains(
            invalidRevision is null
                ? "no persisted exact execution prompt"
                : "invalid persisted workflow revision",
            exception.Message,
            StringComparison.Ordinal);
        var stored = await fixture.LoadStepAsync();
        Assert.Equal(executionPrompt, stored.ExecutionPrompt);
        Assert.Equal(
            invalidRevision ?? fixture.OriginalRevision,
            stored.WorkflowRevision);
    }

    [Fact]
    public async Task ExplicitRetry_CanBindCurrentWorkflowAndPersistsFullPrompt()
    {
        await using var fixture =
            await DurablePromptFixture.CreateAsync(
                executionPrompt: string.Empty,
                invocationKind: ExecutionInvocationKind.Worker);
        var current = await fixture.ReloadWorkflowAsync(
            "CURRENT RETRY {{ task }} :: {{ agent.instructions }}");
        var retryContext = fixture.Context with
        {
            Task = "Retry the durable assignment.",
            ResumeSession = true,
            RecoverInterruptedSession = false
        };

        var retry =
            await CopilotReasoningHost
                .RenderAndPersistExecutionInstructionsAsync(
                    retryContext,
                    current,
                    "Use the current retry contract.",
                    fixture.WorkspacePath,
                    stagedPromotion: null,
                    new WorkflowPromptRenderer(),
                    fixture.Factory,
                    CancellationToken.None);

        Assert.False(retry.Recovered);
        Assert.Equal(current.Revision, retry.WorkflowRevision);
        Assert.Contains("CURRENT RETRY", retry.Prompt, StringComparison.Ordinal);
        Assert.Contains(
            "Retry the durable assignment.",
            retry.Prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Use the current retry contract.",
            retry.Prompt,
            StringComparison.Ordinal);
        Assert.NotEqual(fixture.OriginalRevision, retry.WorkflowRevision);

        var stored = await fixture.LoadStepAsync();
        Assert.Equal(retry.Prompt, stored.ExecutionPrompt);
        Assert.Equal(current.Revision, stored.WorkflowRevision);
        Assert.NotEqual(retryContext.Task, stored.ExecutionPrompt);
    }

    private sealed class DurablePromptFixture : IAsyncDisposable
    {
        private DurablePromptFixture(
            string root,
            string workspacePath,
            string copilotHome,
            Guid flowId,
            Guid stepId,
            Guid sessionId,
            DurablePromptDbContextFactory factory,
            WorkflowDefinitionProvider workflowProvider,
            AgentExecutionContext context,
            string originalPrompt,
            string originalRevision)
        {
            Root = root;
            WorkspacePath = workspacePath;
            CopilotHome = copilotHome;
            FlowId = flowId;
            StepId = stepId;
            SessionId = sessionId;
            Factory = factory;
            WorkflowProvider = workflowProvider;
            Context = context;
            OriginalPrompt = originalPrompt;
            OriginalRevision = originalRevision;
        }

        public string Root { get; }

        public string WorkspacePath { get; }

        public string CopilotHome { get; }

        public Guid FlowId { get; }

        public Guid StepId { get; }

        public Guid SessionId { get; }

        public DurablePromptDbContextFactory Factory { get; }

        public WorkflowDefinitionProvider WorkflowProvider { get; }

        public AgentExecutionContext Context { get; }

        public string OriginalPrompt { get; }

        public string OriginalRevision { get; }

        public static async Task<DurablePromptFixture> CreateAsync(
            string? executionPrompt = null,
            ExecutionInvocationKind invocationKind =
                ExecutionInvocationKind.Worker,
            string? workflowRevisionOverride = null)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"ai-harness-durable-prompt-{Guid.NewGuid():N}");
            var workspace = Path.Combine(root, "workspace");
            var copilotHome = Path.Combine(root, "copilot-home");
            var agents = Path.Combine(root, ".github", "agents");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(agents);
            await WriteWorkflowAsync(
                root,
                "ORIGINAL WORKFLOW {{ task }} :: {{ agent.instructions }}");
            var paths = new HarnessPaths(
                root,
                agents,
                Path.Combine(root, "harness.db"));
            var provider = new WorkflowDefinitionProvider(
                paths,
                new WorkflowLoader(),
                NullLogger<WorkflowDefinitionProvider>.Instance);
            var originalWorkflow = provider.GetEffective();
            var flow = new FlowRun
            {
                Title = "Durable prompt recovery",
                OriginalRequest = "Preserve the original execution instructions.",
                ConsolidatedRequest =
                    "Preserve the original execution instructions.",
                Kind = FlowKind.Delivery,
                Status = FlowStatus.Running,
                RepositoryPath = root,
                RepositoryKnowledge = "A prompt recovery fixture.",
                WorkspacePath = workspace
            };
            var sessionId = Guid.NewGuid();
            var step = new FlowStep
            {
                FlowRunId = flow.Id,
                Iteration = 1,
                Sequence = 10,
                AgentId = "software-engineer",
                AgentName = "Software Engineer",
                AgentRole = "software-engineer",
                Label = "Execute the durable assignment",
                PlanStepKey = "implement",
                PlanDutiesJson = """["Implement"]""",
                InvocationKind = invocationKind,
                Status = StepStatus.Running,
                Phase = AgentRunPhase.StreamingTurn,
                Attempt = 1,
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                CopilotSessionId = sessionId,
                CopilotSessionHome = copilotHome,
                WorkflowRevision =
                    workflowRevisionOverride ?? originalWorkflow.Revision
            };
            flow.Steps.Add(step);
            var context = new AgentExecutionContext(
                flow.Id,
                flow.Iteration,
                step.AgentId,
                step.AgentName,
                step.AgentRole,
                "model",
                "high",
                step.Attempt,
                "Original durable task.",
                flow.RepositoryKnowledge,
                flow.RepositoryPath,
                flow.WorkspacePath,
                sessionId,
                OutcomeType.PullRequest,
                "Original plan.",
                [],
                [],
                FlowStepId: step.Id,
                InvocationKind: invocationKind,
                PlanStepKey: step.PlanStepKey,
                FlowKind: flow.Kind);
            var originalPrompt = executionPrompt ??
                CopilotReasoningHost.BoundRenderedPrompt(
                    context,
                    new WorkflowPromptRenderer().Render(
                        originalWorkflow.PromptTemplate,
                        CopilotReasoningHost.BuildPromptValues(
                            context,
                            "Original role instructions.",
                            workspace)));
            step.ExecutionPrompt = originalPrompt;

            var options = new DbContextOptionsBuilder<HarnessDbContext>()
                .UseSqlite(
                    $"Data Source={paths.DatabasePath};Pooling=False")
                .Options;
            var factory = new DurablePromptDbContextFactory(options);
            await using (var database =
                         await factory.CreateDbContextAsync())
            {
                await database.Database.EnsureCreatedAsync();
                database.Flows.Add(flow);
                await database.SaveChangesAsync();
            }
            return new DurablePromptFixture(
                root,
                workspace,
                copilotHome,
                flow.Id,
                step.Id,
                sessionId,
                factory,
                provider,
                context,
                originalPrompt,
                originalWorkflow.Revision);
        }

        public async Task<WorkflowDefinition> ReloadWorkflowAsync(
            string promptTemplate)
        {
            await WriteWorkflowAsync(Root, promptTemplate);
            await WorkflowProvider.ReloadAsync();
            var current = WorkflowProvider.GetEffective();
            Assert.NotEqual(OriginalRevision, current.Revision);
            return current;
        }

        public async Task<FlowStep> LoadStepAsync()
        {
            await using var database =
                await Factory.CreateDbContextAsync();
            return await database.FlowSteps
                .AsNoTracking()
                .SingleAsync(step => step.Id == StepId);
        }

        public ValueTask DisposeAsync()
        {
            WorkflowProvider.Dispose();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
            return ValueTask.CompletedTask;
        }

        public static AgentManifest Manifest() =>
            new(
                "software-engineer",
                "Software Engineer",
                "Implements the requested change.",
                "software-engineer",
                "blue",
                10,
                "software-engineer.agent.md",
                "Complete the assigned work.");

        private static Task WriteWorkflowAsync(
            string root,
            string promptTemplate) =>
            File.WriteAllTextAsync(
                Path.Combine(root, "WORKFLOW.md"),
                $$"""
                  ---
                  workspace:
                    root: workspace
                  agent:
                    max_concurrent_agents: 1
                    max_attempts: 1
                  studio:
                    flow_kinds:
                      advisory:
                        required_duties: [PrepareOutcome]
                        maximum_permission: ReadOnlySource
                      delivery:
                        required_duties: [Implement, Verify, PrepareOutcome, Publish]
                        pre_review_maximum_permission: WorkspaceWrite
                        post_approval_maximum_permission: Publish
                  ---

                  {{promptTemplate}}
                  """);
    }

    private sealed class DurablePromptDbContextFactory(
        DbContextOptions<HarnessDbContext> options)
        : IDbContextFactory<HarnessDbContext>
    {
        public HarnessDbContext CreateDbContext() => new(options);

        public Task<HarnessDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
