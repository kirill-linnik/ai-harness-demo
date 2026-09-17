using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    private sealed record Snapshot(
        Guid FlowId,
        Guid StepId,
        int Attempt,
        string WorkflowRevision,
        IReadOnlyList<Document> Documents);

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
        StageLarge("instructions", "role-contract.md", agentInstructions);
        if (!string.IsNullOrWhiteSpace(context.DirectPrompt))
        {
            StageLarge("direct", "assignment.md", context.DirectPrompt);
        }
        else
        {
            StageLarge("task", "assignment.md", context.Task);
            StageLarge("knowledge", "repository-knowledge.md", knowledge);
            StageLarge("outcome", "verification-context.md", context.OutcomeContext);
            if (dependencies is not null &&
                dependencies.Sum(item => (long)Encoding.UTF8.GetByteCount(item.Output)) >
                InlineSectionBytes)
            {
                for (var index = 0; index < dependencies.Length; index++)
                {
                    Add($"dependency:{index}", $"handoff-{index + 1:00}.md", dependencies[index].Output);
                }
            }
            foreach (var document in context.ContextDocuments ?? [])
            {
                Add($"attachment:{document.Name}", document.Name, document.Content);
            }
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
        var outcomeContext = ReferenceOrOriginal("outcome", context.OutcomeContext);
        foreach (var document in staged.Where(item =>
                     item.Key.StartsWith("attachment:", StringComparison.Ordinal)))
        {
            outcomeContext += Environment.NewLine + Environment.NewLine + Reference(document);
        }
        var prepared = context with
        {
            Task = ReferenceOrOriginal("task", context.Task),
            RepositoryKnowledge = ReferenceOrOriginal("knowledge", knowledge),
            SourceProjectPath = string.Empty,
            OutcomeContext = outcomeContext,
            DirectPrompt = ReferenceOrOriginal("direct", context.DirectPrompt),
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
                new Snapshot(context.FlowId, context.FlowStepId, context.Attempt, workflowRevision, staged),
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
        (document.Key.StartsWith("attachment:", StringComparison.Ordinal)
            ? "Search this evidence registry by exact evidence ID or command, then read matching rows. " +
              "Do not load the whole registry, infer identifiers, or rerun successful checks."
            : "Read the complete relevant assignment or handoff in bounded view_range sections before acting. " +
              "A truncated tool response is not the complete input. Search the verification context by " +
              "criterion and evidence ID rather than loading unrelated history.") +
        " The file digest is not the AcceptancePlanHash.";

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
