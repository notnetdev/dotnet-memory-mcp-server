using MemoryMcpServer.Contracts;
using MemoryMcpServer.Options;
using Npgsql;

namespace MemoryMcpServer.Services;

public sealed class ContextService : IContextService
{
    private readonly IContextRetrievalService _retrievalService;
    private readonly string _connectionString;
    private readonly ILogger<ContextService> _logger;
    private readonly RetrievalOptions _options;

    public ContextService(IContextRetrievalService retrievalService, IConfiguration configuration, ILogger<ContextService> logger, RetrievalOptions options)
    {
        _retrievalService = retrievalService;
        _logger = logger;
        _options = options ?? new RetrievalOptions();
        _connectionString = configuration["MCP_SCANNER_CONNECTION"]
            ?? Environment.GetEnvironmentVariable("MCP_SCANNER_CONNECTION")
            ?? throw new InvalidOperationException("MCP_SCANNER_CONNECTION is required.");
    }

    public async Task<GetContextResponse> GetContextAsync(GetContextRequest request, string traceId, CancellationToken cancellationToken)
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
            _logger.LogInformation(
                "memory.get_context trace={TraceId} no snapshot found; taskIntent={TaskIntent} constraints={Constraints}",
                traceId,
                string.Join(',', retrieval.TaskIntent),
                string.Join(',', retrieval.ConstraintsApplied));

