using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TokenCLI;
using System.Net.Sockets;
using System.Security.Authentication;

if (args is ["--native-check", var testPrefix] && testPrefix.StartsWith("TokenCLI.Tests/", StringComparison.Ordinal))
{
    var isolated = new WindowsCredentials(testPrefix);
    var persisted = isolated.Read("openai") == "fake-rotated-key";
    Console.WriteLine(JsonSerializer.Serialize(new { persisted }));
    return persisted ? 0 : 1;
}
var publishedCli = args is ["--cli", var executable] ? Path.GetFullPath(executable) : null;

var tests = new List<(string Name, Func<Task> Run)>();
void Test(string name, Func<Task> action) => tests.Add((name, action));
void Check(bool condition) { if (!condition) throw new InvalidOperationException("assertion_failed"); }
const string Secret = "fake-secret-never-a-real-api-key";
JsonObject Payload() => JsonNode.Parse("""
{"model":"gpt-6.1-sol","reasoning":{"effort":"medium"},"service_tier":"default","max_output_tokens":3072,"tools":[],"tool_choice":"none","store":false,"stream":false,"truncation":"disabled","prompt_cache_options":{"mode":"explicit","ttl":"30m"},"input":[{"role":"developer","content":"instructions"},{"type":"message","role":"user","content":[{"type":"input_text","text":"完整輸入"}]}]}
""")!.AsObject();
byte[] Envelope(string operation = "generate", JsonObject? payload = null) => Encoding.UTF8.GetBytes(new JsonObject
    { ["operation"] = operation, ["payload"] = payload ?? Payload() }.ToJsonString());
HttpResponseMessage Reply(JsonObject? data = null, int status = 200) => new((HttpStatusCode)status)
    { Content = new StringContent((data ?? new JsonObject()).ToJsonString(), Encoding.UTF8, "application/json") };
async Task<JsonObject> Invoke(FakeHandler handler, string operation = "generate", JsonObject? payload = null,
    CancellationToken ct = default, TimeSpan? timeout = null) =>
    await new ApiEngine(new FakeCredentials(Secret), () => handler, timeout).ExecuteAsync(Envelope(operation, payload), true, ct);
void Error(JsonObject result, string code, string outcome)
{
    Check(result["ok"]!.GetValue<bool>() == false && result["error_code"]!.GetValue<string>() == code);
    Check(result["outcome"]!.GetValue<string>() == outcome && !result.ToJsonString().Contains(Secret));
    Check(!result.ContainsKey("calculated_usd") && !result.ContainsKey("billed_usd"));
}

foreach (var operation in new[] { "count", "generate" })
Test($"exact_{operation}_contract", async () =>
{
    var handler = new FakeHandler(async (request, ct) =>
    {
        Check(request.Method == HttpMethod.Post && request.Version == HttpVersion.Version11);
        Check(request.VersionPolicy == HttpVersionPolicy.RequestVersionExact && request.Headers.ConnectionClose == true);
        Check(request.RequestUri!.ToString() == "https://api.openai.com/v1/responses" + (operation == "count" ? "/input_tokens" : ""));
        Check(request.Headers.Authorization!.Parameter == Secret);
        var actual = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
        Check(!actual.ToJsonString().Contains(Secret));
        var expected = Payload();
        if (operation == "count") expected = new JsonObject(Contract.CountFields.Select(k => new KeyValuePair<string, JsonNode?>(k, expected[k]!.DeepClone())));
        Check(JsonNode.DeepEquals(actual, expected));
        return Reply(new JsonObject { ["input_tokens"] = 25 });
    });
    var result = await Invoke(handler, operation);
    Check(result["ok"]!.GetValue<bool>() && handler.Calls == 1 && result["input_tokens"]!.GetValue<int>() == 25);
});

