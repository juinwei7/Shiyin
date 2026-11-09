using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shiyin.Services.Mcp;

/// <summary>
/// 本地 MCP 伺服器（M§44.1、D147、design-windows §11.12）：shiyin-mcp.exe，stdio 一行一個 JSON-RPC 2.0 訊息。
/// 只做 initialize、ping、tools/list、tools/call；stdout 只寫協定訊息，log 不寫參數和會議內容（N7）
/// </summary>
public static class McpServer
{
    public static readonly string[] ProtocolVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];
    public const string FallbackProtocolVersion = "2025-06-18";

    static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static void Run(TextReader input, TextWriter output, ShiyinPaths paths, string version, IReadOnlyDictionary<string, string>? overrides = null)
    {
        Log.Info("mcp server started");
        while (input.ReadLine() is { } line)
        {
            if (Handle(line, paths, version, overrides) is not { } reply) continue;
            output.Write(reply.ToJsonString(Json) + "\n");
            output.Flush();
        }
        Log.Info("mcp server stopped");
    }

    /// <summary>處理一行，回傳要寫出的回應；通知、客戶端的回應不用回</summary>
    public static JsonObject? Handle(string line, ShiyinPaths paths, string version, IReadOnlyDictionary<string, string>? overrides = null)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0) return null;
        JsonNode? node;
        try { node = JsonNode.Parse(trimmed); }
        catch (JsonException) { return Failure(null, -32700, "Parse error"); }
        if (node is not JsonObject message) return Failure(null, -32600, "Invalid request");
        if (message["method"] is not JsonValue m || !m.TryGetValue<string>(out var method)) return null;
        if (message["id"] is not { } id) return null;
        var parameters = message["params"] as JsonObject ?? [];
        switch (method)
        {
            case "initialize":
                var asked = parameters["protocolVersion"] is JsonValue pv && pv.TryGetValue<string>(out var v) ? v : "";
                return Success(id, new JsonObject
                {
                    ["protocolVersion"] = ProtocolVersions.Contains(asked) ? asked : FallbackProtocolVersion,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "shiyin", ["title"] = "Shiyin", ["version"] = version },
                    ["instructions"] = McpTools.Instructions,
                });
            case "ping":
                return Success(id, []);
            case "tools/list":
                return Success(id, new JsonObject { ["tools"] = McpTools.Definitions() });
            case "tools/call":
                var name = parameters["name"] is JsonValue nv && nv.TryGetValue<string>(out var n) ? n : "";
                if (!McpTools.Names.Contains(name)) return Failure(id, -32602, $"Unknown tool: {name}");
                var watch = Stopwatch.StartNew();
                var result = McpTools.Run(name, parameters["arguments"] as JsonObject ?? [], paths, overrides);
                Log.Info($"tool {name}: {(result.IsError ? "error" : "ok")}, {result.Text.Length} chars, {watch.ElapsedMilliseconds} ms");
                return Success(id, new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = result.Text }),
                    ["isError"] = result.IsError,
                });
            default:
                return Failure(id, -32601, $"Method not found: {method}");
        }
    }

    static JsonObject Success(JsonNode id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result };

    static JsonObject Failure(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };
}
