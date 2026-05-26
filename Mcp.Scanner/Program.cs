using Npgsql;

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
            Console.WriteLine("[info] Stage 1 completed successfully.");
            return SuccessExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[error] Unexpected scanner failure: {ex.Message}");
            return UnexpectedErrorExitCode;
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
}