Test("complete_audit_and_actual_identity", async () =>
{
    var data = JsonNode.Parse("""
{"id":"resp_actual","model":"returned-not-requested-model","service_tier":"actual-tier","reasoning":{"effort":"high","extra":true},"status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"usage":{"input_tokens":123,"output_tokens":100,"input_tokens_details":{"cached_tokens":5,"cache_write_tokens":8,"nested":{"ttl":"30m"}},"output_tokens_details":{"reasoning_tokens":30}},"output":[{"type":"message","content":[{"type":"output_text","text":"one"},{"type":"output_text","text":"two"}]},{"type":"message","content":[{"type":"output_text","text":"完整"}]}]}
""")!.AsObject();
    var handler = new FakeHandler((_, _) => { var response = Reply(data); response.Headers.Add("x-request-id", "req_actual"); return Task.FromResult(response); });
    var result = await Invoke(handler);
    Check(result["model"]!.GetValue<string>() == "returned-not-requested-model");
    Check(result["provider_request_id"]!.GetValue<string>() == "req_actual" && result["response_id"]!.GetValue<string>() == "resp_actual");
    Check(JsonNode.DeepEquals(result["usage"], data["usage"]) && JsonNode.DeepEquals(result["reasoning"], data["reasoning"]));
    Check(result["output_text"]!.GetValue<string>() == "onetwo完整");
    Check(JsonNode.DeepEquals(JsonNode.Parse(result["raw_response_json"]!.GetValue<string>()), data));
});
Test("missing_usage_is_not_zero", async () =>
{
    var result = await Invoke(new FakeHandler((_, _) => Task.FromResult(Reply())));
    Check(result["usage"] is null && result["model"] is null && result["input_tokens"] is null);
    Check(result["present_fields"]!.AsArray().Count == 0 && result["output_text"] is null);
});

var invalids = new Dictionary<string, Action<JsonObject>>
{
    ["url"] = p => p["url"] = "https://evil.invalid",
    ["method"] = p => p["method"] = "GET",
    ["provider"] = p => p["provider"] = "anthropic",
    ["model"] = p => p["model"] = "other",
    ["reasoning"] = p => p["reasoning"]!["effort"] = "high",
    ["extra_reasoning"] = p => p["reasoning"]!["unknown"] = true,
    ["tier"] = p => p["service_tier"] = "priority",
    ["limit"] = p => p["max_output_tokens"] = 8192,
    ["float_limit"] = p => p["max_output_tokens"] = JsonNode.Parse("3072.0"),
    ["tools"] = p => p["tools"] = JsonNode.Parse("[{\"type\":\"web_search\"}]"),
    ["tool_choice"] = p => p["tool_choice"] = "auto",
    ["store_number"] = p => p["store"] = 0,
    ["stream"] = p => p["stream"] = true,
    ["truncation"] = p => p["truncation"] = "auto",
    ["previous_response_id"] = p => p["previous_response_id"] = null,
    ["conversation"] = p => p["conversation"] = null,
    ["instructions"] = p => p["instructions"] = "extra",
    ["missing_cache_mode"] = p => p["prompt_cache_options"]!.AsObject().Remove("mode"),
    ["flat_input"] = p => p["input"] = "flattened",
    ["empty_input"] = p => p["input"] = new JsonArray(),
    ["role"] = p => p["input"]![0]!["role"] = "tool",
    ["file"] = p => p["input"]![1]!["content"] = JsonNode.Parse("[{\"type\":\"input_file\",\"file_url\":\"https://evil.invalid\"}]"),
    ["breakpoint"] = p => p["input"]![1]!["content"]![0]!["prompt_cache_breakpoint"] = new JsonObject { ["mode"] = "explicit" },
    ["extra_message"] = p => p["input"]![0]!["unknown"] = true
};
foreach (var entry in invalids)
Test("reject_" + entry.Key, async () =>
{
    var payload = Payload(); entry.Value(payload);
    var vault = new FakeCredentials(Secret);
    var handler = new FakeHandler((_, _) => throw new InvalidOperationException());
    Error(await new ApiEngine(vault, () => handler).ExecuteAsync(Envelope(payload: payload), true), "invalid_input", "not_sent");
    Check(vault.Reads == 0 && handler.Calls == 0);
});
foreach (var operation in new[] { "shell", "python", "GET" })
Test("reject_operation_" + operation, async () =>
    Error(await Invoke(new FakeHandler((_, _) => throw new InvalidOperationException()), operation), "invalid_input", "not_sent"));
