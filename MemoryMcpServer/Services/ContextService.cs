using MemoryMcpServer.Contracts;
using Npgsql;

namespace MemoryMcpServer.Services;

public sealed class ContextService : IContextService
{
    private readonly IContextRetrievalService _retrievalService;
    private readonly string _connectionString;

    public ContextService(IContextRetrievalService retrievalService, IConfiguration configuration)
    {
        _retrievalService = retrievalService;
        _connectionString = configuration["MCP_SCANNER_CONNECTION"]
            ?? Environment.GetEnvironmentVariable("MCP_SCANNER_CONNECTION")
            ?? throw new InvalidOperationException("MCP_SCANNER_CONNECTION is required.");
    }

    public async Task<GetContextResponse> GetContextAsync(GetContextRequest request, CancellationToken cancellationToken)
    {
        var normalizedTask = (request.Task ?? string.Empty).Trim();
        var normalizedScope = (request.Scope ?? string.Empty).Trim();
        var constraints = (request.Constraints ?? Array.Empty<string>())
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var filesHint = (request.FilesHint ?? Array.Empty<string>())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var retrieval = await _retrievalService.RetrieveAsync(
            new RetrievalRequest(
                Task: normalizedTask,
                Scope: normalizedScope,
                Constraints: constraints,
                FilesHint: filesHint),
            cancellationToken);

        if (retrieval.ScanRunId is null)
        {
            return BuildEmptyResponse(retrieval.TaskIntent, retrieval.ConstraintsApplied);
        }

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var relatedSymbols = await ExpandRelatedSymbolsAsync(connection, retrieval.ScanRunId.Value, retrieval.PrimaryTargets, cancellationToken);
        var proposedEdits = BuildProposedEdits(retrieval.TaskIntent, retrieval.PrimaryTargets);
        var verification = BuildVerification(retrieval.PrimaryTargets);
        var inclusionReasons = BuildInclusionReasons(
            retrieval.PrimaryTargets,
            relatedSymbols,
            filesHint,
            constraints,
            normalizedScope,
            retrieval.TargetHints);

        var confidence = CalculateConfidence(retrieval.PrimaryTargets, filesHint, constraints);

        return new GetContextResponse(
            TaskIntent: retrieval.TaskIntent,
            PrimaryTargets: retrieval.PrimaryTargets,
            RelatedSymbols: relatedSymbols,
            ConstraintsApplied: retrieval.ConstraintsApplied,
            ProposedEdits: proposedEdits,
            Verification: verification,
            Freshness: retrieval.Freshness,
            Confidence: confidence,
            InclusionReasons: inclusionReasons);
    }

    private static async Task<IReadOnlyList<RelatedSymbol>> ExpandRelatedSymbolsAsync(
        NpgsqlConnection connection,
        long runId,
        IReadOnlyList<ContextTarget> primaryTargets,
        CancellationToken cancellationToken)
    {
        if (primaryTargets.Count == 0)
        {
            return Array.Empty<RelatedSymbol>();
        }

        const string sql = """
                           SELECT r.from_symbol_key, r.to_symbol_key, r.relation_type, s.kind, s.file_path
                           FROM relations r
                           LEFT JOIN symbols s ON s.scan_run_id = r.scan_run_id AND s.symbol_key = r.to_symbol_key
                           WHERE r.scan_run_id = @run_id
                             AND r.from_symbol_key = ANY(@symbols)
                             AND r.relation_type IN ('implements', 'declared_in_file');
                           """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("symbols", primaryTargets.Select(p => p.SymbolKey).ToArray());

        var list = new List<RelatedSymbol>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var toSymbol = reader.GetString(1);
            var kind = reader.IsDBNull(3) ? "external" : reader.GetString(3);
            var filePath = reader.IsDBNull(4) ? null : reader.GetString(4);
            list.Add(new RelatedSymbol(toSymbol, kind, filePath));
        }

