using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MemoryMcpServer.Contracts;
using MemoryMcpServer.Services;

namespace MemoryMcpServer.Mcp;

public sealed class StdioMcpServer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IContextService _contextService;
    private readonly ILogger<StdioMcpServer> _logger;

    public StdioMcpServer(IContextService contextService, ILogger<StdioMcpServer> logger)
    {
        _contextService = contextService;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting MCP stdio server...");

        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();

        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await ReadMessageAsync(input, cancellationToken);
            if (message is null)
            {
                break;
            }

            var response = await ProcessMessageAsync(message, cancellationToken);
            if (response is not null)
            {
                await WriteMessageAsync(output, response, cancellationToken);
            }
        }
    }

    private async Task<string?> ReadMessageAsync(Stream input, CancellationToken cancellationToken)
    {
        var headerText = await ReadHeadersAsync(input, cancellationToken);
        if (headerText is null)
        {
            return null;
        }

        var contentLength = ParseContentLength(headerText);
        if (contentLength <= 0)
        {
            return null;
        }

        var payload = new byte[contentLength];
        var read = 0;
        while (read < contentLength)
        {
            var chunk = await input.ReadAsync(payload.AsMemory(read, contentLength - read), cancellationToken);
            if (chunk <= 0)
            {
                return null;
            }

            read += chunk;
        }

        return Encoding.UTF8.GetString(payload);
    }

    private static async Task<string?> ReadHeadersAsync(Stream input, CancellationToken cancellationToken)
    {
        var buffer = new List<byte>(256);
        var state = 0;

        while (true)
        {
            var one = new byte[1];
            var read = await input.ReadAsync(one.AsMemory(0, 1), cancellationToken);
            if (read == 0)
            {
                return buffer.Count == 0 ? null : Encoding.ASCII.GetString(buffer.ToArray());
            }

            var b = one[0];
            buffer.Add(b);

            state = state switch
            {
                0 when b == (byte)'\r' => 1,
                1 when b == (byte)'\n' => 2,
                2 when b == (byte)'\r' => 3,
                3 when b == (byte)'\n' => 4,
                _ => 0
            };

            if (state == 4)
            {
                break;
            }
        }

        return Encoding.ASCII.GetString(buffer.ToArray());
    }

    private static int ParseContentLength(string headers)
    {
        var lines = headers.Split(["\r\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            if (!line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line["Content-Length:".Length..].Trim();
            if (int.TryParse(value, out var length))
            {
                return length;
            }
        }

        return -1;
    }

    private static async Task WriteMessageAsync(Stream output, JsonObject payload, CancellationToken cancellationToken)
    {
        var json = payload.ToJsonString(JsonOptions);
        var body = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");

        await output.WriteAsync(header, cancellationToken);
        await output.WriteAsync(body, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    private async Task<JsonObject?> ProcessMessageAsync(string message, CancellationToken cancellationToken)
    {
        JsonObject? request;
        try
        {
            request = JsonNode.Parse(message)?.AsObject();
        }
        catch (JsonException)
        {
            return BuildErrorResponse(null, -32700, "Parse error");
        }

        if (request is null)
        {
            return BuildErrorResponse(null, -32600, "Invalid Request");
        }

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
            "tools/call" => await HandleToolsCallAsync(id, request["params"]?.AsObject(), cancellationToken),
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
                    ["name"] = "memory.get_context",
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

    private async Task<JsonObject> HandleToolsCallAsync(JsonNode? id, JsonObject? @params, CancellationToken cancellationToken)
    {
        if (@params is null)
        {
            return BuildErrorResponse(id, -32602, "Invalid params");
        }

        var name = @params["name"]?.GetValue<string>();
        if (!string.Equals(name, "memory.get_context", StringComparison.Ordinal))
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
            var traceId = Guid.NewGuid().ToString("N");
            var result = await _contextService.GetContextAsync(
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
            _logger.LogError(ex, "MCP tools/call failed for memory.get_context");
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