using System.Text.Json;
using System.Text.Json.Nodes;
using MemoryMcpServer.Contracts;
using MemoryMcpServer.Services;

namespace MemoryMcpServer.Mcp;

public static class HttpMcpServer
{
    private const string ToolName = "memory_get_context";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<JsonObject?> ProcessAsync(
        JsonObject request,
        IContextService contextService,
        ILogger logger,
        string traceId,
        CancellationToken cancellationToken)
    {
        var id = request["id"]?.DeepClone();
        var method = request["method"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(method))
        {
            return BuildErrorResponse(id, -32600, "Invalid Request");
        }

        if (string.Equals(method, "notifications/initialized", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return method switch
        {
            "initialize" => BuildResultResponse(id, BuildInitializeResult()),
            "tools/list" => BuildResultResponse(id, BuildToolsListResult()),
            "tools/call" => await HandleToolsCallAsync(id, request["params"]?.AsObject(), contextService, logger, traceId, cancellationToken),
            "ping" => BuildResultResponse(id, new JsonObject()),
            _ => BuildErrorResponse(id, -32601, $"Method not found: {method}")
        };
    }

    private static JsonObject BuildInitializeResult()
    {
        return new JsonObject
        {
            ["protocolVersion"] = "2024-11-05",
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject
                {
                    ["listChanged"] = false
                }
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "memory-mcp-server",
                ["version"] = "1.0.0-mvp"
            }
        };
    }

    private static JsonObject BuildToolsListResult()
    {
        return new JsonObject
        {
            ["tools"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = ToolName,
                    ["description"] = "Returns deterministic context pack from local scanner snapshot data.",
                    ["inputSchema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["task"] = new JsonObject
                            {
                                ["type"] = "string"
                            },
                            ["scope"] = new JsonObject
                            {
                                ["type"] = "string"
                            },
                            ["constraints"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["items"] = new JsonObject
                                {
                                    ["type"] = "string"
                                }
                            },
                            ["filesHint"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["items"] = new JsonObject
                                {
                                    ["type"] = "string"
                                }
                            }
                        },
                        ["required"] = new JsonArray("task")
                    }
                }
            }
        };
    }

    private static async Task<JsonObject> HandleToolsCallAsync(
        JsonNode? id,
        JsonObject? @params,
        IContextService contextService,
        ILogger logger,
        string traceId,
        CancellationToken cancellationToken)
    {
        if (@params is null)
        {
            return BuildErrorResponse(id, -32602, "Invalid params");
        }

        var name = @params["name"]?.GetValue<string>();
        var isSupportedName = string.Equals(name, ToolName, StringComparison.Ordinal)
                              || string.Equals(name, "memory.get_context", StringComparison.Ordinal);

        if (!isSupportedName)
        {
            return BuildErrorResponse(id, -32602, $"Unknown tool: {name}");
        }

        var args = @params["arguments"]?.AsObject() ?? new JsonObject();
        var task = args["task"]?.GetValue<string>() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(task))
        {
            return BuildErrorResponse(id, -32602, "Tool argument 'task' is required.");
        }

        var scope = args["scope"]?.GetValue<string>();
        var constraints = ReadStringArray(args["constraints"]);
        var filesHint = ReadStringArray(args["filesHint"]);

        try
        {
            var result = await contextService.GetContextAsync(
                new GetContextRequest(task, scope, constraints, filesHint),
                traceId,
                cancellationToken);

            var machineResultNode = BuildMachineFirstResultNode(result, task, scope ?? string.Empty, traceId);
            var contentSummary = new
            {
                primaryTargetsCount = result.PrimaryTargets.Count,
                relatedSymbolsCount = result.RelatedSymbols.Count,
                commit = result.Freshness.Commit,
                topologyPresent = true,
                mcpTop3 = BuildMcpTop3(result)
            };
            return BuildResultResponse(id, new JsonObject
            {
                ["isError"] = false,
                ["structuredContent"] = machineResultNode,
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = JsonSerializer.Serialize(contentSummary, JsonOptions)
                    }
                }
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MCP tools/call failed for memory.get_context");
            return BuildResultResponse(id, new JsonObject
            {
                ["isError"] = true,
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = $"memory.get_context failed: {ex.Message}"
                    }
                }
            });
        }
    }

    private static JsonNode? BuildMachineFirstResultNode(
        GetContextResponse response,
        string task,
        string scope,
        string traceId)
    {
        var filteredPrimary = response.PrimaryTargets
            .Where(t => !IsNoiseFile(t.FilePath))
            .ToArray();

        var primarySource = filteredPrimary.Length > 0 ? filteredPrimary : response.PrimaryTargets.ToArray();

        var inclusionLookup = response.InclusionReasons
            .GroupBy(x => x.Artifact, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Reason).FirstOrDefault() ?? "matched_by_scope", StringComparer.Ordinal);

        var groupedTargets = primarySource
            .Where(t => !string.IsNullOrWhiteSpace(t.FilePath))
            .GroupBy(t => t.FilePath!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Max(x => x.Score))
            .Take(6)
            .ToArray();

        var primaryItems = groupedTargets
            .Select(g => new
            {
                path = ToRepoRelativePath(g.Key),
                symbols = g.Select(x => ExtractSymbolName(x.SymbolKey)).Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToArray(),
                lineHints = Array.Empty<int>(),
                why = BuildWhy(g, inclusionLookup)
            })
            .ToArray();

        var entrypoints = BuildUiEntryPoints(primarySource);
        var commandChain = BuildExecutionFlow(primarySource, response.RelatedSymbols);
        var exclude = BuildExcludeList(response.ConstraintsApplied);

        var relatedItems = response.RelatedSymbols
            .Where(r => !IsNoiseFile(r.FilePath))
            .Take(8)
            .Select(r => new
            {
                symbol = ExtractSymbolName(r.SymbolKey),
                declaredIn = ToRepoRelativePath(r.FilePath),
                usedFrom = entrypoints.FirstOrDefault() ?? primaryItems.FirstOrDefault()?.path ?? "n/a"
            })
            .ToArray();

        if (relatedItems.Length == 0)
        {
            relatedItems = response.RelatedSymbols
                .Take(5)
                .Select(r => new
                {
                    symbol = ExtractSymbolName(r.SymbolKey),
                    declaredIn = ToRepoRelativePath(r.FilePath),
                    usedFrom = entrypoints.FirstOrDefault() ?? primaryItems.FirstOrDefault()?.path ?? "n/a"
                })
                .ToArray();
        }

        var implementedBy = response.RelatedSymbols
            .Where(r => string.Equals(r.RelationType, "implements", StringComparison.OrdinalIgnoreCase))
            .Select(r => ExtractSymbolName(r.SymbolKey))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();

        var declaredIn = response.RelatedSymbols
            .Where(r => string.Equals(r.RelationType, "declared_in_file", StringComparison.OrdinalIgnoreCase))
            .Select(r => ToRepoRelativePath(r.FilePath))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();

        var highConfidenceBoundary = BuildHighConfidenceBoundary(entrypoints, commandChain, implementedBy);
        var shortlist = primaryItems.Select(x => x.path).Where(x => !string.IsNullOrWhiteSpace(x)).Take(10).ToArray();

        var machine = new
        {
            task,
            scope,
            constraintsApplied = exclude,
            response.Freshness,
            primaryTargets = new
            {
                count = response.PrimaryTargets.Count,
                items = primaryItems
            },
            relatedSymbols = new
            {
                count = response.RelatedSymbols.Count,
                items = relatedItems
            },
            retrievalConstraintsApplied = response.ConstraintsApplied,
            topology = new
            {
                focus = primaryItems.SelectMany(x => x.symbols).Take(4).ToArray(),
                flow = commandChain,
                uiBoundary = entrypoints,
                entrypoints,
                executionPath = commandChain.Length > 1
                    ? new[] { string.Join(" -> ", commandChain) }
                    : Array.Empty<string>(),
                implementedBy,
                declaredIn,
                exclude,
                highConfidenceBoundary
            },
            actionableShortlistTop10 = shortlist,
            mcpTop3 = BuildMcpTop3(response),
            trace = new
            {
                traceId
            }
        };

        return JsonSerializer.SerializeToNode(machine, JsonOptions);
    }

    private static string[] BuildExecutionFlow(
        IReadOnlyList<ContextTarget> primaryTargets,
        IReadOnlyList<RelatedSymbol> relatedSymbols)
    {
        var symbols = primaryTargets
            .Select(t => t.SymbolKey)
            .Concat(relatedSymbols.Select(r => r.SymbolKey))
            .ToArray();

        var ui = symbols.FirstOrDefault(s => s.Contains("IndexModel", StringComparison.OrdinalIgnoreCase));
        var command = symbols.FirstOrDefault(s => s.Contains("Command", StringComparison.OrdinalIgnoreCase)
                                                  && !s.Contains("Handler", StringComparison.OrdinalIgnoreCase)
                                                  && !s.Contains("Dispatcher", StringComparison.OrdinalIgnoreCase)
                                                  && !s.Contains("Pipeline", StringComparison.OrdinalIgnoreCase));
        var handler = symbols.FirstOrDefault(s => s.Contains("Handler", StringComparison.OrdinalIgnoreCase));
        var gateway = symbols.FirstOrDefault(s => s.Contains("Gateway", StringComparison.OrdinalIgnoreCase)
                                                  || s.Contains("Port", StringComparison.OrdinalIgnoreCase));

        return new[] { ui, command, handler, gateway }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => ExtractSymbolName(s!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] BuildUiEntryPoints(IReadOnlyList<ContextTarget> primaryTargets)
    {
        return primaryTargets
            .Where(t => !string.IsNullOrWhiteSpace(t.FilePath) && IsUiFile(t.FilePath!))
            .Select(t => ToRepoRelativePath(t.FilePath))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray()!;
    }

    private static string[] BuildHighConfidenceBoundary(
        IReadOnlyList<string> entrypoints,
        IReadOnlyList<string> commandChain,
        IReadOnlyList<string> implementedBy)
    {
        var boundary = new List<string>();

        if (entrypoints.Count > 0)
        {
            boundary.Add("ui-entrypoint");
        }

        if (commandChain.Any(s => s.Contains("Command", StringComparison.OrdinalIgnoreCase))
            && commandChain.Any(s => s.Contains("Handler", StringComparison.OrdinalIgnoreCase)))
        {
            boundary.Add("command-handler-flow");
        }

        if (implementedBy.Any(s => s.Contains("Gateway", StringComparison.OrdinalIgnoreCase)
                                   || s.Contains("Port", StringComparison.OrdinalIgnoreCase)))
        {
            boundary.Add("media-link-boundary");
        }

        if (boundary.Count == 0)
        {
            boundary.Add("bounded-topology-expansion");
        }

        return boundary.ToArray();
    }

    private static string[] BuildExcludeList(IReadOnlyList<string> constraintsApplied)
    {
        var byConstraint = constraintsApplied
            .Where(c => c.StartsWith("do-not-touch", StringComparison.OrdinalIgnoreCase))
            .SelectMany(c => c.Split(':', 2).Skip(1))
            .SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToArray();

        var defaults = new[]
        {
            "**/Tests/**",
            "**/WebRoutes*"
        };

        return byConstraint
            .Concat(defaults)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
    }

    private static bool IsUiFile(string filePath)
        => (filePath.Contains("\\Web\\", StringComparison.OrdinalIgnoreCase)
            || filePath.Contains("/Web/", StringComparison.OrdinalIgnoreCase)
            || filePath.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase)
            || filePath.Contains("\\Pages\\", StringComparison.OrdinalIgnoreCase)
            || filePath.Contains("/Pages/", StringComparison.OrdinalIgnoreCase))
           && !filePath.Contains("\\Tests\\", StringComparison.OrdinalIgnoreCase)
           && !filePath.Contains("/Tests/", StringComparison.OrdinalIgnoreCase);

    private static bool IsNoiseFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        return filePath.Contains("\\Tests\\", StringComparison.OrdinalIgnoreCase)
               || filePath.Contains("/Tests/", StringComparison.OrdinalIgnoreCase)
               || filePath.Contains("WebRoutes", StringComparison.OrdinalIgnoreCase);
    }

    private static string[] BuildMcpTop3(GetContextResponse response)
    {
        return response.PrimaryTargets
            .Where(t => !IsNoiseFile(t.FilePath))
            .Select(t => ExtractSymbolName(t.SymbolKey))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();
    }

    private static string BuildWhy(
        IGrouping<string, ContextTarget> group,
        IReadOnlyDictionary<string, string> inclusionLookup)
    {
        var reasons = group
            .Select(t => inclusionLookup.TryGetValue(t.SymbolKey, out var r) ? r : "matched_by_scope")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToArray();

        var reason = reasons.Length > 0 ? string.Join(",", reasons) : "matched_by_scope";
        return $"ranked:{reason}";
    }

    private static string? ToRepoRelativePath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        var normalized = filePath.Replace('\\', '/');
        var marker = normalized.IndexOf("/Hockey.", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0)
        {
            return normalized[(marker + 1)..];
        }

        var marker2 = normalized.IndexOf("/MemoryMcpServer/", StringComparison.OrdinalIgnoreCase);
        if (marker2 >= 0)
        {
            return normalized[(marker2 + 1)..];
        }

        return normalized;
    }

    private static string? ShortFilePath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        var normalized = filePath.Replace('\\', '/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 3)
        {
            return normalized;
        }

        return string.Join('/', parts[^3], parts[^2], parts[^1]);
    }

    private static string ExtractSymbolName(string symbolKey)
    {
        if (string.IsNullOrWhiteSpace(symbolKey))
        {
            return symbolKey;
        }

        var tail = symbolKey.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return string.IsNullOrWhiteSpace(tail) ? symbolKey : tail;
    }

    private static string[] ReadStringArray(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return Array.Empty<string>();
        }

        return array
            .Select(x => x?.GetValue<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static JsonObject BuildResultResponse(JsonNode? id, JsonObject result)
    {
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = result
        };
    }

    private static JsonObject BuildErrorResponse(JsonNode? id, int code, string message)
    {
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message
            }
        };
    }
}
