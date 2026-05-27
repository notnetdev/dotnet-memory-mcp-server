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

            var resultNode = JsonSerializer.SerializeToNode(result, JsonOptions);
            return BuildResultResponse(id, new JsonObject
            {
                ["isError"] = false,
                ["structuredContent"] = resultNode,
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = JsonSerializer.Serialize(result, JsonOptions)
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
