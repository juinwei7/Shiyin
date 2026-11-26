#if DEBUG
using System.Text.Json.Nodes;
using Shiyin.Model;
using Shiyin.Notes;
using Shiyin.Services;

namespace Shiyin.App.Debug;

/// <summary>
/// 控制通道：會議列表的排序、分組、篩選（M§71），資料夾自動歸類（M§72），定期會議（M§73）。名稱同 Mac 的 DebugControl；
/// Mac 寫到檔案的（foldersuggest）這裡直接回傳 JSON（design-windows §11.30、§11.31）。定期會議（M§73）D177 拿掉
/// </summary>
public static partial class ControlPipe
{
    static JsonNode? Organize(AppHost host, MainWindow? main, string[] args)
    {
        if (args.Length == 0) return null;
        long? Id(int i) => args.Length > i && long.TryParse(args[i], out var v) ? v : null;
        var state = main?.State;
        // 資料夾的參數照舊收名字（R118、M§92.4），進來就換成 id：Named 只找已經有的，Ensure 沒有就建（同以前移進不存在的資料夾時會建）
        long? Named(string name) => host.Db.Folders().FirstOrDefault(f => f.Name == name)?.Id;
        long Ensure(string name) => host.Db.CreateFolder(name);
        switch (args[0])
        {
            // 移除一個人（R85.7、M§60.2，同 Mac）：personremove <id>（等於選了「移除這個人…」，打開確認）；
            // personrenameui <id>（等於選了「重新命名…」：選那個人、打開重新命名的對話框）
            case "personremove" when Id(1) is long removeId && main is not null:
                _ = main.ConfirmRemovePerson(removeId);
                return Ok;
            case "personrenameui" when Id(1) is long renameId && main is not null:
                main.RenamePersonUi(renameId);
                return Ok;
            // 照 Sheet 樣子的對話框（R106、design-windows §11.39；同 Mac 的 exportui、preview editinfo|replace|rediarize）：
            // dialogui <export|rediarize|editinfo|replace|split|trash|purge> <會議 id[,…]> [段落 id（split）或錯誤寫法（replace）]，
            // 開著的是哪一個看 state 的 sheet（標題）
            case "dialogui" when args.Length > 2 && main is not null:
            {
                var ids = Ids(args[2]);
                if (ids.Count == 0) return new JsonObject { ["error"] = "no meeting id" };
                var first = ids[0];
                switch (args[1])
                {
                    case "export": _ = Dialogs.Export(main, first); break;
                    case "rediarize": _ = Dialogs.Rediarize(main, first); break;
                    case "editinfo" when host.Db.Meeting(first) is { } meeting: _ = Dialogs.EditInfo(main, meeting); break;
                    case "replace": _ = Dialogs.Replace(main, first, args.Length > 3 ? args[3] : ""); break;
                    case "split" when Id(3) is long segmentId:
                        main.State.Reveal(first);
                        if (main.State.Detail?.Segments.FirstOrDefault(s => s.Id == segmentId) is not { } segment) return new JsonObject { ["error"] = "no such segment" };
                        _ = Dialogs.SplitSegment(main, main.State.Detail, segment);
                        break;
                    case "trash": _ = main.ConfirmTrash(ids); break;
                    case "purge": _ = main.ConfirmPurge(ids); break;
                    default: return new JsonObject { ["error"] = "unknown dialog" };
                }
                return Ok;
            }
            // 重新命名的對話框（R102、design-windows §11.35，同 Mac）：renameui <meeting|folder|person|bookmark|newfolder> <id、名稱或會議 id,…>
            case "renameui" when args.Length > 2 && main is not null:
            {
                RenameTarget? target = args[1] switch
                {
                    "meeting" when Id(2) is long m => new RenameTarget.Meeting(m),
                    "folder" when Named(args[2]) is long f => new RenameTarget.Folder(f),
                    "person" when Id(2) is long p => new RenameTarget.Person(p),
                    "bookmark" when Id(2) is long b => new RenameTarget.Bookmark(b),
                    "newfolder" => new RenameTarget.NewFolder(SidebarDrag.Decode(args[2])),
                    _ => null,
                };
                if (target is null) return new JsonObject { ["error"] = "target" };
                main.RenameUi(target);
                return Ok;
            }
            // foldernew <名稱>：等於按「資料夾」標題的「+」建立（R104，同 Mac）
            case "foldernew" when args.Length > 1:
                host.Controller.CreateFolder(args[1]);
                return Ok;
            // importinto <資料夾> <檔案>：等於從檔案總管把檔案拖到側欄的資料夾（R103.3，同 Mac）
            case "importinto" when args.Length > 2 && main is not null:
                main.ImportInto([args[2]], Ensure(args[1]));
                return Ok;
            // 每一種東西都能直接改、刪（R100、M§74，同 Mac 的名稱）：renamemeetingui <id>（等於側欄右鍵「重新命名…」、Enter／F2）；
            // folderdeleteui <名稱>（打開刪除的確認）；sidebarkey return|delete（等於在側欄的會議列表按 Enter、Delete）
            case "renamemeetingui" when Id(1) is long renameMeeting && main is not null:
                main.RenameMeetingUi(renameMeeting);
                return Ok;
            case "folderdeleteui" when args.Length > 1 && main is not null:
                if (Named(args[1]) is not long deleteFolder) return new JsonObject { ["error"] = "folder" };
                _ = main.ConfirmDeleteFolder(deleteFolder);
                return Ok;
            case "sidebarkey" when args.Length > 1 && main is not null:
                return new JsonObject { ["handled"] = args[1] == "return" ? main.SidebarRename() : main.SidebarDelete() };
            // 側欄的收合（R101、design-windows §11.34，同 Mac）：sidebarsection <folders|meetings|people|group:…> on|off（on 是展開）、
            // sidebarexpand <folder:名稱|folderid:id> on|off（folder:名稱 換成那個資料夾的鍵 folderid:id，R118）
            case "sidebarsection" when args.Length > 2:
                SidebarCollapse.SetSection(host.Settings, args[1], args[2] == "on");
                return Ok;
            case "sidebarexpand" when args.Length > 2:
            {
                var key = args[1];
                if (key.StartsWith("folder:", StringComparison.Ordinal))
                {
                    if (Named(key["folder:".Length..]) is not long expandFolder) return new JsonObject { ["error"] = "folder" };
                    key = SidebarCollapse.FolderKey(expandFolder);
                }
                SidebarCollapse.SetRow(host.Settings, key, args[2] == "on");
                return Ok;
            }
            // folder <會議 id> <資料夾|->：移到資料夾（- 是移出，同 Mac；準備示範資料用；沒有這個資料夾就建一個）
            case "folder" when args.Length > 2 && Id(1) is long folderMeeting:
                host.Controller.MoveToFolder(folderMeeting, args[2] == "-" ? null : Ensure(args[2]));
                return Ok;
            // noteitem <editui|edit|delete|cancel> <summary|decisions|actionItems|openQuestions|topic|custom|person> <位置> [第二個位置] [文字]：
            // 選取中的會議的一條紀錄（editui 打開就地改的輸入框、edit 直接存、delete 刪掉）
            case "noteitem" when args.Length > 3 && main?.State.Detail is { } noteDetail:
            {
                var a = int.TryParse(args[3], out var x) ? x : 0;
                var twoLevel = args[2] is "topic" or "custom" or "person";
                var b = twoLevel && args.Length > 4 && int.TryParse(args[4], out var y) ? y : 0;
                NoteItemRef? item = args[2] switch
                {
                    "summary" => NoteItemRef.Summary(a), "decisions" => NoteItemRef.Decisions(a), "actionItems" => NoteItemRef.ActionItems(a),
                    "openQuestions" => NoteItemRef.OpenQuestions(a), "topic" => NoteItemRef.TopicPoint(a, b), "custom" => NoteItemRef.Custom(a, b),
                    "person" => NoteItemRef.Person(a, b), _ => null,
                };
                if (item is not { } r) return new JsonObject { ["error"] = "section" };
                var textIndex = twoLevel ? 5 : 4;
                switch (args[1])
                {
                    case "editui": main.NotesTabs.BeginEdit(noteDetail, r); break;
                    case "edit": host.Controller.EditNoteItem(noteDetail.Meeting.Id!.Value, r, args.Length > textIndex ? args[textIndex] : ""); break;
                    case "delete": host.Controller.DeleteNoteItem(noteDetail.Meeting.Id!.Value, r); break;
                    default: main.NotesTabs.BeginEdit(noteDetail, null); break;
                }
                return Ok;
            }
            // bookmarkrename <標記 id> <名稱>、bookmarkrenameui <標記 id>（打開重新命名的對話框）；termrename <詞的 id> <詞>（改成已經有的詞時合併）
            case "bookmarkrename" when args.Length > 2 && Id(1) is long bookmarkId && main?.State.Detail is { } bookmarkDetail:
                host.Controller.RenameBookmark(bookmarkDetail.Meeting.Id!.Value, bookmarkId, args[2]);
                return Ok;
            case "bookmarkrenameui" when Id(1) is long uiBookmark && main is not null:
                main.RenameUi(new RenameTarget.Bookmark(uiBookmark));
                return Ok;
            case "termrename" when args.Length > 2 && Id(1) is long termId:
                host.Controller.RenameTerm(termId, args[2]);
                return Ok;
            // reload：直接改資料庫之後重讀
            case "reload":
                state?.Reload();
                return Ok;
            // listsort <dateDesc|dateAsc|title|duration>、listgroup <none|month|folder>
            case "listsort" when args.Length > 1 && MeetingSorts.Parse(args[1]) is { } sort:
                state?.SetSort(sort);
                return Ok;
            case "listgroup" when args.Length > 1 && MeetingGroupings.Parse(args[1]) is { } grouping:
                state?.SetGrouping(grouping);
                return Ok;
            // listfilter <review|mine|person <id>|template <id>|clear>：review、mine 是切換（同 Mac）
            case "listfilter" when args.Length > 1 && state is not null:
            {
                var f = state.Filter;
                state.SetFilter(args[1] switch
                {
                    "review" => f with { NeedsReview = !f.NeedsReview },
                    "mine" => f with { MyActionItems = !f.MyActionItems },
                    "person" => f with { PersonId = Id(2) },
                    "template" => f with { TemplateId = args.Length > 2 ? args[2] : null },
                    _ => new MeetingFilter(),
                });
                return Ok;
            }
            // folderrule <資料夾> <JSON|JSON 檔的路徑|-> [apply]：等於在條件的編輯按「儲存」（- 是移除）；沒有這個資料夾就建一個。
            // JSON 是 {"keywords":[…],"people":[id…],"templateId":…}；從 SSH 傳中文不方便時給檔案路徑
            case "folderrule" when args.Length > 2:
            {
                var rule = new FolderRule { FolderId = Ensure(args[1]) };
                if (args[2] != "-")
                {
                    var json = File.Exists(args[2]) ? File.ReadAllText(args[2]) : args[2];
                    if (JsonNode.Parse(json) is JsonObject o)
                    {
                        rule.Keywords = o["keywords"]?.AsArray().Select(x => x!.GetValue<string>()).ToList() ?? [];
                        rule.People = o["people"]?.AsArray().Select(x => x!.GetValue<long>()).ToList() ?? [];
                        rule.TemplateId = o["templateId"]?.GetValue<string>();
                    }
                }
                host.Controller.SaveFolderRule(rule, args.Length > 3 && args[3] == "apply");
                state?.Reload();
                return Ok;
            }
            // editfolderrule <資料夾>：打開條件的編輯（等於資料夾右鍵「自動加入的條件…」）
            case "editfolderrule" when args.Length > 1 && main is not null:
                if (Named(args[1]) is not long ruleFolder) return new JsonObject { ["error"] = "folder" };
                _ = Dialogs.FolderRule(main, ruleFolder);
                return Ok;
            // autofile <會議 id>：照資料夾的條件放（同轉錄完、整理完），回傳放進的資料夾（名字，另外多 folderId）
            case "autofile" when Id(1) is long fileId:
            {
                var folder = host.Db.AutoFile(fileId);
                if (folder is not null) host.Controller.Organized(fileId, folder);
                return new JsonObject { ["folder"] = folder is null ? null : host.Db.FolderName(folder), ["folderId"] = folder };
            }
            // foldersuggest <會議 id>：紀錄分頁頂端的建議（沒有時 folder 是空字串）
            case "foldersuggest" when Id(1) is long suggestId && state is not null:
            {
                var meeting = host.Db.Meeting(suggestId);
                var s = meeting is null ? null : Organizing.Suggestion(meeting, state.Meetings, state.MeetingPeople, host.Recorder.MeetingId, state.FolderName);
                var reason = new JsonObject();
                if (s?.People is { } people) reason["people"] = new JsonArray([.. people.Select(p => (JsonNode)p)]);
                if (s?.Title is { } word) reason["title"] = word;
                return new JsonObject
                {
                    ["folder"] = s?.Folder ?? "", ["folderId"] = s?.FolderId, ["count"] = s?.Count ?? 0, ["reason"] = reason,
                    ["text"] = s is null ? "" : Organizing.SuggestionText(s, state.People),
                };
            }
            // foldersuggestion accept|dismiss <會議 id>：等於按「放進去」「不用」
            case "foldersuggestion" when args.Length > 2 && Id(2) is long accepted && state is not null:
            {
                if (args[1] == "accept" && host.Db.Meeting(accepted) is { } meeting
                    && Organizing.Suggestion(meeting, state.Meetings, state.MeetingPeople, host.Recorder.MeetingId, state.FolderName) is { } s)
                    host.Controller.AcceptFolderSuggestion(accepted, s.FolderId);
                else if (args[1] == "dismiss") host.Controller.DismissFolderSuggestion(accepted);
                state.Reload();
                return Ok;
            }
        }
        return null;
    }

