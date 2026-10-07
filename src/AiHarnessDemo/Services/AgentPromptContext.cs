using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Core.Verification;
using AiHarnessDemo.Data;
using Microsoft.EntityFrameworkCore;

namespace AiHarnessDemo.Services;

internal sealed record PreparedAgentPromptContext(
    AgentExecutionContext Context,
    string AgentInstructions,
    string? SnapshotJson,
    IReadOnlyList<string> DocumentPaths);

internal static class AgentPromptContext
{
    internal const string SnapshotEventType = "agent.context-snapshot";
    internal const int MaximumWorkingPromptBytes = 32 * 1024;
    internal const string CustomerInputFileName = "customer-input.md";
    private const int InlineSectionBytes = 4 * 1024;
    private static readonly JsonSerializerOptions SnapshotOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private sealed record Document(
        string Key,
        string Name,
        string Content,
        string Sha256,
        string Path);

    private sealed record AttachmentReference(
        Guid Id,
        string Name,
        string FileName,
        string ContentType,
        long Length,
        string Sha256,
        string Path);

    private sealed record Snapshot(
        Guid FlowId,
        Guid StepId,
        int Attempt,
        string WorkflowRevision,
        IReadOnlyList<Document> Documents,
        IReadOnlyList<AttachmentReference>? Attachments = null);