        return list
            .DistinctBy(x => x.SymbolKey)
            .OrderBy(x => x.SymbolKey, StringComparer.Ordinal)
            .Take(20)
            .ToArray();
    }

    private static IReadOnlyList<ProposedEdit> BuildProposedEdits(IReadOnlyList<string> intent, IReadOnlyList<ContextTarget> primaryTargets)
    {
        return primaryTargets
            .Take(5)
            .Select(t => new ProposedEdit(
                What: $"{string.Join('+', intent)} {t.Kind}",
                Where: t.FilePath ?? t.SymbolKey,
                Why: "Matched by task/scope/filesHint and ranked by deterministic rules."))
            .ToArray();
    }

    private static IReadOnlyList<string> BuildVerification(IReadOnlyList<ContextTarget> primaryTargets)
    {
        var verification = new List<string>
        {
            "dotnet build",
            "run tests related to impacted symbols"
        };

        if (primaryTargets.Any(t => string.Equals(t.Kind, "method", StringComparison.OrdinalIgnoreCase)))
        {
            verification.Add("verify callers of changed methods");
        }

        return verification
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<InclusionReason> BuildInclusionReasons(
        IReadOnlyList<ContextTarget> primaryTargets,
        IReadOnlyList<RelatedSymbol> relatedSymbols,
        IReadOnlyList<string> filesHint,
        IReadOnlyList<string> constraints,
        string scope,
        IReadOnlyList<string> targetHints)
    {
        var reasons = new List<InclusionReason>();

        foreach (var target in primaryTargets)
        {
            var reason = filesHint.Any(f => (target.FilePath ?? string.Empty).Contains(f, StringComparison.OrdinalIgnoreCase))
                ? "matched_by_file_hint"
                : targetHints.Any(h => target.SymbolKey.Contains(h, StringComparison.OrdinalIgnoreCase))
                    ? "matched_by_symbol_name"
                    : "matched_by_scope";

            reasons.Add(new InclusionReason(target.SymbolKey, reason));
        }

        foreach (var related in relatedSymbols)
        {
            reasons.Add(new InclusionReason(related.SymbolKey, "expanded_by_graph_1_hop"));
        }

        if (!string.IsNullOrWhiteSpace(scope))
        {
            reasons.Add(new InclusionReason("request", "scope_filter_applied"));
        }

        if (constraints.Count > 0)
        {
            reasons.Add(new InclusionReason("request", "constraints_filter_applied"));
        }

        return reasons
            .OrderBy(r => r.Artifact, StringComparer.Ordinal)
            .ThenBy(r => r.Reason, StringComparer.Ordinal)
            .ToArray();
    }

    private static double CalculateConfidence(
        IReadOnlyList<ContextTarget> primaryTargets,
        IReadOnlyList<string> filesHint,
        IReadOnlyList<string> constraints)
    {
        if (primaryTargets.Count == 0)
        {
            return 0.1;
        }

        var baseScore = 0.4;
        var targetsBoost = Math.Min(0.3, primaryTargets.Count * 0.03);
        var fileHintBoost = filesHint.Count > 0 ? 0.2 : 0;
        var constraintsPenalty = constraints.Count > 0 ? 0.05 : 0;

        var score = baseScore + targetsBoost + fileHintBoost - constraintsPenalty;
        return Math.Round(Math.Clamp(score, 0.0, 0.99), 2);
    }

    private static GetContextResponse BuildEmptyResponse(IReadOnlyList<string> intent, IReadOnlyList<string> constraintsApplied)
    {
        return new GetContextResponse(
            TaskIntent: intent,
            PrimaryTargets: Array.Empty<ContextTarget>(),
            RelatedSymbols: Array.Empty<RelatedSymbol>(),
            ConstraintsApplied: constraintsApplied,
            ProposedEdits: Array.Empty<ProposedEdit>(),
            Verification: ["dotnet build"],
            Freshness: new Freshness("unknown", DateTimeOffset.UtcNow),
            Confidence: 0.1,
            InclusionReasons: [new InclusionReason("request", "no_snapshot_found")]);
    }
}
