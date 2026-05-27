namespace MemoryMcpServer.Contracts;

public sealed record GetContextResponse(
    IReadOnlyList<string> TaskIntent,
    IReadOnlyList<ContextTarget> PrimaryTargets,
    IReadOnlyList<RelatedSymbol> RelatedSymbols,
    IReadOnlyList<string> ConstraintsApplied,
    IReadOnlyList<ProposedEdit> ProposedEdits,
    IReadOnlyList<string> Verification,
    Freshness Freshness,
    double Confidence,
    IReadOnlyList<InclusionReason> InclusionReasons);

public sealed record ContextTarget(string SymbolKey, string? FilePath, string Kind, double Score);

public sealed record RelatedSymbol(string SymbolKey, string Kind, string? FilePath, string RelationType);

public sealed record ProposedEdit(string What, string Where, string Why);

public sealed record Freshness(string Commit, DateTimeOffset IndexedAtUtc);

public sealed record InclusionReason(string Artifact, string Reason);
