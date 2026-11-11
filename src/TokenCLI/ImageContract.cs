using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TokenCLI;

public sealed record ImageRequest(ApiCall Call, string Output, string Format);

public static class ImageContract
{
    public const int MaxResponseBytes = 32 * 1024 * 1024;
    public const int MaxImageBytes = 20 * 1024 * 1024;
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(180);
    public static ImageRequest Parse(JsonObject envelope)
    {
        string[] fields = ["prompt", "output", "model", "size", "quality", "output_format", "background"];
        if (!envelope.ContainsKey("prompt") || !envelope.ContainsKey("output") || envelope.Any(p => !fields.Contains(p.Key)))
            throw new ContractException();
        string Get(string key, string fallback) => envelope.ContainsKey(key) ? Contract.Text(envelope[key]) : fallback;
        var prompt = Contract.Text(envelope["prompt"]);
        var model = Get("model", "gpt-image-2");
        var size = Get("size", "1024x1024");
        var quality = Get("quality", "medium");
        var format = Get("output_format", "png");
        var background = Get("background", "opaque");
        var output = Contract.Text(envelope["output"]);
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 32000 ||
            model is not ("gpt-image-2" or "gpt-image-1.5" or "gpt-image-1-mini") ||
            size is not ("1024x1024" or "1536x1024" or "1024x1536") ||
            quality is not ("low" or "medium" or "high") || format is not ("png" or "jpeg" or "webp") ||
            background is not ("opaque" or "transparent" or "auto") ||
            (format == "jpeg" && background == "transparent") || string.IsNullOrWhiteSpace(output))
            throw new ContractException();
        // Only ordinary local paths. No URL, UNC/device path, ADS, or Windows reserved names.
        var full = Path.GetFullPath(output);
        if (full.StartsWith(@"\\", StringComparison.Ordinal) || output.Contains("://", StringComparison.Ordinal) ||
            full[Path.GetPathRoot(full)!.Length..].Contains(':') ||
            full[Path.GetPathRoot(full)!.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p =>
                p.EndsWith(' ') || p.EndsWith('.') || Regex.IsMatch(p, @"\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|\z)", RegexOptions.IgnoreCase)))
            throw new ContractException();
        var extension = Path.GetExtension(full).ToLowerInvariant();
        if (!(format == "png" && extension == ".png" || format == "jpeg" && extension is ".jpg" or ".jpeg" || format == "webp" && extension == ".webp"))
            throw new ContractException();
        var body = new JsonObject
        {
            ["model"] = model, ["prompt"] = prompt, ["size"] = size, ["quality"] = quality,
            ["output_format"] = format, ["background"] = background, ["n"] = 1, ["stream"] = false, ["moderation"] = "auto"
        };
        return new(new("openai", "generate_image", new Uri("https://api.openai.com/v1/images/generations"), body), full, format);
    }
    public static byte[] Decode(JsonObject data, string format, string? secret = null)
    {
        if (data["data"] is not JsonArray { Count: 1 } images || images[0] is not JsonObject item || item.ContainsKey("url"))
            throw new ContractException();
        if (data.ContainsKey("output_format") && Contract.Text(data["output_format"]) != format) throw new ContractException();
        var encoded = Contract.Text(item["b64_json"]);
        if (encoded.Length > (MaxImageBytes + 2L) / 3 * 4) throw new ContractException();
        var bytes = Convert.FromBase64String(encoded);
        if (bytes.Length == 0 || bytes.Length > MaxImageBytes ||
            (secret is not null && bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)) >= 0))
            throw new ContractException();
        var valid = format switch
        {
            "png" => bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            "jpeg" => bytes.Length >= 4 && bytes[0] == 255 && bytes[1] == 216 && bytes[^2] == 255 && bytes[^1] == 217,
            "webp" => bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8),
            _ => false
        };
        if (!valid) throw new ContractException();
        return bytes;
    }
}
