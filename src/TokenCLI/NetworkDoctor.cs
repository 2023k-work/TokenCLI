using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;

namespace TokenCLI;

public interface INetworkProbe : IDisposable
{
    Task<int> ResolveAsync(string host, CancellationToken cancellation);
    Task ConnectAsync(CancellationToken cancellation);
    Task TlsAsync(string host, CancellationToken cancellation);
    string? AddressFamily { get; }
    string? TlsProtocol { get; }
    IReadOnlyList<string>? CertificateErrors { get; }
    IReadOnlyList<int>? ChainStatusCodes { get; }
}

public sealed class NetworkProbe : INetworkProbe
{
    private IPAddress? selected;
    private TcpClient? client;
    private SslStream? tls;
    private SslPolicyErrors? certificateErrors;
    private int[]? chainStatusCodes;
    public string? AddressFamily => selected?.AddressFamily switch
    {
        System.Net.Sockets.AddressFamily.InterNetwork => "IPv4",
        System.Net.Sockets.AddressFamily.InterNetworkV6 => "IPv6", _ => null
    };
    public string? TlsProtocol => tls?.IsAuthenticated == true ? tls.SslProtocol.ToString() : null;
    public IReadOnlyList<string>? CertificateErrors => certificateErrors is null ? null : new[]
    {
        (SslPolicyErrors.RemoteCertificateNotAvailable, "certificate_missing"),
        (SslPolicyErrors.RemoteCertificateNameMismatch, "name_mismatch"),
        (SslPolicyErrors.RemoteCertificateChainErrors, "chain_errors")
    }.Where(p => certificateErrors.Value.HasFlag(p.Item1)).Select(p => p.Item2).ToArray();
    public IReadOnlyList<int>? ChainStatusCodes => chainStatusCodes;
    public static bool CertificateAccepted(SslPolicyErrors errors) => errors == SslPolicyErrors.None;
    public async Task<int> ResolveAsync(string host, CancellationToken cancellation)
    {
        var addresses = (await Dns.GetHostAddressesAsync(host, cancellation))
            .Where(a => a.AddressFamily is System.Net.Sockets.AddressFamily.InterNetwork or System.Net.Sockets.AddressFamily.InterNetworkV6).ToArray();
        selected = addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            ?? addresses.FirstOrDefault() ?? throw new SocketException((int)SocketError.HostNotFound);
        return addresses.Length;
    }
    public async Task ConnectAsync(CancellationToken cancellation)
    {
        var address = selected ?? throw new InvalidOperationException();
        client = new TcpClient(address.AddressFamily);
        // Exactly one candidate, one connect. No proxy, alternate destination or fallback loop.
        await client.ConnectAsync(address, 443, cancellation);
    }
    public async Task TlsAsync(string host, CancellationToken cancellation)
    {
        tls = new SslStream((client ?? throw new InvalidOperationException()).GetStream(), false,
            (_, _, chain, errors) =>
            {
                certificateErrors = errors;
                chainStatusCodes = chain?.ChainStatus.Select(s => (int)s.Status).ToArray();
                // Observe only enum flags, never subject/issuer/messages. Reject EVERY policy error.
                return CertificateAccepted(errors);
            });
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = host,
            ApplicationProtocols = [SslApplicationProtocol.Http11]
            // OS TLS defaults. Observation callback accepts only SslPolicyErrors.None.
        }, cancellation);
        // Deliberately never write application data or construct an HTTP request.
    }
    public void Dispose()
    {
        try { tls?.Dispose(); }
        finally { client?.Dispose(); }
    }
}

