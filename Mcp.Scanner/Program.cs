using Npgsql;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;

namespace Mcp.Scanner;

internal static class Program
{
    private const int SuccessExitCode = 0;
    private const int InvalidArgumentsExitCode = 1;
    private const int DatabaseErrorExitCode = 2;
    private const int UnexpectedErrorExitCode = 99;

    private const string ScanCommand = "scan";
    private const string SolutionArg = "--solution";
    private const string RepoArg = "--repo";
    private const string CommitArg = "--commit";
    private const string ConnectionArg = "--connection";
    private const string EnvironmentConnection = "MCP_SCANNER_CONNECTION";

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (!TryParseArguments(args, out var scanOptions, out var validationError))
            {
                Console.Error.WriteLine($"[error] {validationError}");
                PrintUsage();
                return InvalidArgumentsExitCode;
            }

            Console.WriteLine("[info] Stage 1: scanner bootstrap");
            Console.WriteLine($"[info] Solution: {scanOptions.SolutionPath}");
            Console.WriteLine($"[info] Repo: {scanOptions.RepoPath}");
            Console.WriteLine($"[info] Commit: {scanOptions.CommitSha}");

            var pingResult = await CheckDatabaseConnectionAsync(scanOptions.ConnectionString);
            if (!pingResult.Success)
            {
                Console.Error.WriteLine($"[error] PostgreSQL health-check failed: {pingResult.Error}");
                return DatabaseErrorExitCode;
            }

            Console.WriteLine("[info] PostgreSQL health-check: OK");
            var schemaResult = await EnsureSchemaAsync(scanOptions.ConnectionString);
            if (!schemaResult.Success)
            {
                Console.Error.WriteLine($"[error] Schema initialization failed: {schemaResult.Error}");
                return DatabaseErrorExitCode;
            }

            Console.WriteLine("[info] Schema initialization: OK");

            var scanResult = await ExecuteSymbolScanAsync(scanOptions);
            if (!scanResult.Success)
            {
                Console.Error.WriteLine($"[error] Stage 3 symbol scan failed: {scanResult.Error}");
                return UnexpectedErrorExitCode;
            }

