using Shiyin.LLM;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Notes;
using Shiyin.Platform;

namespace Shiyin.App;

/// <summary>引導和設定頁共用（M§34.3）：存或刪 key（通知「準備好了沒」重算）、送一次測試</summary>
public static class SummarizerSetup
{
    /// <summary>存 key；DEBUG 沒有加 debugRealKeychain 時只放記憶體。存不進去丟 InvalidOperationException</summary>
    public static void SaveKey(AppHost host, string account, string value)
    {
        if (host.Keys is CredentialStore store) store.Save(account, value);
        else if (host.Keys is MemoryKeyStore memory) memory.Keys[account] = value;
        host.Settings.NotifyKeysChanged();
    }

    public static void DeleteKey(AppHost host, string account)
    {
        if (host.Keys is CredentialStore store) store.Delete(account);
        else if (host.Keys is MemoryKeyStore memory) memory.Keys.Remove(account);
        host.Settings.NotifyKeysChanged();
    }

    /// <summary>送一個很短的請求（同 Mac 的 SummarizerTest）；成功回傳花了幾秒、解除暫停（R46.3），失敗丟出整理工具的錯誤</summary>
    public static async Task<long> Test(AppHost host, LLMProvider provider, string model)
    {
        var started = DateTime.UtcNow;
        var language = host.Settings.DefaultMeetingLanguage;
        var prompt = language.Text("這是連線測試。逐字稿：\n[00:00] 說話者 1：大家好，今天只是測試一下。",
            "This is a connection test. Transcript:\n[00:00] Speaker 1: Hi everyone, this is just a test.",
            "これは接続テストです。文字起こし：\n[00:00] 話者 1：皆さん、今日はテストだけです。");  // l10n-skip
        await host.Llm.Generate(provider, model, NotesPrompt.System(language), prompt, CancellationToken.None);
        host.Settings.ClearSummarizerPause();
        return (long)Math.Round((DateTime.UtcNow - started).TotalSeconds);
    }

    /// <summary>這個整理工具在設定裡的模型（引導裡選的不一定是目前的整理工具）</summary>
    public static string ModelFor(AppHost host, LLMProvider provider) =>
        provider == host.Settings.Provider ? host.Settings.Model : host.Settings.String(provider.ModelKey()) ?? "";

    /// <summary>測試會丟出的錯誤（其他的照常往上丟）</summary>
    public static bool IsTestFailure(Exception e) => e is LLMException or HttpRequestException or IOException or TimeoutException or TaskCanceledException;
}