public static class NetworkDoctor
{
    public static readonly TimeSpan StageTimeout = TimeSpan.FromSeconds(10);
    public static async Task<JsonObject> RunAsync(string provider = "openai", CancellationToken cancellation = default,
        INetworkProbe? injected = null, TimeSpan? stageTimeout = null)
    {
        var host = provider switch { "openai" => "api.openai.com", "anthropic" => "api.anthropic.com", _ => throw new ArgumentException("invalid_provider") };
        var clock = Stopwatch.StartNew();
        var timeout = stageTimeout ?? StageTimeout;
        JsonObject Stage() => new()
        {
            ["status"] = "not_run", ["elapsed_s"] = null, ["error_code"] = null,
            ["socket_error"] = null, ["native_error_code"] = null, ["hresult"] = null
        };
        var stages = new JsonObject { ["dns"] = Stage(), ["tcp"] = Stage(), ["tls"] = Stage() };
        var result = new JsonObject
        {
            ["diagnostic_schema_version"] = 1, ["diagnostic_id"] = Guid.NewGuid().ToString(),
            ["ok"] = false, ["provider"] = provider, ["host"] = host, ["port"] = 443,
            ["connection_mode"] = "direct", ["timeout_per_stage_s"] = timeout.TotalSeconds,
            ["credentials_accessed"] = false, ["http_requests_sent"] = 0, ["provider_api_calls"] = 0,
            ["failed_stage"] = null, ["error_code"] = null, ["elapsed_s"] = null, ["stages"] = stages
        };
        var probe = injected ?? new NetworkProbe();
        void TlsEvidence()
        {
            stages["tls"]!["certificate_errors"] = probe.CertificateErrors is { } errors
                ? new JsonArray(errors.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray()) : null;
            stages["tls"]!["chain_status_codes"] = probe.ChainStatusCodes is { } codes
                ? new JsonArray(codes.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray()) : null;
        }
        async Task<bool> Run(string name, Func<CancellationToken, Task> action)
        {
            var stage = stages[name]!.AsObject();
            var timer = Stopwatch.StartNew();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(timeout);
            try
            {
                deadline.Token.ThrowIfCancellationRequested();
                await action(deadline.Token);
                stage["status"] = "passed";
                return true;
            }
            catch (Exception ex)
            {
                stage["status"] = "failed";
                var socket = FindSocket(ex);
                var code = Classify(name, ex, socket, cancellation.IsCancellationRequested);
                if (name == "tls")
                {
                    TlsEvidence();
                    if (ex is AuthenticationException && probe.CertificateErrors?.Count > 0) code = "tls_certificate_validation_failed";
                }
                stage["error_code"] = code;
                stage["hresult"] = ex.HResult;
                if (name == "tls")
                {
                    var chain = new JsonArray();
                    for (Exception? inner = ex; inner is not null; inner = inner.InnerException)
                    {
                        chain.Add(JsonValue.Create(inner.HResult));
                        if (inner is System.ComponentModel.Win32Exception security && inner is not SocketException)
                        {
                            stage["native_security_error_code"] = security.NativeErrorCode;
                            if (ex is not OperationCanceledException && security.NativeErrorCode == unchecked((int)0x8009030E))
                            {
                                code = "tls_security_context_unavailable";
                                stage["error_code"] = code;
                            }
                        }
                    }
                    stage["exception_hresult_chain"] = chain;
                }
                if (socket is not null)
                {
                    stage["socket_error"] = Enum.IsDefined(socket.SocketErrorCode) ? socket.SocketErrorCode.ToString() : "Unknown";
                    stage["native_error_code"] = socket.NativeErrorCode;
                }
                result["failed_stage"] = name; result["error_code"] = code;
                return false;
            }
            finally { stage["elapsed_s"] = timer.Elapsed.TotalSeconds; }
        }
        try
        {
            if (!await Run("dns", async ct => stages["dns"]!["address_count"] = await probe.ResolveAsync(host, ct))) return result;
            stages["tcp"]!["address_family"] = probe.AddressFamily;
            if (!await Run("tcp", async ct =>
            {
                stages["tcp"]!["connection_attempts"] = 1;
                await probe.ConnectAsync(ct);
            })) return result;
            if (!await Run("tls", ct => probe.TlsAsync(host, ct))) return result;
            TlsEvidence();
            stages["tls"]!["protocol"] = probe.TlsProtocol;
            result["ok"] = true;
            return result;
        }
        finally
        {
            result["elapsed_s"] = clock.Elapsed.TotalSeconds;
            try { probe.Dispose(); }
            catch (Exception) { result["cleanup_failed"] = true; }
        }
    }
    private static SocketException? FindSocket(Exception ex)
    {
        for (Exception? item = ex; item is not null; item = item.InnerException)
            if (item is SocketException socket) return socket;
        return null;
    }
    private static string Classify(string stage, Exception ex, SocketException? socket, bool userCancelled)
    {
        if (ex is OperationCanceledException) return userCancelled ? "cancelled" : stage + "_timeout";
        if (socket is not null) return stage + "_" + (socket.SocketErrorCode switch
        {
            SocketError.AccessDenied => "access_denied",
            SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => "name_resolution_failed",
            SocketError.ConnectionRefused => "connection_refused",
            SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.NetworkDown => "unreachable",
            SocketError.TimedOut => "timeout",
            SocketError.ConnectionReset or SocketError.ConnectionAborted => "connection_reset",
            _ => "socket_error"
        });
        if (ex is AuthenticationException) return "tls_handshake_failed";
        if (ex is UnauthorizedAccessException or System.Security.SecurityException) return stage + "_access_denied";
        if (ex is IOException) return stage + "_io_error";
        return stage + "_unexpected_error";
    }
}
