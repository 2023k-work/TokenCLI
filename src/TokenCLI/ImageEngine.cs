using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TokenCLI;

public sealed class ImageEngine(ICredentials credentials, Func<HttpMessageHandler>? handlerFactory = null, TimeSpan? deadlineOverride = null)
{
    public static JsonObject EmptyEvidence()
    {
        var result = ApiEngine.EmptyEvidence();
        result["image_schema_version"] = 1;
        result["operation"] = "generate_image";
        result["artifact"] = null;
        result["image_data_omitted"] = true;
        return result;
    }
    public async Task<JsonObject> ExecuteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellation = default)
    {
        var clock = Stopwatch.StartNew();
        var result = EmptyEvidence();
        JsonObject Failure(string code)
        {
            result["ok"] = false; result["error_code"] = code;
            result["elapsed_s"] = clock.Elapsed.TotalSeconds;
            return result;
        }
        ImageRequest request;
        try { request = ImageContract.Parse(Contract.Parse(input)); }
        catch (Exception ex) when (ex is ContractException or JsonException or ArgumentException or InvalidOperationException or NotSupportedException or PathTooLongException)
        { return Failure("invalid_input"); }
        if (cancellation.IsCancellationRequested) return Failure("cancelled");
        string? temporary = null;
        var ownsTemporary = false;
        try
        {
            if (File.Exists(request.Output) || Directory.Exists(request.Output)) return Failure("output_exists");
            var directory = Path.GetDirectoryName(request.Output)!;
            if (!Directory.Exists(directory)) return Failure("output_directory_missing");
            // Verify write access BEFORE any paid request. Never reserve/overwrite the final file.
            temporary = Path.Combine(directory, ".tokencli-" + Guid.NewGuid().ToString("N") + ".tmp");
            using var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            ownsTemporary = true;
            result = await new ApiEngine(credentials, handlerFactory, deadlineOverride).ExecuteImagesAsync(input, cancellation);
            result["image_schema_version"] = 1; result["artifact"] = null; result["image_data_omitted"] = true;
            if (result["ok"]?.GetValue<bool>() != true) return result;
            // Raw base64 exists only in memory until validated, then is omitted from CLI JSON.
            var raw = JsonNode.Parse(result["raw_response_json"]!.GetValue<string>())!.AsObject();
            result["raw_response_json"] = null;
            var bytes = ImageContract.Decode(raw, request.Format);
            raw["data"]![0]!["b64_json"] = "[IMAGE_DATA_OMITTED]";
            result["raw_response_json"] = raw.ToJsonString();
            await file.WriteAsync(bytes, cancellation);
            await file.FlushAsync(cancellation);
            file.Flush(flushToDisk: true);
            file.Close();
            cancellation.ThrowIfCancellationRequested();
            File.Move(temporary, request.Output, overwrite: false);
            temporary = null;
            result["artifact"] = new JsonObject
            {
                ["path"] = request.Output, ["format"] = request.Format, ["bytes"] = bytes.Length,
                ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
            };
            result["elapsed_s"] = clock.Elapsed.TotalSeconds;
            return result;
        }
        catch (OperationCanceledException) { return Failure("cancelled"); }
        catch (Exception ex) when (ex is ContractException or JsonException or FormatException or InvalidOperationException)
        { result["raw_response_json"] = null; return Failure("invalid_response"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return Failure("output_write_failed"); }
        finally
        {
            if (temporary is not null && ownsTemporary)
                try { File.Delete(temporary); }
                catch (Exception) { result["temporary_cleanup_failed"] = true; }
        }
    }
}