Test("reject_unknown_root_duplicate_and_invalid_json", async () =>
{
    foreach (var raw in new[] { "bad", "{}{}", "{\"operation\":\"count\",\"operation\":\"generate\"}", "{\"operation\":\"count\",\"payload\":{},\"url\":\"https://evil.invalid\"}" })
    {
        var vault = new FakeCredentials(Secret);
        Error(await new ApiEngine(vault).ExecuteAsync(Encoding.UTF8.GetBytes(raw), true), "invalid_input", "not_sent");
        Check(vault.Reads == 0);
    }
});
Test("missing_and_failed_credentials", async () =>
{
    foreach (var (vault, code) in new[] { (new FakeCredentials(null), "missing_token"), (new FakeCredentials(Secret) { Fail = true }, "credential_error") })
        Error(await new ApiEngine(vault).ExecuteAsync(Envelope(), true), code, "not_sent");
});
foreach (var operation in new[] { "count", "generate" })
foreach (var (status, code) in new[] { (401, "authentication_failed"), (403, "authentication_failed"), (429, "rate_limited"), (500, "http_error"), (302, "redirect_rejected"), (307, "redirect_rejected") })
Test($"{operation}_{status}_no_retry", async () =>
{
    var handler = new FakeHandler((_, _) =>
    {
        var reply = Reply(new JsonObject { ["error"] = Secret }, status);
        reply.Headers.Add("x-request-id", Secret); reply.Headers.Location = new Uri("https://evil.invalid/" + Secret);
        return Task.FromResult(reply);
    });
    var result = await Invoke(handler, operation);
    Error(result, code, "http_error");
    Check(handler.Calls == 1 && result["http_status"]!.GetValue<int>() == status && result["raw_response_json"] is null);
});
Test("network_failure_no_retry", async () =>
{
    var handler = new FakeHandler((_, _) => throw new HttpRequestException(Secret));
    Error(await Invoke(handler), "network_error", "attempted_unknown"); Check(handler.Calls == 1);
});
Test("timeout_no_retry", async () =>
{
    var handler = new FakeHandler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Reply(); });
    Error(await Invoke(handler, timeout: TimeSpan.FromMilliseconds(20)), "timeout", "attempted_unknown"); Check(handler.Calls == 1);
});
Test("cancel_before_send", async () =>
{
    using var cts = new CancellationTokenSource(); cts.Cancel();
    var handler = new FakeHandler((_, _) => Task.FromResult(Reply()));
    Error(await Invoke(handler, ct: cts.Token), "cancelled", "not_sent"); Check(handler.Calls == 0);
});
Test("cancel_inflight_no_retry", async () =>
{
    using var cts = new CancellationTokenSource();
    var handler = new FakeHandler(async (_, ct) => { cts.Cancel(); await Task.Delay(Timeout.Infinite, ct); return Reply(); });
    Error(await Invoke(handler, ct: cts.Token), "cancelled", "attempted_unknown"); Check(handler.Calls == 1);
});
Test("response_header_and_stream_size_limits", async () =>
{
    foreach (var header in new[] { true, false })
    {
        var handler = new FakeHandler((_, _) =>
        {
            var reply = Reply();
            reply.Content = new RawStreamContent(new MemoryStream(new byte[ApiEngine.MaxResponseBytes + 1]));
            if (header) reply.Content.Headers.ContentLength = ApiEngine.MaxResponseBytes + 1;
            return Task.FromResult(reply);
        });
        Error(await Invoke(handler), "response_too_large", "attempted_unknown"); Check(handler.Calls == 1);
    }
});
Test("raw_invalid_duplicate_nonfinite_and_error_json", async () =>
{
    foreach (var raw in new[] { "bad", "[]", "{\"usage\":1,\"usage\":2}", "{\"usage\":1e999}", "{\"error\":\"" + Secret + "\"}" })
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(raw) }));
        var result = await Invoke(handler);
        Error(result, "invalid_response", "attempted_unknown"); Check(result["raw_response_json"] is null);
    }
});
Test("compressed_response_rejected", async () =>
{
    var handler = new FakeHandler((_, _) => { var reply = Reply(); reply.Content.Headers.ContentEncoding.Add("gzip"); return Task.FromResult(reply); });
    Error(await Invoke(handler), "invalid_response", "attempted_unknown");
});
Test("secret_reflection_all_fields_and_escaped_json", async () =>
{
    var data = new JsonObject { ["id"] = Secret, ["model"] = Secret,
        ["usage"] = new JsonObject { [Secret] = Secret }, ["authorization"] = "Bearer " + Secret,
        ["output"] = new JsonArray(new JsonObject { ["type"] = "message", ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = Secret }) }) };
    var handler = new FakeHandler((_, _) => Task.FromResult(Reply(data)));
    var result = await Invoke(handler);
    Check(result["ok"]!.GetValue<bool>() && !result.ToJsonString().Contains(Secret));
    Check(result["output_text"]!.GetValue<string>() == "[REDACTED]");
    var escaped = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"model\":\"" + Secret.Replace("f", "\\u0066") + "\"}") }));
    Check((await Invoke(escaped))["model"]!.GetValue<string>() == "[REDACTED]");
});
Test("secret_in_input_rejected", async () =>
{
    var payload = Payload(); payload["input"]![0]!["content"] = Secret;
    var handler = new FakeHandler((_, _) => throw new InvalidOperationException());
    Error(await Invoke(handler, payload: payload), "sensitive_input", "not_sent"); Check(handler.Calls == 0);
});
Test("input_limit", async () =>
{
    var vault = new FakeCredentials(Secret);
    Error(await new ApiEngine(vault).ExecuteAsync(new byte[Contract.MaxRequestBytes + 1], true), "invalid_input", "not_sent");
    Check(vault.Reads == 0);
    try { await TokenCLI.Program.ReadInput(new MemoryStream(new byte[Contract.MaxRequestBytes + 1]), default); throw new InvalidOperationException(); }
    catch (ContractException) { }
});
foreach (var provider in new[] { "openai", "anthropic" })
Test("general_" + provider, async () =>
{
    var handler = new FakeHandler(async (request, ct) =>
    {
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
        Check(body["model"]!.GetValue<string>() == "test-model");
        if (provider == "openai")
        {
            Check(request.RequestUri!.ToString() == "https://api.openai.com/v1/responses");
            Check(body["input"]!.GetValue<string>() == "hello" && body["store"]!.GetValue<bool>() == false);
            return Reply(new JsonObject { ["model"] = "actual-model" });
        }
        Check(request.RequestUri!.ToString() == "https://api.anthropic.com/v1/messages");
        Check(request.Headers.GetValues("x-api-key").Single() == Secret && body["messages"]![0]!["content"]!.GetValue<string>() == "hello");
        return Reply(new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "answer" }) });
    });
    var raw = Encoding.UTF8.GetBytes(new JsonObject { ["provider"] = provider, ["model"] = "test-model", ["prompt"] = "hello" }.ToJsonString());
    var result = await new ApiEngine(new FakeCredentials(Secret), () => handler).ExecuteAsync(raw, false);
    Check(result["ok"]!.GetValue<bool>() && handler.Calls == 1);
    Check(provider == "openai" ? result["model"]!.GetValue<string>() == "actual-model" : result["output_text"]!.GetValue<string>() == "answer");
});
Test("native_credential_roundtrip_isolated_fake_namespace", () =>
{
    var vault = new WindowsCredentials("TokenCLI.Tests/" + Guid.NewGuid());
    try
    {
        Check(!vault.Exists("openai")); vault.Save("openai", Secret);
        Check(vault.Exists("openai") && vault.Read("openai") == Secret);
        vault.Save("openai", "fake-rotated-key"); Check(vault.Read("openai") == "fake-rotated-key");
    }
    finally { vault.Delete("openai"); }
    Check(!vault.Exists("openai") && vault.Read("openai") is null); vault.Delete("openai");
    return Task.CompletedTask;
});
Test("native_credential_invalid_token_rejected", () =>
{
    var vault = new WindowsCredentials("TokenCLI.Tests/" + Guid.NewGuid());
    foreach (var token in new[] { "", " has-space", "bad\nkey", new string('x', 1281) })
    {
        try { vault.Save("openai", token); throw new InvalidOperationException(); }
        catch (ArgumentException) { }
    }
    Check(!vault.Exists("openai")); return Task.CompletedTask;
});
Test("native_credential_persists_across_processes", async () =>
{
    var prefix = "TokenCLI.Tests/" + Guid.NewGuid();
    var vault = new WindowsCredentials(prefix);
    try
    {
        vault.Save("openai", "fake-rotated-key");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(typeof(FakeCredentials).Assembly.Location);
        start.ArgumentList.Add("--native-check"); start.ArgumentList.Add(prefix);
        using var child = Process.Start(start)!;
        var text = await child.StandardOutput.ReadToEndAsync();
        await child.WaitForExitAsync();
        Check(child.ExitCode == 0 && JsonNode.Parse(text)!["persisted"]!.GetValue<bool>());
    }
    finally { vault.Delete("openai"); }
});
Test("real_cli_process_json_and_no_secret_output", async () =>
{
    var cli = Path.GetFullPath("../../../../../src/TokenCLI/bin/Release/net8.0/TokenCLI.dll", AppContext.BaseDirectory);
    async Task<(int Code, string Out, string Err)> Run(string[] arguments, string input = "")
    {
        var start = new ProcessStartInfo(publishedCli ?? "dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        if (publishedCli is null) start.ArgumentList.Add(cli);
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(input); process.StandardInput.Close();
        await process.WaitForExitAsync(); return (process.ExitCode, await stdout, await stderr);
    }
    var version = await Run(["version"]); Check(version.Code == 0 && JsonNode.Parse(version.Out)!["name"]!.GetValue<string>() == "TokenCLI");
    var schema = await Run(["schema", "input"]); Check(schema.Code == 0 && JsonNode.Parse(schema.Out)!["additionalProperties"]!.GetValue<bool>() == false);
    var networkSchema = await Run(["schema", "network"]);
    Check(networkSchema.Code == 0 && JsonNode.Parse(networkSchema.Out)!["properties"]!["http_requests_sent"]!["const"]!.GetValue<int>() == 0);
    var help = await Run(["help"]);
    Check(help.Code == 0 && help.Out.Contains("responses") && help.Out.Contains("doctor network") && !help.Out.Contains("aoelr", StringComparison.OrdinalIgnoreCase));
    var invalidDoctor = await Run(["doctor", "network", "https://evil.invalid"]);
    Check(invalidDoctor.Code == 2 && JsonNode.Parse(invalidDoctor.Out)!["error_code"]!.GetValue<string>() == "invalid_arguments");
    foreach (var command in new[] { "responses", "aoelr" })
    {
        var bad = await Run([command], "{\"operation\":\"generate\",\"payload\":{},\"secret\":\"" + Secret + "\"}");
        Check(bad.Code == 2 && !bad.Out.Contains(Secret) && !bad.Err.Contains(Secret));
        Error(JsonNode.Parse(bad.Out)!.AsObject(), "invalid_input", "not_sent");
    }
    var set = await Run(["credentials", "set", "openai"], Secret); Check(set.Code == 2 && !set.Out.Contains(Secret) && !set.Err.Contains(Secret));
});

foreach (var provider in new[] { "openai", "anthropic" })
Test("doctor_fixed_host_and_stages_" + provider, async () =>
{
    var probe = new FakeNetworkProbe();
    var result = await NetworkDoctor.RunAsync(provider, injected: probe);
    Check(result["ok"]!.GetValue<bool>() && probe.Calls.SequenceEqual(new[] { "dns", "tcp", "tls" }));
    var expected = provider == "openai" ? "api.openai.com" : "api.anthropic.com";
    Check(probe.Hosts.All(h => h == expected) && result["host"]!.GetValue<string>() == expected);
    Check(result["credentials_accessed"]!.GetValue<bool>() == false && result["http_requests_sent"]!.GetValue<int>() == 0);
    Check(result["provider_api_calls"]!.GetValue<int>() == 0 && probe.Disposed);
    Check(result["stages"]!["tls"]!["protocol"]!.GetValue<string>() == "Tls13");
});
foreach (var (stage, exception, code) in new (string, Exception, string)[]
{
    ("dns", new SocketException((int)SocketError.HostNotFound), "dns_name_resolution_failed"),
    ("tcp", new SocketException((int)SocketError.AccessDenied), "tcp_access_denied"),
    ("tcp", new SocketException((int)SocketError.ConnectionRefused), "tcp_connection_refused"),
    ("tcp", new SocketException((int)SocketError.NetworkUnreachable), "tcp_unreachable"),
    ("tcp", new UnauthorizedAccessException(Secret), "tcp_access_denied"),
    ("tls", new AuthenticationException(Secret), "tls_handshake_failed"),
    ("tls", new IOException(Secret, new SocketException((int)SocketError.ConnectionReset)), "tls_connection_reset"),
    ("tls", new IOException(Secret), "tls_io_error"),
    ("dns", new InvalidOperationException(Secret), "dns_unexpected_error")
})
Test("doctor_classification_" + code + "_" + exception.GetType().Name, async () =>
{
    var probe = new FakeNetworkProbe { FailureStage = stage, Failure = exception };
    var result = await NetworkDoctor.RunAsync(injected: probe);
    Check(!result["ok"]!.GetValue<bool>() && result["error_code"]!.GetValue<string>() == code);
    Check(result["failed_stage"]!.GetValue<string>() == stage && probe.Disposed && !result.ToJsonString().Contains(Secret));
    Check(probe.Calls.Count == (stage == "dns" ? 1 : stage == "tcp" ? 2 : 3));
    if (stage != "tls") Check(result["stages"]!["tls"]!["status"]!.GetValue<string>() == "not_run");
    if (exception is SocketException && code == "tcp_access_denied") Check(result["stages"]!["tcp"]!["socket_error"]!.GetValue<string>() == "AccessDenied");
});
Test("doctor_certificate_errors_observed_without_bypass", async () =>
{
    Check(NetworkProbe.CertificateAccepted(System.Net.Security.SslPolicyErrors.None));
    foreach (var errors in new[] { System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors,
        System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch, System.Net.Security.SslPolicyErrors.RemoteCertificateNotAvailable,
        System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors | System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch })
        Check(!NetworkProbe.CertificateAccepted(errors));
    var probe = new FakeNetworkProbe { FailureStage = "tls", Failure = new AuthenticationException(Secret),
        CertificateErrors = ["chain_errors"], ChainStatusCodes = [32] };
    var result = await NetworkDoctor.RunAsync(injected: probe);
    Check(result["error_code"]!.GetValue<string>() == "tls_certificate_validation_failed");
    Check(result["stages"]!["tls"]!["chain_status_codes"]![0]!.GetValue<int>() == 32 && !result.ToJsonString().Contains(Secret));
});
Test("doctor_tls_security_numeric_code_without_exception_text", async () =>
{
    var code = unchecked((int)0x8009030E);
    var probe = new FakeNetworkProbe { FailureStage = "tls", Failure = new AuthenticationException(Secret,
        new System.ComponentModel.Win32Exception(code, Secret)) };
    var result = await NetworkDoctor.RunAsync(injected: probe);
    Check(result["stages"]!["tls"]!["native_security_error_code"]!.GetValue<int>() == code);
    Check(result["error_code"]!.GetValue<string>() == "tls_security_context_unavailable");
    Check(result["stages"]!["tls"]!["exception_hresult_chain"]!.AsArray().Count == 2);
    Check(!result.ToJsonString().Contains(Secret) && probe.Disposed);
    Check(!result.ToJsonString(new JsonSerializerOptions { WriteIndented = false }).Contains(Secret));
});
foreach (var stage in new[] { "dns", "tcp", "tls" })
Test("doctor_timeout_" + stage, async () =>
{
    var probe = new FakeNetworkProbe { DelayStage = stage };
    var result = await NetworkDoctor.RunAsync(injected: probe, stageTimeout: TimeSpan.FromMilliseconds(20));
    Check(result["error_code"]!.GetValue<string>() == stage + "_timeout" && probe.Disposed);
    Check(probe.Calls.Count(c => c == stage) == 1);
});
Test("doctor_cancelled_before_dns_no_connection", async () =>
{
    var probe = new FakeNetworkProbe();
    using var cts = new CancellationTokenSource(); cts.Cancel();
    var result = await NetworkDoctor.RunAsync(cancellation: cts.Token, injected: probe);
    Check(result["error_code"]!.GetValue<string>() == "cancelled" && probe.Calls.Count == 0 && probe.Disposed);
});
Test("doctor_cancel_inflight_stops_after_current_stage", async () =>
{
    using var cts = new CancellationTokenSource(); cts.CancelAfter(30);
    var probe = new FakeNetworkProbe { DelayStage = "tcp" };
    var result = await NetworkDoctor.RunAsync(cancellation: cts.Token, injected: probe);
    Check(result["error_code"]!.GetValue<string>() == "cancelled" && probe.Disposed);
    Check(probe.Calls.SequenceEqual(new[] { "dns", "tcp" }));
    Check(result["stages"]!["tls"]!["status"]!.GetValue<string>() == "not_run");
});
Test("doctor_unknown_host_rejected_before_network", async () =>
{
    var probe = new FakeNetworkProbe();
    try { await NetworkDoctor.RunAsync("https://evil.invalid", injected: probe); throw new InvalidOperationException(); }
    catch (ArgumentException) { }
    Check(probe.Calls.Count == 0);
});

var failures = 0;
var clock = Stopwatch.StartNew();
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL " + test.Name + " " + ex.GetType().Name); }
}
Console.WriteLine(JsonSerializer.Serialize(new { tests = tests.Count, passed = tests.Count - failures, failed = failures, elapsed_s = clock.Elapsed.TotalSeconds, real_api_calls = 0 }));
return failures == 0 ? 0 : 1;

