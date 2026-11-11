using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TokenCLI;

public sealed class ApiEngine(ICredentials credentials, Func<HttpMessageHandler>? handlerFactory = null, TimeSpan? deadlineOverride = null)
{
    public const int MaxResponseBytes = 4_000_000;
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(90);
    public static JsonObject EmptyEvidence() => new()
    {
        ["schema_version"] = 1, ["ok"] = false, ["bridge_request_id"] = Guid.NewGuid().ToString(),
        ["operation"] = null, ["outcome"] = "not_sent", ["http_attempted"] = false,
        ["http_status"] = null, ["provider_request_id"] = null, ["elapsed_s"] = 0,
        ["error_code"] = null, ["response_id"] = null, ["model"] = null, ["service_tier"] = null,
        ["reasoning"] = null, ["status"] = null, ["incomplete_details"] = null,
        ["usage"] = null, ["input_tokens"] = null, ["output_text"] = null,
        ["raw_response_json"] = null, ["present_fields"] = new JsonArray()
    };
    public static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None, Credentials = null,
        PooledConnectionLifetime = TimeSpan.Zero, PooledConnectionIdleTimeout = TimeSpan.Zero,
        ConnectTimeout = Deadline
    };
    public Task<JsonObject> ExecuteAsync(ReadOnlyMemory<byte> input, bool aoelr, CancellationToken cancellation = default) =>
        ExecuteCoreAsync(input, node => aoelr ? Contract.Aoelr(node) : Contract.GenerateText(node), false, cancellation);
    internal Task<JsonObject> ExecuteImagesAsync(ReadOnlyMemory<byte> input, CancellationToken cancellation) =>
        ExecuteCoreAsync(input, node => ImageContract.Parse(node).Call, true, cancellation);
    private async Task<JsonObject> ExecuteCoreAsync(ReadOnlyMemory<byte> input, Func<JsonObject, ApiCall> parse, bool image, CancellationToken cancellation)
    {
        var clock = Stopwatch.StartNew();
        var result = EmptyEvidence();
        JsonObject Finish(string? code = null)
        {
            result["ok"] = code is null; result["error_code"] = code;
            result["elapsed_s"] = clock.Elapsed.TotalSeconds;
            return result;
        }
        ApiCall call;
        JsonObject envelope;
        try { envelope = Contract.Parse(input); call = parse(envelope); }
        catch (Exception ex) when (ex is ContractException or JsonException or InvalidOperationException or ArgumentException)
        { return Finish("invalid_input"); }
        result["operation"] = call.Operation;
        if (cancellation.IsCancellationRequested) return Finish("cancelled");
        string? token;
        try { token = credentials.Read(call.Provider); }
        catch (CredentialException) { return Finish("credential_error"); }
        if (string.IsNullOrEmpty(token)) return Finish("missing_token");
        if (ContainsSecret(envelope, token)) return Finish("sensitive_input");
        try
        {
            using var handler = handlerFactory?.Invoke() ?? CreateHandler();
            using var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(deadlineOverride ?? (image ? ImageContract.Deadline : Deadline));
            using var request = new HttpRequestMessage(HttpMethod.Post, call.Endpoint)
            {
                Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = new StringContent(call.Body.ToJsonString(), Encoding.UTF8, "application/json")
            };
            request.Headers.ConnectionClose = true;
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
            if (call.Provider == "openai") request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            else
            {
                request.Headers.Add("x-api-key", token);
                request.Headers.Add("anthropic-version", "2023-06-01");
            }
            deadline.Token.ThrowIfCancellationRequested();
            result["http_attempted"] = true; result["outcome"] = "attempted_unknown";
            // Exactly one application send, fresh HTTP/1.1 connection, no redirect/auth/retry handlers.
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            var status = (int)response.StatusCode;
            result["http_status"] = status;
            if (response.Headers.TryGetValues("x-request-id", out var ids))
            {
                var allIds = ids.ToArray();
                var id = allIds.Length == 1 ? allIds[0] : null;
                if (id is not null && !id.Contains(token, StringComparison.Ordinal) && Regex.IsMatch(id, @"\A[A-Za-z0-9_-]{1,200}\z"))
                    result["provider_request_id"] = id;
            }
            if (status is < 200 or >= 300)
            {
                result["outcome"] = "http_error";
                return Finish(status switch
                {
                    401 or 403 => "authentication_failed", 429 => "rate_limited",
                    >= 300 and < 400 => "redirect_rejected", _ => "http_error"
                });
            }
            if (response.Content.Headers.ContentEncoding.Any(e => !e.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                return Finish("invalid_response");
            if (response.Content.Headers.ContentLength > (image ? ImageContract.MaxResponseBytes : MaxResponseBytes)) return Finish("response_too_large");
            using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[16384];
            int read;
            while ((read = await body.ReadAsync(chunk, deadline.Token)) != 0)
            {
                if (buffer.Length + read > (image ? ImageContract.MaxResponseBytes : MaxResponseBytes)) return Finish("response_too_large");
                buffer.Write(chunk, 0, read);
            }
            using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 64 });
            Contract.RejectDuplicates(document.RootElement);
            var data = JsonNode.Parse(document.RootElement.GetRawText()) as JsonObject ?? throw new ContractException();
            if (data["error"] is not null) return Finish("invalid_response");
            if (image) _ = ImageContract.Decode(data, Contract.Text(call.Body["output_format"]), token);
            data = (JsonObject)Scrub(data, token)!;
            result["raw_response_json"] = data.ToJsonString();
            result["present_fields"] = new JsonArray(data.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray());
            result["response_id"] = data["id"]?.DeepClone();
            foreach (var key in new[] { "model", "service_tier", "reasoning", "status", "incomplete_details", "usage", "input_tokens" })
                result[key] = data[key]?.DeepClone();
            if (call.Provider == "openai" && data["output"] is JsonArray output)
            {
                var text = new StringBuilder();
                foreach (var item in output.OfType<JsonObject>())
                    if (StringValue(item["type"]) == "message" && item["content"] is JsonArray content)
                        foreach (var block in content.OfType<JsonObject>())
                            if (StringValue(block["type"]) == "output_text" && StringValue(block["text"]) is { } value) text.Append(value);
                result["output_text"] = text.ToString();
            }
            else if (call.Provider == "anthropic" && data["content"] is JsonArray content)
            {
                result["output_text"] = string.Concat(content.OfType<JsonObject>()
                    .Where(b => StringValue(b["type"]) == "text").Select(b => StringValue(b["text"]) ?? ""));
            }
            result["outcome"] = "response_received";
            return Finish();
        }
        catch (OperationCanceledException) { return Finish(cancellation.IsCancellationRequested ? "cancelled" : "timeout"); }
        catch (Exception ex) when (ex is HttpRequestException or IOException) { return Finish("network_error"); }
        catch (Exception ex) when (ex is JsonException or ContractException or InvalidOperationException or FormatException or ArgumentException)
        { return Finish("invalid_response"); }
        catch (Exception) { return Finish("internal_error"); }
    }
    private static string? StringValue(JsonNode? value) => value is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;
    public static bool ContainsSecret(JsonNode? node, string token) => node switch
    {
        JsonObject obj => obj.Any(p => p.Key.Contains(token, StringComparison.Ordinal) || ContainsSecret(p.Value, token)),
        JsonArray array => array.Any(p => ContainsSecret(p, token)),
        _ => StringValue(node)?.Contains(token, StringComparison.Ordinal) ?? false
    };
    public static JsonNode? Scrub(JsonNode? node, string token)
    {
        if (node is JsonObject obj)
        {
            var clean = new JsonObject();
            foreach (var pair in obj)
            {
                var key = pair.Key.Replace(token, "[REDACTED]", StringComparison.Ordinal);
                if (clean.ContainsKey(key)) throw new ContractException();
                clean[key] = pair.Key.ToLowerInvariant() is "authorization" or "proxy-authorization" or "x-api-key" or "api_key" or "access_token"
                    ? JsonValue.Create("[REDACTED]") : Scrub(pair.Value, token);
            }
            return clean;
        }
        if (node is JsonArray array) return new JsonArray(array.Select(p => Scrub(p, token)).ToArray());
        return StringValue(node) is { } text ? JsonValue.Create(text.Replace(token, "[REDACTED]", StringComparison.Ordinal)) : node?.DeepClone();
    }
}
