using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shiyin.Data;
using Shiyin.LLM;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Notes;
using Shiyin.Platform;
using Shiyin.Services;
using Shiyin.Services.Capture;
using Shiyin.Transcribe;
using Shiyin.Whisper;

namespace Shiyin.App;

/// <summary>
/// 設定視窗（同 Mac 的 SettingsView，design-windows §12.8）：整理工具與金鑰、範本、詞彙表、說話者、錄音、模型與儲存空間、進階。
/// 設定存在 SHIYIN_HOME\settings.json，改了馬上生效
/// </summary>
public sealed class SettingsWindow : Window
{
    readonly AppHost host;
    readonly SelectorBar bar = new() { Margin = new Thickness(12, 8, 12, 0) };
    readonly Border page = new();
    string current = "provider";

    public SettingsWindow(AppHost host)
    {
        this.host = host;
        Title = L.T("設定");
        WindowPlacement.Center(this, 760, 640);
        WindowPlacement.MinimumSize(this, 600, 480);
        // 四頁（R44.5、M§33.3）：範本併進整理工具、模型併進錄音與轉錄、說話者和詞彙表合成一頁、儲存空間併進進階
        (string Id, string Label)[] pages =
        [
            ("provider", L.T("整理工具")), ("recording", L.T("錄音與轉錄")), ("names", L.T("說話者與詞彙")), ("advanced", L.T("進階")),
        ];
        foreach (var (id, label) in pages)
        {
            var item = new SelectorBarItem { Text = label, Tag = id };
            bar.Items.Add(item);
            if (id == current) bar.SelectedItem = item;
        }
        bar.SelectionChanged += (_, _) => { if (bar.SelectedItem?.Tag is string id) { current = id; Show(); } };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(bar);
        Grid.SetRow(page, 1);
        root.Children.Add(page);
        Content = Ui.WindowBackground(root);
        host.Models.Changed += _ => host.Post(() => { if (current == "recording") Show(); });
        // 企業伺服器的登入狀態變了（登入、失效、登出）
        host.Enterprise.Changed += () => host.Post(() => { if (current is "advanced" or "provider" or "recording") Show(); });
        Show();
    }

    /// <summary>重畫目前這一頁</summary>
    public void Refresh() => Show();

    /// <summary>DEBUG 的 settings 指令：直接打開某一頁；3.x 的頁名當別名（templates → 整理工具、glossary、speakers → 說話者與詞彙、storage → 進階）</summary>
    public void Open(string id)
    {
        id = id switch { "templates" => "provider", "glossary" or "speakers" => "names", "storage" => "advanced", _ => id };
        foreach (var item in bar.Items) if ((string)item.Tag == id) bar.SelectedItem = item;
    }

    // 每一頁都用卡片分組（同 Mac 設定的 Form）
    void Show() => page.Child = Ui.Scroll(current switch
    {
        "recording" => Recording(),
        "names" => Ui.Column(16, Section(MeetingLanguages.Ui("說話者", "Speakers"), Speakers()), Section(L.T("詞彙表"), Glossary())),
        "advanced" => Advanced(),
        // 範本是整理的一部分（R44.5、D110）：原本自己一頁
        _ => Ui.Column(16, Ui.Card(Provider()), Section(MeetingLanguages.Ui("範本", "Templates"), Templates())),
    }, 24);

    AppSettings S => host.Settings;

    static TextBlock Note(string text) => Ui.Text(text, 12, color: Ui.Secondary);

    static FrameworkElement Section(string title, params UIElement[] children)
    {
        var column = Ui.Column(10, Ui.Text(title, 15, bold: true));
        foreach (var c in children) column.Children.Add(c);
        return Ui.Card(column);
    }

    // MARK: 整理工具與金鑰（R6.1）

