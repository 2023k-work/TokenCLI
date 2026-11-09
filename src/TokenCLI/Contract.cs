using System.Text.Json;
using System.Text.Json.Nodes;

namespace TokenCLI;

public sealed class ContractException : Exception { }
public sealed record ApiCall(string Provider, string Operation, Uri Endpoint, JsonObject Body);

public static class Contract
{
    public const int MaxRequestBytes = 262144;
    public static readonly string[] CountFields = ["model", "input", "reasoning", "tools", "tool_choice", "truncation"];
    private static void Require(bool condition) { if (!condition) throw new ContractException(); }
    public static JsonObject Parse(ReadOnlyMemory<byte> bytes)
    {
        Require(bytes.Length <= MaxRequestBytes);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        RejectDuplicates(document.RootElement);
        return JsonNode.Parse(bytes.Span) as JsonObject ?? throw new ContractException();
    }
    public static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in value.EnumerateObject())
            {
                Require(keys.Add(item.Name));
                RejectDuplicates(item.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
        else if (value.ValueKind == JsonValueKind.Number && !double.IsFinite(value.GetDouble()))
            throw new ContractException();
    }
    private static JsonObject Object(JsonNode? value) => value as JsonObject ?? throw new ContractException();
    private static JsonArray Array(JsonNode? value) => value as JsonArray ?? throw new ContractException();
    public static string Text(JsonNode? value)
    {
        if (value is JsonValue item && item.TryGetValue<string>(out var text)) return text;
        throw new ContractException();
    }
    private static void Keys(JsonObject value, string[] required, string[]? optional = null)
    {
        Require(required.All(value.ContainsKey));
        Require(value.All(p => required.Contains(p.Key) || (optional?.Contains(p.Key) ?? false)));
    }
    private static void Equal(JsonNode? value, string expected) => Require(Text(value) == expected);
    private static void FixedObject(JsonNode? node, string key, string value)
    {
        var obj = Object(node); Keys(obj, [key]); Equal(obj[key], value);
    }
    public static ApiCall Aoelr(JsonObject envelope)
    {
        Keys(envelope, ["operation", "payload"]);
        var operation = Text(envelope["operation"]);
        Require(operation is "count" or "generate");
        var body = Object(envelope["payload"]);
        Keys(body, ["model", "reasoning", "service_tier", "max_output_tokens", "tools", "tool_choice", "store", "stream", "truncation", "prompt_cache_options", "input"]);
        Equal(body["model"], "gpt-6.1-sol");
        FixedObject(body["reasoning"], "effort", "medium");
        Equal(body["service_tier"], "default");
        Require(body["max_output_tokens"]?.ToJsonString() == "3072");
        Require(Array(body["tools"]).Count == 0);
        Equal(body["tool_choice"], "none");
        Require(body["store"]?.ToJsonString() == "false" && body["stream"]?.ToJsonString() == "false");
        Equal(body["truncation"], "disabled");
        var cache = Object(body["prompt_cache_options"]);
        Keys(cache, ["mode", "ttl"]); Equal(cache["mode"], "explicit"); Equal(cache["ttl"], "30m");
        var messages = Array(body["input"]);
        Require(messages.Count is >= 1 and <= 256);
        foreach (var node in messages)
        {
            var message = Object(node); Keys(message, ["role", "content"], ["type"]);
            Require(Text(message["role"]) is "system" or "developer" or "user" or "assistant");
            if (message.ContainsKey("type")) Equal(message["type"], "message");
            if (message["content"] is JsonArray content)
            {
                Require(content.Count is >= 1 and <= 256);
                foreach (var part in content)
                {
                    var block = Object(part); Keys(block, ["type", "text"]);
                    Equal(block["type"], "input_text"); Require(Text(block["text"]).Length <= 200000);
                }
            }
            else _ = Text(message["content"]);
        }
        var projected = new JsonObject();
        if (operation == "count")
            foreach (var field in CountFields) projected[field] = body[field]!.DeepClone();
        else projected = (JsonObject)body.DeepClone();
        return new("openai", operation, new Uri("https://api.openai.com/v1/responses" + (operation == "count" ? "/input_tokens" : "")), projected);
    }
    public static ApiCall GenerateText(JsonObject envelope)
    {
        Keys(envelope, ["provider", "model", "prompt"], ["max_output_tokens"]);
        var provider = Text(envelope["provider"]);
        Require(provider is "openai" or "anthropic");
        var model = Text(envelope["model"]);
        Require(System.Text.RegularExpressions.Regex.IsMatch(model, @"\A[A-Za-z0-9._:-]{1,128}\z"));
        var prompt = Text(envelope["prompt"]);
        Require(!string.IsNullOrWhiteSpace(prompt) && prompt.Length <= 32000);
        var limit = 1024;
        if (envelope.ContainsKey("max_output_tokens"))
        {
            var raw = envelope["max_output_tokens"]?.ToJsonString() ?? "";
            Require(System.Text.RegularExpressions.Regex.IsMatch(raw, @"\A[0-9]+\z") && int.TryParse(raw, out limit));
        }
        Require(limit is >= 16 and <= 8192);
        var body = provider == "openai"
            ? new JsonObject { ["model"] = model, ["input"] = prompt, ["max_output_tokens"] = limit, ["store"] = false, ["stream"] = false }
            : new JsonObject { ["model"] = model, ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }), ["max_tokens"] = limit, ["stream"] = false };
        return new(provider, "generate", new Uri(provider == "openai" ? "https://api.openai.com/v1/responses" : "https://api.anthropic.com/v1/messages"), body);
    }
}