            Console.WriteLine($"[info] Stage 3 symbol scan: OK (runId={scanResult.ScanRunId}, symbols={scanResult.SymbolsCount})");
            Console.WriteLine("[info] Stage 3 completed successfully.");
            return SuccessExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[error] Unexpected scanner failure: {ex.Message}");
            return UnexpectedErrorExitCode;
        }
    }

    private static async Task<SymbolScanResult> ExecuteSymbolScanAsync(ScanOptions options)
    {
        try
        {
            EnsureMsBuildRegistered();

            await using var connection = new NpgsqlConnection(options.ConnectionString);
            await connection.OpenAsync();

            var scanRunId = await CreateScanRunAsync(connection, options);

            var symbols = await ExtractSymbolsAsync(options.SolutionPath);
            await InsertSymbolsAsync(connection, scanRunId, symbols);

            await CompleteScanRunAsync(connection, scanRunId, status: "succeeded", error: null);
            return SymbolScanResult.Ok(scanRunId, symbols.Count);
        }
        catch (Exception ex)
        {
            return SymbolScanResult.Fail(ex.Message);
        }
    }

    private static void EnsureMsBuildRegistered()
    {
        if (!MSBuildLocator.IsRegistered)
        {
            MSBuildLocator.RegisterDefaults();
        }
    }

    private static async Task<long> CreateScanRunAsync(NpgsqlConnection connection, ScanOptions options)
    {
        const string sql = """
                           INSERT INTO scan_runs (repo_path, commit_sha, status, started_at_utc)
                           VALUES (@repo_path, @commit_sha, @status, @started_at_utc)
                           RETURNING id;
                           """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("repo_path", options.RepoPath);
        command.Parameters.AddWithValue("commit_sha", options.CommitSha);
        command.Parameters.AddWithValue("status", "running");
        command.Parameters.AddWithValue("started_at_utc", DateTimeOffset.UtcNow);

        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result);
    }

    private static async Task CompleteScanRunAsync(NpgsqlConnection connection, long scanRunId, string status, string? error)
    {
        const string sql = """
                           UPDATE scan_runs
                           SET status = @status,
                               finished_at_utc = @finished_at_utc,
                               error = @error
                           WHERE id = @id;
                           """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("finished_at_utc", DateTimeOffset.UtcNow);
        command.Parameters.AddWithValue("error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("id", scanRunId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<ExtractedSymbol>> ExtractSymbolsAsync(string solutionPath)
    {
        using var workspace = MSBuildWorkspace.Create();
        var solution = await workspace.OpenSolutionAsync(solutionPath);

        var extracted = new List<ExtractedSymbol>();

        foreach (var project in solution.Projects.Where(p => p.Language == LanguageNames.CSharp))
        {
            foreach (var document in project.Documents)
            {
                if (!document.SupportsSyntaxTree)
                {
                    continue;
                }

                var syntaxRoot = await document.GetSyntaxRootAsync();
                if (syntaxRoot is null)
                {
                    continue;
                }

                var semanticModel = await document.GetSemanticModelAsync();
                if (semanticModel is null)
                {
                    continue;
                }

                var filePath = document.FilePath;

                foreach (var node in syntaxRoot.DescendantNodes())
                {
                    var typeSymbol = node switch
                    {
                        ClassDeclarationSyntax classDecl => semanticModel.GetDeclaredSymbol(classDecl) as INamedTypeSymbol,
                        InterfaceDeclarationSyntax interfaceDecl => semanticModel.GetDeclaredSymbol(interfaceDecl) as INamedTypeSymbol,
                        _ => null
                    };

                    if (typeSymbol is not null)
                    {
                        extracted.Add(ExtractedSymbol.From(typeSymbol, filePath));
                        continue;
                    }

                    if (node is MethodDeclarationSyntax methodDecl)
                    {
                        var methodSymbol = semanticModel.GetDeclaredSymbol(methodDecl) as IMethodSymbol;
                        if (methodSymbol is not null)
                        {
                            extracted.Add(ExtractedSymbol.From(methodSymbol, filePath));
                        }
                    }
                }
            }
        }

        return extracted
            .DistinctBy(s => s.SymbolKey)
            .ToList();
    }

    private static async Task InsertSymbolsAsync(NpgsqlConnection connection, long scanRunId, IReadOnlyCollection<ExtractedSymbol> symbols)
    {
        const string sql = """
                           INSERT INTO symbols (scan_run_id, symbol_key, kind, name, containing_type, "namespace", file_path)
                           VALUES (@scan_run_id, @symbol_key, @kind, @name, @containing_type, @namespace, @file_path)
                           ON CONFLICT (scan_run_id, symbol_key) DO NOTHING;
                           """;

        foreach (var symbol in symbols)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("scan_run_id", scanRunId);
            command.Parameters.AddWithValue("symbol_key", symbol.SymbolKey);
            command.Parameters.AddWithValue("kind", symbol.Kind);
            command.Parameters.AddWithValue("name", symbol.Name);
            command.Parameters.AddWithValue("containing_type", (object?)symbol.ContainingType ?? DBNull.Value);
            command.Parameters.AddWithValue("namespace", (object?)symbol.Namespace ?? DBNull.Value);
            command.Parameters.AddWithValue("file_path", (object?)symbol.FilePath ?? DBNull.Value);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task<DatabasePingResult> EnsureSchemaAsync(string connectionString)
    {
        const string schemaSql = """
                                 CREATE TABLE IF NOT EXISTS scan_runs
                                 (
                                     id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                                     repo_path TEXT NOT NULL,
                                     commit_sha TEXT NOT NULL,
                                     status TEXT NOT NULL,
                                     started_at_utc TIMESTAMPTZ NOT NULL,
                                     finished_at_utc TIMESTAMPTZ NULL,
                                     error TEXT NULL
                                 );

                                 CREATE INDEX IF NOT EXISTS ix_scan_runs_repo_path_started_at
                                     ON scan_runs (repo_path, started_at_utc DESC);

                                 CREATE TABLE IF NOT EXISTS symbols
                                 (
                                     id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                                     scan_run_id BIGINT NOT NULL REFERENCES scan_runs(id) ON DELETE CASCADE,
                                     symbol_key TEXT NOT NULL,
                                     kind TEXT NOT NULL,
                                     name TEXT NOT NULL,
                                     containing_type TEXT NULL,
                                     namespace TEXT NULL,
                                     file_path TEXT NULL
                                 );

                                 CREATE UNIQUE INDEX IF NOT EXISTS ux_symbols_scan_run_symbol_key
                                     ON symbols (scan_run_id, symbol_key);

                                 CREATE INDEX IF NOT EXISTS ix_symbols_scan_run_kind
                                     ON symbols (scan_run_id, kind);

                                 CREATE TABLE IF NOT EXISTS relations
                                 (
                                     id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                                     scan_run_id BIGINT NOT NULL REFERENCES scan_runs(id) ON DELETE CASCADE,
                                     from_symbol_key TEXT NOT NULL,
                                     relation_type TEXT NOT NULL,
                                     to_symbol_key TEXT NOT NULL
                                 );

                                 CREATE INDEX IF NOT EXISTS ix_relations_scan_run_from
                                     ON relations (scan_run_id, from_symbol_key);

                                 CREATE INDEX IF NOT EXISTS ix_relations_scan_run_to
                                     ON relations (scan_run_id, to_symbol_key);

                                 CREATE INDEX IF NOT EXISTS ix_relations_scan_run_type
                                     ON relations (scan_run_id, relation_type);
                                 """;

        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            await using var command = new NpgsqlCommand(schemaSql, connection);
            await command.ExecuteNonQueryAsync();

            return DatabasePingResult.Ok();
        }
        catch (Exception ex)
        {
            return DatabasePingResult.Fail(ex.Message);
        }
    }

    private static async Task<DatabasePingResult> CheckDatabaseConnectionAsync(string connectionString)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            await using var command = new NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync();

            return DatabasePingResult.Ok();
        }
        catch (Exception ex)
        {
            return DatabasePingResult.Fail(ex.Message);
        }
    }

    private static bool TryParseArguments(string[] args, out ScanOptions options, out string error)
    {
        options = default!;
        error = string.Empty;

        if (args.Length == 0)
        {
            error = "Command is required.";
            return false;
        }

        if (!string.Equals(args[0], ScanCommand, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Unsupported command '{args[0]}'.";
            return false;
        }

        var parsed = ParseNamedArguments(args.Skip(1).ToArray());

        if (!TryGetRequiredArgument(parsed, SolutionArg, out var solutionPath, out error))
        {
            return false;
        }

        if (!TryGetRequiredArgument(parsed, RepoArg, out var repoPath, out error))
        {
            return false;
        }

        if (!TryGetRequiredArgument(parsed, CommitArg, out var commitSha, out error))
        {
            return false;
        }

        if (!TryGetConnectionString(parsed, out var connectionString, out error))
        {
            return false;
        }

        if (!File.Exists(solutionPath))
        {
            error = $"Solution file was not found: {solutionPath}";
            return false;
        }

        if (!Directory.Exists(repoPath))
        {
            error = $"Repository directory was not found: {repoPath}";
            return false;
        }

        options = new ScanOptions(solutionPath, repoPath, commitSha, connectionString);
        return true;
    }

    private static bool TryGetConnectionString(
        IReadOnlyDictionary<string, string> parsed,
        out string connectionString,
        out string error)
    {
        connectionString = string.Empty;
        error = string.Empty;

        if (parsed.TryGetValue(ConnectionArg, out var cliConnection) && !string.IsNullOrWhiteSpace(cliConnection))
        {
            connectionString = cliConnection;
            return true;
        }

        var envConnection = Environment.GetEnvironmentVariable(EnvironmentConnection);
        if (!string.IsNullOrWhiteSpace(envConnection))
        {
            connectionString = envConnection;
            return true;
        }

        error =
            $"Connection string is required. Pass '{ConnectionArg} " +
            "<connection-string>' or set environment variable " +
            $"'{EnvironmentConnection}'.";

        return false;
    }

    private static bool TryGetRequiredArgument(
        IReadOnlyDictionary<string, string> parsed,
        string argumentName,
        out string value,
        out string error)
    {
        value = string.Empty;
        error = string.Empty;

        if (!parsed.TryGetValue(argumentName, out var rawValue) || string.IsNullOrWhiteSpace(rawValue))
        {
            error = $"Argument '{argumentName}' is required.";
            return false;
        }

        value = rawValue;
        return true;
    }

    private static IReadOnlyDictionary<string, string> ParseNamedArguments(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (!key.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                result[key] = string.Empty;
                continue;
            }

            result[key] = args[i + 1];
            i++;
        }

        return result;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine(
            "  Mcp.Scanner scan --solution <path.sln> --repo <repo-path> --commit <sha> [--connection <connection-string>]");
        Console.WriteLine();
        Console.WriteLine($"Or set environment variable: {EnvironmentConnection}");
    }

    private readonly record struct ScanOptions(string SolutionPath, string RepoPath, string CommitSha, string ConnectionString);

    private readonly record struct DatabasePingResult(bool Success, string? Error)
    {
        public static DatabasePingResult Ok() => new(true, null);
        public static DatabasePingResult Fail(string error) => new(false, error);
    }

    private readonly record struct SymbolScanResult(bool Success, long ScanRunId, int SymbolsCount, string? Error)
    {
        public static SymbolScanResult Ok(long scanRunId, int symbolsCount) => new(true, scanRunId, symbolsCount, null);
        public static SymbolScanResult Fail(string error) => new(false, 0, 0, error);
    }

    private sealed record ExtractedSymbol(
        string SymbolKey,
        string Kind,
        string Name,
        string? ContainingType,
        string? Namespace,
        string? FilePath)
    {
        public static ExtractedSymbol From(INamedTypeSymbol symbol, string? filePath)
            => new(
                SymbolKey: symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                Kind: symbol.TypeKind switch
                {
                    TypeKind.Interface => "interface",
                    TypeKind.Class => "class",
                    _ => "type"
                },
                Name: symbol.Name,
                ContainingType: symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                Namespace: symbol.ContainingNamespace?.ToDisplayString(),
                FilePath: filePath);

        public static ExtractedSymbol From(IMethodSymbol symbol, string? filePath)
            => new(
                SymbolKey: symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                Kind: "method",
                Name: symbol.Name,
                ContainingType: symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                Namespace: symbol.ContainingNamespace?.ToDisplayString(),
                FilePath: filePath);
    }
}