    FrameworkElement Provider()
    {
        var provider = S.Provider;
        var column = Ui.Column(14);
        // 選單顯示實際用的整理工具；這台電腦沒有 claude、codex 時多「之後再設定」，選它等於清掉設定（R35.3、M§19.6）
        // 沒安裝的 claude、codex 不能選（R45.3）；目前選的就是它時照樣顯示
        bool Missing(LLMProvider p) => p is LLMProvider.Claude or LLMProvider.Codex && !S.HasTool(p == LLMProvider.Claude ? "claude" : "codex");
        // 公司（企業伺服器）一直都在，沒登入就在這頁登入（R51.1、M D124）
        var options = LLMProviders.All
            .Select(p => ((LLMProvider?)p, Missing(p) ? L.F("%@（沒有安裝）", p.Label()) : p == LLMProvider.Enterprise ? CompanyLabel() : p.Label())).ToList();
        if (S.DetectedProvider == LLMProvider.None) options.Insert(0, (null, L.T("之後再設定（先不整理）")));
        column.Children.Add(Ui.Combo(options, S.NeedsProviderSetup ? null : provider, p =>
        {
            if (p is { } chosen) S.Set(AppSettings.ProviderKey, chosen.Code());
            else S.Remove(AppSettings.ProviderKey);
            Show();
        }, L.T("整理工具"), p => p is not { } item || !Missing(item) || item == provider));
        // 還沒準備好：說原因，設定好之前不會自動整理（R45.3、M§34.3）
        if (provider != LLMProvider.None && ProblemNotice(S.SummarizerProblem) is { } notice)
            column.Children.Add(Ui.Text(notice, 12, color: Ui.Danger));

        // 公司：沒登入就在這裡登入；登入後模型由公司的後台決定（R51.1、R51.5）
        if (provider == LLMProvider.Enterprise)
        {
            column.Children.Add(Enterprise(compact: true));
            if (host.Enterprise.IsSignedIn && host.Enterprise.Account?.Llm == true) column.Children.Add(CompanyModels());
            _ = host.Enterprise.RefreshInfo();
        }

        if (provider is LLMProvider.Claude or LLMProvider.Codex)
        {
            var status = Ui.Text(L.T("檢查登入狀態…"), 13, color: Ui.Secondary);
            var login = Ui.Button(L.F("登入 %@", provider.ShortLabel()), () => CliLogin.Open(host, provider));
            login.Visibility = Visibility.Collapsed;
            async void Check(bool userInitiated)
            {
                if (host.Tools.Find(provider == LLMProvider.Claude ? "claude" : "codex") is null)
                {
                    status.Text = provider == LLMProvider.Codex ? L.T("這台電腦沒有安裝 codex：沒有在用 Codex 的話，請改選 API")
                        : L.T("這台電腦沒有安裝 claude：沒有在用 Claude Code 的話，請改選 API");
                    return;
                }
                var result = await CliLogin.Refresh(provider);
                if (S.Provider != provider) return;   // 查的時候換了整理工具
                // 使用者按了「重新檢查」、查到已登入就測一次（R45.3）；打開設定頁時的自動檢查不測（不花額度）
                if (userInitiated && result is { LoggedIn: true }) { autoTest = true; Show(); return; }
                status.Text = result is null ? L.T("已安裝，看不出登入狀態") : result.LoggedIn ? L.F("已登入 %@", result.Detail) : L.T("沒有登入");
                status.Foreground = result is null ? Ui.Secondary : result.LoggedIn ? Ui.Celadon : Ui.Danger;
                login.Visibility = result is { LoggedIn: false } ? Visibility.Visible : Visibility.Collapsed;
            }
            Check(false);
            column.Children.Add(Ui.Row(8, Ui.Text(L.T("狀態"), 13), status, login, Ui.IconButton(Symbol.Refresh, L.T("重新檢查登入狀態"), () => Check(true))));
        }

        // OpenAI 相容 API 的網址（M§19.3）
        if (provider == LLMProvider.OpenAICompatible)
        {
            var url = new TextBox { Header = L.T("網址"), Text = S.CompatibleBaseUrl, PlaceholderText = "https://api.groq.com/openai/v1" };
            url.LostFocus += (_, _) => { S.Set(AppSettings.CompatibleBaseUrlKey, url.Text); Show(); };
            var presets = new DropDownButton { Content = L.T("常用"), VerticalAlignment = VerticalAlignment.Bottom };
            var menu = new MenuFlyout();
            foreach (var (name, address) in OpenAICompatibleApi.Presets)
                menu.Items.Add(Ui.Item($"{name}  {address}", () => { S.Set(AppSettings.CompatibleBaseUrlKey, address); Show(); }));
            presets.Flyout = menu;
            column.Children.Add(Ui.Bar(null, url, presets));
            if (S.CompatibleBaseUrl.Trim().Length > 0 && OpenAICompatibleApi.Normalized(S.CompatibleBaseUrl) is null)
                column.Children.Add(Ui.Text(L.T("網址要以 http:// 或 https:// 開頭"), 12, color: Ui.Danger));
        }

        if (provider.UsesApiKey())
        {
            // 只看有沒有存，不讀出 key（R18.7）
            var saved = host.Keys.Exists(provider.KeyAccount());
            var key = new PasswordBox
            {
                Header = "API key",
                PlaceholderText = saved ? L.T("已儲存在認證管理員（輸入新的會取代）") : provider.KeyOptional() ? L.T("可以不填（本機的 Ollama、LM Studio 不需要）") : L.T("貼上 API key"),
            };
            var error = Ui.Text("", 12, color: Ui.Danger);
            var buttons = Ui.Row(8, Ui.Button(L.T("儲存 key"), () =>
            {
                if (key.Password.Trim().Length == 0) return;
                try
                {
                    SummarizerSetup.SaveKey(host, provider.KeyAccount(), key.Password.Trim());
                    autoTest = S.SummarizerReady;   // 存好 key 就測一次（R45.3）
                    Show();
                }
                catch (InvalidOperationException e) { error.Text = e.Message; }
            }));
            if (saved)
                buttons.Children.Add(Ui.Button(L.T("刪除 key"), () =>
                {
                    try
                    {
                        SummarizerSetup.DeleteKey(host, provider.KeyAccount());
                    }
                    catch (InvalidOperationException e) { error.Text = e.Message; }
                    Show();
                }));
            column.Children.Add(Ui.Column(6, key, buttons, error));
            if (provider.ApiKeyUrl() is { } url)
                column.Children.Add(Ui.Link(L.T("取得 API key"), () => _ = Windows.System.Launcher.LaunchUriAsync(new Uri(url))));
        }

        if (provider != LLMProvider.None && provider != LLMProvider.Enterprise)
        {
            var model = new TextBox
            {
                Header = L.T("模型"), Text = S.String(provider.ModelKey()) ?? "",
                PlaceholderText = provider == LLMProvider.OpenAICompatible ? L.T("必填") : provider == LLMProvider.Enterprise ? L.T("留空使用公司設定的預設模型")
                    : provider.DefaultModel().Length == 0 ? L.T("留空使用預設模型") : L.F("預設：%@", provider.DefaultModel()),
            };
            model.TextChanged += (_, _) => S.Set(provider.ModelKey(), model.Text);
            var canList = provider == LLMProvider.OpenAICompatible ? OpenAICompatibleApi.Normalized(S.CompatibleBaseUrl) is not null
                : provider == LLMProvider.Enterprise ? host.Enterprise.IsSignedIn
                : provider.UsesApiKey() && host.Keys.Exists(provider.KeyAccount());
            if (canList)
            {
                var pick = new DropDownButton { Content = L.T("選擇"), VerticalAlignment = VerticalAlignment.Bottom };
                var menu = new MenuFlyout();
                menu.Items.Add(new MenuFlyoutItem { Text = L.T("正在取得模型清單…"), IsEnabled = false });
                pick.Flyout = menu;
                menu.Opening += async (_, _) =>
                {
                    var models = await host.Llm.ListModels(provider, CancellationToken.None);
                    menu.Items.Clear();
                    if (models.Count == 0) menu.Items.Add(new MenuFlyoutItem { Text = L.T("取得不到模型清單，可以直接輸入模型名稱。"), IsEnabled = false });
                    foreach (var name in models) menu.Items.Add(Ui.Item(name, () => model.Text = name));
                };
                column.Children.Add(Ui.Bar(null, model, pick));
            }
            else column.Children.Add(model);
        }
        if (provider != LLMProvider.None && (provider != LLMProvider.Enterprise || host.Enterprise.IsSignedIn))
        {
            // 測試：送一個很短的請求，確認登入、key、模型都能用
            var result = Ui.Text("", 13);
            var test = Ui.Button(L.T("測試整理工具"), () => { });
            async void RunTest()
            {
                test.IsEnabled = false;
                test.Content = L.T("測試中…");
                try
                {
                    var seconds = await SummarizerSetup.Test(host, provider, S.Model);
                    result.Text = L.F("可以使用（%lld 秒）", seconds);
                    result.Foreground = Ui.Celadon;
                }
                catch (Exception e) when (SummarizerSetup.IsTestFailure(e))
                {
                    result.Text = e.Message;
                    result.Foreground = Ui.Danger;
                }
                test.IsEnabled = true;
                test.Content = L.T("測試整理工具");
            }
            test.Click += (_, _) => RunTest();
            column.Children.Add(Ui.Row(10, test, result));
            if (autoTest) { autoTest = false; RunTest(); }
        }
        var auto = Ui.Toggle(L.T("轉錄完成後自動整理成會議紀錄"), S.AutoSummarize, on => S.Set(AppSettings.AutoSummarizeKey, on));
        auto.IsEnabled = provider != LLMProvider.None;
        var correct = Ui.Toggle(L.T("轉錄完成後檢查可能聽錯的字"), S.CorrectTranscript, on => S.Set(AppSettings.CorrectTranscriptKey, on));
        correct.IsEnabled = provider != LLMProvider.None;
        column.Children.Add(auto);
        column.Children.Add(correct);
        column.Children.Add(Note(L.T("依詞彙表和上下文找出可能聽錯的字，標在逐字稿裡，你決定要不要改。不會自動改掉原文。")));
        column.Children.Add(Ui.Card(Ui.Text(Privacy(provider), 13, color: Ui.Secondary)));
        return column;
    }

    /// <summary>存好 key、按「重新檢查」查到已登入之後，重畫時自動測一次（R45.3）</summary>
    bool autoTest;

