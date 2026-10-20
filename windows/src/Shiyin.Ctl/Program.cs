using System.IO.Pipes;
using System.Text;

// shiyinctl <指令> [參數…]：例如 shiyinctl state、shiyinctl record-sim C:\eval\a.wav 4 1、shiyinctl tab transcript
if (args.Length == 0)
{
    Console.Error.WriteLine("usage: shiyinctl <command> [args...]");
    return 2;
}
using var pipe = new NamedPipeClientStream(".", "shiyin-debug", PipeDirection.InOut);
try { pipe.Connect(5000); }
catch (TimeoutException)
{
    Console.Error.WriteLine("Shiyin (DEBUG) is not running");
    return 1;
}
using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
writer.WriteLine(new System.Text.Json.Nodes.JsonArray(args.Select(a => (System.Text.Json.Nodes.JsonNode?)a).ToArray()).ToJsonString());
var reply = reader.ReadLine() ?? "";
// 有第二個參數是 .json 檔名的指令（state）也可以寫進檔案，同 Mac 的 shiyinctl state /tmp/s.json
if (args[0] == "state" && args.Length > 1) File.WriteAllText(args[1], reply);
else Console.WriteLine(reply);
return reply.Contains("\"error\"") ? 1 : 0;
