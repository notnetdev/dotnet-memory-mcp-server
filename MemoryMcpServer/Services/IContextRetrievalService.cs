using MemoryMcpServer.Contracts;

namespace MemoryMcpServer.Services;

public interface IContextRetrievalService
{
    Task<RetrievalResult> RetrieveAsync(RetrievalRequest request, CancellationToken cancellationToken);
}

public sealed record RetrievalRequest(
    string Task,
    string Scope,
    IReadOnlyList<string> Constraints,
    IReadOnlyList<string> FilesHint);

public sealed record RetrievalResult(
    long? ScanRunId,
    Freshness Freshness,
    IReadOnlyList<string> TaskIntent,
    IReadOnlyList<string> TargetHints,
    IReadOnlyList<ContextTarget> PrimaryTargets,
    IReadOnlyList<string> ConstraintsApplied);