    /// <summary>選單上的「公司（範例公司）」；沒登入時「公司（企業伺服器）」</summary>
    string CompanyLabel() => host.Enterprise.Account?.ServerName is { Length: > 0 } name ? L.F("公司（%@）", name) : L.T("公司（企業伺服器）");

    /// <summary>
    /// 公司提供的模型（R51.1、M§36.3）：「公司的預設」存空字串；只有一個時只顯示名稱。
    /// 選過的不在清單裡了就當成預設，設定不清掉（伺服器恢復後照舊）
    /// </summary>
    FrameworkElement CompanyModels()
    {
        var slot = Ui.Column(6);
        slot.Children.Add(Ui.Bar(Ui.Text(L.T("模型"), 14), null, Ui.Text(L.T("正在取得公司的模型…"), 14, color: Ui.Secondary)));
        slot.Children.Add(Note(L.T("模型和 API key 由公司在伺服器的後台設定。")));
        async void Load()
        {
            var models = await host.Llm.EnterpriseModels(CancellationToken.None);
            if (S.Provider != LLMProvider.Enterprise) return;
            slot.Children.RemoveAt(0);
            FrameworkElement row;
            if (models is not { Count: > 0 })
                row = Ui.Bar(Ui.Text(L.T("模型"), 14), null, Ui.Text(L.T("公司的預設"), 14, color: Ui.Secondary));
            else
            {
                var fallback = models.FirstOrDefault(m => m.IsDefault) ?? models[0];
                if (models.Count == 1) row = Ui.Bar(Ui.Text(L.T("模型"), 14), null, Ui.Text(fallback.Name, 14, color: Ui.Secondary));
                else
                {
                    var options = new List<(string, string)> { ("", L.F("公司的預設（%@）", fallback.Name)) };
                    options.AddRange(models.Select(m => (m.Id, m.Name)));
                    var chosen = S.String(LLMProvider.Enterprise.ModelKey()) ?? "";
                    row = Ui.Combo(options, models.Any(m => m.Id == chosen) ? chosen : "", id => S.Set(LLMProvider.Enterprise.ModelKey(), id), L.T("模型"));
                }
            }
            slot.Children.Insert(0, row);
        }
        Load();
        return slot;
    }

    /// <summary>選單下面的紅字（M§34.3）：沒安裝、沒登入在「狀態」那一列已經寫了原因</summary>
    string? ProblemNotice(SummarizerProblem? problem) => problem?.Kind switch
    {
        null or SummarizerProblemKind.NotChosen or SummarizerProblemKind.Off => null,
        SummarizerProblemKind.NotInstalled or SummarizerProblemKind.NotLoggedIn => L.T("設定好之前不會自動整理。"),
        SummarizerProblemKind.MissingKey => L.T("還沒有存 API key：貼上之後按「儲存 key」。設定好之前不會自動整理。"),
        SummarizerProblemKind.Incomplete => L.T("還沒有填網址或模型。設定好之前不會自動整理。"),
        // 還沒登入時同一頁就有網址和「登入」，不用再說一次
        SummarizerProblemKind.EnterpriseSignedOut => host.Enterprise.HasAccount ? L.T("公司的伺服器登入已失效：按「重新登入」。設定好之前不會自動整理。") : null,
        SummarizerProblemKind.EnterpriseNoModels => L.T("公司的伺服器還沒有提供整理用的模型：請管理者在伺服器後台的「整理模型」加上模型。"),
        _ => L.F("上次整理失敗：%@。改好設定，或按「測試整理工具」成功之後，才會再自動整理。", problem!.Detail.TrimEnd('。', '.', ' ')),
    };

    string Privacy(LLMProvider provider) => provider switch
    {
        LLMProvider.None => S.NeedsProviderSetup
            ? L.T("還沒設定整理工具：錄音、轉錄、逐字稿全部留在這台電腦。選好整理工具之後，才會整理成會議紀錄。")
            : L.T("目前不整理：錄音、轉錄、逐字稿全部留在這台電腦。"),
        LLMProvider.Claude or LLMProvider.Anthropic => L.T("整理、提問、猜說話者時會把逐字稿文字、你的筆記、詞彙表和會議基本資料送到 Anthropic（Claude）。錄音檔不會送出。"),
        LLMProvider.Codex or LLMProvider.OpenAI => L.T("整理、提問、猜說話者時會把逐字稿文字、你的筆記、詞彙表和會議基本資料送到 OpenAI。錄音檔不會送出。"),
        LLMProvider.Gemini => L.T("整理、提問、猜說話者時會把逐字稿文字、你的筆記、詞彙表和會議基本資料送到 Google（Gemini）。錄音檔不會送出。"),
        // 送到公司的伺服器，再由公司設定的模型整理（R51.4、M§36.5）
        LLMProvider.Enterprise => host.Enterprise.Host.Length == 0
            ? L.T("整理、提問、猜說話者時會把逐字稿文字、你的筆記、詞彙表和會議基本資料送到公司的伺服器，再由公司設定的模型整理。錄音檔不會因為整理而送出。")
            : L.F("整理、提問、猜說話者時會把逐字稿文字、你的筆記、詞彙表和會議基本資料送到 %@（公司的伺服器），再由公司設定的模型整理。錄音檔不會因為整理而送出。", host.Enterprise.Host),
        _ => OpenAICompatibleApi.Normalized(S.CompatibleBaseUrl) is not { } url
            ? L.T("整理、提問、猜說話者時會把逐字稿文字、你的筆記、詞彙表和會議基本資料送到你填的網址。錄音檔不會送出。")
            : OpenAICompatibleApi.IsLocal(url)
                ? L.F("整理、提問、猜說話者時會把逐字稿文字、你的筆記、詞彙表和會議基本資料送到這台電腦上的服務（%@），不會離開電腦。錄音檔不會送出。", url.Host)
                : url.Scheme == "http"
                    ? L.F("整理、提問、猜說話者時會把逐字稿文字、你的筆記、詞彙表和會議基本資料送到 %@。錄音檔不會送出。用 http 傳送沒有加密，建議改用 https。", url.Host)
                    : L.F("整理、提問、猜說話者時會把逐字稿文字、你的筆記、詞彙表和會議基本資料送到 %@。錄音檔不會送出。", url.Host),
    };

    // MARK: 範本（R6.2）

    string? templateId;

