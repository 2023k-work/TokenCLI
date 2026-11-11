using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TokenCLI;

static class ImageTests
{
    const string Secret = "fake-secret-never-a-real-api-key";
    static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII=");
    static void Check(bool value) { if (!value) throw new InvalidOperationException("image_assertion_failed"); }
    static JsonObject Payload(string directory, string format = "png") => new()
    { ["prompt"] = "小橘貓", ["output"] = Path.Combine(directory, "cat." + format), ["output_format"] = format };
    static JsonObject Data(byte[]? bytes = null) => new()
    {
        ["data"] = new JsonArray(new JsonObject { ["b64_json"] = Convert.ToBase64String(bytes ?? Png) }),
        ["usage"] = JsonNode.Parse("""{"input_tokens":12,"input_tokens_details":{"text_tokens":12,"image_tokens":0},"output_tokens":99,"output_tokens_details":{"image_tokens":99,"nested":{"future":true}}}""")
    };
    static HttpResponseMessage Reply(JsonObject? data = null, int status = 200)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent((data ?? Data()).ToJsonString()) };
        response.Headers.Add("x-request-id", "req_image"); return response;
    }
    static void Error(JsonObject result, string code, string outcome)
    {
        Check(result["ok"]!.GetValue<bool>() == false && result["error_code"]!.GetValue<string>() == code);
        Check(result["outcome"]!.GetValue<string>() == outcome && !result.ToJsonString().Contains(Secret));
        Check(result["artifact"] is null && result["image_data_omitted"]!.GetValue<bool>());
    }
    static async Task<JsonObject> Invoke(JsonObject payload, FakeHandler handler, CancellationToken cancellation = default, TimeSpan? timeout = null, FakeCredentials? credentials = null) =>
        await new ImageEngine(credentials ?? new FakeCredentials(Secret), () => handler, timeout).ExecuteAsync(Encoding.UTF8.GetBytes(payload.ToJsonString()), cancellation);
    public static void Add(Action<string, Func<Task>> register)
    {
        void Test(string name, Func<string, Task> run) => register("image_" + name, async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "TokenCLI.ImageTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try { await run(directory); }
            finally { Directory.Delete(directory, recursive: true); }
        });
        Test("exact_request_atomic_artifact_and_usage", async dir =>
        {
            var data = Data(); data["model"] = "actual-provider-model";
            var handler = new FakeHandler(async (request, ct) =>
            {
                Check(request.Method == HttpMethod.Post && request.RequestUri!.ToString() == "https://api.openai.com/v1/images/generations");
                Check(request.Version == HttpVersion.Version11 && request.VersionPolicy == HttpVersionPolicy.RequestVersionExact);
                Check(request.Headers.Authorization!.Parameter == Secret && request.Headers.ConnectionClose == true);
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct));
                Check(JsonNode.DeepEquals(body, JsonNode.Parse("""{"model":"gpt-image-2","prompt":"小橘貓","size":"1024x1024","quality":"medium","output_format":"png","background":"opaque","n":1,"stream":false,"moderation":"auto"}""")));
                Check(!File.Exists(Path.Combine(dir, "cat.png")));
                return Reply(data);
            });
            var result = await Invoke(Payload(dir), handler);
            Check(result["ok"]!.GetValue<bool>() && handler.Calls == 1 && result["http_status"]!.GetValue<int>() == 200);
            Check(File.ReadAllBytes(Path.Combine(dir, "cat.png")).SequenceEqual(Png) && Directory.GetFiles(dir).Length == 1);
            Check(result["artifact"]!["sha256"]!.GetValue<string>() == Convert.ToHexString(SHA256.HashData(Png)).ToLowerInvariant());
            Check(JsonNode.DeepEquals(result["usage"], data["usage"]) && result["model"]!.GetValue<string>() == "actual-provider-model");
            Check(result["provider_request_id"]!.GetValue<string>() == "req_image");
            Check(!result.ToJsonString().Contains(Convert.ToBase64String(Png)) && result["raw_response_json"]!.GetValue<string>().Contains("[IMAGE_DATA_OMITTED]"));
            await File.WriteAllTextAsync(Path.Combine(dir, "evidence.json"), result.ToJsonString());
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "image-success-evidence.json"), result.ToJsonString());
        });
        foreach (var format in new[] { "jpeg", "webp" })
        Test("format_" + format, async dir =>
        {
            var bytes = format == "jpeg" ? new byte[] { 255, 216, 255, 217 } : "RIFF0000WEBP"u8.ToArray();
            var payload = Payload(dir, format);
            var handler = new FakeHandler((_, _) => Task.FromResult(Reply(Data(bytes))));
            Check((await Invoke(payload, handler))["ok"]!.GetValue<bool>());
            Check(File.ReadAllBytes(Path.Combine(dir, "cat." + format)).SequenceEqual(bytes));
        });
        Test("missing_usage_and_model_not_fabricated", async dir =>
        {
            var data = Data(); data.Remove("usage");
            var result = await Invoke(Payload(dir), new FakeHandler((_, _) => Task.FromResult(Reply(data))));
            Check(result["ok"]!.GetValue<bool>() && result["usage"] is null && result["model"] is null && result["response_id"] is null);
        });
        Test("invalid_input_before_credentials_or_http", async dir =>
        {
            foreach (var (key, value) in new (string, JsonNode?)[]
            {
                ("url", JsonValue.Create("https://evil.invalid")), ("method", JsonValue.Create("GET")), ("n", JsonValue.Create(2)),
                ("tools", new JsonArray()), ("stream", JsonValue.Create(true)), ("model", JsonValue.Create("other")),
                ("response_format", JsonValue.Create("url")), ("quality", JsonValue.Create("auto")), ("size", JsonValue.Create("9999x9999")),
                ("prompt", JsonValue.Create(" ")), ("prompt", JsonValue.Create(new string('x', 32001))),
                ("output", JsonValue.Create("https://evil.invalid/a.png")), ("output", JsonValue.Create(@"\\server\share\a.png")),
                ("output", JsonValue.Create(Path.Combine(dir, "cat.png:stream"))), ("output", JsonValue.Create(Path.Combine(dir, "NUL.png"))),
                ("output", JsonValue.Create(Path.Combine(dir, "wrong.jpg"))), ("background", JsonValue.Create("bad")), ("model", null)
            })
            {
                var payload = Payload(dir); payload[key] = value;
                var credentials = new FakeCredentials(Secret); var handler = new FakeHandler((_, _) => throw new InvalidOperationException());
                Error(await Invoke(payload, handler, credentials: credentials), "invalid_input", "not_sent");
                Check(credentials.Reads == 0 && handler.Calls == 0 && Directory.GetFiles(dir).Length == 0);
            }
            var bad = Payload(dir, "jpeg"); bad["background"] = "transparent";
            Error(await Invoke(bad, new FakeHandler((_, _) => throw new InvalidOperationException())), "invalid_input", "not_sent");
        });
        Test("no_overwrite_or_missing_parent_before_send", async dir =>
        {
            var path = Path.Combine(dir, "cat.png"); await File.WriteAllTextAsync(path, "original");
            var credentials = new FakeCredentials(Secret); var handler = new FakeHandler((_, _) => throw new InvalidOperationException());
            Error(await Invoke(Payload(dir), handler, credentials: credentials), "output_exists", "not_sent");
            Check(File.ReadAllText(path) == "original");
            Error(await Invoke(Payload(Path.Combine(dir, "missing")), handler, credentials: credentials), "output_directory_missing", "not_sent");
            Check(handler.Calls == 0 && credentials.Reads == 0);
        });
        Test("missing_credential_cleanup", async dir =>
        {
            var handler = new FakeHandler((_, _) => throw new InvalidOperationException());
            Error(await Invoke(Payload(dir), handler, credentials: new FakeCredentials(null)), "missing_token", "not_sent");
            Check(handler.Calls == 0 && Directory.GetFiles(dir).Length == 0);
        });
        Test("duplicate_oversized_and_compressed_input_response", async dir =>
        {
            var credentials = new FakeCredentials(Secret);
            var unused = new FakeHandler((_, _) => throw new InvalidOperationException());
            foreach (var bytes in new[] { Encoding.UTF8.GetBytes("{\"prompt\":\"a\",\"prompt\":\"b\",\"output\":\"cat.png\"}"), new byte[Contract.MaxRequestBytes + 1] })
                Error(await new ImageEngine(credentials, () => unused).ExecuteAsync(bytes), "invalid_input", "not_sent");
            Check(credentials.Reads == 0 && unused.Calls == 0);
            var compressed = new FakeHandler((_, _) => { var response = Reply(); response.Content.Headers.ContentEncoding.Add("gzip"); return Task.FromResult(response); });
            Error(await Invoke(Payload(dir), compressed), "invalid_response", "attempted_unknown");
            Check(compressed.Calls == 1 && Directory.GetFiles(dir).Length == 0);
        });
        Test("explicit_models_and_transparency_not_substituted", async dir =>
        {
            var index = 0;
            foreach (var model in new[] { "gpt-image-2", "gpt-image-1.5", "gpt-image-1-mini" })
            {
                var payload = Payload(dir); payload["output"] = Path.Combine(dir, "image" + index++ + ".png");
                payload["model"] = model; payload["background"] = "transparent";
                var handler = new FakeHandler(async (request, ct) =>
                {
                    var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
                    Check(body["model"]!.GetValue<string>() == model && body["background"]!.GetValue<string>() == "transparent");
                    return Reply();
                });
                Check((await Invoke(payload, handler))["ok"]!.GetValue<bool>() && handler.Calls == 1);
            }
        });
        foreach (var (status, code) in new[] { (401, "authentication_failed"), (429, "rate_limited"), (500, "http_error"), (302, "redirect_rejected"), (307, "redirect_rejected") })
        Test("http_" + status + "_no_retry_or_raw_error", async dir =>
        {
            var handler = new FakeHandler((_, _) =>
            {
                var response = Reply(new JsonObject { ["error"] = Secret }, status);
                response.Headers.Location = new Uri("https://evil.invalid/" + Secret);
                response.Headers.Remove("x-request-id"); response.Headers.Add("x-request-id", Secret);
                return Task.FromResult(response);
            });
            var result = await Invoke(Payload(dir), handler);
            Error(result, code, "http_error"); Check(handler.Calls == 1 && result["raw_response_json"] is null && result["provider_request_id"] is null);
            Check(Directory.GetFiles(dir).Length == 0);
        });
        Test("timeout_and_cancel_no_retry", async dir =>
        {
            foreach (var cancel in new[] { false, true })
            {
                using var cts = new CancellationTokenSource();
                var handler = new FakeHandler(async (_, ct) =>
                { if (cancel) cts.Cancel(); await Task.Delay(Timeout.Infinite, ct); return Reply(); });
                Error(await Invoke(Payload(dir), handler, cts.Token, TimeSpan.FromMilliseconds(20)), cancel ? "cancelled" : "timeout", "attempted_unknown");
                Check(handler.Calls == 1 && Directory.GetFiles(dir).Length == 0);
            }
            using var before = new CancellationTokenSource(); before.Cancel();
            var untouched = new FakeHandler((_, _) => throw new InvalidOperationException());
            Error(await Invoke(Payload(dir), untouched, before.Token), "cancelled", "not_sent"); Check(untouched.Calls == 0);
        });
        Test("network_error_single_attempt", async dir =>
        {
            var handler = new FakeHandler((_, _) => throw new HttpRequestException(Secret));
            Error(await Invoke(Payload(dir), handler), "network_error", "attempted_unknown");
            Check(handler.Calls == 1 && Directory.GetFiles(dir).Length == 0);
        });
        Test("response_header_and_stream_limits", async dir =>
        {
            foreach (var header in new[] { true, false })
            {
                var handler = new FakeHandler((_, _) =>
                {
                    var response = Reply(); response.Content = new RawStreamContent(new MemoryStream(new byte[ImageContract.MaxResponseBytes + 1]));
                    if (header) response.Content.Headers.ContentLength = ImageContract.MaxResponseBytes + 1;
                    return Task.FromResult(response);
                });
                Error(await Invoke(Payload(dir), handler), "response_too_large", "attempted_unknown"); Check(handler.Calls == 1);
            }
            Check(Directory.GetFiles(dir).Length == 0);
        });
        Test("invalid_binary_base64_url_format_and_count", async dir =>
        {
            foreach (var change in new Action<JsonObject>[]
            {
                d => d["data"]![0]!["b64_json"] = "invalid!", d => d["data"]![0]!["b64_json"] = Convert.ToBase64String("not an image"u8),
                d => d["data"]![0]!["url"] = "https://evil.invalid/", d => d["output_format"] = "webp",
                d => d["data"] = new JsonArray(), d => d["data"]!.AsArray().Add(new JsonObject()),
                d => d["data"]![0]!["b64_json"] = Convert.ToBase64String(new byte[ImageContract.MaxImageBytes + 1])
            })
            {
                var data = Data(); change(data);
                var handler = new FakeHandler((_, _) => Task.FromResult(Reply(data)));
                Error(await Invoke(Payload(dir), handler), "invalid_response", "attempted_unknown");
                Check(handler.Calls == 1 && Directory.GetFiles(dir).Length == 0);
            }
        });
        Test("secret_reflection_in_metadata_binary_and_input", async dir =>
        {
            var binary = Png.Concat(Encoding.UTF8.GetBytes(Secret)).ToArray();
            Error(await Invoke(Payload(dir), new FakeHandler((_, _) => Task.FromResult(Reply(Data(binary))))), "invalid_response", "attempted_unknown");
            Check(Directory.GetFiles(dir).Length == 0);
            var data = Data(); data["model"] = Secret; data["usage"]![Secret] = Secret;
            var result = await Invoke(Payload(dir), new FakeHandler((_, _) => Task.FromResult(Reply(data))));
            Check(result["ok"]!.GetValue<bool>() && !result.ToJsonString().Contains(Secret));
            var payload = Payload(dir); payload["output"] = Path.Combine(dir, Secret + ".png");
            var handler = new FakeHandler((_, _) => throw new InvalidOperationException());
            Error(await Invoke(payload, handler), "sensitive_input", "not_sent"); Check(handler.Calls == 0);
        });
        Test("raced_output_keeps_original_and_usage_no_resend", async dir =>
        {
            var handler = new FakeHandler(async (_, _) => { await File.WriteAllTextAsync(Path.Combine(dir, "cat.png"), "other-writer"); return Reply(); });
            var result = await Invoke(Payload(dir), handler);
            Error(result, "output_write_failed", "response_received");
            Check(result["usage"] is not null && result["http_status"]!.GetValue<int>() == 200 && handler.Calls == 1);
            Check(File.ReadAllText(Path.Combine(dir, "cat.png")) == "other-writer" && Directory.GetFiles(dir).Length == 1);
            Check(!result.ToJsonString().Contains(Convert.ToBase64String(Png)));
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "image-error-evidence.json"), result.ToJsonString());
        });
    }
}
