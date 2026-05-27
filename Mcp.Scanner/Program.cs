using Npgsql;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using System.Diagnostics;

namespace Mcp.Scanner;

internal static class Program
{
    private const int CommandTimeoutSeconds = 120;

    private const int SuccessExitCode = 0;
    private const int InvalidArgumentsExitCode = 1;
    private const int DatabaseErrorExitCode = 2;
    private const int UnexpectedErrorExitCode = 99;

    private const string ScanCommand = "scan";
    private const string ReportCommand = "report";
    private const string ValidateCommand = "validate";
    private const string SolutionArg = "--solution";
    private const string RepoArg = "--repo";
    private const string CommitArg = "--commit";
    private const string ConnectionArg = "--connection";
    private const string EnvironmentConnection = "MCP_SCANNER_CONNECTION";

    public static async Task<int> Main(string[] args)
    {
        using var shutdownCts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdownCts.Cancel();
            Console.Error.WriteLine("[warn] Cancellation requested (Ctrl+C). Finishing current safe point...");
        };

        try
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine("[error] Command is required.");
                PrintUsage();
                return InvalidArgumentsExitCode;
            }

            var command = args[0];

            if (string.Equals(command, ReportCommand, StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseReportArguments(args, out var reportOptions, out var reportValidationError))
                {
                    Console.Error.WriteLine($"[error] {reportValidationError}");
                    PrintUsage();
                    return InvalidArgumentsExitCode;
                }

                var reportResult = await ExecuteReportAsync(reportOptions, shutdownCts.Token);
                if (!reportResult.Success)
                {
                    Console.Error.WriteLine($"[error] Stage 6 report failed: {reportResult.Error}");
                    return reportResult.IsDatabaseError ? DatabaseErrorExitCode : InvalidArgumentsExitCode;
                }

                Console.WriteLine("[info] Stage 6 report completed successfully.");
                return SuccessExitCode;
            }

            if (string.Equals(command, ValidateCommand, StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseReportArguments(args, out var validateOptions, out var validateValidationError))
                {
                    Console.Error.WriteLine($"[error] {validateValidationError}");
                    PrintUsage();
                    return InvalidArgumentsExitCode;
                }

                var validateResult = await ExecuteReportAsync(validateOptions, shutdownCts.Token);
                if (!validateResult.Success)
                {
                    Console.Error.WriteLine($"[error] Stage 6 validate failed: {validateResult.Error}");
                    return validateResult.IsDatabaseError ? DatabaseErrorExitCode : InvalidArgumentsExitCode;
                }

                Console.WriteLine("[info] Stage 6 validate completed successfully.");
                return SuccessExitCode;
            }

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

            var pingResult = await CheckDatabaseConnectionAsync(scanOptions.ConnectionString, shutdownCts.Token);
            if (!pingResult.Success)
            {
                Console.Error.WriteLine($"[error] PostgreSQL health-check failed: {pingResult.Error}");
                return DatabaseErrorExitCode;
            }

            Console.WriteLine("[info] PostgreSQL health-check: OK");
            var schemaResult = await EnsureSchemaAsync(scanOptions.ConnectionString, shutdownCts.Token);
            if (!schemaResult.Success)
            {
                Console.Error.WriteLine($"[error] Schema initialization failed: {schemaResult.Error}");
                return DatabaseErrorExitCode;
            }

            Console.WriteLine("[info] Schema initialization: OK");

            var scanResult = await ExecuteSymbolScanAsync(scanOptions, shutdownCts.Token);
            if (!scanResult.Success)
            {
                Console.Error.WriteLine($"[error] Stage 5 scan failed: {scanResult.Error}");
                return UnexpectedErrorExitCode;
            }

            Console.WriteLine($"[info] Stage 5 scan: OK (runId={scanResult.ScanRunId}, symbols={scanResult.SymbolsCount}, relations={scanResult.RelationsCount})");
            Console.WriteLine($"[info] Latest successful snapshot for repo: {scanResult.LatestSuccessfulScanRunId}");
            Console.WriteLine($"[info] Scan summary: projects={scanResult.ProjectsCount}, files={scanResult.FilesCount}, durationMs={scanResult.DurationMs}");
            Console.WriteLine("[info] Stage 5 completed successfully.");
            return SuccessExitCode;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("[error] Scanner execution cancelled.");
            return UnexpectedErrorExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[error] Unexpected scanner failure: {DescribeException(ex)}");
            return UnexpectedErrorExitCode;
        }
    }

    private static async Task<SymbolScanResult> ExecuteSymbolScanAsync(ScanOptions options, CancellationToken cancellationToken)
    {
        long? scanRunId = null;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            EnsureMsBuildRegistered();

            await using var connection = new NpgsqlConnection(options.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            scanRunId = await CreateScanRunAsync(connection, options, cancellationToken);
            var currentScanRunId = scanRunId.Value;

            var extraction = await ExtractFactsAsync(options.SolutionPath, options.RepoPath, cancellationToken);

            var symbols = extraction.Symbols;
            var relations = extraction.Relations;
            var metrics = new ScanMetrics(extraction.ProjectsCount, extraction.FilesCount, symbols.Count, relations.Count, stopwatch.ElapsedMilliseconds);

            await using var writeTransaction = await connection.BeginTransactionAsync(cancellationToken);

            await InsertSymbolsAsync(connection, currentScanRunId, symbols, writeTransaction, cancellationToken);
            await InsertRelationsAsync(connection, currentScanRunId, relations, writeTransaction, cancellationToken);

            await CompleteScanRunAsync(connection, currentScanRunId, status: "succeeded", error: null, metrics, writeTransaction, cancellationToken);

            await writeTransaction.CommitAsync(cancellationToken);

            var latestSuccessfulRunId = await GetLatestSuccessfulScanRunIdAsync(connection, options.RepoPath, cancellationToken);
            return SymbolScanResult.Ok(currentScanRunId, metrics, latestSuccessfulRunId);
        }
        catch (Exception ex)
        {
            if (scanRunId.HasValue)
            {
                try
                {
                    await using var failConnection = new NpgsqlConnection(options.ConnectionString);
                    await failConnection.OpenAsync(CancellationToken.None);
                    await CompleteScanRunAsync(failConnection, scanRunId.Value, status: "failed", error: DescribeException(ex), metrics: null, transaction: null, CancellationToken.None);
                }
                catch
                {
                    // ignore secondary failure, первичная ошибка уже будет возвращена
                }
            }

            return SymbolScanResult.Fail(DescribeException(ex));
        }
    }

    private static void EnsureMsBuildRegistered()
    {
        if (!MSBuildLocator.IsRegistered)
        {
            MSBuildLocator.RegisterDefaults();
        }
    }

    private static async Task<long> CreateScanRunAsync(NpgsqlConnection connection, ScanOptions options, CancellationToken cancellationToken)
    {
        const string sql = """
                           INSERT INTO scan_runs (repo_path, commit_sha, status, started_at_utc)
                           VALUES (@repo_path, @commit_sha, @status, @started_at_utc)
                           RETURNING id;
                           """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.CommandTimeout = CommandTimeoutSeconds;
        command.Parameters.AddWithValue("repo_path", options.RepoPath);
        command.Parameters.AddWithValue("commit_sha", options.CommitSha);
        command.Parameters.AddWithValue("status", "running");
        command.Parameters.AddWithValue("started_at_utc", DateTimeOffset.UtcNow);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result);
    }

    private static async Task CompleteScanRunAsync(
        NpgsqlConnection connection,
        long scanRunId,
        string status,
        string? error,
        ScanMetrics? metrics,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
                           UPDATE scan_runs
                           SET status = @status,
                               finished_at_utc = @finished_at_utc,
                               error = @error,
                               projects_count = @projects_count,
                               files_count = @files_count,
                               symbols_count = @symbols_count,
                               relations_count = @relations_count,
                               duration_ms = @duration_ms
                           WHERE id = @id;
                           """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.CommandTimeout = CommandTimeoutSeconds;
        command.Transaction = transaction;
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("finished_at_utc", DateTimeOffset.UtcNow);
        command.Parameters.AddWithValue("error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("projects_count", metrics?.ProjectsCount is null ? DBNull.Value : metrics.Value.ProjectsCount);
        command.Parameters.AddWithValue("files_count", metrics?.FilesCount is null ? DBNull.Value : metrics.Value.FilesCount);
        command.Parameters.AddWithValue("symbols_count", metrics?.SymbolsCount is null ? DBNull.Value : metrics.Value.SymbolsCount);
        command.Parameters.AddWithValue("relations_count", metrics?.RelationsCount is null ? DBNull.Value : metrics.Value.RelationsCount);
        command.Parameters.AddWithValue("duration_ms", metrics?.DurationMs is null ? DBNull.Value : metrics.Value.DurationMs);
        command.Parameters.AddWithValue("id", scanRunId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long?> GetLatestSuccessfulScanRunIdAsync(NpgsqlConnection connection, string repoPath, CancellationToken cancellationToken)
    {
        const string sql = """
                           SELECT id
                           FROM latest_successful_scan_runs
                           WHERE repo_path = @repo_path;
                           """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.CommandTimeout = CommandTimeoutSeconds;
        command.Parameters.AddWithValue("repo_path", repoPath);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null or DBNull)
        {
            return null;
        }

        return Convert.ToInt64(result);
    }

    private static async Task<ExtractionResult> ExtractFactsAsync(string solutionPath, string repoPath, CancellationToken cancellationToken)
    {
        using var workspace = MSBuildWorkspace.Create();
        var projects = await LoadProjectsForScanAsync(workspace, solutionPath, repoPath, cancellationToken);

        var extractedSymbols = new List<ExtractedSymbol>();
        var extractedRelations = new List<ExtractedRelation>();
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var projectsCount = 0;

        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            projectsCount++;

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

                var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
                if (semanticModel is null)
                {
                    continue;
                }

                var filePath = document.FilePath;
                if (!string.IsNullOrWhiteSpace(filePath))
                {
                    files.Add(filePath);
                }

                foreach (var node in syntaxRoot.DescendantNodes())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var typeSymbol = node switch
                    {
                        ClassDeclarationSyntax classDecl => semanticModel.GetDeclaredSymbol(classDecl) as INamedTypeSymbol,
                        InterfaceDeclarationSyntax interfaceDecl => semanticModel.GetDeclaredSymbol(interfaceDecl) as INamedTypeSymbol,
                        _ => null
                    };

                    if (typeSymbol is not null)
                    {
                        extractedSymbols.Add(ExtractedSymbol.From(typeSymbol, filePath));

                        if (!string.IsNullOrWhiteSpace(filePath))
                        {
                            extractedRelations.Add(ExtractedRelation.SymbolDeclaredInFile(typeSymbol, filePath));
                        }

                        foreach (var implementedInterface in typeSymbol.Interfaces)
                        {
                            extractedRelations.Add(ExtractedRelation.Implements(typeSymbol, implementedInterface));
                        }

                        continue;
                    }

                    if (node is MethodDeclarationSyntax methodDecl)
                    {
                        var methodSymbol = semanticModel.GetDeclaredSymbol(methodDecl) as IMethodSymbol;
                        if (methodSymbol is not null)
                        {
                            extractedSymbols.Add(ExtractedSymbol.From(methodSymbol, filePath));

                            if (!string.IsNullOrWhiteSpace(filePath))
                            {
                                extractedRelations.Add(ExtractedRelation.SymbolDeclaredInFile(methodSymbol, filePath));
                            }
                        }
                    }
                }
            }
        }

        var distinctSymbols = extractedSymbols
            .DistinctBy(s => s.SymbolKey)
            .ToList();

        var distinctRelations = extractedRelations
            .DistinctBy(r => (r.FromSymbolKey, r.RelationType, r.ToSymbolKey))
            .ToList();

        return new ExtractionResult(distinctSymbols, distinctRelations, projectsCount, files.Count);
    }

    private static async Task<IReadOnlyCollection<Project>> LoadProjectsForScanAsync(
        MSBuildWorkspace workspace,
        string solutionPath,
        string repoPath,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(solutionPath);

        if (string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase))
        {
            var solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: cancellationToken);
            return solution.Projects.Where(p => p.Language == LanguageNames.CSharp).ToArray();
        }

        if (string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase))
        {
            var projectFiles = Directory
                .EnumerateFiles(repoPath, "*.csproj", SearchOption.AllDirectories)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var projectFile in projectFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (workspace.CurrentSolution.Projects.Any(p =>
                        string.Equals(p.FilePath, projectFile, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var project = await workspace.OpenProjectAsync(projectFile, cancellationToken: cancellationToken);
            }

            return workspace.CurrentSolution.Projects
                .Where(p => p.Language == LanguageNames.CSharp)
                .ToArray();
        }

        throw new InvalidOperationException($"Unsupported solution format '{extension}'. Use .sln or .slnx.");
    }

    private static async Task<ReportResult> ExecuteReportAsync(ReportOptions options, CancellationToken cancellationToken)
    {
        var pingResult = await CheckDatabaseConnectionAsync(options.ConnectionString, cancellationToken);
        if (!pingResult.Success)
        {
            return ReportResult.Fail($"PostgreSQL health-check failed: {pingResult.Error}", isDatabaseError: true);
        }

        var schemaResult = await EnsureSchemaAsync(options.ConnectionString, cancellationToken);
        if (!schemaResult.Success)
        {
            return ReportResult.Fail($"Schema initialization failed: {schemaResult.Error}", isDatabaseError: true);
        }

        const string sql = """
                           SELECT
                               id,
                               commit_sha,
                               started_at_utc,
                               finished_at_utc,
                               projects_count,
                               files_count,
                               symbols_count,
                               relations_count,
                               duration_ms
                           FROM latest_successful_scan_runs
                           WHERE repo_path = @repo_path;
                           """;

        try
        {
            await using var connection = new NpgsqlConnection(options.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = new NpgsqlCommand(sql, connection);
            command.CommandTimeout = CommandTimeoutSeconds;
            command.Parameters.AddWithValue("repo_path", options.RepoPath);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return ReportResult.Fail($"No successful scan snapshot found for repo: {options.RepoPath}", isDatabaseError: false);
            }

            var report = new ScanReport(
                ScanRunId: reader.GetInt64(0),
                CommitSha: reader.GetString(1),
                StartedAtUtc: reader.GetFieldValue<DateTimeOffset>(2),
                FinishedAtUtc: reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                ProjectsCount: reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                FilesCount: reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                SymbolsCount: reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                RelationsCount: reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                DurationMs: reader.IsDBNull(8) ? 0 : reader.GetInt64(8));

            PrintReport(options.RepoPath, report);
            return ReportResult.Ok();
        }
        catch (Exception ex)
        {
            return ReportResult.Fail(DescribeException(ex), isDatabaseError: true);
        }
    }

    private static void PrintReport(string repoPath, ScanReport report)
    {
        Console.WriteLine("[info] Scan report (latest successful snapshot)");
        Console.WriteLine($"[info] Repo: {repoPath}");
        Console.WriteLine($"[info] RunId: {report.ScanRunId}");
        Console.WriteLine($"[info] Commit: {report.CommitSha}");
        Console.WriteLine($"[info] StartedAtUtc: {report.StartedAtUtc:O}");
        Console.WriteLine($"[info] FinishedAtUtc: {report.FinishedAtUtc:O}");
        Console.WriteLine($"[info] Projects: {report.ProjectsCount}");
        Console.WriteLine($"[info] Files: {report.FilesCount}");
        Console.WriteLine($"[info] Symbols: {report.SymbolsCount}");
        Console.WriteLine($"[info] Relations: {report.RelationsCount}");
        Console.WriteLine($"[info] DurationMs: {report.DurationMs}");
    }

    private static string DescribeException(Exception ex)
        => ex switch
        {
            OperationCanceledException => "Operation cancelled.",
            PostgresException pg => $"PostgreSQL error (SQLSTATE {pg.SqlState}): {pg.MessageText}",
            InvalidOperationException invalidOp when invalidOp.Message.Contains("MSBuild", StringComparison.OrdinalIgnoreCase)
                => "MSBuild/Roslyn initialization failed. Ensure .NET SDK and workload are installed and solution builds locally.",
            _ => ex.Message
        };

    private static async Task InsertSymbolsAsync(
        NpgsqlConnection connection,
        long scanRunId,
        IReadOnlyCollection<ExtractedSymbol> symbols,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
                           INSERT INTO symbols (scan_run_id, symbol_key, kind, name, containing_type, "namespace", file_path)
                           VALUES (@scan_run_id, @symbol_key, @kind, @name, @containing_type, @namespace, @file_path)
                           ON CONFLICT (scan_run_id, symbol_key) DO NOTHING;
                           """;

        foreach (var symbol in symbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var command = new NpgsqlCommand(sql, connection);
            command.CommandTimeout = CommandTimeoutSeconds;
            command.Transaction = transaction;
            command.Parameters.AddWithValue("scan_run_id", scanRunId);
            command.Parameters.AddWithValue("symbol_key", symbol.SymbolKey);
            command.Parameters.AddWithValue("kind", symbol.Kind);
            command.Parameters.AddWithValue("name", symbol.Name);
            command.Parameters.AddWithValue("containing_type", (object?)symbol.ContainingType ?? DBNull.Value);
            command.Parameters.AddWithValue("namespace", (object?)symbol.Namespace ?? DBNull.Value);
            command.Parameters.AddWithValue("file_path", (object?)symbol.FilePath ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertRelationsAsync(
        NpgsqlConnection connection,
        long scanRunId,
        IReadOnlyCollection<ExtractedRelation> relations,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
                           INSERT INTO relations (scan_run_id, from_symbol_key, relation_type, to_symbol_key)
                           VALUES (@scan_run_id, @from_symbol_key, @relation_type, @to_symbol_key)
                           ON CONFLICT (scan_run_id, from_symbol_key, relation_type, to_symbol_key) DO NOTHING;
                           """;

        foreach (var relation in relations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var command = new NpgsqlCommand(sql, connection);
            command.CommandTimeout = CommandTimeoutSeconds;
            command.Transaction = transaction;
            command.Parameters.AddWithValue("scan_run_id", scanRunId);
            command.Parameters.AddWithValue("from_symbol_key", relation.FromSymbolKey);
            command.Parameters.AddWithValue("relation_type", relation.RelationType);
            command.Parameters.AddWithValue("to_symbol_key", relation.ToSymbolKey);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<DatabasePingResult> EnsureSchemaAsync(string connectionString, CancellationToken cancellationToken)
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
                                      projects_count INTEGER NULL,
                                      files_count INTEGER NULL,
                                      symbols_count INTEGER NULL,
                                      relations_count INTEGER NULL,
                                      duration_ms BIGINT NULL,
                                     error TEXT NULL
                                 );

                                  ALTER TABLE scan_runs ADD COLUMN IF NOT EXISTS projects_count INTEGER NULL;
                                  ALTER TABLE scan_runs ADD COLUMN IF NOT EXISTS files_count INTEGER NULL;
                                  ALTER TABLE scan_runs ADD COLUMN IF NOT EXISTS symbols_count INTEGER NULL;
                                  ALTER TABLE scan_runs ADD COLUMN IF NOT EXISTS relations_count INTEGER NULL;
                                  ALTER TABLE scan_runs ADD COLUMN IF NOT EXISTS duration_ms BIGINT NULL;

                                 CREATE INDEX IF NOT EXISTS ix_scan_runs_repo_path_started_at
                                     ON scan_runs (repo_path, started_at_utc DESC);

                                 CREATE INDEX IF NOT EXISTS ix_scan_runs_repo_status_started_at
                                     ON scan_runs (repo_path, status, started_at_utc DESC);

                                 CREATE OR REPLACE VIEW latest_successful_scan_runs AS
                                 SELECT DISTINCT ON (repo_path)
                                     id,
                                     repo_path,
                                     commit_sha,
                                     status,
                                     started_at_utc,
                                     finished_at_utc,
                                     projects_count,
                                     files_count,
                                     symbols_count,
                                     relations_count,
                                     duration_ms,
                                     error
                                 FROM scan_runs
                                 WHERE status = 'succeeded'
                                 ORDER BY repo_path, started_at_utc DESC;

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

                                 CREATE UNIQUE INDEX IF NOT EXISTS ux_relations_scan_run_from_type_to
                                     ON relations (scan_run_id, from_symbol_key, relation_type, to_symbol_key);

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
            await connection.OpenAsync(cancellationToken);

            await using var command = new NpgsqlCommand(schemaSql, connection);
            command.CommandTimeout = CommandTimeoutSeconds;
            await command.ExecuteNonQueryAsync(cancellationToken);

            return DatabasePingResult.Ok();
        }
        catch (OperationCanceledException)
        {
            return DatabasePingResult.Fail("Operation cancelled.");
        }
        catch (Exception ex)
        {
            return DatabasePingResult.Fail(DescribeException(ex));
        }
    }

    private static async Task<DatabasePingResult> CheckDatabaseConnectionAsync(string connectionString, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = new NpgsqlCommand("SELECT 1", connection);
            command.CommandTimeout = CommandTimeoutSeconds;
            await command.ExecuteScalarAsync(cancellationToken);

            return DatabasePingResult.Ok();
        }
        catch (OperationCanceledException)
        {
            return DatabasePingResult.Fail("Operation cancelled.");
        }
        catch (Exception ex)
        {
            return DatabasePingResult.Fail(DescribeException(ex));
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

        var extension = Path.GetExtension(solutionPath);
        if (!string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase))
        {
            error = $"Unsupported solution format '{extension}'. Use .sln or .slnx.";
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

    private static bool TryParseReportArguments(string[] args, out ReportOptions options, out string error)
    {
        options = default;
        error = string.Empty;

        if (!string.Equals(args[0], ReportCommand, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(args[0], ValidateCommand, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Unsupported command '{args[0]}' for report/validate parser.";
            return false;
        }

        var parsed = ParseNamedArguments(args.Skip(1).ToArray());

        if (!TryGetRequiredArgument(parsed, RepoArg, out var repoPath, out error))
        {
            return false;
        }

        if (!TryGetConnectionString(parsed, out var connectionString, out error))
        {
            return false;
        }

        options = new ReportOptions(repoPath, connectionString);
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
        Console.WriteLine(
            "  Mcp.Scanner report --repo <repo-path> [--connection <connection-string>]");
        Console.WriteLine(
            "  Mcp.Scanner validate --repo <repo-path> [--connection <connection-string>]");
        Console.WriteLine();
        Console.WriteLine($"Or set environment variable: {EnvironmentConnection}");
    }

    private readonly record struct ScanOptions(string SolutionPath, string RepoPath, string CommitSha, string ConnectionString);

    private readonly record struct ReportOptions(string RepoPath, string ConnectionString);

    private readonly record struct DatabasePingResult(bool Success, string? Error)
    {
        public static DatabasePingResult Ok() => new(true, null);
        public static DatabasePingResult Fail(string error) => new(false, error);
    }

    private readonly record struct SymbolScanResult(
        bool Success,
        long ScanRunId,
        int SymbolsCount,
        int RelationsCount,
        int ProjectsCount,
        int FilesCount,
        long DurationMs,
        long? LatestSuccessfulScanRunId,
        string? Error)
    {
        public static SymbolScanResult Ok(long scanRunId, ScanMetrics metrics, long? latestSuccessfulScanRunId)
            => new(
                true,
                scanRunId,
                metrics.SymbolsCount,
                metrics.RelationsCount,
                metrics.ProjectsCount,
                metrics.FilesCount,
                metrics.DurationMs,
                latestSuccessfulScanRunId,
                null);

        public static SymbolScanResult Fail(string error)
            => new(false, 0, 0, 0, 0, 0, 0, null, error);
    }

    private readonly record struct ExtractionResult(
        IReadOnlyCollection<ExtractedSymbol> Symbols,
        IReadOnlyCollection<ExtractedRelation> Relations,
        int ProjectsCount,
        int FilesCount);

    private readonly record struct ScanMetrics(
        int ProjectsCount,
        int FilesCount,
        int SymbolsCount,
        int RelationsCount,
        long DurationMs);

    private readonly record struct ScanReport(
        long ScanRunId,
        string CommitSha,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset? FinishedAtUtc,
        int ProjectsCount,
        int FilesCount,
        int SymbolsCount,
        int RelationsCount,
        long DurationMs);

    private readonly record struct ReportResult(bool Success, bool IsDatabaseError, string? Error)
    {
        public static ReportResult Ok() => new(true, false, null);
        public static ReportResult Fail(string error, bool isDatabaseError) => new(false, isDatabaseError, error);
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

    private sealed record ExtractedRelation(string FromSymbolKey, string RelationType, string ToSymbolKey)
    {
        public static ExtractedRelation Implements(INamedTypeSymbol implementation, INamedTypeSymbol abstraction)
            => new(
                FromSymbolKey: implementation.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                RelationType: "implements",
                ToSymbolKey: abstraction.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));

        public static ExtractedRelation SymbolDeclaredInFile(ISymbol symbol, string filePath)
            => new(
                FromSymbolKey: symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                RelationType: "declared_in_file",
                ToSymbolKey: $"file:{filePath}");
    }
}