    FrameworkElement Templates()
    {
        var templates = host.Db.Templates();
        templateId ??= templates.FirstOrDefault()?.Id;
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, Width = 200 };
        foreach (var t in templates)
        {
            var item = new ListViewItem { Tag = t.Id, Content = Ui.Bar(Ui.Text(t.DisplayName(), 14, wrap: false), null, t.Id == S.DefaultTemplate ? Ui.Caption(L.T("預設")) : null) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, t.DisplayName());
            list.Items.Add(item);
            if (t.Id == templateId) list.SelectedItem = item;
        }
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is ListViewItem { Tag: string id }) { templateId = id; Show(); } };
        var create = Ui.Button(L.T("新增"), () => CreateTemplate(null, null));
        var copy = Ui.Button(L.T("複製"), () => CreateTemplate(templates.FirstOrDefault(t => t.Id == templateId), null));
        var left = Ui.Column(8, list, Ui.Row(8, create, copy));
        var right = templates.FirstOrDefault(t => t.Id == templateId) is { } template ? TemplateEditor(template) : Ui.Text(L.T("選一個範本"), color: Ui.Secondary);
        return Ui.Split(left, right, 16);
    }

    void CreateTemplate(TemplateRecord? source, TemplateDefinition? edited)
    {
        var id = "custom-" + Guid.NewGuid().ToString("N")[..8];
        var definition = edited ?? source?.DefinitionFor(MeetingLanguages.Interface) ?? new TemplateDefinition("", ["summary", "decisions", "actionItems"], [], "");
        host.Controller.SaveTemplate(new TemplateRecord
        {
            Id = id, Name = source is null ? L.T("新範本") : L.F("%@（副本）", source.DisplayName()), Builtin = false, Definition = definition.JsonString(),
        });
        templateId = id;
        Show();
    }

    FrameworkElement TemplateEditor(TemplateRecord template)
    {
        var definition = template.DefinitionFor(MeetingLanguages.Interface);
        var sections = definition.Sections.ToList();
        var custom = definition.Custom.ToList();
        var name = new TextBox { Header = L.T("名稱"), Text = template.DisplayName(), IsEnabled = !template.Builtin };
        // AcceptsReturn 要在 Text 之前設：單行的 TextBox 設 Text 時只留第一行（多行的說明會被截掉，存檔就不見了）
        var description = new TextBox { Header = L.T("用途說明"), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Text = definition.Description, IsEnabled = !template.Builtin };
        var instruction = new TextBox { Header = L.T("額外指示（選填）"), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Text = definition.Instruction, IsEnabled = !template.Builtin };
        var column = Ui.Column(12, name, description);
        var toggles = Ui.Column(4, Ui.Text(L.T("要產生的段落"), 14, bold: true));
        foreach (var id in TemplateDefinition.BuiltinSectionIds)
        {
            var box = new CheckBox { Content = TemplateDefinition.SectionLabel(id, MeetingLanguages.Interface), IsChecked = sections.Contains(id), IsEnabled = !template.Builtin };
            box.Click += (_, _) => { if (box.IsChecked == true) { if (!sections.Contains(id)) sections.Add(id); } else sections.Remove(id); };
            toggles.Children.Add(box);
        }
        column.Children.Add(toggles);
        var customs = Ui.Column(8, Ui.Text(L.T("自訂段落"), 14, bold: true));
        for (var i = 0; i < custom.Count; i++)
        {
            var index = i;
            var title = new TextBox { PlaceholderText = L.T("段落名稱"), Text = custom[i].Title, IsEnabled = !template.Builtin };
            title.TextChanged += (_, _) => custom[index] = custom[index] with { Title = title.Text };
            var what = new TextBox { PlaceholderText = L.T("要寫什麼"), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Text = custom[i].Instruction, IsEnabled = !template.Builtin };
            what.TextChanged += (_, _) => custom[index] = custom[index] with { Instruction = Lines(what.Text) };
            var delete = Ui.IconButton(Symbol.Delete, L.T("刪除"), () => { custom.RemoveAt(index); Save(); Show(); });
            delete.IsEnabled = !template.Builtin;
            customs.Children.Add(Ui.Bar(null, Ui.Column(4, title, what), delete));
        }
        if (!template.Builtin) customs.Children.Add(Ui.Link(L.T("新增自訂段落"), () => { custom.Add(new TemplateDefinition.CustomSpec("", "")); Save(); Show(); }));
        column.Children.Add(customs);
        column.Children.Add(instruction);
        void Save()
        {
            if (template.Builtin) return;
            var updated = new TemplateDefinition(Lines(description.Text), sections, custom, Lines(instruction.Text));
            host.Controller.SaveTemplate(new TemplateRecord
            {
                Id = template.Id, Name = name.Text.Length == 0 ? L.T("未命名範本") : name.Text, Builtin = false, Definition = updated.JsonString(),
            });
        }
        // 說明放中間那欄（長的會換行），按鈕照內容寬放右邊：都放在橫的 StackPanel 裡時，英文的按鈕會被擠出視窗（2026-10-22 介面巡檢）
        var actions = Ui.Row(8);
        var setDefault = Ui.Button(L.T("設為預設範本"), () => { S.Set(AppSettings.DefaultTemplateKey, template.Id); Show(); });
        setDefault.IsEnabled = S.DefaultTemplate != template.Id;
        actions.Children.Add(setDefault);
        if (!template.Builtin)
        {
            actions.Children.Add(Ui.Button(L.T("刪除"), () =>
            {
                // 刪掉的是預設範本的話，預設改回一般會議，不然新會議會指到不存在的範本
                if (S.DefaultTemplate == template.Id) S.Set(AppSettings.DefaultTemplateKey, "general");
                host.Controller.DeleteTemplate(template.Id);
                templateId = null;
                Show();
            }));
            actions.Children.Add(Ui.Button(L.T("儲存"), () => { Save(); Show(); }, accent: true));
        }
        var note = template.Builtin ? Note(L.T("內建範本不能修改，可以按「複製」做一份自己的。")) : null;
        if (note is not null) note.VerticalAlignment = VerticalAlignment.Center;
        column.Children.Add(Ui.Bar(null, note, actions, 12));
        return column;
    }

    /// <summary>WinUI 的 TextBox 把換行存成 \r：範本會放進提示詞，跟 Mac 一樣存成 \n</summary>
    static string Lines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    // MARK: 詞彙表

    FrameworkElement Glossary()
    {
        var column = Ui.Column(12, Ui.Text(L.T("系統名、產品名、人名的正確寫法。轉錄時會當成提示（最多約 150 字），整理時也會參考。實驗顯示，有沒有詞彙提示，專有名詞的正確率差很多。"), 13, color: Ui.Secondary));
        var input = new TextBox { PlaceholderText = L.T("新增詞彙，例如產品名或人名") };
        void Add()
        {
            if (input.Text.Trim().Length == 0) return;
            host.Controller.AddTerm(input.Text.Trim());
            Show();
        }
        input.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) Add(); };
        column.Children.Add(Ui.Bar(null, input, Ui.Button(L.T("新增"), Add)));
        var suggestions = host.Db.GlossarySuggestions();
        if (suggestions.Count > 0)
        {
            var list = Ui.Column(6, Ui.Caption(L.T("建議加入：從參加者、說話者名字、校正過的字、會議標題找到的")));
            foreach (var s in suggestions)
                list.Children.Add(Ui.Bar(Ui.Row(8, Ui.Text(s.Term, 14), Ui.Caption(s.SourceLabel)), null,
                    Ui.Row(8, Ui.Link(L.T("忽略"), () => { host.Controller.DismissGlossarySuggestion(s.Term); Show(); }), Ui.Link(L.T("加入"), () => { host.Controller.AddTerm(s.Term); Show(); }))));
            column.Children.Add(Ui.Card(new ScrollViewer { Content = list, MaxHeight = 160 }));
        }
        var terms = host.Db.Glossary();
        if (terms.Count == 0) column.Children.Add(Ui.Text(L.T("還沒有詞彙。用詞修正時勾選「加進詞彙表」也會加到這裡。"), 13, color: Ui.Secondary));
        foreach (var term in terms)
            column.Children.Add(Ui.Bar(null, Ui.Text(term.Term, 14), Ui.IconButton(Symbol.Delete, L.F("刪除「%@」", term.Term), () => { host.Controller.DeleteTerm(term.Id!.Value); Show(); })));
        var prompt = Ui.Text(L.F("目前的轉錄提示：%@", WhisperRules.Prompt(terms.Select(t => t.Term).ToList(), S.DefaultMeetingLanguage)), 12, color: Ui.Tertiary, select: true);
        column.Children.Add(prompt);
        return column;
    }

    // MARK: 說話者（M§21.4、R19.3）

    FrameworkElement Speakers()
    {
        var column = Ui.Column(12);
        column.Children.Add(Ui.Toggle(L.T("記住說話者的聲音，之後的會議自動帶入名字"), S.Voiceprints, on => S.Set(AppSettings.VoiceprintsKey, on)));
        column.Children.Add(Ui.Text(L.T("替說話者取名時，拾音會記下這個人的聲音特徵。之後的會議認出同一個人，就自動帶入名字並標「自動」，不對可以改。聲音特徵只存在這台電腦的資料庫，不會上傳，也不會放進匯出的 .shiyin 封包。"), 13, color: Ui.Secondary));
        var people = host.Db.PeopleSummaries();
        var deleteAll = Ui.Button(L.T("全部刪除…"), () => { });
        deleteAll.IsEnabled = people.Count > 0;
        deleteAll.Click += async (_, _) =>
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot, Title = Ui.DialogTitle(L.T("刪除所有認識的人的聲音特徵？")), Content = L.T("已經帶入的名字會留著，之後的會議不會再自動帶入。"),
                PrimaryButtonText = L.T("全部刪除"), CloseButtonText = L.T("取消"),
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) { host.Controller.DeleteAllPeople(); Show(); }
        };
        column.Children.Add(Ui.Bar(Ui.Text(L.T("認識的人"), 15, bold: true), null, deleteAll));
        if (people.Count == 0) column.Children.Add(Ui.Text(L.T("還沒有認識的人。在逐字稿點「說話者 1」改成名字，拾音就會記住這個人。"), 13, color: Ui.Secondary));
        foreach (var person in people)
        {
            var prints = L.F("%lld 份聲音特徵", person.VoiceprintCount);
            var detail = person.LastMeetingTitle is { } title && person.LastMeetingDate is { } date ? L.F("%@・最近：%@ %@", prints, date, title) : prints;
            var rename = Ui.Link(L.T("改名"), () => { });
            rename.Click += async (_, _) =>
            {
                var box = new TextBox { Text = person.Name };
                var dialog = new ContentDialog { XamlRoot = Content.XamlRoot, Title = Ui.DialogTitle(L.T("改名")), Content = box, PrimaryButtonText = L.T("儲存"), CloseButtonText = L.T("取消") };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary && box.Text.Trim().Length > 0) { host.Controller.RenamePerson(person.Id, box.Text.Trim()); Show(); }
            };
            column.Children.Add(Ui.Bar(null, Ui.Column(2, Ui.Text(person.Name, 14), Ui.Caption(detail)),
                Ui.Row(8, rename, Ui.IconButton(Symbol.Delete, L.F("刪除「%@」的聲音特徵（已經帶入的名字留著）", person.Name), () => { host.Controller.DeletePerson(person.Id); Show(); }))));
        }
        return column;
    }

    // MARK: 錄音與轉錄（R1、R4、R44.5）：錄音來源、語言、轉錄（含模型）、提醒與快捷鍵四組

    FrameworkElement Recording()
    {
        var source = Ui.Column(12);
        source.Children.Add(Ui.Combo(new[] { "mic", "both", "system" }.Select(s => (s, MainWindow.SourceLabel(s))), S.RecordSource,
            s => { S.Set(AppSettings.RecordSourceKey, s); Show(); }, L.T("錄音來源")));
        List<(string Id, string Name)> microphones;
        try { microphones = WasapiSource.Microphones(); }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException) { microphones = []; }
        var mic = Ui.Combo(new[] { ("", L.T("系統預設")) }.Concat(microphones), S.InputDevice, id => S.Set(AppSettings.InputDeviceKey, id), L.T("麥克風"));
        mic.IsEnabled = S.RecordSource != "system";
        source.Children.Add(mic);
        source.Children.Add(Note(L.T("錄線上會議時選「麥克風＋電腦聲音」：你的聲音和對方的聲音會分別錄在左右聲道。")));
        source.Children.Add(Ui.Button(L.T("麥克風權限設定"), Shell.OpenMicrophonePrivacy));

        var language = Ui.Column(12);
        language.Children.Add(Ui.Combo(MeetingLanguages.All.Select(l => (l, l.Name())), S.DefaultMeetingLanguage, l => S.Set(AppSettings.DefaultMeetingLanguageKey, l.Code()), L.T("預設會議語言")));
        language.Children.Add(Note(L.T("新的錄音和匯入預設用這個語言轉錄、整理會議紀錄；錄音選單、匯入時和會議頁都可以改。")));
        language.Children.Add(Ui.Toggle(L.T("自動偵測會議語言"), S.AutoDetectLanguage, on =>
        {
            S.Set(AppSettings.AutoDetectLanguageKey, on);
            if (on) host.Models.AutoStart(ModelStore.Model.LanguageDetection);
            Show();
        }));
        if (S.AutoDetectLanguage && !host.Models.IsInstalled(ModelStore.Model.LanguageDetection))
        {
            var busy = host.Models.StateOf(ModelStore.Model.LanguageDetection).IsBusy;
            language.Children.Add(Ui.Bar(null, Ui.Text(busy ? L.T("正在下載語言偵測模型…") : L.T("語言偵測模型還沒下載，下載好之前錄音不會自動偵測"), 12, color: Ui.Signal),
                busy ? null : Ui.Button(L.T("下載"), () => host.Models.Start(ModelStore.Model.LanguageDetection))));
        }
        language.Children.Add(Note(L.T("開始錄音後聽約 10 秒，判斷是中文、英文還是日文；跟會議語言不同而且有把握時會自動切換，錄音畫面可以改回來。需要 60 MB 的語言偵測模型。")));

        var transcription = Ui.Column(12);
        // 正式轉錄引擎（R4.1、M§32.3；Qwen3-ASR 見 design-windows §8.4）
        var engine = S.TranscriptionEngine;
        var engines = new List<(string, string)>
        {
            ("breeze", L.T("Breeze ASR 25（聯發科，建議）")), ("qwen", L.T("Qwen3-ASR 1.7B（阿里巴巴）")), ("system", L.T("快速辨識")),
        };
        // 企業伺服器（R50.1）：登入過才有
        if (host.Enterprise.Account is { } account) engines.Insert(0, ("server", L.F("企業伺服器（%@）", account.ServerName)));
        transcription.Children.Add(Ui.Combo(engines, engine is "qwen" or "system" or "server" ? engine : "breeze", e =>
        {
            S.Set(AppSettings.TranscriptionEngineKey, e);
            if (e == "qwen") host.Models.AutoStart(ModelStore.Model.Qwen);
            host.Gpu.OfferQwenCuda();
            host.Runner.Wake();
            Show();
        }, L.T("正式轉錄")));
        transcription.Children.Add(Note(engine switch
        {
            "server" => L.F("錄音會送到 %@（公司的伺服器）轉錄，轉完就刪；說話者辨識也在伺服器上做，聲紋只存在這台電腦。會參考詞彙表。連不上時會等，也可以改在這台電腦轉錄。", host.Enterprise.Host),
            "system" => L.T("速度快很多，下載也小；但不參考詞彙表，英文專有名詞、人名比較容易錯。"),
            "qwen" => L.T("會參考詞彙表，多人會議的漏字和同音字比 Breeze 少；阿里巴巴開源的模型，全程在這台電腦上跑、不連網，輸出會轉成台灣繁體；需要下載約 2.5 GB 的模型和 llama.cpp 的執行檔。日文會議改用 Whisper large-v3-turbo（574 MB）。"),
            _ => L.T("會參考詞彙表，中英夾雜、專有名詞比較準；聯發科開源的台灣模型，需要下載約 1.7 GB。日文會議改用 Whisper large-v3-turbo（574 MB）。"),
        }));
        transcription.Children.Add(Ui.Toggle(L.T("轉錄時辨識說話者"), S.Diarize, on => S.Set(AppSettings.DiarizeKey, on)));
        transcription.Children.Add(Ui.Toggle(L.T("錄音中顯示即時逐字稿"), S.LiveTranscription, on => S.Set(AppSettings.LiveTranscriptionKey, on)));
        // 模型清單（原「模型與儲存空間」頁，R44.5）
        transcription.Children.Add(ModelRows());

        var alerts = Ui.Column(12);
        alerts.Children.Add(Ui.Toggle(L.T("偵測到線上會議時提醒錄音"), S.MeetingDetection, on => S.Set(AppSettings.MeetingDetectionKey, on)));
        // 忘了停止錄音（M§35、R48.5）
        alerts.Children.Add(Ui.Toggle(L.T("很久沒人說話時提醒，30 分鐘後自動停止"), S.IdleStop, on => S.Set(AppSettings.IdleStopKey, on)));
        alerts.Children.Add(Note(L.T("錄音中 15 分鐘沒人說話時提醒你；再 15 分鐘還是沒人說話就自動停止，只轉錄到最後有人說話的地方，錄音檔完整保留。")));
        alerts.Children.Add(Note(L.T("Zoom、Teams、Webex、Google Meet（瀏覽器）等開始用麥克風、拾音沒在錄音時提醒你；會議結束時提醒停止。只提醒，不會自動錄音。")));
        foreach (var app in host.Detector.Muted)
            alerts.Children.Add(Ui.Bar(null, Ui.Text(L.F("不提醒：%@", MeetingDetector.DisplayName(app)), 13), Ui.Link(L.T("恢復提醒"), () => { host.Detector.Unmute(app); Show(); })));
        alerts.Children.Add(Ui.Toggle(L.T("全域快捷鍵 Ctrl+Alt+Shift+R：在其他 app 裡也能開始或停止錄音"), S.GlobalHotKey, on =>
        {
            S.Set(AppSettings.GlobalHotKeyKey, on);
            host.HotKeyError = host.HotKeys.SetRecordEnabled(on);
            Show();
        }));
        if (S.GlobalHotKey && host.HotKeyError is { } error) alerts.Children.Add(Ui.Text(L.F("沒辦法註冊 Ctrl+Alt+Shift+R：%@", error), 12, color: Ui.Danger));
        alerts.Children.Add(Ui.Toggle(L.T("錄音中按 Ctrl+Alt+Shift+B 標記重點（在其他 app 裡也能用）"), S.BookmarkHotKey, on =>
        {
            S.Set(AppSettings.BookmarkHotKeyKey, on);
            host.HotKeys.SetBookmarkEnabled(on && host.Recorder.IsRecording);
        }));
        return Ui.Column(16, Section(L.T("錄音來源"), source), Section(L.T("語言"), language), Section(L.T("轉錄"), transcription), Section(L.T("提醒與快捷鍵"), alerts));
    }

    // MARK: 模型（R9.4）：在「錄音與轉錄」的轉錄那一組；儲存空間（R10.3）在「進階」

    FrameworkElement ModelRows()
    {
        var transcribing = host.Runner.Running(JobLane.Transcribe) is not null;
        var models = Ui.Column(12);
        foreach (var (model, note) in new[]
                 {
                     (ModelStore.Model.Breeze, ""),
                     (ModelStore.Model.Qwen, L.T("中文、英文可選的引擎（阿里巴巴開源），約 2.5 GB，含 llama.cpp 的執行檔；「正式轉錄」選了它才會用。")),
                     (ModelStore.Model.Japanese, L.T("日文會議用，574 MB。第一次轉錄日文會議時會自動下載。")),
                     (ModelStore.Model.FastRecognition, L.T("即時逐字稿和快速辨識用，約 240 MB。")),
                     (ModelStore.Model.Diarization, L.T("說話者辨識用，約 46 MB。")),
                     (ModelStore.Model.LanguageDetection, L.T("自動偵測會議語言用，60 MB。")),
                 })
        {
            models.Children.Add(ModelRow(model, model == ModelStore.Model.LanguageDetection ? host.Recorder.IsRecording : transcribing));
            if (note.Length > 0) models.Children.Add(Note(note));
        }
        // Qwen 的 CUDA 版（design-windows §8.4）：有 NVIDIA 顯示卡或已經下載過才顯示
        if (host.Gpu.Applies || host.Models.StateOf(ModelStore.Model.QwenCuda) is not ModelStore.State.Missing)
        {
            models.Children.Add(ModelRow(ModelStore.Model.QwenCuda, transcribing));
            models.Children.Add(Note(L.T("有 NVIDIA 顯示卡、選了 Qwen3-ASR 時自動下載，Qwen 的轉錄會快很多。272 MB，解開約 610 MB（llama.cpp 官方的 CUDA 版，要先有上面的 NVIDIA 顯示卡加速）。刪除後不會再自動下載。")));
        }
        // NVIDIA 顯示卡加速（R27 R4.6）：只在有 NVIDIA 顯示卡的 x64 電腦（或已經下載過）顯示
        if (host.Gpu.Applies || host.Models.StateOf(ModelStore.Model.GpuCuda) is not ModelStore.State.Missing)
        {
            // 元件已經載入時刪不掉（DLL 在用），重新開啟拾音之後才能刪
            var inUse = WhisperEngine.NativeDirectory is { } pack && WhisperEngine.LoadedDirectory == pack;
            models.Children.Add(ModelRow(ModelStore.Model.GpuCuda, transcribing || inUse,
                inUse ? L.T("轉錄正在用這個元件，重新開啟拾音後才能刪除") : null));
            models.Children.Add(Note(L.T("有 NVIDIA 顯示卡時自動下載，轉錄會快很多。685 MB，解開約 1.1 GB（whisper.cpp 官方的 CUDA 版）。刪除後不會再自動下載。")));
        }
        return models;
    }

    FrameworkElement StorageRows()
    {
        var audio = Size(host.Paths.Audio);
        var modelBytes = Size(host.Paths.Models);
        var place = Ui.Link(host.Paths.Home, () => Shell.Reveal(host.Paths.Database));
        return Ui.Column(10,
            Ui.Bar(Ui.Text(MeetingLanguages.Ui("錄音", "Recordings"), 14), null, Ui.Text(Recorder.ByteCount(audio), 14, color: Ui.Secondary)),
            Ui.Bar(Ui.Text(L.T("模型"), 14), null, Ui.Text(Recorder.ByteCount(modelBytes), 14, color: Ui.Secondary)),
            Ui.Toggle(L.T("轉錄完成後把錄音壓縮成 m4a（約每小時 43 MB）"), S.CompressAudio, on => S.Set(AppSettings.CompressAudioKey, on)),
            Ui.Bar(Ui.Text(L.T("資料位置"), 14), null, place));
    }

    static long Size(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        try { return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); }
        catch (IOException) { return 0; }
    }

    FrameworkElement ModelRow(ModelStore.Model model, bool deleteDisabled, string? disabledReason = null)
    {
        var state = host.Models.StateOf(model);
        var info = ModelStore.Info(model);
        var left = Ui.Column(4, Ui.Text(info.Name, 14));
        switch (state)
        {
            case ModelStore.State.Downloading { Total: > 0 } d:
                left.Children.Add(new ProgressBar { Maximum = d.Total, Value = d.Received });
                left.Children.Add(Ui.Caption(L.F("%@／%@", Recorder.ByteCount(d.Received), Recorder.ByteCount(d.Total))));
                break;
            case ModelStore.State.Downloading: left.Children.Add(new ProgressBar { IsIndeterminate = true }); break;
            case ModelStore.State.Paused p:
                left.Children.Add(Ui.Caption(p.Received > 0 ? L.F("已暫停（%@）", Recorder.ByteCount(p.Received)) : L.T("已暫停，可以接著下載")));
                break;
            case ModelStore.State.Verifying: left.Children.Add(Ui.Caption(L.T("正在驗證檔案…"))); break;
            case ModelStore.State.Installed: left.Children.Add(Ui.Text(L.T("已下載"), 12, color: Ui.Celadon)); break;
            case ModelStore.State.Failed f: left.Children.Add(Ui.Text(f.Message, 12, color: Ui.Danger)); break;
            default: left.Children.Add(Ui.Caption(L.T("還沒下載"))); break;
        }
        FrameworkElement? right = state switch
        {
            ModelStore.State.Downloading => Ui.Button(L.T("暫停"), () => host.Models.Pause(model)),
            ModelStore.State.Verifying => null,
            ModelStore.State.Installed => DeleteButton(model, info.Name, deleteDisabled, disabledReason),
            ModelStore.State.Paused or ModelStore.State.Failed => Ui.Button(L.T("繼續下載"), () => host.Models.Start(model)),
            _ => Ui.Button(L.T("下載"), () => host.Models.Start(model), accent: true),
        };
        return Ui.Bar(null, left, right, 12);
    }

    Button DeleteButton(ModelStore.Model model, string name, bool disabled, string? reason = null)
    {
        var button = Ui.Button(L.T("刪除"), () => { });
        button.IsEnabled = !disabled;
        if (disabled) ToolTipService.SetToolTip(button, reason ?? L.T("正在轉錄，完成後才能刪除模型"));
        button.Click += async (_, _) =>
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot, Title = Ui.DialogTitle(L.F("刪除「%@」？", name)), Content = L.T("之後要用的時候需要重新下載。"),
                PrimaryButtonText = L.T("刪除"), CloseButtonText = L.T("取消"),
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) { host.Models.Delete(model); Show(); }
        };
        return button;
    }

    // MARK: 進階

    FrameworkElement Advanced()
    {
        var column = Ui.Column(16);
        // 介面語言（D57）：Windows 沒有替桌面 app 個別設定語言的地方
        // 語言的名稱用各自的語言寫
        var language = Ui.Combo(new[] { ("", L.T("跟著 Windows")), ("zh", "中文"), ("en", "English") }, S.String(AppSettings.InterfaceLanguageKey) ?? "",  // l10n-skip
            v => { S.Set(AppSettings.InterfaceLanguageKey, v.Length == 0 ? null : v); App.Current.Notice(L.T("重新打開拾音後換成新的介面語言")); }, L.T("介面語言"));
        // 企業伺服器（R49、M§36.3）：放在最上面，公司的人照「進階 → 企業伺服器」找得到
        column.Children.Add(Section(L.T("企業伺服器"), Enterprise()));
        column.Children.Add(Section(L.T("介面"), language));
        var update = Ui.Column(8, Ui.Bar(Ui.Text(L.T("目前版本"), 14), null, Ui.Text(host.Version, 14, color: Ui.Secondary)));
        if (host.Updater is { } updater)
        {
            var label = Ui.Text(updater.Current.Label, 13, color: updater.Current is Updater.State.Failed ? Ui.Danger : Ui.Secondary);
            var check = Ui.Button(L.T("檢查更新"), () => { });
            check.Click += async (_, _) => { await updater.Check(userInitiated: true); Show(); };
            var row = Ui.Row(10, check, label);
            if (updater.Current is Updater.State.Available a)
                row.Children.Insert(1, Ui.Button(L.F("下載並更新到 %@", a.Version), async () => { await updater.Install(a.Version); Show(); }, accent: true));
            update.Children.Add(row);
        }
        else update.Children.Add(Note(L.T("這個版本沒有更新金鑰（開發版），不會檢查更新。")));
        column.Children.Add(Section(L.T("更新"), update));
        column.Children.Add(Section(L.T("儲存空間"), StorageRows()));
        column.Children.Add(Section(L.T("其他"),
            Ui.Button(L.T("重新顯示首次使用引導"), () => { S.Set(AppSettings.OnboardingKey, false); App.Current.ShowOnboarding(); }),
            Ui.Button(L.T("重建搜尋索引"), () => { host.Db.RebuildSearchIndex(); App.Current.Notice(L.T("搜尋索引已重建")); }),
            Ui.Button(L.T("回報問題…"), () => App.Current.ShowReport()),
            Ui.Button(L.T("關於拾音"), () => App.Current.ShowAbout())));
        // 匿名使用統計（M§30.5、R40.4）：有統計網址的版本才有。放在最下面、只留一行，開關收在「詳細…」裡
        if (host.Usage.IsOffered)
            column.Children.Add(Ui.Card(Ui.Bar(Ui.Text(L.T("匿名使用統計"), 14), null, Ui.Button(L.T("詳細…"), ShowUsageStats))));
        return column;
    }

    // MARK: 企業伺服器（R49、design-windows §12.11）

    /// <summary>
    /// 登入前：網址、登入（用預設瀏覽器打開伺服器的登入頁，登入完走 shiyin://auth 回來）；登入後：帳號、轉錄和整理要不要用、登出。
    /// compact：整理工具頁選了「公司」時（R51.1）：沒登入時一樣，登入後只有一列伺服器和帳號
    /// </summary>
    FrameworkElement Enterprise(bool compact = false)
    {
        var server = host.Enterprise;
        var column = Ui.Column(10);
        if (compact && server.Account is { } signedIn)
        {
            column.Children.Add(Ui.Bar(Ui.Text(L.T("公司的伺服器"), 14), null, Ui.Column(2,
                Ui.Text(L.F("%@（%@）", signedIn.ServerName, server.Host), 14, color: Ui.Secondary, select: true),
                Ui.Row(6, Ui.Text(signedIn.Email, 13, color: Ui.Secondary, select: true),
                    Ui.Text(server.IsSignedIn ? L.T("已登入") : L.T("登入已失效"), 13, color: server.IsSignedIn ? Ui.Celadon : Ui.Danger)))));
            if (server.ExpiredMessage is { } expired)
            {
                var error = Ui.Text("", 12, color: Ui.Danger);
                column.Children.Add(Ui.Text(expired, 12, color: Ui.Danger));
                column.Children.Add(Ui.Row(10, Ui.Button(L.T("重新登入"), () => SignIn(server.SavedUrl?.ToString() ?? "", error)), error));
            }
        }
        else if (server.Account is { } account)
        {
            column.Children.Add(Ui.Bar(Ui.Text(L.T("伺服器"), 14), null, Ui.Text(L.F("%@（%@）", account.ServerName, server.Host), 14, color: Ui.Secondary, select: true)));
            column.Children.Add(Ui.Bar(Ui.Text(L.T("帳號"), 14), null, Ui.Row(8, Ui.Text(account.Email, 14, color: Ui.Secondary, select: true),
                Ui.Text(server.IsSignedIn ? L.T("已登入") : L.T("登入已失效"), 14, color: server.IsSignedIn ? Ui.Celadon : Ui.Danger))));
            if (server.ExpiredMessage is { } message)
            {
                var error = Ui.Text("", 12, color: Ui.Danger);
                column.Children.Add(Ui.Text(message, 12, color: Ui.Danger));
                column.Children.Add(Ui.Row(10, Ui.Button(L.T("重新登入"), () => SignIn(server.SavedUrl?.ToString() ?? "", error)), error));
            }
            column.Children.Add(Ui.Toggle(L.T("用企業伺服器轉錄"), S.TranscriptionEngine == "server", on =>
            {
                if (on) S.Set(AppSettings.TranscriptionEngineKey, "server"); else S.Remove(AppSettings.TranscriptionEngineKey);
                host.Runner.Wake();
            }));
            if (account.Llm)
                column.Children.Add(Ui.Toggle(L.T("用企業伺服器整理會議紀錄"), S.ChosenProvider == LLMProvider.Enterprise, on =>
                {
                    if (on) S.Set(AppSettings.ProviderKey, LLMProvider.Enterprise.Code()); else S.Remove(AppSettings.ProviderKey);
                    S.NotifySummarizerChanged();
                }));
            column.Children.Add(Note(L.F("轉錄：錄音會送到 %@（公司的伺服器），轉完就刪。說話者辨識也在伺服器上做，聲紋只存在這台電腦；筆記、會議紀錄不會因為轉錄送出。伺服器用的轉錄引擎：%@。",
                server.Host, account.Engine == "qwen" ? L.T("Qwen3-ASR（阿里巴巴開源）") : L.T("Breeze ASR 25（聯發科）"))));
            column.Children.Add(Ui.Button(L.T("登出"), async () => { await server.SignOut(); Show(); }));
        }
        else
        {
            var url = new TextBox { Header = L.T("網址"), PlaceholderText = "https://shiyin.example.com" };
            var error = Ui.Text("", 12, color: Ui.Danger);
            url.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) SignIn(url.Text, error); };
            column.Children.Add(url);
            column.Children.Add(Ui.Row(10, Ui.Button(L.T("登入"), () => SignIn(url.Text, error), accent: true), error));
            column.Children.Add(Note(compact
                ? L.T("填入公司給的企業伺服器網址、按「登入」，在打開的網頁用公司帳號登入。登入後用公司在伺服器設定的模型整理，不用自己的 key；錄音也會交給公司的伺服器轉錄（可以到「進階」改回這台電腦）。")
                : L.T("公司有拾音的企業伺服器時，填入公司給的網址、按「登入」，在打開的網頁用公司帳號登入。登入後錄音交給公司的伺服器轉錄，沒有 NVIDIA 顯示卡的電腦也很快；整理也可以用公司提供的模型。")));
        }
        return column;
    }

    /// <summary>檢查網址 → 用預設瀏覽器打開伺服器的登入頁；登入完瀏覽器轉到 shiyin://auth，App 交給 AppHost.CompleteEnterpriseSignIn</summary>
    async void SignIn(string text, TextBlock error)
    {
        error.Foreground = Ui.Secondary;
        error.Text = L.T("登入中…");
        try
        {
            var attempt = await host.Enterprise.BeginSignIn(text);
            await Windows.System.Launcher.LaunchUriAsync(attempt.LoginUrl);
            error.Text = L.T("在打開的網頁登入，登入完會回到拾音");
        }
        catch (EnterpriseException e)
        {
            error.Foreground = Ui.Danger;
            error.Text = e.Message;
        }
    }

    /// <summary>「匿名使用統計」的「詳細…」（M§30.5、R40.4）：開關、說明、重設匿名 ID。不顯示送出的 JSON（使用者決定）</summary>
    async void ShowUsageStats()
    {
        var usage = host.Usage;
        var toggle = Ui.Toggle(L.T("分享匿名使用統計"), usage.IsEnabled, on =>
        {
            S.Set(Services.UsageStats.EnabledKey, on);
            // 關掉時立刻清掉還沒送的資料（R40.4），打開時建立今天的匯總
            if (on) usage.Touch(); else usage.ClearPending();
        });
        var reset = Ui.Text("", 13, color: Ui.Secondary);
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot, Title = Ui.DialogTitle(L.T("匿名使用統計")),
            Content = Ui.Column(12, toggle,
                Ui.Text(L.T("每天送一份匿名的使用統計給拾音的開發者：版本、系統、錄音和轉錄了幾分鐘、用了哪些功能、失敗幾次和哪一種失敗。不會送會議標題、逐字稿、會議紀錄、人名、聲紋或任何檔案。"), 13, color: Ui.Secondary),
                Ui.Row(10, Ui.Button(L.T("重設匿名 ID"), () => { usage.ResetInstallId(); reset.Text = L.T("已重設匿名 ID"); }), reset)),
            CloseButtonText = L.T("完成"), DefaultButton = ContentDialogButton.Close,
        };
        try { await dialog.ShowAsync(); }
        catch (Exception e) { Log.Error($"usage dialog failed: {e.GetType().Name}"); }
    }
}
