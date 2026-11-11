using System.Text;
using System.Text.Json.Nodes;

namespace TokenCLI;

public static class ImageTerminal
{
    public static byte[] ReadRequest(CancellationToken cancellation)
    {
        if (Console.IsInputRedirected) throw new InvalidOperationException();
        Console.Error.WriteLine("OpenAI 付費產圖：gpt-image-2、1024x1024、中品質、PNG、不透明背景，一次一張。費用依供應商帳單。 ");
        Console.Error.Write("圖片描述：");
        var prompt = Console.ReadLine() ?? "";
        Console.Error.Write("儲存路徑（直接 Enter 使用目前目錄的新 PNG）：");
        var output = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(output)) output = "image-" + Guid.NewGuid().ToString("N") + ".png";
        var request = new JsonObject { ["prompt"] = prompt, ["output"] = output };
        var parsed = ImageContract.Parse(request);
        Console.Error.WriteLine("儲存位置：" + parsed.Output);
        Console.Error.Write("輸入 Y 確認付費呼叫一次，其他輸入取消：");
        if (!string.Equals(Console.ReadLine()?.Trim(), "Y", StringComparison.OrdinalIgnoreCase)) throw new OperationCanceledException();
        cancellation.ThrowIfCancellationRequested();
        return Encoding.UTF8.GetBytes(request.ToJsonString());
    }
}
