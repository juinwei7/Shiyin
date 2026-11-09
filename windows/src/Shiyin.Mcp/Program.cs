using System.Reflection;
using System.Text;
using Shiyin.Services;
using Shiyin.Services.Mcp;

// shiyin-mcp.exe（design-windows §11.12）：AI 工具（Claude Code、Codex、Claude Desktop…）用 stdio 叫起來，讀這台電腦上的會議。
// 不碰錄音、工作佇列；--set 鍵=值 只對這次執行有效（同 DEBUG 版的啟動參數）
var paths = ShiyinPaths.FromEnvironment();
Log.Configure(paths.Logs);
var utf8 = new UTF8Encoding(false);
using var input = new StreamReader(Console.OpenStandardInput(), utf8);
using var output = new StreamWriter(Console.OpenStandardOutput(), utf8);
var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0";
McpServer.Run(input, output, paths, version, AppSettings.ParseOverrides(args));
