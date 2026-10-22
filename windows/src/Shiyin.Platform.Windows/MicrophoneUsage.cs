using Microsoft.Win32;
using Shiyin.Services;

namespace Shiyin.Platform;

/// <summary>
/// 哪些 app 正在用麥克風（design-windows §12.4、D55）：讀隱私設定的使用紀錄
/// HKCU\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone。
/// 封裝的 app 用套件家族名稱當子鍵，桌面 app 在 NonPackaged 下、路徑的 \ 換成 #；LastUsedTimeStop = 0 表示正在用。
/// 這是 Windows「麥克風使用中」指示的資料來源
/// </summary>
public sealed class ConsentStoreMicrophoneUsage : IMicrophoneUsage
{
    const string Root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
    readonly string self = (Environment.ProcessPath ?? "").ToLowerInvariant();

    public IReadOnlySet<string> AppsUsingMicrophone()
    {
        var result = new HashSet<string>();
        using var root = Registry.CurrentUser.OpenSubKey(Root);
        if (root is null) return result;
        foreach (var name in root.GetSubKeyNames())
        {
            if (name == "NonPackaged")
            {
                using var nonPackaged = root.OpenSubKey(name);
                if (nonPackaged is null) continue;
                foreach (var app in nonPackaged.GetSubKeyNames())
                {
                    using var key = nonPackaged.OpenSubKey(app);
                    var path = app.Replace('#', '\\').ToLowerInvariant();
                    if (key is not null && InUse(key) && path != self) result.Add(Path.GetFileName(path));
                }
                continue;
            }
            using var packaged = root.OpenSubKey(name);
            if (packaged is not null && InUse(packaged)) result.Add(name.ToLowerInvariant());
        }
        return result;
    }

    static bool InUse(RegistryKey key) =>
        key.GetValue("LastUsedTimeStop") is long stop && stop == 0 && key.GetValue("LastUsedTimeStart") is long start && start > 0;
}
