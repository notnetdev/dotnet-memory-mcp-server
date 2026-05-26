using MemoryMcpServer.Contracts;
using Npgsql;

namespace MemoryMcpServer.Services;

public sealed class ContextRetrievalService : IContextRetrievalService
{
    private readonly string _connectionString;

    public ContextRetrievalService(IConfiguration configuration)
    {
        _connectionString = configuration["MCP_SCANNER_CONNECTION"]
            ?? Environment.GetEnvironmentVariable("MCP_SCANNER_CONNECTION")
            ?? throw new InvalidOperationException("MCP_SCANNER_CONNECTION is required.");
    }

    public async Task<RetrievalResult> RetrieveAsync(RetrievalRequest request, CancellationToken cancellationToken)
    {
        var task = request.Task.Trim();
        var scope = request.Scope.Trim();
        var constraints = request.Constraints
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var filesHint = request.FilesHint
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var parsed = ParseTask(task);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var snapshot = await GetLatestSnapshotAsync(connection, scope, cancellationToken);
        if (snapshot is null)
        {
            return new RetrievalResult(
                ScanRunId: null,
                Freshness: new Freshness("unknown", DateTimeOffset.UtcNow),
                TaskIntent: parsed.Actions,
                TargetHints: parsed.TargetHints,
                PrimaryTargets: Array.Empty<ContextTarget>(),
                ConstraintsApplied: BuildConstraintsApplied(constraints, scope, filesHint));
        }

        var candidates = await QueryCandidatesAsync(
            connection,
            snapshot.Value.RunId,
            parsed.TargetHints,
            scope,
            filesHint,
            constraints,
            cancellationToken);

        var primaryTargets = candidates
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.SymbolKey, StringComparer.Ordinal)
            .Take(10)
            .Select(c => new ContextTarget(c.SymbolKey, c.FilePath, c.Kind, c.Score))
            .ToArray();

        return new RetrievalResult(
            ScanRunId: snapshot.Value.RunId,
            Freshness: new Freshness(snapshot.Value.CommitSha, snapshot.Value.IndexedAtUtc),
            TaskIntent: parsed.Actions,
            TargetHints: parsed.TargetHints,
            PrimaryTargets: primaryTargets,
            ConstraintsApplied: BuildConstraintsApplied(constraints, scope, filesHint));
    }

    private static ParsedTask ParseTask(string task)
    {
        var actions = new List<string>();

        if (ContainsAny(task, "add", "new", "добав")) actions.Add("add");
        if (ContainsAny(task, "change", "update", "измени", "обнов")) actions.Add("change");
        if (ContainsAny(task, "fix", "bug", "ошиб", "исправ")) actions.Add("fix");
        if (ContainsAny(task, "remove", "delete", "удал")) actions.Add("remove");
        if (ContainsAny(task, "refactor", "рефактор")) actions.Add("refactor");
        if (ContainsAny(task, "test", "тест")) actions.Add("test");

        if (actions.Count == 0)
        {
            actions.Add("change");
        }

        var targetHints = Tokenize(task)
            .Where(t => !actions.Contains(t, StringComparer.OrdinalIgnoreCase))
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ParsedTask(
            Actions: actions
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            TargetHints: targetHints);
    }

    private static bool ContainsAny(string text, params string[] tokens)
        => tokens.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));

    private async Task<SnapshotInfo?> GetLatestSnapshotAsync(NpgsqlConnection connection, string scope, CancellationToken cancellationToken)
    {
        const string sql = """
                           SELECT id, repo_path, commit_sha, finished_at_utc
                           FROM latest_successful_scan_runs
                           ORDER BY
                               CASE
                                   WHEN @scope = '' THEN started_at_utc
                                   WHEN repo_path ILIKE '%' || @scope || '%' THEN started_at_utc + interval '100 years'
                                   ELSE started_at_utc
                               END DESC
                           LIMIT 1;
                           """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("scope", scope);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new SnapshotInfo(
            RunId: reader.GetInt64(0),
            RepoPath: reader.GetString(1),
            CommitSha: reader.GetString(2),
            IndexedAtUtc: reader.IsDBNull(3) ? DateTimeOffset.UtcNow : reader.GetFieldValue<DateTimeOffset>(3));
    }

    private static async Task<IReadOnlyList<Candidate>> QueryCandidatesAsync(
        NpgsqlConnection connection,
        long runId,
        IReadOnlyList<string> targetHints,
        string scope,
        IReadOnlyList<string> filesHint,
        IReadOnlyList<string> constraints,
        CancellationToken cancellationToken)
    {
        const string sql = """
                           SELECT symbol_key, kind, file_path, name
                           FROM symbols
                           WHERE scan_run_id = @run_id;
                           """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("run_id", runId);

        var scopeTokens = Tokenize(scope);
        var doNotTouchTokens = ParseDoNotTouch(constraints);

        var result = new List<Candidate>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var symbolKey = reader.GetString(0);
            var kind = reader.GetString(1);
            var filePath = reader.IsDBNull(2) ? null : reader.GetString(2);
            var name = reader.GetString(3);

            if (IsFilteredByDoNotTouch(filePath, doNotTouchTokens))
            {
                continue;
            }

            var score = 0.0;

            var hasFileHint = !string.IsNullOrWhiteSpace(filePath) && filesHint.Any(h => filePath.Contains(h, StringComparison.OrdinalIgnoreCase));
            if (hasFileHint)
            {
                score += 100.0; // жесткий приоритет filesHint
            }

            if (targetHints.Any(h => symbolKey.Contains(h, StringComparison.OrdinalIgnoreCase) || name.Contains(h, StringComparison.OrdinalIgnoreCase)))
            {
                score += 10.0;
            }

            if (scopeTokens.Length > 0 &&
                (scopeTokens.Any(t => symbolKey.Contains(t, StringComparison.OrdinalIgnoreCase)) ||
                 (!string.IsNullOrWhiteSpace(filePath) && scopeTokens.Any(t => filePath.Contains(t, StringComparison.OrdinalIgnoreCase)))))
            {
                score += 3.0;
            }

            if (score <= 0)
            {
                continue;
            }

            result.Add(new Candidate(symbolKey, kind, filePath, score));
        }

        return result;
    }

    private static bool IsFilteredByDoNotTouch(string? filePath, IReadOnlyList<string> doNotTouchTokens)
    {
        if (string.IsNullOrWhiteSpace(filePath) || doNotTouchTokens.Count == 0)
        {
            return false;
        }

        return doNotTouchTokens.Any(token => filePath.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static string[] ParseDoNotTouch(IReadOnlyList<string> constraints)
        => constraints
            .Where(c => c.StartsWith("do-not-touch", StringComparison.OrdinalIgnoreCase))
            .SelectMany(c => c.Split(':', 2).Skip(1))
            .SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string[] Tokenize(string value)
        => value.Split([' ', ',', '.', ';', ':', '/', '\\', '(', ')', '[', ']', '{', '}', '-', '_', '"', '\''], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

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

    private readonly record struct ParsedTask(IReadOnlyList<string> Actions, IReadOnlyList<string> TargetHints);

    private readonly record struct SnapshotInfo(long RunId, string RepoPath, string CommitSha, DateTimeOffset IndexedAtUtc);

    private readonly record struct Candidate(string SymbolKey, string Kind, string? FilePath, double Score);
}