sealed class FakeCredentials(string? token) : TokenCLI.ICredentials
{
    public int Reads { get; private set; }
    public bool Fail { get; init; }
    public string? Read(string provider) { Reads++; if (Fail) throw new CredentialException(); return token; }
    public bool Exists(string provider) => token is not null;
    public void Save(string provider, string value) => throw new NotSupportedException();
    public void Delete(string provider) => throw new NotSupportedException();
}
sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
{
    public int Calls { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    { Calls++; return handle(request, cancellationToken); }
}
sealed class RawStreamContent(Stream stream) : HttpContent
{
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
    protected override Task SerializeToStreamAsync(Stream target, TransportContext? context) => stream.CopyToAsync(target);
    protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(stream);
    protected override void Dispose(bool disposing) { if (disposing) stream.Dispose(); base.Dispose(disposing); }
}
sealed class FakeNetworkProbe : INetworkProbe
{
    public List<string> Calls { get; } = [];
    public List<string> Hosts { get; } = [];
    public string? FailureStage { get; init; }
    public Exception? Failure { get; init; }
    public string? DelayStage { get; init; }
    public bool Disposed { get; private set; }
    public string? AddressFamily => "IPv4";
    public string? TlsProtocol => "Tls13";
    public IReadOnlyList<string>? CertificateErrors { get; init; }
    public IReadOnlyList<int>? ChainStatusCodes { get; init; }
    private async Task Invoke(string stage, CancellationToken cancellation)
    {
        Calls.Add(stage);
        if (FailureStage == stage) throw Failure!;
        if (DelayStage == stage) await Task.Delay(Timeout.Infinite, cancellation);
    }
    public async Task<int> ResolveAsync(string host, CancellationToken cancellation)
    { Hosts.Add(host); await Invoke("dns", cancellation); return 2; }
    public Task ConnectAsync(CancellationToken cancellation) => Invoke("tcp", cancellation);
    public Task TlsAsync(string host, CancellationToken cancellation)
    { Hosts.Add(host); return Invoke("tls", cancellation); }
    public void Dispose() => Disposed = true;
}
