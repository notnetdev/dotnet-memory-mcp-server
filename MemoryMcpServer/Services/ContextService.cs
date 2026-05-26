using MemoryMcpServer.Contracts;
using Npgsql;

namespace MemoryMcpServer.Services;

public sealed class ContextService : IContextService
{
    private readonly string _connectionString;

    public ContextService(IConfiguration configuration)
    {
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

        var intent = ParseIntent(normalizedTask);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var snapshot = await GetLatestSnapshotAsync(connection, cancellationToken);
        if (snapshot is null)
        {
            return BuildEmptyResponse(intent, constraints);
        }

        var symbolCandidates = await QueryPrimaryTargetsAsync(connection, snapshot.Value.RunId, normalizedTask, normalizedScope, filesHint, constraints, cancellationToken);
        var primaryTargets = symbolCandidates
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.SymbolKey, StringComparer.Ordinal)
            .Take(10)
            .Select(c => new ContextTarget(c.SymbolKey, c.FilePath, c.Kind, c.Score))
            .ToArray();

        var relatedSymbols = await ExpandRelatedSymbolsAsync(connection, snapshot.Value.RunId, primaryTargets, cancellationToken);
        var constraintsApplied = BuildConstraintsApplied(constraints, normalizedScope, filesHint);
        var proposedEdits = BuildProposedEdits(intent, primaryTargets);
        var verification = BuildVerification(primaryTargets);
        var inclusionReasons = BuildInclusionReasons(primaryTargets, relatedSymbols, filesHint, constraints, normalizedScope);
        var confidence = CalculateConfidence(primaryTargets, filesHint, constraints);