    internal static async Task<PreparedAgentPromptContext> PrepareAsync(
        AgentExecutionContext context,
        string agentInstructions,
        string workflowRevision,
        StagedAgentManifest manifest,
        AgentManifestStager stager,
        IDbContextFactory<HarnessDbContext> databaseFactory,
        CancellationToken cancellationToken)
    {
        if (context.RecoverInterruptedSession)
        {
            await using var database =
                await databaseFactory.CreateDbContextAsync(cancellationToken);
            var saved = await database.FlowEvents.AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.FlowRunId == context.FlowId &&
                        item.FlowStepId == context.FlowStepId &&
                        item.Type == SnapshotEventType,
                    cancellationToken);
            if (saved is null)
            {
                return new PreparedAgentPromptContext(context, agentInstructions, null, []);
            }
            var snapshot = Deserialize(saved.DataJson);
            if (snapshot.FlowId != context.FlowId ||
                snapshot.StepId != context.FlowStepId ||
                snapshot.Attempt != context.Attempt ||
                !string.Equals(snapshot.WorkflowRevision, workflowRevision, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The durable context snapshot does not match this execution attempt.");
            }
            foreach (var document in snapshot.Documents)
            {
                var restored = await stager.StageContextDocumentAsync(
                    manifest, context.FlowId, context.FlowStepId, context.Attempt,
                    document.Name, document.Content, cancellationToken);
                if (!string.Equals(restored.Path, document.Path, StringComparison.Ordinal) ||
                    !string.Equals(restored.Sha256, document.Sha256, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Recovered context must retain its exact content and referenced path.");
                }
            }
            var attachmentIds = (snapshot.Attachments ?? [])
                .Select(item => item.Id)
                .ToArray();
            var savedAttachments = await database.FlowAttachments.AsNoTracking()
                .Where(item =>
                    item.FlowRunId == context.FlowId &&
                    attachmentIds.Contains(item.Id))
                .ToDictionaryAsync(item => item.Id, cancellationToken);
            foreach (var attachment in snapshot.Attachments ?? [])
            {
                if (!savedAttachments.TryGetValue(attachment.Id, out var persisted))
                {
                    throw new InvalidOperationException(
                        "A durable customer attachment is missing for this execution attempt.");
                }
                CustomerAttachmentStore.Validate(persisted, context.FlowId);
                if (!string.Equals(persisted.FileName, attachment.FileName,
                        StringComparison.Ordinal) ||
                    !string.Equals(persisted.ContentType, attachment.ContentType,
                        StringComparison.Ordinal) ||
                    persisted.Length != attachment.Length ||
                    !string.Equals(persisted.Digest, attachment.Sha256,
                        StringComparison.Ordinal) ||
                    !string.Equals(CustomerAttachmentStore.StagedFileName(persisted),
                        attachment.Name, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The durable customer attachment no longer matches this attempt.");
                }
                var restored = await stager.StageContextFileAsync(
                    manifest, context.FlowId, context.FlowStepId, context.Attempt,
                    attachment.Name, persisted.Content, cancellationToken);
                if (!string.Equals(restored.Path, attachment.Path,
                        StringComparison.Ordinal) ||
                    !string.Equals(restored.Sha256, attachment.Sha256,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Recovered customer attachment must retain its exact bytes and path.");
                }
            }
            return new PreparedAgentPromptContext(
                context, agentInstructions, saved.DataJson,
                [.. snapshot.Documents.Select(item => item.Path)]);
        }

        var documents = new List<Document>();
        void Add(string key, string name, string content) =>
            documents.Add(new Document(
                key, name, content, OutcomeVerificationRules.ComputeSha256(content), string.Empty));
        void StageLarge(string key, string name, string content)
        {
            if (Encoding.UTF8.GetByteCount(content) > InlineSectionBytes)
            {
                Add(key, name, content);
            }
        }

        var knowledge = CopilotReasoningHost.PrepareRepositoryKnowledgeContent(
            context.RepositoryKnowledge, context.SourceProjectPath);
        var dependencies = context.StudioDependencyOutputs?
            .Select(item => item with
            {
                Output = CopilotReasoningHost.RemoveSourceProjectPath(
                    item.Output, context.SourceProjectPath)
            }).ToArray();
        var responseCorrection = !string.IsNullOrWhiteSpace(context.ResponseCorrectionInstructions);
        if (!responseCorrection)
        {
            StageLarge("instructions", "role-contract.md", agentInstructions);
        }
        var attachments = new List<FlowAttachment>();
        if (context.InvocationKind != ExecutionInvocationKind.Publication && !responseCorrection)
        {
            await using var database =
                await databaseFactory.CreateDbContextAsync(cancellationToken);
            var customerMessages = await database.FlowMessages.AsNoTracking()
                .Where(item =>
                    item.FlowRunId == context.FlowId &&
                    item.Role == ConversationRole.Customer)
                .OrderBy(item => item.CreatedAt)
                .ThenBy(item => item.Id)
                .ToListAsync(cancellationToken);
            if (context.InvocationKind is
                ExecutionInvocationKind.Planning or
                ExecutionInvocationKind.Worker or
                ExecutionInvocationKind.PreMortem or
                ExecutionInvocationKind.BlockerExplanation)
            {
                var original = await database.Flows.AsNoTracking()
                    .Where(item => item.Id == context.FlowId)
                    .Select(item => item.OriginalRequest)
                    .SingleAsync(cancellationToken);
                Add("customer", CustomerInputFileName,
                    BuildCustomerInputDocument(
                        original,
                        customerMessages.Select(item => item.Content).ToArray()));
            }
            attachments = await database.FlowAttachments.AsNoTracking()
                .Where(item => item.FlowRunId == context.FlowId)
                .OrderBy(item => item.CreatedAt)
                .ThenBy(item => item.Id)
                .ToListAsync(cancellationToken);
            if (attachments.Count > CustomerAttachmentStore.MaximumFilesPerFlow ||
                attachments.Any(item => !customerMessages
                    .Any(message => message.Id == item.FlowMessageId)))
            {
                throw new InvalidOperationException(
                    "The flow has invalid customer attachment ownership.");
            }
        }
        if (!string.IsNullOrWhiteSpace(context.DirectPrompt))
        {
            StageLarge("direct", "assignment.md", context.DirectPrompt);
        }
        else
        {
            if (responseCorrection)
            {
                Add("task", "assignment.md", context.Task);
            }
            else
            {
                StageLarge("task", "assignment.md", context.Task);
            }
            if (!responseCorrection)
            {
                StageLarge("knowledge", "repository-knowledge.md", knowledge);
            }
            StageLarge("outcome", "verification-context.md", context.OutcomeContext);
            if (!responseCorrection && dependencies is not null &&
                dependencies.Sum(item => (long)Encoding.UTF8.GetByteCount(item.Output)) >
                InlineSectionBytes)
            {
                for (var index = 0; index < dependencies.Length; index++)
                {
                    Add($"dependency:{index}", $"handoff-{index + 1:00}.md", dependencies[index].Output);
                }
            }
        }
        foreach (var document in context.ContextDocuments ?? [])
        {
            Add($"attachment:{document.Name}", document.Name, document.Content);
        }
        if (responseCorrection)
        {
            Add("contract", "response-contract.md", context.OutcomeContract);
        }
        var stagedAttachments = new List<(FlowAttachment Attachment, StagedContextDocument Staged)>();
        foreach (var attachment in attachments)
        {
            CustomerAttachmentStore.Validate(attachment, context.FlowId);
            var stagedFile = await stager.StageContextFileAsync(
                manifest, context.FlowId, context.FlowStepId, context.Attempt,
                CustomerAttachmentStore.StagedFileName(attachment),
                attachment.Content, cancellationToken);
            stagedAttachments.Add((attachment, stagedFile));
        }
        if (stagedAttachments.Count > 0)
        {
            Add("uploads", "customer-attachments.md",
                CustomerAttachmentStore.BuildIndex(stagedAttachments));
        }
        ValidateDocuments(documents);
        var staged = await Task.WhenAll(documents.Select(async document =>
        {
            var file = await stager.StageContextDocumentAsync(
                manifest, context.FlowId, context.FlowStepId, context.Attempt,
                document.Name, document.Content, cancellationToken);
            return document with { Path = file.Path };
        }));
        var byKey = staged.ToDictionary(item => item.Key, StringComparer.Ordinal);
        string ReferenceOrOriginal(string key, string original) =>
            byKey.TryGetValue(key, out var document) ? Reference(document) : original;
        var customerReference = byKey.TryGetValue("customer", out var customer)
            ? $"{Environment.NewLine}{Environment.NewLine}## Original customer inputs" +
              $"{Environment.NewLine}{Reference(customer)}"
            : string.Empty;
        var uploadsReference = byKey.TryGetValue("uploads", out var uploads)
            ? $"{Environment.NewLine}{Environment.NewLine}## Customer-uploaded files" +
              $"{Environment.NewLine}{Reference(uploads)}"
            : string.Empty;
        var extraContext = string.Join(
            Environment.NewLine + Environment.NewLine,
            staged.Where(item =>
                    item.Key.StartsWith("attachment:", StringComparison.Ordinal))
                .Select(Reference));
        var outcomeContext = ReferenceOrOriginal("outcome", context.OutcomeContext);
        if (string.IsNullOrWhiteSpace(context.DirectPrompt) &&
            !string.IsNullOrWhiteSpace(extraContext))
        {
            outcomeContext += Environment.NewLine + Environment.NewLine + extraContext;
        }
        var taskReferences = customerReference + uploadsReference;
        var preparedKnowledge = ReferenceOrOriginal("knowledge", knowledge);
        if (context.InvocationKind == ExecutionInvocationKind.Publication)
        {
            var projectName = RepositoryKnowledgeSynthesizer.ResolveProjectName(
                knowledge, context.SourceProjectPath);
            preparedKnowledge = $"# {projectName}{Environment.NewLine}{Environment.NewLine}" +
                preparedKnowledge;
        }
        var prepared = context with
        {
            Task = ReferenceOrOriginal("task", context.Task) +
                (string.IsNullOrWhiteSpace(context.DirectPrompt)
                    ? taskReferences
                    : string.Empty),
            RepositoryKnowledge = preparedKnowledge,
            SourceProjectPath = string.Empty,
            OutcomeContext = outcomeContext,
            DirectPrompt = ReferenceOrOriginal("direct", context.DirectPrompt) +
                (string.IsNullOrWhiteSpace(context.DirectPrompt)
                    ? string.Empty
                    : taskReferences +
                      (string.IsNullOrWhiteSpace(extraContext)
                          ? string.Empty
                          : Environment.NewLine + Environment.NewLine + extraContext)),
            StudioDependencyOutputs = dependencies?.Select((item, index) => item with
            {
                Output = ReferenceOrOriginal($"dependency:{index}", item.Output)
            }).ToArray(),
            UsesBoundedWorkingPrompt = true
        };
        return new PreparedAgentPromptContext(
            prepared,
            ReferenceOrOriginal("instructions", agentInstructions),
            JsonSerializer.Serialize(
                new Snapshot(
                    context.FlowId, context.FlowStepId, context.Attempt,
                    workflowRevision, staged,
                    stagedAttachments.Select(item => new AttachmentReference(
                        item.Attachment.Id,
                        CustomerAttachmentStore.StagedFileName(item.Attachment),
                        item.Attachment.FileName,
                        item.Attachment.ContentType,
                        item.Attachment.Length,
                        item.Staged.Sha256,
                        item.Staged.Path)).ToArray()),
                SnapshotOptions),
            [.. staged.Select(item => item.Path)]);
    }

    internal static void ValidateRenderedPrompt(
        string prompt,
        IReadOnlyList<string> documentPaths)
    {
        var bytes = Encoding.UTF8.GetByteCount(prompt);
        if (bytes > MaximumWorkingPromptBytes)
        {
            throw new InvalidOperationException(
                $"The working prompt requires {bytes} UTF-8 bytes, exceeding its " +
                $"{MaximumWorkingPromptBytes}-byte budget. Required context must be referenced, not truncated.");
        }
        foreach (var path in documentPaths)
        {
            if (!prompt.Contains(path, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "WORKFLOW.md omitted a required context-document reference; execution failed before dispatch.");
            }
        }
    }

    private static string Reference(Document document) =>
        $"Complete host-owned context: {document.Name}{Environment.NewLine}" +
        $"CONTEXT_FILE: {document.Path}{Environment.NewLine}" +
        $"CONTEXT_SHA256: {document.Sha256}{Environment.NewLine}" +
        (document.Key == "customer"
            ? "Read the exact customer-supplied facts before declaring a named detail missing. " +
              "Use only what the current confirmed brief and assignment require; later customer " +
              "corrections take precedence. Do not mistake an intake summary for the complete " +
              "submission or repeat unrelated source fields in your result."
            : document.Key == "uploads"
                ? "Read this index for the flow's immutable uploaded files before looking for " +
                  "customer assets. Use only the needed files, copy them into the isolated " +
                  "workspace when required, and never execute uploads or access customer folders."
            : document.Key.StartsWith("attachment:", StringComparison.Ordinal)
                ? "Search this evidence registry by exact evidence ID or command, then read matching rows. " +
                  "Do not load the whole registry or infer identifiers. Do not rerun checks merely " +
                  "to reconstruct this registry. Independent verification must collect its own " +
                  "decisive observations once against the unchanged candidate."
                : "Read the complete relevant assignment or handoff in bounded view_range sections before acting. " +
                  "A truncated tool response is not the complete input. Search the verification context by " +
                  "criterion and evidence ID rather than loading unrelated history.") +
        " The file digest is not the AcceptancePlanHash.";

    internal static string BuildCustomerInputDocument(
        string originalRequest,
        IReadOnlyList<string> customerMessages)
    {
        ArgumentNullException.ThrowIfNull(originalRequest);
        ArgumentNullException.ThrowIfNull(customerMessages);
        var content = new StringBuilder()
            .AppendLine("# Original customer submission")
            .AppendLine()
            .AppendLine(originalRequest);
        var start = customerMessages.Count > 0 &&
            string.Equals(customerMessages[0], originalRequest, StringComparison.Ordinal)
                ? 1
                : 0;
        for (var index = start; index < customerMessages.Count; index++)
        {
            content.AppendLine()
                .AppendLine($"# Subsequent customer input {index - start + 1}")
                .AppendLine()
                .AppendLine(customerMessages[index]);
        }
        return content.ToString();
    }

    private static Snapshot Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("The durable context snapshot is empty.");
        }
        Snapshot snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<Snapshot>(json, SnapshotOptions)
                ?? throw new InvalidOperationException("The durable context snapshot is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The durable context snapshot is invalid.", exception);
        }
        if (snapshot.Documents is null)
        {
            throw new InvalidOperationException("The durable context snapshot has no document set.");
        }
        ValidateDocuments(snapshot.Documents);
        if (snapshot.Attachments is { } attachments &&
            (attachments.Count > CustomerAttachmentStore.MaximumFilesPerFlow ||
             attachments.Select(item => item.Id).Distinct().Count() != attachments.Count ||
             attachments.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() !=
                attachments.Count ||
             attachments.Any(item =>
                 item.Id == Guid.Empty ||
                 string.IsNullOrWhiteSpace(item.FileName) ||
                 string.IsNullOrWhiteSpace(item.ContentType) ||
                 string.IsNullOrWhiteSpace(item.Path) ||
                 item.Length is < 0 or > CustomerAttachmentStore.MaximumFileBytes ||
                 !OutcomeVerificationRules.IsSha256(item.Sha256)) ||
             attachments.Count > 0 &&
             !snapshot.Documents.Any(item => item.Key == "uploads")))
        {
            throw new InvalidOperationException(
                "The durable customer attachment references are invalid.");
        }
        foreach (var document in snapshot.Documents)
        {
            if (string.IsNullOrWhiteSpace(document.Path) ||
                !string.Equals(
                    document.Sha256,
                    OutcomeVerificationRules.ComputeSha256(document.Content),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The durable context document failed its integrity check.");
            }
        }
        return snapshot;
    }

    private static void ValidateDocuments(IReadOnlyList<Document> documents)
    {
        if (documents.Count > 32 ||
            documents.Any(item => item is null || string.IsNullOrWhiteSpace(item.Key) ||
                string.IsNullOrWhiteSpace(item.Name) || item.Content is null) ||
            documents.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count() != documents.Count ||
            documents.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != documents.Count ||
            documents.Sum(item => (long)Encoding.UTF8.GetByteCount(item.Content)) >
                AgentManifestStager.MaximumStagedPromptBytes)
        {
            throw new InvalidOperationException("The execution context document set is invalid or oversized.");
        }
    }
}
