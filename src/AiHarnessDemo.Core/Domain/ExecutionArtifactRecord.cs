namespace AiHarnessDemo.Core.Domain;

public sealed class ExecutionArtifactRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid FlowRunId { get; set; }

    public Guid? FlowStepId { get; set; }

    public int Iteration { get; set; }

    public required string RelativePath { get; set; }

    public required string Digest { get; set; }

    public long Length { get; set; }

    public required byte[] Content { get; set; }

    public bool ContentStored { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
