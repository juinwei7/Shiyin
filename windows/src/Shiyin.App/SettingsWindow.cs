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
        (string Id, string Label)[] pages =
        [
            ("provider", L.T("整理工具")), ("templates", MeetingLanguages.Ui("範本", "Templates")), ("glossary", L.T("詞彙表")),
            ("speakers", MeetingLanguages.Ui("說話者", "Speakers")), ("recording", L.T("錄音")), ("storage", L.T("模型與儲存空間")), ("advanced", L.T("進階")),
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
        host.Models.Changed += _ => host.Post(() => { if (current == "storage") Show(); });
        Show();
    }

    /// <summary>DEBUG 的 settings 指令：直接打開某一頁</summary>
    public void Open(string id)
    {
        foreach (var item in bar.Items) if ((string)item.Tag == id) bar.SelectedItem = item;
    }

    void Show() => page.Child = Ui.Scroll(current switch
    {
        "templates" => Templates(),
        "glossary" => Glossary(),
        "speakers" => Speakers(),
        "recording" => Recording(),
        "storage" => Storage(),
        "advanced" => Advanced(),
        _ => Provider(),
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
        column.Children.Add(Ui.Combo(LLMProviders.All.Select(p => (p, p.Label())), provider, p =>
        {
            S.Set(AppSettings.ProviderKey, p.Code());
            Show();
        }, L.T("整理工具")));

        if (provider is LLMProvider.Claude or LLMProvider.Codex)
        {
            var status = Ui.Text(L.T("檢查登入狀態…"), 13, color: Ui.Secondary);
            var login = Ui.Button(L.F("登入 %@", provider.ShortLabel()), () => CliLogin.Open(host, provider));
            login.Visibility = Visibility.Collapsed;
            async void Check()
            {
                if (new WindowsToolLocator().Find(provider == LLMProvider.Claude ? "claude" : "codex") is null)
                {
                    status.Text = L.T("找不到，請先安裝並登入");
                    return;
                }
                var result = await CliLogin.Check(provider);
                if (S.Provider != provider) return;   // 查的時候換了整理工具
                status.Text = result is null ? L.T("已安裝，看不出登入狀態") : result.LoggedIn ? L.F("已登入 %@", result.Detail) : L.T("沒有登入");
                status.Foreground = result is null ? Ui.Secondary : result.LoggedIn ? Ui.Celadon : Ui.Danger;
                login.Visibility = result is { LoggedIn: false } ? Visibility.Visible : Visibility.Collapsed;
            }
            Check();
            column.Children.Add(Ui.Row(8, Ui.Text(L.T("狀態"), 13), status, login, Ui.IconButton(Symbol.Refresh, L.T("重新檢查登入狀態"), Check)));
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
                    if (host.Keys is CredentialStore store) store.Save(provider.KeyAccount(), key.Password.Trim());
                    else if (host.Keys is MemoryKeyStore memory) memory.Keys[provider.KeyAccount()] = key.Password.Trim();
                    Show();
                }
                catch (InvalidOperationException e) { error.Text = e.Message; }
            }));
            if (saved)
                buttons.Children.Add(Ui.Button(L.T("刪除 key"), () =>
                {
                    try
                    {
                        if (host.Keys is CredentialStore store) store.Delete(provider.KeyAccount());
                        else if (host.Keys is MemoryKeyStore memory) memory.Keys.Remove(provider.KeyAccount());
                    }
                    catch (InvalidOperationException e) { error.Text = e.Message; }
                    Show();
                }));
            column.Children.Add(Ui.Column(6, key, buttons, error));
        }

        if (provider != LLMProvider.None)
        {
            var model = new TextBox
            {
                Header = L.T("模型"), Text = S.String(provider.ModelKey()) ?? "",
                PlaceholderText = provider == LLMProvider.OpenAICompatible ? L.T("必填") : provider.DefaultModel().Length == 0 ? L.T("留空使用預設模型") : L.F("預設：%@", provider.DefaultModel()),
            };
            model.TextChanged += (_, _) => S.Set(provider.ModelKey(), model.Text);
            var canList = provider == LLMProvider.OpenAICompatible ? OpenAICompatibleApi.Normalized(S.CompatibleBaseUrl) is not null
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

            // 測試：送一個很短的請求，確認登入、key、模型都能用
            var result = Ui.Text("", 13);
            var test = Ui.Button(L.T("測試整理工具"), () => { });
            test.Click += async (_, _) =>
            {
                test.IsEnabled = false;
                test.Content = L.T("測試中…");
                var started = DateTime.UtcNow;
                var language = S.DefaultMeetingLanguage;
                var prompt = language.Text("這是連線測試。逐字稿：\n[00:00] 說話者 1：大家好，今天只是測試一下。",
                    "This is a connection test. Transcript:\n[00:00] Speaker 1: Hi everyone, this is just a test.",
                    "これは接続テストです。文字起こし：\n[00:00] 話者 1：皆さん、今日はテストだけです。");  // l10n-skip
                try
                {
                    await host.Llm.Generate(provider, S.Model, NotesPrompt.System(language), prompt, CancellationToken.None);
                    result.Text = L.F("可以使用（%lld 秒）", (long)Math.Round((DateTime.UtcNow - started).TotalSeconds));
                    result.Foreground = Ui.Celadon;
                }
                catch (Exception e) when (e is LLMException or HttpRequestException or IOException or TimeoutException or TaskCanceledException)
                {
                    result.Text = e.Message;
                    result.Foreground = Ui.Danger;
                }
                test.IsEnabled = true;
                test.Content = L.T("測試整理工具");
            };
            column.Children.Add(Ui.Row(10, test, result));
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

    string Privacy(LLMProvider provider) => provider switch
    {
        LLMProvider.None => L.T("目前不整理：錄音、轉錄、逐字稿全部留在這台電腦。"),
        LLMProvider.Claude or LLMProvider.Anthropic => L.T("整理、提問、猜說話者時會把逐字稿文字、你的筆記、詞彙表和會議基本資料送到 Anthropic（Claude）。錄音檔不會送出。"),
        LLMProvider.Codex or LLMProvider.OpenAI => L.T("整理、提問、猜說話者時會把逐字稿文字、你的筆記、詞彙表和會議基本資料送到 OpenAI。錄音檔不會送出。"),
        LLMProvider.Gemini => L.T("整理、提問、猜說話者時會把逐字稿文字、你的筆記、詞彙表和會議基本資料送到 Google（Gemini）。錄音檔不會送出。"),
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
        var actions = Ui.Row(8);
        if (template.Builtin) actions.Children.Add(Note(L.T("內建範本不能修改，可以按「複製」做一份自己的。")));
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
        column.Children.Add(actions);
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
                XamlRoot = Content.XamlRoot, Title = L.T("刪除所有認識的人的聲音特徵？"), Content = L.T("已經帶入的名字會留著，之後的會議不會再自動帶入。"),
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
                var dialog = new ContentDialog { XamlRoot = Content.XamlRoot, Title = L.T("改名"), Content = box, PrimaryButtonText = L.T("儲存"), CloseButtonText = L.T("取消") };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary && box.Text.Trim().Length > 0) { host.Controller.RenamePerson(person.Id, box.Text.Trim()); Show(); }
            };
            column.Children.Add(Ui.Bar(null, Ui.Column(2, Ui.Text(person.Name, 14), Ui.Caption(detail)),
                Ui.Row(8, rename, Ui.IconButton(Symbol.Delete, L.F("刪除「%@」的聲音特徵（已經帶入的名字留著）", person.Name), () => { host.Controller.DeletePerson(person.Id); Show(); }))));
        }
        return column;
    }

    // MARK: 錄音（R1）

    FrameworkElement Recording()
    {
        var column = Ui.Column(12);
        column.Children.Add(Ui.Combo(new[] { "mic", "both", "system" }.Select(s => (s, MainWindow.SourceLabel(s))), S.RecordSource,
            s => { S.Set(AppSettings.RecordSourceKey, s); Show(); }, L.T("錄音來源")));
        List<(string Id, string Name)> microphones;
        try { microphones = WasapiSource.Microphones(); }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException) { microphones = []; }
        var mic = Ui.Combo(new[] { ("", L.T("系統預設")) }.Concat(microphones), S.InputDevice, id => S.Set(AppSettings.InputDeviceKey, id), L.T("麥克風"));
        mic.IsEnabled = S.RecordSource != "system";
        column.Children.Add(mic);
        column.Children.Add(Ui.Combo(MeetingLanguages.All.Select(l => (l, l.Name())), S.DefaultMeetingLanguage, l => S.Set(AppSettings.DefaultMeetingLanguageKey, l.Code()), L.T("預設會議語言")));
        column.Children.Add(Note(L.T("新的錄音和匯入預設用這個語言轉錄、整理會議紀錄；錄音選單、匯入時和會議頁都可以改。")));
        column.Children.Add(Ui.Toggle(L.T("自動偵測會議語言"), S.AutoDetectLanguage, on =>
        {
            S.Set(AppSettings.AutoDetectLanguageKey, on);
            if (on) host.Models.AutoStart(ModelStore.Model.LanguageDetection);
            Show();
        }));
        if (S.AutoDetectLanguage && !host.Models.IsInstalled(ModelStore.Model.LanguageDetection))
        {
            var busy = host.Models.StateOf(ModelStore.Model.LanguageDetection).IsBusy;
            column.Children.Add(Ui.Bar(null, Ui.Text(busy ? L.T("正在下載語言偵測模型…") : L.T("語言偵測模型還沒下載，下載好之前錄音不會自動偵測"), 12, color: Ui.Signal),
                busy ? null : Ui.Button(L.T("下載"), () => host.Models.Start(ModelStore.Model.LanguageDetection))));
        }
        column.Children.Add(Note(L.T("開始錄音後聽約 10 秒，判斷是中文、英文還是日文；跟會議語言不同而且有把握時會自動切換，錄音畫面可以改回來。需要 60 MB 的語言偵測模型。")));
        column.Children.Add(Ui.Toggle(L.T("錄音中顯示即時逐字稿"), S.LiveTranscription, on => S.Set(AppSettings.LiveTranscriptionKey, on)));
        column.Children.Add(Ui.Toggle(L.T("轉錄時辨識說話者"), S.Diarize, on => S.Set(AppSettings.DiarizeKey, on)));
        column.Children.Add(Ui.Combo(new[] { ("breeze", L.T("Breeze ASR 25（建議）")), ("system", L.T("快速辨識")) }, S.UseFastTranscriber ? "system" : "breeze",
            e => { S.Set(AppSettings.TranscriptionEngineKey, e); host.Runner.Wake(); Show(); }, L.T("正式轉錄")));
        column.Children.Add(Note(S.UseFastTranscriber
            ? L.T("速度快很多，下載也小；但不參考詞彙表，英文專有名詞、人名比較容易錯。")
            : L.T("會參考詞彙表，中英夾雜、專有名詞比較準；需要下載約 1.7 GB 的模型。日文會議改用 Whisper large-v3-turbo（574 MB）。")));
        column.Children.Add(Ui.Toggle(L.T("全域快捷鍵 Ctrl+Alt+Shift+R：在其他 app 裡也能開始或停止錄音"), S.GlobalHotKey, on =>
        {
            S.Set(AppSettings.GlobalHotKeyKey, on);
            host.HotKeyError = host.HotKeys.SetRecordEnabled(on);
            Show();
        }));
        if (S.GlobalHotKey && host.HotKeyError is { } error) column.Children.Add(Ui.Text(L.F("沒辦法註冊 Ctrl+Alt+Shift+R：%@", error), 12, color: Ui.Danger));
        column.Children.Add(Ui.Toggle(L.T("錄音中按 Ctrl+Alt+Shift+B 標記重點（在其他 app 裡也能用）"), S.BookmarkHotKey, on =>
        {
            S.Set(AppSettings.BookmarkHotKeyKey, on);
            host.HotKeys.SetBookmarkEnabled(on && host.Recorder.IsRecording);
        }));
        column.Children.Add(Ui.Toggle(L.T("偵測到線上會議時提醒錄音"), S.MeetingDetection, on => S.Set(AppSettings.MeetingDetectionKey, on)));
        column.Children.Add(Note(L.T("Zoom、Teams、Webex、Google Meet（瀏覽器）等開始用麥克風、拾音沒在錄音時提醒你；會議結束時提醒停止。只提醒，不會自動錄音。")));
        foreach (var app in host.Detector.Muted)
            column.Children.Add(Ui.Bar(null, Ui.Text(L.F("不提醒：%@", MeetingDetector.DisplayName(app)), 13), Ui.Link(L.T("恢復提醒"), () => { host.Detector.Unmute(app); Show(); })));
        column.Children.Add(Ui.Card(Ui.Column(8,
            Ui.Text(L.T("錄線上會議時選「麥克風＋電腦聲音」：你的聲音和對方的聲音會分別錄在左右聲道。"), 13, color: Ui.Secondary),
            Ui.Button(L.T("麥克風權限設定"), Shell.OpenMicrophonePrivacy))));
        return column;
    }

    // MARK: 模型與儲存空間（R9.4、R10.3）

    FrameworkElement Storage()
    {
        var transcribing = host.Runner.Running(JobLane.Transcribe) is not null;
        var models = Ui.Column(12);
        foreach (var (model, note) in new[]
                 {
                     (ModelStore.Model.Breeze, ""),
                     (ModelStore.Model.Japanese, L.T("日文會議用，574 MB。第一次轉錄日文會議時會自動下載。")),
                     (ModelStore.Model.FastRecognition, L.T("即時逐字稿和快速辨識用，約 240 MB。")),
                     (ModelStore.Model.Diarization, L.T("說話者辨識用，約 46 MB。")),
                     (ModelStore.Model.LanguageDetection, L.T("自動偵測會議語言用，60 MB。")),
                 })
        {
            models.Children.Add(ModelRow(model, model == ModelStore.Model.LanguageDetection ? host.Recorder.IsRecording : transcribing));
            if (note.Length > 0) models.Children.Add(Note(note));
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
        var audio = Size(host.Paths.Audio);
        var modelBytes = Size(host.Paths.Models);
        var place = Ui.Link(host.Paths.Home, () => Shell.Reveal(host.Paths.Database));
        var storage = Ui.Column(10,
            Ui.Bar(Ui.Text(MeetingLanguages.Ui("錄音", "Recordings"), 14), null, Ui.Text(Recorder.ByteCount(audio), 14, color: Ui.Secondary)),
            Ui.Bar(Ui.Text(L.T("模型"), 14), null, Ui.Text(Recorder.ByteCount(modelBytes), 14, color: Ui.Secondary)),
            Ui.Toggle(L.T("轉錄完成後把錄音壓縮成 m4a（約每小時 43 MB）"), S.CompressAudio, on => S.Set(AppSettings.CompressAudioKey, on)),
            Ui.Bar(Ui.Text(L.T("資料位置"), 14), null, place));
        return Ui.Column(16, Section(L.T("模型"), models), Section(L.T("儲存空間"), storage));
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
                XamlRoot = Content.XamlRoot, Title = L.F("刪除「%@」？", name), Content = L.T("之後要用的時候需要重新下載。"),
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
        column.Children.Add(Section(L.T("其他"),
            Ui.Button(L.T("重新顯示首次使用引導"), () => { S.Set(AppSettings.OnboardingKey, false); App.Current.ShowOnboarding(); }),
            Ui.Button(L.T("重建搜尋索引"), () => { host.Db.RebuildSearchIndex(); App.Current.Notice(L.T("搜尋索引已重建")); }),
            Ui.Button(L.T("回報問題…"), () => App.Current.ShowReport()),
            Ui.Button(L.T("關於拾音"), () => App.Current.ShowAbout())));
        return column;
    }
}
