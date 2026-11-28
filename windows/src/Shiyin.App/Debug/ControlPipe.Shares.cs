#if DEBUG
using System.Text.Json.Nodes;
using Shiyin.Services;

namespace Shiyin.App.Debug;

/// <summary>
/// 控制通道：分享會議（M§99.6、design-windows §11.58）。名稱同 Mac 的 DebugControl；Mac 寫到檔案的這裡直接回 JSON（要等網路的等做完才回）
/// </summary>
public static partial class ControlPipe
{
    /// <summary>share … nowait 的結果（shareresult 拿）</summary>
    static JsonNode? lastShare;

    static JsonNode? Shares(AppHost host, MainWindow? main, string[] args)
    {
        if (args.Length == 0) return null;
        long? Id(int i) => args.Length > i && long.TryParse(args[i], out var v) ? v : null;
        var controller = host.Controller;
        switch (args[0])
        {
            // share <會議 id> <JSON 選項> [nowait]：同按「分享」（不開對話框）；選項 {"emails": [...], "link": "company|public", "days": 7, "contents": {...}}。
            // 回傳 ok、error、shareId、notAdded（沒加進名單的信箱）；nowait 時馬上回，結果用 shareresult 拿（測 sharecancel）
            case "share" when Id(1) is long meetingId && args.Length > 2:
            {
                var options = JsonNode.Parse(args[2]) as JsonObject ?? [];
                var emails = (options["emails"] as JsonArray ?? []).Select(e => e?.GetValue<string>() ?? "").Where(e => e.Length > 0).ToList();
                var c = options["contents"] as JsonObject;
                var contents = new EnterpriseShares.Contents(c?["audio"]?.GetValue<bool>() ?? true, c?["userNotes"]?.GetValue<bool>() ?? true, c?["documents"]?.GetValue<bool>() ?? true);
                var link = options["link"]?.GetValue<string>();
                int? days = options["days"] is JsonValue d ? d.GetValue<int>() : null;
                async Task<JsonNode> Run()
                {
                    string? error = null;
                    void Capture(string e) => error = e;
                    controller.Error += Capture;
                    try
                    {
                        var back = await controller.ShareMeeting(meetingId, emails, link, days, contents);
                        var id = controller.ShareId(meetingId);
                        return lastShare = new JsonObject
                        {
                            ["ok"] = id is not null, ["error"] = error ?? "", ["shareId"] = id ?? "", ["notAdded"] = new JsonArray([.. back.Select(e => (JsonNode)e)]),
                        };
                    }
                    finally { controller.Error -= Capture; }
                }
                lastShare = null;
                if (args.Length > 3 && args[3] == "nowait")
                {
                    _ = Run();
                    return Ok;
                }
                return Later(Run);
            }
            case "shareresult": return lastShare?.DeepClone() ?? new JsonObject { ["pending"] = true };
            case "sharecancel": controller.CancelShare(); return Ok;
            // shareinfo <會議 id>：問伺服器這場的分享（名單、連結、期限、版本）
            case "shareinfo" when Id(1) is long infoId:
                return Later(async () =>
                {
                    await controller.LoadShareDetail(infoId);
                    var d = controller.ShareDetail(infoId);
                    return new JsonObject
                    {
                        ["shareId"] = controller.ShareId(infoId) ?? "", ["version"] = d?.Version ?? 0,
                        ["recipients"] = new JsonArray([.. (d?.Recipients ?? []).Select(r => (JsonNode)new JsonObject
                        {
                            ["email"] = r.Email, ["opened"] = r.OpenedAt is not null, ["imported"] = r.ImportedAt is not null,
                        })]),
                        ["link"] = d?.Link?.Url ?? "", ["access"] = d?.Link?.Access ?? "", ["expiresAt"] = d?.ExpiresAt ?? "", ["uploading"] = d?.Uploading ?? false,
                        ["contents"] = new JsonObject { ["audio"] = d?.Contents.Audio ?? false, ["userNotes"] = d?.Contents.UserNotes ?? false, ["documents"] = d?.Contents.Documents ?? false },
                    };
                });
            case "shareupdate" when Id(1) is long updateId:
                return Later(async () =>
                {
                    await controller.UpdateShareContents(updateId);
                    return new JsonObject { ["version"] = controller.ShareDetail(updateId)?.Version ?? 0 };
                });
            case "sharestop" when Id(1) is long stopId:
                return Later(async () =>
                {
                    await controller.StopSharing(stopId);
                    return new JsonObject { ["ok"] = controller.ShareId(stopId) is null };
                });
            // sharedwithme：查新的分享（同啟動、回到前景），回傳清單
            case "sharedwithme":
                return Later(async () =>
                {
                    await controller.RefreshSharedWithMe();
                    return new JsonObject
                    {
                        ["items"] = new JsonArray([.. controller.SharedWithMe.Select(i => (JsonNode)new JsonObject
                        {
                            ["id"] = i.Id, ["title"] = i.Title, ["from"] = i.From.Email, ["version"] = i.Version, ["importedVersion"] = i.ImportedVersion ?? 0, ["isNew"] = i.IsNew,
                        })]),
                    };
                });
            // shareselect <分享 id>：側欄點那一場（右邊預覽）
            case "shareselect" when args.Length > 1 && main is not null: main.State.SelectShared(args[1]); return Ok;
            // shareimport <分享 id> [replace|copy]：同按「匯入」，回傳匯入的會議 id。之前匯入過時，replace 取代、copy 另存一份；
            // 沒給就跟按鈕一樣跳出問的對話框（不等結果）
            case "shareimport" when args.Length > 1:
            {
                if (controller.SharedWithMe.FirstOrDefault(i => i.Id == args[1]) is not { } item) return new JsonObject { ["error"] = "no such share" };
                if (args.Length < 3 && main is not null && controller.PreviousImports(item.Id).Count > 0)
                {
                    host.Post(() => main.ImportShared(item));
                    return Ok;
                }
                var replacing = args.Length > 2 && args[2] == "replace" ? controller.PreviousImports(item.Id) : [];
                return Later(async () =>
                {
                    long? revealed = null;
                    void Capture(long id) => revealed = id;
                    controller.Reveal += Capture;
                    try { await controller.ImportShared(item, main?.State.CurrentFolder, replacing); }
                    finally { controller.Reveal -= Capture; }
                    return new JsonObject { ["ok"] = revealed is not null, ["meetingId"] = revealed };
                });
            }
            // shareopen <shiyin://share?…>：等於點網頁的「用拾音打開」（會跳確認）
            case "shareopen" when args.Length > 1:
            {
                var url = args[1];
                host.Post(() => App.Current.OpenLink(url));
                return Ok;
            }
            // shareopensheet <會議 id>：打開分享對話框；shareui add <信箱,…>｜share｜close：對話框裡填信箱、按「分享」、關掉
            case "shareopensheet" when Id(1) is long sheetId && main is not null: _ = ShareDialog.Show(main, sheetId); return Ok;
            case "shareui" when args.Length > 1 && ShareDialog.Current is { } dialog:
                switch (args[1])
                {
                    case "add" when args.Length > 2: dialog.DebugAddEmails(args[2].Split(',', StringSplitOptions.RemoveEmptyEntries)); break;
                    case "share": dialog.Share(); break;
                    case "close": dialog.Close(); break;
                }
                return Ok;
        }
        return null;
    }
}
#endif