        return new GetContextResponse(
            TaskIntent: intent,
            PrimaryTargets: primaryTargets,
            RelatedSymbols: relatedSymbols,
            ConstraintsApplied: constraintsApplied,
            ProposedEdits: proposedEdits,
            Verification: verification,
            Freshness: new Freshness(snapshot.Value.CommitSha, snapshot.Value.IndexedAtUtc),
            Confidence: confidence,
            InclusionReasons: inclusionReasons);
    }

    private static IReadOnlyList<string> ParseIntent(string task)
    {
        var intents = new List<string>();

        if (ContainsAny(task, "fix", "bug", "ошиб", "исправ")) intents.Add("fix");
        if (ContainsAny(task, "add", "new", "добав")) intents.Add("add");
        if (ContainsAny(task, "change", "update", "измени", "обнов")) intents.Add("change");
        if (ContainsAny(task, "remove", "delete", "удал")) intents.Add("remove");
        if (ContainsAny(task, "refactor", "рефактор")) intents.Add("refactor");
        if (ContainsAny(task, "test", "тест")) intents.Add("test");

        if (intents.Count == 0)
        {
            intents.Add("change");
        }

        return intents
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(i => i, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool ContainsAny(string text, params string[] tokens)
        => tokens.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));

    private async Task<SnapshotInfo?> GetLatestSnapshotAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
                           SELECT id, commit_sha, finished_at_utc
                           FROM latest_successful_scan_runs
                           ORDER BY started_at_utc DESC
                           LIMIT 1;
                           """;

        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new SnapshotInfo(
            RunId: reader.GetInt64(0),
            CommitSha: reader.GetString(1),
            IndexedAtUtc: reader.IsDBNull(2) ? DateTimeOffset.UtcNow : reader.GetFieldValue<DateTimeOffset>(2));
    }

    private static async Task<IReadOnlyList<SymbolCandidate>> QueryPrimaryTargetsAsync(
        NpgsqlConnection connection,
        long runId,
        string task,
        string scope,
        IReadOnlyList<string> filesHint,
        IReadOnlyList<string> constraints,
        CancellationToken cancellationToken)
    {
        var taskTokens = Tokenize(task);
        var scopeTokens = Tokenize(scope);

        const string sql = """
                           SELECT symbol_key, kind, file_path, name
                           FROM symbols
                           WHERE scan_run_id = @run_id;
                           """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("run_id", runId);

        var candidates = new List<SymbolCandidate>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var symbolKey = reader.GetString(0);
            var kind = reader.GetString(1);
            var filePath = reader.IsDBNull(2) ? null : reader.GetString(2);
            var name = reader.GetString(3);

            if (IsDoNotTouch(filePath, constraints))
            {
                continue;
            }

            var score = 0.0;

            if (!string.IsNullOrWhiteSpace(filePath) && filesHint.Any(f => filePath.Contains(f, StringComparison.OrdinalIgnoreCase)))
            {
                score += 2.0;
            }

            if (taskTokens.Any(t => name.Contains(t, StringComparison.OrdinalIgnoreCase) || symbolKey.Contains(t, StringComparison.OrdinalIgnoreCase)))
            {
                score += 1.2;
            }

            if (scopeTokens.Length > 0 &&
                (scopeTokens.Any(t => symbolKey.Contains(t, StringComparison.OrdinalIgnoreCase)) ||
                 (!string.IsNullOrWhiteSpace(filePath) && scopeTokens.Any(t => filePath.Contains(t, StringComparison.OrdinalIgnoreCase)))))
            {
                score += 0.8;
            }

            if (score > 0)
            {
                candidates.Add(new SymbolCandidate(symbolKey, kind, filePath, score));
            }
        }

        return candidates;
    }

    private static bool IsDoNotTouch(string? filePath, IReadOnlyList<string> constraints)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        var doNotTouch = constraints
            .Where(c => c.StartsWith("do-not-touch", StringComparison.OrdinalIgnoreCase))
            .SelectMany(c => c.Split(':', 2).Skip(1))
            .SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToArray();

        return doNotTouch.Any(x => filePath.Contains(x, StringComparison.OrdinalIgnoreCase));
    }

    private static string[] Tokenize(string value)
        => value.Split([' ', ',', '.', ';', ':', '/', '\\', '(', ')', '[', ']', '{', '}', '-', '_', '"', '\''], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

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

    private static IReadOnlyList<string> BuildConstraintsApplied(
        IReadOnlyList<string> constraints,
        string scope,
        IReadOnlyList<string> filesHint)
    {
        var applied = new List<string>();

        applied.AddRange(constraints);

        if (!string.IsNullOrWhiteSpace(scope))
        {
            applied.Add($"scope:{scope}");
        }

        if (filesHint.Count > 0)
        {
            applied.Add($"filesHint:{string.Join(',', filesHint)}");
        }

        return applied
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
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
        string scope)
    {
        var reasons = new List<InclusionReason>();

        foreach (var target in primaryTargets)
        {
            var reason = filesHint.Any(f => (target.FilePath ?? string.Empty).Contains(f, StringComparison.OrdinalIgnoreCase))
                ? "matched_by_file_hint"
                : "matched_by_task_or_scope";

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

    private static GetContextResponse BuildEmptyResponse(IReadOnlyList<string> intent, IReadOnlyList<string> constraints)
    {
        return new GetContextResponse(
            TaskIntent: intent,
            PrimaryTargets: Array.Empty<ContextTarget>(),
            RelatedSymbols: Array.Empty<RelatedSymbol>(),
            ConstraintsApplied: constraints,
            ProposedEdits: Array.Empty<ProposedEdit>(),
            Verification: ["dotnet build"],
            Freshness: new Freshness("unknown", DateTimeOffset.UtcNow),
            Confidence: 0.1,
            InclusionReasons: [new InclusionReason("request", "no_snapshot_found")]);
    }

    private readonly record struct SnapshotInfo(long RunId, string CommitSha, DateTimeOffset IndexedAtUtc);

    private readonly record struct SymbolCandidate(string SymbolKey, string Kind, string? FilePath, double Score);
}