    /// <summary>state 的 list（同 Mac）：排序、分組、篩選、說明、我的名字、目前列表的順序、每組的標題和場數</summary>
    static JsonNode? ListState(MainWindow? main)
    {
        if (main?.State is not { } s) return null;
        return new JsonObject
        {
            ["sort"] = s.Sort.Code(), ["group"] = s.EffectiveGrouping.Code(),
            ["filter"] = new JsonObject
            {
                ["review"] = s.Filter.NeedsReview, ["mine"] = s.Filter.MyActionItems, ["person"] = s.Filter.PersonId ?? -1, ["template"] = s.Filter.TemplateId ?? "",
            },
            ["filterSummary"] = s.FilterSummary, ["myName"] = s.MyName ?? "",
            ["ids"] = new JsonArray([.. s.FilteredMeetings.Select(r => (JsonNode)r.Id)]),
            ["groups"] = new JsonArray([.. s.Groups.Select(g => (JsonNode)new JsonObject { ["title"] = g.Title, ["count"] = g.Rows.Count })]),
        };
    }

    static JsonNode FolderRulesState(MainWindow? main) => new JsonArray([.. (main?.State.FolderRules ?? []).Select(r => (JsonNode)new JsonObject
    {
        ["folder"] = main!.State.FolderName(r.FolderId), ["folderId"] = r.FolderId, ["keywords"] = new JsonArray([.. r.Keywords.Select(k => (JsonNode)k)]), ["people"] = new JsonArray([.. r.People.Select(p => (JsonNode)p)]),
        ["templateId"] = r.TemplateId ?? "",
    })]);
}
#endif
