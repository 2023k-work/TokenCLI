using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TokenCLI;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        try { return await RunAsync(args, new WindowsCredentials(), cancellation.Token); }
        catch (Exception ex) when (ex is CredentialException or ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        { Write(new JsonObject { ["ok"] = false, ["error_code"] = ex is CredentialException ? "credential_error" : "local_error" }); return 2; }
        catch (OperationCanceledException)
        { Write(new JsonObject { ["ok"] = false, ["error_code"] = "cancelled" }); return 130; }
        catch (Exception)
        { Write(new JsonObject { ["ok"] = false, ["error_code"] = "internal_error" }); return 2; }
    }
    public static async Task<int> RunAsync(string[] args, ICredentials credentials, CancellationToken cancellation)
    {
        if (args.Length == 0 || args is ["help"] or ["--help"] or ["-h"])
        {
            Console.WriteLine("TokenCLI 0.1.1\n  credentials set openai|anthropic  (hidden terminal input)\n  credentials status\n  credentials delete openai|anthropic\n  responses       (one JSON {operation,payload} from stdin)\n  generate-text   (one JSON {provider,model,prompt,max_output_tokens?} from stdin)\n  schema input|output|generate-text\n  version\nNo MCP registration. No token argument, read-token command or automatic retries.");
            return 0;
        }
        if (args is ["version"])
        { Write(new JsonObject { ["name"] = "TokenCLI", ["version"] = "0.1.1" }); return 0; }
        if (args is ["schema", var schema] && schema is "input" or "output" or "generate-text")
        {
            var name = schema switch { "input" => "responses.input.json", "output" => "responses.output.json", _ => "generate-text.input.json" };
            var assembly = Assembly.GetExecutingAssembly();
            var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith("." + name, StringComparison.Ordinal));
            using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
            Console.WriteLine(await reader.ReadToEndAsync(cancellation));
            return 0;
        }
        if (args is ["credentials", "status"])
        {
            Write(new JsonObject { ["ok"] = true, ["providers"] = new JsonObject
            { ["openai"] = credentials.Exists("openai"), ["anthropic"] = credentials.Exists("anthropic") } }); return 0;
        }
        if (args is ["credentials", var action, var provider] && action is "set" or "delete")
        {
            WindowsCredentials.ValidateProvider(provider);
            if (action == "set")
            {
                if (Console.IsInputRedirected) throw new InvalidOperationException("interactive_terminal_required");
                Console.Error.Write($"{provider} API token (hidden): ");
                var token = ReadHidden(cancellation);
                credentials.Save(provider, token);
            }
            else credentials.Delete(provider);
            Write(new JsonObject { ["ok"] = true, ["provider"] = provider, ["action"] = action }); return 0;
        }
        if (args is ["responses"] or ["aoelr"] or ["generate-text"])
        {
            JsonObject evidence;
            try
            {
                using var input = Console.OpenStandardInput();
                var bytes = await ReadInput(input, cancellation);
                evidence = await new ApiEngine(credentials).ExecuteAsync(bytes, args[0] != "generate-text", cancellation);
            }
            catch (ContractException) { evidence = ApiEngine.EmptyEvidence(); evidence["error_code"] = "invalid_input"; }
            catch (OperationCanceledException) { evidence = ApiEngine.EmptyEvidence(); evidence["error_code"] = "cancelled"; }
            Write(evidence);
            if (evidence["ok"]?.GetValue<bool>() == true) return 0;
            if (evidence["error_code"]?.GetValue<string>() == "cancelled") return 130;
            return evidence["outcome"]?.GetValue<string>() == "attempted_unknown" ? 3 : 2;
        }
        Write(new JsonObject { ["ok"] = false, ["error_code"] = "invalid_arguments" }); return 2;
    }
    public static async Task<byte[]> ReadInput(Stream input, CancellationToken cancellation)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int length;
        while ((length = await input.ReadAsync(buffer, cancellation)) > 0)
        {
            if (output.Length + length > Contract.MaxRequestBytes) throw new ContractException();
            output.Write(buffer, 0, length);
        }
        return output.ToArray();
    }
    private static string ReadHidden(CancellationToken cancellation)
    {
        var buffer = new List<char>();
        try
        {
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter) { Console.Error.WriteLine(); return new string(buffer.ToArray()); }
                if (key.Key == ConsoleKey.Escape) throw new OperationCanceledException();
                if (key.Key == ConsoleKey.Backspace) { if (buffer.Count > 0) buffer.RemoveAt(buffer.Count - 1); }
                else if (!char.IsControl(key.KeyChar))
                {
                    if (buffer.Count == 1280) throw new ArgumentException("token_too_long");
                    buffer.Add(key.KeyChar);
                }
            }
        }
        finally { for (var i = 0; i < buffer.Count; i++) buffer[i] = '\0'; }
    }
    private static void Write(JsonObject value) => Console.WriteLine(value.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
}