            return BuildEmptyResponse(retrieval.TaskIntent, retrieval.ConstraintsApplied);
        }

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var relatedSymbols = await ExpandRelatedSymbolsAsync(connection, retrieval.ScanRunId.Value, retrieval.PrimaryTargets, _options, cancellationToken);
        var proposedEdits = BuildProposedEdits(retrieval.TaskIntent, retrieval.PrimaryTargets, relatedSymbols, _options);
        var verification = BuildVerification(retrieval.PrimaryTargets, relatedSymbols);
        var inclusionReasons = BuildInclusionReasons(
            retrieval.PrimaryTargets,
            relatedSymbols,
            filesHint,
            constraints,
            normalizedScope,
            retrieval.TargetHints,
            retrieval.PrimaryTargetReasons);

        var confidence = CalculateConfidence(retrieval.PrimaryTargets, filesHint, constraints, _options.Confidence);

        _logger.LogInformation(
            "memory.get_context trace={TraceId} runId={RunId} commit={Commit} indexedAt={IndexedAtUtc:o} primaryTargets={PrimaryTargetsCount} relatedSymbols={RelatedSymbolsCount} confidence={Confidence}; reasons={Reasons}",
            traceId,
            retrieval.ScanRunId,
            retrieval.Freshness.Commit,
            retrieval.Freshness.IndexedAtUtc,
            retrieval.PrimaryTargets.Count,
            relatedSymbols.Count,
            confidence,
            string.Join(";", inclusionReasons.Select(r => $"{r.Artifact}:{r.Reason}")));

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
        RetrievalOptions options,
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
            var relationType = reader.GetString(2);
            var kind = reader.IsDBNull(3) ? "external" : reader.GetString(3);
            var filePath = reader.IsDBNull(4) ? null : reader.GetString(4);
            list.Add(new RelatedSymbol(toSymbol, kind, filePath, relationType));
        }

        return list
            .DistinctBy(x => (x.SymbolKey, x.RelationType))
            .OrderBy(x => x.RelationType, StringComparer.Ordinal)
            .ThenBy(x => x.SymbolKey, StringComparer.Ordinal)
            .Take(options.Limits.MaxRelatedSymbols)
            .ToArray();
    }

    private static IReadOnlyList<ProposedEdit> BuildProposedEdits(
        IReadOnlyList<string> intent,
        IReadOnlyList<ContextTarget> primaryTargets,
        IReadOnlyList<RelatedSymbol> relatedSymbols,
        RetrievalOptions options)
    {
        var relatedBySymbol = relatedSymbols
            .GroupBy(x => x.SymbolKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(x => x.RelationType).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.Ordinal);

        return primaryTargets
            .Take(options.Limits.MaxProposedEdits)
            .Select(t => new ProposedEdit(
                What: $"{string.Join('+', intent)} {t.Kind}",
                Where: t.FilePath ?? t.SymbolKey,
                Why: relatedBySymbol.TryGetValue(t.SymbolKey, out var relationTypes) && relationTypes.Length > 0
                    ? $"Matched by task/scope/filesHint and connected via {string.Join(',', relationTypes)}."
                    : "Matched by task/scope/filesHint and ranked by deterministic rules."))
            .ToArray();
    }

    private static IReadOnlyList<string> BuildVerification(IReadOnlyList<ContextTarget> primaryTargets, IReadOnlyList<RelatedSymbol> relatedSymbols)
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

        if (relatedSymbols.Any(x => string.Equals(x.RelationType, "implements", StringComparison.OrdinalIgnoreCase)))
        {
            verification.Add("verify interface-to-implementation behavior");
        }

        if (relatedSymbols.Any(x => string.Equals(x.RelationType, "declared_in_file", StringComparison.OrdinalIgnoreCase)))
        {
            verification.Add("verify changed files compile with related declarations");
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
        IReadOnlyList<string> targetHints,
        IReadOnlyDictionary<string, string> retrievalReasonLookup)
    {
        var reasons = new List<InclusionReason>();

        foreach (var target in primaryTargets)
        {
            var reason = retrievalReasonLookup.TryGetValue(target.SymbolKey, out var retrievalReason)
                ? retrievalReason
                : filesHint.Any(f => (target.FilePath ?? string.Empty).Contains(f, StringComparison.OrdinalIgnoreCase))
                    ? "matched_by_file_hint"
                    : targetHints.Any(h => target.SymbolKey.Contains(h, StringComparison.OrdinalIgnoreCase))
                        ? "matched_by_symbol_name"
                        : "matched_by_scope";

            if (string.Equals(reason, "matched_by_scope", StringComparison.OrdinalIgnoreCase)
                && HasRuSynonymHit(target.SymbolKey, targetHints))
            {
                reason = "matched_by_ru_synonym";
            }

            reasons.Add(new InclusionReason(target.SymbolKey, reason));
        }

        foreach (var related in relatedSymbols)
        {
            var reason = string.Equals(related.RelationType, "implements", StringComparison.OrdinalIgnoreCase)
                ? "expanded_by_implements"
                : string.Equals(related.RelationType, "declared_in_file", StringComparison.OrdinalIgnoreCase)
                    ? "expanded_by_declared_in_file"
                    : "expanded_by_graph_1_hop";

            reasons.Add(new InclusionReason(related.SymbolKey, reason));
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

    private static bool HasRuSynonymHit(string symbolKey, IReadOnlyList<string> targetHints)
    {
        foreach (var hint in targetHints)
        {
            if (!hint.StartsWith("ru_synonym:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var token = hint["ru_synonym:".Length..];
            if (token.Length > 0 && symbolKey.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static double CalculateConfidence(
        IReadOnlyList<ContextTarget> primaryTargets,
        IReadOnlyList<string> filesHint,
        IReadOnlyList<string> constraints,
        ConfidenceOptions options)
    {
        if (primaryTargets.Count == 0)
        {
            return options.EmptyScore;
        }

        var baseScore = options.BaseScore;
        var targetsBoost = Math.Min(options.MaxTargetsBoost, primaryTargets.Count * options.TargetBoostPerItem);
        var fileHintBoost = filesHint.Count > 0 ? options.FileHintBoost : 0;
        var constraintsPenalty = constraints.Count > 0 ? options.ConstraintsPenalty : 0;

        var score = baseScore + targetsBoost + fileHintBoost - constraintsPenalty;
        return Math.Round(Math.Clamp(score, 0.0, options.MaxScore), 2);
    }

    private GetContextResponse BuildEmptyResponse(IReadOnlyList<string> intent, IReadOnlyList<string> constraintsApplied)
    {
        return new GetContextResponse(
            TaskIntent: intent,
            PrimaryTargets: Array.Empty<ContextTarget>(),
            RelatedSymbols: Array.Empty<RelatedSymbol>(),
            ConstraintsApplied: constraintsApplied,
            ProposedEdits: Array.Empty<ProposedEdit>(),
            Verification: ["dotnet build"],
            Freshness: new Freshness("unknown", DateTimeOffset.UtcNow),
            Confidence: _options.Confidence.EmptyScore,
            InclusionReasons: [new InclusionReason("request", "no_snapshot_found")]);
    }
}
