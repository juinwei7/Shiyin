using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Shiyin.LLM;
using Shiyin.Localization;

namespace Shiyin.App;

/// <summary>只有 Claude 桌面版、沒有 Claude Code 時的說明（R56.2、M§37.3、design-windows §10）：拾音不用桌面版裡的那一份，請使用者另外裝命令列版</summary>
static class ClaudeCodeHint
{
    /// <summary>claude 找不到、有 Claude 桌面版</summary>
    public static bool Applies(AppHost host) => host.Tools.Find("claude") is null && DesktopApps.HasClaude();

    /// <summary>recheck：「重新檢查」，裝好之後重新找 claude、查登入</summary>
    public static FrameworkElement Build(Action recheck)
    {
        var command = Ui.Text(DesktopApps.ClaudeInstallCommand, 13, select: true);
        command.FontFamily = new FontFamily("Consolas");
        // 「複製」在共用的翻譯表裡是複製會議（Duplicate），這裡是複製到剪貼簿
        var copyLabel = L.English ? "Copy" : "複製";  // l10n-skip
        Microsoft.UI.Xaml.Controls.Button? copy = null;
        copy = Ui.Button(copyLabel, () =>
        {
            Copy.Text(DesktopApps.ClaudeInstallCommand);
            copy!.Content = L.T("已複製");
        });
        return Ui.Column(6,
            Ui.Text(L.T("你有 Claude 桌面版：另外安裝 Claude Code（命令列版）之後，拾音就能用你的 Claude 訂閱（Pro 以上）整理。"), 13),
            Ui.Text(L.T("打開 PowerShell，貼上這一行安裝："), 13, color: Ui.Secondary),
            Ui.Row(8, command, copy),
            Ui.Row(12, Ui.Link(L.T("安裝說明"), () => _ = Windows.System.Launcher.LaunchUriAsync(new Uri(DesktopApps.ClaudeInstallGuide))),
                Ui.Button(L.T("重新檢查"), recheck)),
            Ui.Caption(L.T("裝好之後在終端機執行 claude 登入，再按「重新檢查」。")));
    }
}
