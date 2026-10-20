using System.Runtime.InteropServices;
using System.Text;
using Shiyin.Localization;
using Shiyin.LLM;

namespace Shiyin.Platform;

/// <summary>
/// API key 存在認證管理員（design-windows §12.5、D49）：一般認證，目標名稱 com.weiwei.shiyin.apikey/&lt;帳號&gt;，只存在這台電腦。
/// 同一個 Windows 使用者的程式都讀得到，不會跳出存取確認。不寫進檔案、資料庫、log（N6）
/// </summary>
public sealed class CredentialStore : IKeyStore
{
    public const string Service = "com.weiwei.shiyin.apikey";

    static string Target(string account) => $"{Service}/{account}";

    public string? Read(string account)
    {
        if (!Native.CredRead(Target(account), Native.CRED_TYPE_GENERIC, 0, out var pointer))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == Native.ERROR_NOT_FOUND) return null;
            throw new InvalidOperationException(L.F("讀不到認證管理員裡的 key（錯誤代碼 %lld）", error));
        }
        try
        {
            var credential = Marshal.PtrToStructure<Native.CREDENTIAL>(pointer);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero) return null;
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            Native.CredFree(pointer);
        }
    }

    public bool Exists(string account)
    {
        try { return Read(account) is { Length: > 0 }; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>存 key；存不進去時丟出說明原因的錯誤</summary>
    public void Save(string account, string key)
    {
        var bytes = Encoding.UTF8.GetBytes(key);
        var target = Marshal.StringToHGlobalUni(Target(account));
        var user = Marshal.StringToHGlobalUni(account);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Native.CREDENTIAL
            {
                Type = Native.CRED_TYPE_GENERIC, TargetName = target, UserName = user, CredentialBlob = blob, CredentialBlobSize = bytes.Length,
                Persist = Native.CRED_PERSIST_LOCAL_MACHINE,
            };
            if (!Native.CredWrite(ref credential, 0))
                throw new InvalidOperationException(L.F("沒辦法把 key 存進認證管理員（錯誤代碼 %lld）", Marshal.GetLastPInvokeError()));
        }
        finally
        {
            // 記憶體裡的 key 先清掉再釋放
            for (var i = 0; i < bytes.Length; i++) Marshal.WriteByte(blob, i, 0);
            Marshal.FreeHGlobal(blob);
            Marshal.FreeHGlobal(target);
            Marshal.FreeHGlobal(user);
            Array.Clear(bytes);
        }
    }

    public void Delete(string account)
    {
        if (!Native.CredDelete(Target(account), Native.CRED_TYPE_GENERIC, 0) && Marshal.GetLastPInvokeError() != Native.ERROR_NOT_FOUND)
            throw new InvalidOperationException(L.F("沒辦法刪除認證管理員裡的 key（錯誤代碼 %lld）", Marshal.GetLastPInvokeError()));
    }
}

/// <summary>DEBUG 預設不讀真的認證管理員（同 Mac 3.4.1 起的規則）：key 只放記憶體（llmkey）</summary>
public sealed class MemoryKeyStore : IKeyStore
{
    public Dictionary<string, string> Keys { get; } = [];
    public string? Read(string account) => Keys.GetValueOrDefault(account);
    public bool Exists(string account) => Keys.ContainsKey(account);
}
