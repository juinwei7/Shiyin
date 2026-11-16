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
