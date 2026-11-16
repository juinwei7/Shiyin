using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Notes;
using Shiyin.Services;

namespace Shiyin.App;

/// <summary>資料夾的自動加入條件（R98、M§72、design-windows §11.31）。定期會議的確認（R99、M§73）D177 拿掉</summary>
public static partial class Dialogs
{
    /// <summary>DEBUG 的 state：條件的編輯開著時是哪個資料夾</summary>
    public static string? EditingFolderRule { get; private set; }

    /// <summary>
    /// 資料夾右鍵「自動加入的條件…」（同 Mac 的 FolderRuleSheet）：標題包含（一行，用、，, 或換行分隔）、這些人都參加（同一種框裡的勾選清單，最高 140）、
    /// 用這個範本；「現在不在資料夾裡的會議也套用（N 場）」跟著條件即時算，0 場時停用。
    /// 按鈕：左下「移除條件」（紅字，已經有條件時）、右下「取消」「儲存」（Enter；條件都空時當成移除）。樣子照 Sheet（R106.1）
    /// </summary>
    public static async Task FolderRule(MainWindow window, string folder)
    {
        if (window.XamlRoot is null) return;
        var host = AppHost.Current;
        var state = window.State;
        var existing = state.RuleFor(folder);
        var keywords = Sheet.TextField(string.Join(MeetingLanguages.Ui("、", ", "), existing?.Keywords ?? []), L.T("例如：週會、sync（任一個詞）"));
        AutomationProperties.SetName(keywords, L.T("標題包含"));
        var people = new HashSet<long>(existing?.People ?? []);
        string? templateId = existing?.TemplateId;
        var apply = new CheckBox();
        var matching = 0;
        Shiyin.Model.FolderRule Rule() => new()
        {
            Folder = folder, Keywords = GlossaryInput.Split(keywords.Text), People = [.. people.Order()], TemplateId = templateId,
        };
        void Update()
        {
            var rule = Rule();
            matching = rule.IsEmpty ? 0 : host.Db.MeetingsMatching(rule).Count;
            apply.Content = L.F("現在不在資料夾裡的會議也套用（%lld 場）", matching);
            apply.IsEnabled = matching > 0;
        }
        var heading = L.F("「%@」自動加入的條件", folder);
        var sheet = new SheetDialog(window, heading, Sheet.WideWidth, 16);
        var form = sheet.Form;
        form.Children.Add(Sheet.Header(heading, sheet.Cancel, Sheet.Subtitle(L.T("符合任一個條件的新會議，會自動放進這個資料夾。已經在別的資料夾的會議不會移動。"))));
        keywords.TextChanged += (_, _) => Update();
        form.Children.Add(Sheet.Labeled(L.T("標題包含"), keywords));
        if (state.People.Count > 0)
        {
            var list = new StackPanel { Spacing = 0 };
            foreach (var person in state.People)
            {
                var id = person.Id;
                var check = new CheckBox { Content = person.IsMe ? L.F("%@（我）", person.Name) : person.Name, IsChecked = people.Contains(id) };
                check.Checked += (_, _) => { people.Add(id); Update(); };
                check.Unchecked += (_, _) => { people.Remove(id); Update(); };
                list.Children.Add(check);
            }
            var scroll = new ScrollViewer { Content = list, MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            AutomationProperties.SetName(scroll, L.T("這些人都參加"));
            form.Children.Add(Sheet.Labeled(L.T("這些人都參加"), Sheet.Box(scroll)));
        }
        var template = Sheet.Labeled(L.T("用這個範本"), Sheet.Combo(Ui.Combo(new[] { ((string?)null, L.T("不限")) }.Concat(state.Templates.Select(t => ((string?)t.Id, t.DisplayName()))),
            templateId, v => { templateId = v; Update(); })));
        form.Children.Add(template);
        form.Children.Add(apply);
        Update();
        var result = 0;   // 1 儲存、2 移除條件
        var save = Sheet.Pill(L.T("儲存"), Sheet.Kind.Primary, click: () => { result = 1; sheet.Close(); });
        var remove = existing is null ? null : Sheet.DangerText(Sheet.Pill(L.T("移除條件"), click: () => { result = 2; sheet.Close(); }));
        form.Children.Add(Sheet.Buttons(remove, Sheet.Pill(L.T("取消"), click: sheet.Cancel), save));
        sheet.Submit = () => { result = 1; sheet.Close(); };
        sheet.Primary = save;
        sheet.Focus = keywords;
        sheet.SelectOnFocus = false;
        EditingFolderRule = folder;
        try { await sheet.Show(); }
        finally { EditingFolderRule = null; }
        if (result == 1) host.Controller.SaveFolderRule(Rule(), apply.IsChecked == true && matching > 0);
        else if (result == 2) host.Controller.SaveFolderRule(new Shiyin.Model.FolderRule { Folder = folder }, applyNow: false);
    }
}
