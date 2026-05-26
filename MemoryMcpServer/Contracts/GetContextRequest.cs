namespace MemoryMcpServer.Contracts;

public sealed record GetContextRequest(
    string Task,
    string? Scope,
    IReadOnlyList<string>? Constraints,
    IReadOnlyList<string>? FilesHint);
