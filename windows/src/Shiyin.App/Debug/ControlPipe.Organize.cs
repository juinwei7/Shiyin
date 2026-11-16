#if DEBUG
using System.Text.Json.Nodes;
using Shiyin.Model;
using Shiyin.Notes;
using Shiyin.Services;

namespace Shiyin.App.Debug;

/// <summary>
/// 控制通道：會議列表的排序、分組、篩選（M§71），資料夾自動歸類（M§72），定期會議（M§73）。名稱同 Mac 的 DebugControl；
/// Mac 寫到檔案的（foldersuggest、seriescandidates、seriesopenitems、previousmeeting）這裡直接回傳 JSON（design-windows §11.30～§11.32）
/// </summary>
public static partial class ControlPipe
{
    static JsonNode? Organize(AppHost host, MainWindow? main, string[] args)
    {
        if (args.Length == 0) return null;
        long? Id(int i) => args.Length > i && long.TryParse(args[i], out var v) ? v : null;
        var state = main?.State;
        switch (args[0])
        {
            // 移除一個人（R85.7、M§60.2，同 Mac）：personremove <id>（等於選了「移除這個人…」，打開確認）；
            // personrenameui <id>（等於選了「改名…」：選那個人、打開鉛筆下面的輸入框）
            case "personremove" when Id(1) is long removeId && main is not null:
                _ = main.ConfirmRemovePerson(removeId);
                return Ok;
            case "personrenameui" when Id(1) is long renameId && main is not null:
                main.RenamePersonUi(renameId);
                return Ok;
            // 每一種東西都能直接改、刪（R100、M§74，同 Mac 的名稱）：renamemeetingui <id>（等於側欄右鍵「重新命名…」、Enter／F2）；
            // folderdeleteui <名稱>、seriesdeleteui <id>（打開刪除的確認）；sidebarkey return|delete（等於在側欄的會議列表按 Enter、Delete）
            case "renamemeetingui" when Id(1) is long renameMeeting && main is not null:
                main.RenameMeetingUi(renameMeeting);
                return Ok;
            case "folderdeleteui" when args.Length > 1 && main is not null:
                _ = main.ConfirmDeleteFolder(args[1]);
                return Ok;
            case "seriesdeleteui" when Id(1) is long deleteSeries && main is not null:
                _ = main.ConfirmDeleteSeries(deleteSeries);
                return Ok;
            case "sidebarkey" when args.Length > 1 && main is not null:
                return new JsonObject { ["handled"] = args[1] == "return" ? main.SidebarRename() : main.SidebarDelete() };
            // 側欄的收合（R101、design-windows §11.34，同 Mac）：sidebarsection <folders|meetings|people|group:…> on|off（on 是展開）、
            // sidebarexpand <folder:名稱|series:id> on|off
            case "sidebarsection" when args.Length > 2:
                SidebarCollapse.SetSection(host.Settings, args[1], args[2] == "on");
                return Ok;
            case "sidebarexpand" when args.Length > 2:
                SidebarCollapse.SetRow(host.Settings, args[1], args[2] == "on");
                return Ok;
            // folder <會議 id> <資料夾|->：移到資料夾（- 是移出，同 Mac；準備示範資料用）
            case "folder" when args.Length > 2 && Id(1) is long folderMeeting:
                host.Controller.MoveToFolder(folderMeeting, args[2] == "-" ? "" : args[2]);
                return Ok;
            // seriesnew <名稱> <會議 id,…>：直接建一個定期會議（每週一 10:00，不經過建議；Windows 才有，準備示範資料用：建議要真的錄音的開始時間）
            case "seriesnew" when args.Length > 2:
            {
                var ids = args[2].Split(',').Select(x => long.TryParse(x, out var v) ? v : -1).Where(v => v > 0).ToList();
                var made = host.Db.CreateSeries(new MeetingSeries { Name = args[1], Weekday = 2, Minute = 600, People = [] }, ids);
                state?.Reload();
                return new JsonObject { ["id"] = made.Id };
            }
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
            case "bookmarkrenameui" when Id(1) is long uiBookmark && main?.State.Detail?.Bookmarks.FirstOrDefault(m => m.Id == uiBookmark) is { } shownBookmark:
                _ = Dialogs.RenameBookmark(main, shownBookmark);
                return Ok;
            case "termrename" when args.Length > 2 && Id(1) is long termId:
                host.Controller.RenameTerm(termId, args[2]);
                return Ok;
            // reload：直接改資料庫之後重讀
            case "reload":
                state?.Reload();
                return Ok;
            // listsort <dateDesc|dateAsc|title|duration>、listgroup <none|month|folder|series>
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
            // folderrule <資料夾> <JSON|JSON 檔的路徑|-> [apply]：等於在條件的編輯按「儲存」（- 是移除）。
            // JSON 是 {"keywords":[…],"people":[id…],"templateId":…,"seriesId":…}；從 SSH 傳中文不方便時給檔案路徑
            case "folderrule" when args.Length > 2:
            {
                var rule = new FolderRule { Folder = args[1] };
                if (args[2] != "-")
                {
                    var json = File.Exists(args[2]) ? File.ReadAllText(args[2]) : args[2];
                    if (JsonNode.Parse(json) is JsonObject o)
                    {
                        rule.Keywords = o["keywords"]?.AsArray().Select(x => x!.GetValue<string>()).ToList() ?? [];
                        rule.People = o["people"]?.AsArray().Select(x => x!.GetValue<long>()).ToList() ?? [];
                        rule.TemplateId = o["templateId"]?.GetValue<string>();
                        rule.SeriesId = o["seriesId"]?.GetValue<long>();
                    }
                }
                host.Controller.SaveFolderRule(rule, args.Length > 3 && args[3] == "apply");
                state?.Reload();
                return Ok;
            }
            // editfolderrule <資料夾>：打開條件的編輯（等於資料夾右鍵「自動加入的條件…」）
            case "editfolderrule" when args.Length > 1 && main is not null:
                _ = Dialogs.FolderRule(main, args[1]);
                return Ok;
            // autofile <會議 id>：照資料夾的條件放（同轉錄完、整理完），回傳放進的資料夾
            case "autofile" when Id(1) is long fileId:
            {
                var folder = host.Db.AutoFile(fileId);
                if (folder is not null) host.Controller.Organized(fileId, folder, null);
                return new JsonObject { ["folder"] = folder };
            }
            // foldersuggest <會議 id>：紀錄分頁頂端的建議（沒有時 folder 是空字串）
            case "foldersuggest" when Id(1) is long suggestId && state is not null:
            {
                var meeting = host.Db.Meeting(suggestId);
                var s = meeting is null ? null : Organizing.Suggestion(meeting, state.Meetings, state.MeetingPeople, host.Recorder.MeetingId);
                var reason = new JsonObject();
                if (s?.People is { } people) reason["people"] = new JsonArray([.. people.Select(p => (JsonNode)p)]);
                if (s?.Title is { } word) reason["title"] = word;
                return new JsonObject
                {
                    ["folder"] = s?.Folder ?? "", ["count"] = s?.Count ?? 0, ["reason"] = reason,
                    ["text"] = s is null ? "" : Organizing.SuggestionText(s, state.People),
                };
            }
            // foldersuggestion accept|dismiss <會議 id>：等於按「放進去」「不用」
            case "foldersuggestion" when args.Length > 2 && Id(2) is long accepted && state is not null:
            {
                if (args[1] == "accept" && host.Db.Meeting(accepted) is { } meeting
                    && Organizing.Suggestion(meeting, state.Meetings, state.MeetingPeople, host.Recorder.MeetingId) is { } s)
                    host.Controller.AcceptFolderSuggestion(accepted, s.Folder);
                else if (args[1] == "dismiss") host.Controller.DismissFolderSuggestion(accepted);
                state.Reload();
                return Ok;
            }
            // seriessuggest <會議 id>：紀錄分頁頂端建議加入的定期會議（R99.3；沒有時 series 是 -1、text 是空字串）
            case "seriessuggest" when Id(1) is long joinSuggestId && state is not null:
            {
                var s = host.Db.Meeting(joinSuggestId) is { } meeting ? state.SeriesJoinSuggestion(meeting) : null;
                return new JsonObject
                {
                    ["series"] = s?.Series.Id ?? -1, ["shared"] = new JsonArray([.. (s?.Shared ?? []).Select(p => (JsonNode)p)]),
                    ["text"] = s is null ? "" : Organizing.JoinSuggestionText(s, state.People),
                };
            }
            // seriessuggestion accept|dismiss <會議 id>：等於按「加進去」「不是」
            case "seriessuggestion" when args.Length > 2 && Id(2) is long suggestedId && state is not null:
            {
                if (host.Db.Meeting(suggestedId) is not { } meeting || state.SeriesJoinSuggestion(meeting) is not { } s) return new JsonObject { ["error"] = "no suggestion" };
                if (args[1] == "accept") host.Controller.AcceptSeriesJoin(suggestedId, s.Series.Id!.Value);
                else if (args[1] == "dismiss") host.Controller.DismissSeriesJoin(suggestedId, s.Series.Id!.Value);
                state.Reload();
                return Ok;
            }
            // seriescandidates：可能的定期會議
            case "seriescandidates" when state is not null:
                return new JsonObject
                {
                    ["candidates"] = new JsonArray([.. state.SeriesCandidates.Select(c => (JsonNode)new JsonObject
                    {
                        ["key"] = c.Key, ["name"] = c.Name, ["meetingIds"] = new JsonArray([.. c.MeetingIds.Select(x => (JsonNode)x)]), ["weekday"] = c.Weekday,
                        ["minute"] = c.Minute ?? -1, ["intervalWeeks"] = c.IntervalWeeks, ["people"] = new JsonArray([.. c.People.Select(x => (JsonNode)x)]),
                        ["frequency"] = c.FrequencyText,
                    })]),
                };
            // seriescreate <候選的 key> <名稱|-> [會議 id,…]：等於確認時按「建立」
            case "seriescreate" when args.Length > 2 && state is not null:
            {
                if (state.SeriesCandidates.FirstOrDefault(c => c.Key == args[1]) is not { } candidate) return new JsonObject { ["error"] = "no candidate" };
                var ids = args.Length > 3 ? args[3].Split(',').Select(x => long.TryParse(x, out var v) ? v : -1).Where(v => v > 0).ToList() : candidate.MeetingIds;
                var created = host.Controller.CreateSeries(candidate, args[2] == "-" ? "" : args[2], ids);
                state.Reload();
                return new JsonObject { ["id"] = created?.Id };
            }
            // seriesreject <key>：等於按「不是定期會議」
            case "seriesreject" when args.Length > 1 && state is not null:
                if (state.SeriesCandidates.FirstOrDefault(c => c.Key == args[1]) is { } rejected) host.Controller.RejectSeriesCandidate(rejected);
                state.Reload();
                return Ok;
            // seriesjoin <會議 id> <系列 id|->：右鍵「加到定期會議 ▸」「移出「…」」
            case "seriesjoin" when args.Length > 2 && Id(1) is long joinId:
                host.Controller.SetSeries([joinId], Id(2));
                state?.Reload();
                return Ok;
            // seriesfilter <系列 id|->：側欄點了定期會議（- 回到所有會議）
            case "seriesfilter" when args.Length > 1 && state is not null:
                if (Id(1) is long seriesId) state.ShowSeries(seriesId); else state.ShowMeetings(null);
                return Ok;
            case "seriesrename" when args.Length > 2 && Id(1) is long renameId:
                host.Controller.RenameSeries(renameId, args[2]);
                state?.Reload();
                return Ok;
            case "seriesdelete" when Id(1) is long deleteId:
                host.Controller.DeleteSeries(deleteId);
                state?.Reload();
                return Ok;
            // autojoin <會議 id>：轉錄完的自動加入定期會議，回傳加入的系列
            case "autojoin" when Id(1) is long autoId:
            {
                var series = host.Db.AutoJoinSeries(autoId);
                if (series is not null) host.Controller.Organized(autoId, null, series);
                return new JsonObject { ["series"] = series?.Id };
            }
            // seriessheet：打開可能的定期會議的確認（等於點側欄「發現 N 組…」）
            case "seriessheet" when main is not null:
                _ = Dialogs.SeriesSuggestions(main);
                return Ok;
            // seriesopenitems <系列 id>：還沒完成的待辦
            case "seriesopenitems" when Id(1) is long openId:
                return new JsonObject
                {
                    ["items"] = new JsonArray([.. Data.SeriesOpenItems.Compute(host.Db.SeriesMeetings(openId)).Select(i => (JsonNode)new JsonObject
                    {
                        ["meeting"] = i.Meeting.Id, ["index"] = i.Index, ["task"] = i.Item.Task, ["owner"] = i.Item.Owner,
                    })]),
                };
            // previousmeeting <會議 id>：整理時會接上的上一場（R99.5）和提示詞的那一段
            case "previousmeeting" when Id(1) is long previousId && host.Db.Meeting(previousId) is { } current:
            {
                var previous = host.Db.PreviousSeriesMeeting(current);
                var p = previous is null ? null : PreviousMeeting.Of(previous);
                return new JsonObject
                {
                    ["meeting"] = previous?.Id ?? -1, ["items"] = new JsonArray([.. (p?.Items ?? []).Select(i => (JsonNode)i.Task)]),
                    ["block"] = p is null ? "" : NotesPrompt.PreviousBlock(p, current.MeetingLanguage),
                };
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
        ["folder"] = r.Folder, ["keywords"] = new JsonArray([.. r.Keywords.Select(k => (JsonNode)k)]), ["people"] = new JsonArray([.. r.People.Select(p => (JsonNode)p)]),
        ["templateId"] = r.TemplateId ?? "", ["seriesId"] = r.SeriesId ?? -1,
    })]);

    static JsonNode SeriesState(MainWindow? main) => new JsonArray([.. (main?.State.Series ?? []).Select(x => (JsonNode)new JsonObject
    {
        ["id"] = x.Id, ["name"] = x.Name, ["weekday"] = x.Weekday, ["minute"] = x.Minute ?? -1, ["intervalWeeks"] = x.IntervalWeeks,
        ["people"] = new JsonArray([.. x.People.Select(p => (JsonNode)p)]), ["count"] = main!.State.SeriesCount(x.Id ?? 0),
    })]);
}
#endif
