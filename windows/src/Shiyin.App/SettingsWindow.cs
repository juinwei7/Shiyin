using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
/// 設定視窗（R108、M§82、design-windows §11.41）：左邊導覽（NavigationView）十一頁分四組、上方搜尋，右邊那一頁（標題、捲動的內容）。
/// 原本四頁的段落照搬（R108.6），設定存在 SHIYIN_HOME\settings.json，改了馬上生效；上次停在哪一頁存在 settingsPage
/// </summary>
public sealed class SettingsWindow : Window
{
    readonly AppHost host;
    readonly NavigationView nav;
    readonly AutoSuggestBox search;
    readonly Dictionary<string, NavigationViewItem> items = [];
    /// <summary>組和組之間的分隔線，跟著後面那一組（第 1～3 組）</summary>
    readonly List<(NavigationViewItemSeparator Line, int Group)> separators = [];
    readonly TextBlock heading = Ui.Text("", 24, bold: true, wrap: false);
    readonly Border body = new();
    readonly Grid root = new();
    string current;

    public SettingsWindow(AppHost host)
    {
        this.host = host;
        Title = L.T("設定");
        // R108.5：比原本的四頁（760×640）大、可以拉大；最小同 Mac（720×500）
        WindowPlacement.Center(this, 900, 660);
        WindowPlacement.MinimumSize(this, 720, 500);
        current = SettingsPages.Stored(S);
        search = new AutoSuggestBox { PlaceholderText = L.T("搜尋設定"), QueryIcon = new SymbolIcon(Symbol.Find) };
        AutomationProperties.SetName(search, L.T("搜尋設定"));
        // 打字留下名稱或關鍵字對得上的頁面，只剩一頁時直接選它（R108.4）；Enter 選第一頁
        search.TextChanged += (_, _) => Filter();
        search.QuerySubmitted += (_, _) => { if (ListedPages().FirstOrDefault() is { } first) Select(first); };
        nav = new NavigationView
        {
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left, IsPaneToggleButtonVisible = false, IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsSettingsVisible = false, IsTitleBarAutoPaddingEnabled = false, AlwaysShowHeader = false, OpenPaneLength = 248, AutoSuggestBox = search,
            // ↑ ↓ 換頁（同 Windows 設定、Mac 的側欄）；預設只移焦點，要再按 Enter 或空白鍵
            SelectionFollowsFocus = NavigationViewSelectionFollowsFocus.Enabled,
        };
        for (var g = 0; g < SettingsPages.Groups.Count; g++)
        {
            if (g > 0)
            {
                var line = new NavigationViewItemSeparator();
                separators.Add((line, g));
                nav.MenuItems.Add(line);
            }
            foreach (var id in SettingsPages.Groups[g])
            {
                var item = new NavigationViewItem { Content = SettingsPages.Title(id), Tag = id, Icon = new FontIcon { Glyph = Look(id).Glyph } };
                AutomationProperties.SetName(item, SettingsPages.Title(id));
                items[id] = item;
                nav.MenuItems.Add(item);
            }
        }
        ColorIcons();
        nav.SelectedItem = items[current];
        nav.SelectionChanged += (_, e) => { if (e.SelectedItem is NavigationViewItem { Tag: string id }) Select(id); };
        // 右邊：標題固定在上面，下面的內容捲動（同 Windows 設定）
        heading.Margin = new Thickness(28, 20, 28, 10);
        var page = new Grid();
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        page.Children.Add(heading);
        Grid.SetRow(body, 1);
        page.Children.Add(body);
        nav.Content = page;
        // 導覽的底色同主視窗的側欄（淺色 #F7F7F7、深色 #2B2B2B），右邊是 NavigationView 自己的內容層（左上角圓角、細框）
        root.Children.Add(nav);
        Content = Ui.SidebarBackground(Ui.InterfaceLanguage(root));
        // 開著設定時換了深淺色、對比主題：圖示的顏色、這一頁重畫
        root.ActualThemeChanged += (_, _) => { Ui.SetTheme(root.ActualTheme); ColorIcons(); Show(); };
        root.Loaded += (_, _) =>
        {
            ColorIcons();   // 放進視窗之前 ActualTheme 不一定是視窗的
            // 記住的頁面在導覽的下面（視窗矮、150% 縮放時「檢查與更新」在捲動範圍外）：捲到看得到選到的那一項
            items[current].StartBringIntoView();
        };
        Ui.OnContrastChanged(root, () => { ColorIcons(); Show(); });
        host.Models.Changed += _ => host.Post(() => { if (current is "models" or "transcription") Show(); });
        // 企業伺服器的登入狀態變了（登入、失效、登出）：整理工具的選單、正式轉錄的選單、AI 工具（公司可以關掉）
        host.Enterprise.Changed += () => host.Post(() => { if (current is "enterprise" or "provider" or "transcription" or "ai") Show(); });
        host.Enterprise.Glossary.Changed += () => host.Post(() => { if (current is "glossary") Show(); });
        // 更新的進度、結果：只重畫更新那一塊（整頁重畫會捲回頂端）
        if (host.Updater is { } updater) updater.Changed += () => host.Post(() => { if (current == "status") FillUpdate(updater); });
        Show();
    }

    /// <summary>
    /// 側欄的圖示（同 Windows 11 設定：彩色的圖示，沒有底色的方塊）：Segoe Fluent Icons 的字對應 Mac 的 SF Symbol，顏色是 Mac 的那一色，
    /// 淺色調深、深色調亮（在側欄的底色上對比 3.8:1 以上）。對比主題時不上色，跟著項目的文字顏色（選到、滑過時是反白的字）
    /// </summary>
    static (string Glyph, uint Light, uint Dark) Look(string id) => id switch
    {
        "provider" => ("\uE8BD", 0x7B49C9, 0xA585E8),        // Message（同引導的「整理成紀錄」）；Mac sparkles、紫
        "templates" => ("\uE8A5", 0xC0650E, 0xF5A04D),       // Document；doc.text.fill、橘
        "recording" => ("\uE720", 0xD13438, 0xF06A6A),       // Microphone；mic.fill、紅
        "transcription" => ("\uE9D9", 0x1F6FD8, 0x5C9BF5),   // Diagnostic（波形）；waveform、藍
        "models" => ("\uEDA2", 0x6E7079, 0xA3A5AD),          // HardDrive；internaldrive.fill、灰
        "speakers" => ("\uE716", 0x0E8A8F, 0x3CC4C9),        // People；person.2.fill、青
        "glossary" => ("\uE82D", 0x8A5D36, 0xC29A72),        // Dictionary；character.book.closed.fill、棕
        "general" => ("\uE713", 0x6E7079, 0xA3A5AD),         // Setting；gearshape.fill、灰
        "ai" => ("\uEA86", 0x4F4FCF, 0x8C8CEB),              // Puzzle；puzzlepiece.extension.fill、靛
        "enterprise" => ("\uE825", 0x4A6890, 0x8AA3C7),      // Bank（建築）；building.2.fill、藍灰
        _ => ("\uE895", 0x23884A, 0x4CC777),                 // Sync（同 Windows Update）；checkmark.seal.fill、綠
    };

    void ColorIcons()
    {
        foreach (var (id, item) in items)
        {
            if (item.Icon is not FontIcon icon) continue;
            if (Ui.HighContrast) { icon.ClearValue(IconElement.ForegroundProperty); continue; }
            var (_, light, dark) = Look(id);
            icon.Foreground = new SolidColorBrush(Ui.HexColor((root.ActualTheme == ElementTheme.Dark ? dark : light).ToString("X6")));
        }
    }

    /// <summary>換到這一頁、記住（R108.5）</summary>
    void Select(string id)
    {
        if (!items.TryGetValue(id, out var item)) return;
        SettingsPages.Remember(S, id);
        var changed = id != current;
        current = id;
        if (!ReferenceEquals(nav.SelectedItem, item)) nav.SelectedItem = item;
        if (changed)
        {
            Show();
            // 從別的地方指定頁面（App.ShowSettings、搜尋只剩一項）：導覽捲到看得到那一項
            if (item.IsLoaded) item.StartBringIntoView();
        }
    }

    /// <summary>搜尋（R108.4）：留下對得上的頁面，分隔線只留在兩組看得到的項目中間；只剩一頁時直接選它</summary>
    void Filter()
    {
        var found = SettingsPages.Find(search.Text);
        var wasHidden = items[current].Visibility == Visibility.Collapsed;
        foreach (var (id, item) in items) item.Visibility = found.Contains(id) ? Visibility.Visible : Visibility.Collapsed;
        // 選到的那一項藏起來過：再出現時 NavigationView 的選取條（左邊那一小條）不見了（2026-11-17 虛擬機），排好之後重新選一次
        if (wasHidden && found.Contains(current))
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (items[current].Visibility != Visibility.Visible) return;
                nav.SelectedItem = null;
                nav.SelectedItem = items[current];
            });
        var shownBefore = false;
        for (var g = 0; g < SettingsPages.Groups.Count; g++)
        {
            var any = SettingsPages.Groups[g].Any(found.Contains);
            foreach (var (line, group) in separators)
                if (group == g) line.Visibility = any && shownBefore ? Visibility.Visible : Visibility.Collapsed;
            shownBefore |= any;
        }
        if (search.Text.Trim().Length > 0 && found.Count == 1) Select(found[0]);
    }

    List<string> ListedPages() => [.. SettingsPages.All.Where(id => items[id].Visibility == Visibility.Visible)];

    /// <summary>重畫目前這一頁</summary>
    public void Refresh() => Show();

    /// <summary>打開某一頁：十一頁的代號、0～10、4.8 以前的頁名（Resolve）。認不得就不換</summary>
    public void Open(string name)
    {
        if (SettingsPages.Resolve(name) is { } id) Select(id);
    }

    /// <summary>DEBUG 的 state：目前的頁面、搜尋的字、側欄看得到的頁面</summary>
    public string CurrentPage => current;
    public string SearchText => search.Text;
    public List<string> VisiblePages => ListedPages();

    /// <summary>DEBUG 的 settingssearch：等於在搜尋框打字（AutoSuggestBox 的 TextChanged 晚一點才發，這裡先篩，回傳的才是篩好的）</summary>
    public void Search(string text)
    {
        search.Text = text;
        Filter();
    }

    // 每一頁的段落（R108.6：原本四頁的段落照搬，項目、存的鍵、說明文字不變）。只有一段的頁面不再放段落標題（同 Mac）
    void Show()
    {
        heading.Text = SettingsPages.Title(current);
        body.Child = new ScrollViewer
        {
            // 靠上：只有一張卡片的頁面（說話者、詞彙表…），卡片照內容的高度，不要被拉到視窗底
            Content = new Border { Padding = new Thickness(28, 4, 28, 28), Child = PageContent(current), VerticalAlignment = VerticalAlignment.Top },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    FrameworkElement PageContent(string id) => id switch
    {
        // 範本原本在整理工具的下面（R44.5、D110），R108.3 自己一頁
        "templates" => Ui.Card(Templates()),
        "recording" => Ui.Column(16, Section(L.T("錄音來源"), RecordingSource()), Section(L.T("提醒與快捷鍵"), Alerts())),
        "transcription" => Ui.Column(16, Section(L.T("轉錄"), Transcription()), Section(L.T("語言"), Languages())),
        // 模型清單原本在「錄音與轉錄 → 轉錄」、儲存空間原本在「進階」
        "models" => Ui.Column(16, Section(L.T("模型"), ModelRows()), Section(L.T("儲存空間"), StorageRows())),
        "speakers" => Ui.Card(Speakers()),
        "glossary" => Ui.Card(Glossary()),
        "general" => General(),
        "ai" => Ui.Card(Mcp()),
        "enterprise" => Ui.Card(Enterprise()),
        "status" => Status(),
        _ => Ui.Card(Provider()),
    };

    AppSettings S => host.Settings;

    static TextBlock Note(string text) => Ui.Text(text, 12, color: Ui.Secondary);

    StackPanel? updateRows;
    TextBlock? updateLabel;
    ProgressBar? updateBar;

    /// <summary>「檢查與更新」的更新那一行：狀態、檢查、下載並更新；下載中加進度條（R10.6、design-windows §14.2，Velopack 只給百分比）</summary>
    void FillUpdate(Updater updater)
    {
        if (updateRows is null) return;
        // 下載中只換百分比：整行重畫的話，焦點會跳到下面的開關（再按空白鍵就改到設定）
        if (updater.Current is Updater.State.Downloading d && updateBar is { IsIndeterminate: false } && updateLabel is not null)
        {
            updateLabel.Text = d.Label;
            updateBar.Value = d.Percent;
            if (updateBar.IsTabStop) AutomationProperties.SetName(updateBar, d.Label);
            return;
        }
        // 第一次畫（Status() 裡）還沒放進視窗，XamlRoot 是 null：GetFocusedElement(null) 會丟例外，整頁打不開
        var hadFocus = updateRows.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is DependencyObject focused && Inside(focused, updateRows);
        updateRows.Children.Clear();
        updateLabel = Ui.Text(updater.Current.Label, 13, color: updater.Current is Updater.State.Failed ? Ui.Danger : Ui.Secondary);
        var check = Ui.Button(L.T("檢查更新"), () => _ = updater.Check(userInitiated: true));
        check.IsEnabled = updater.Current is not (Updater.State.Downloading or Updater.State.Installing);
        var row = Ui.Row(10, check, updateLabel);
        if (updater.Current is Updater.State.Available a)
            row.Children.Insert(1, Ui.Button(L.F("下載並更新到 %@", a.Version), () => _ = updater.Install(a.Version), accent: true));
        updateRows.Children.Add(row);
        updateBar = updater.Current switch
        {
            Updater.State.Downloading p => new ProgressBar { Maximum = 100, Value = p.Percent },
            Updater.State.Installing => new ProgressBar { IsIndeterminate = true },
            _ => null,
        };
        if (updateBar is not null) updateRows.Children.Add(updateBar);
        // 按「下載並更新到」之後那顆按鈕不見了：焦點留在這一行（進度條），不要掉到別的設定
        if (!hadFocus) return;
        Control? target = check.IsEnabled ? check : updateBar;
        if (target is null) return;
        target.IsTabStop = true;
        AutomationProperties.SetName(target, updater.Current.Label);
        target.Focus(FocusState.Programmatic);
    }

    static bool Inside(DependencyObject element, DependencyObject container)
    {
        for (var e = element; e is not null; e = VisualTreeHelper.GetParent(e))
            if (e == container) return true;
        return false;
    }

    /// <summary>不跟著「正式轉錄」設定的語言（M§41.3）：「日文和韓文會議改用 Whisper large-v3-turbo（574 MB）。廣東話會議一律用 Qwen3-ASR。」</summary>
    static string OtherEngines()
    {
        var space = MeetingLanguages.Ui("", " ");
        var text = space + L.F("%@會議改用 Whisper large-v3-turbo（574 MB）。", MeetingLanguages.UiNames("turbo"));
        var qwen = MeetingLanguages.UiNames("qwen");
        return qwen.Length == 0 ? text : text + space + L.F("%@會議一律用 Qwen3-ASR。", qwen);
    }

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
        // 公司：登入了企業伺服器、公司有提供整理才出現（R51.1、M D124；登入在「企業伺服器」那一頁）
        var options = LLMProviders.All.Where(p => p != LLMProvider.Enterprise || host.Enterprise.Account?.Llm == true || p == provider)
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

        // 公司：模型由公司的後台決定，這裡只顯示（R51.1）；公司後來才提供整理時，選單就多「公司」（R51.5）
        if (provider == LLMProvider.Enterprise && host.Enterprise.IsSignedIn && host.Enterprise.Account?.Llm == true)
            column.Children.Add(CompanyModels());
        _ = host.Enterprise.RefreshInfo();

        // 只有 Claude 桌面版：說明怎麼裝 Claude Code（R56.2、R56.3）；已經在用別的整理工具的人不顯示
        var claudeHint = (provider == LLMProvider.Claude || S.NeedsProviderSetup) && ClaudeCodeHint.Applies(host);
        if (provider is LLMProvider.Claude or LLMProvider.Codex)
        {
            // 桌面版附的 codex：寫出來，登入是打開那個 app（R55.3）
            var desktopApp = provider == LLMProvider.Codex ? host.Tools.Find("codex")?.DesktopApp : null;
            var status = Ui.Text(L.T("檢查登入狀態…"), 13, color: Ui.Secondary);
            var login = Ui.Button(desktopApp is not null ? L.F("打開 %@ 登入", desktopApp) : L.F("登入 %@", provider.ShortLabel()), () => CliLogin.Open(host, provider));
            login.Visibility = Visibility.Collapsed;
            async void Check(bool userInitiated)
            {
                if (userInitiated) host.Locator.ClearCache();
                if (host.Tools.Find(provider == LLMProvider.Claude ? "claude" : "codex") is null)
                {
                    status.Text = provider == LLMProvider.Codex ? L.T("這台電腦沒有安裝 codex：沒有在用 Codex 的話，請改選 API")
                        : claudeHint ? L.T("這台電腦沒有安裝 Claude Code（命令列版），安裝方法在下面")
                        : L.T("這台電腦沒有安裝 claude：沒有在用 Claude Code 的話，請改選 API");
                    if (userInitiated) Show();
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
            if (desktopApp is not null)
                column.Children.Add(Ui.Caption(L.F("使用 %@ 附的 codex（這台電腦沒有另外安裝 Codex CLI）", desktopApp)));
        }
        if (claudeHint)
            column.Children.Add(ClaudeCodeHint.Build(() => { host.Locator.ClearCache(); Show(); }));

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

    /// <summary>選單上的「公司（範例公司）」</summary>
    string CompanyLabel() => host.Enterprise.Account?.ServerName is { Length: > 0 } name ? L.F("公司（%@）", name) : LLMProvider.Enterprise.Label();

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
        SummarizerProblemKind.EnterpriseSignedOut => L.T("公司的伺服器還沒登入或登入已失效：到「企業伺服器」那一頁登入。設定好之前不會自動整理。"),
        SummarizerProblemKind.EnterpriseNoModels => L.T("公司的伺服器還沒有提供整理用的模型：請管理者在伺服器後台的「整理模型」加上模型。"),
        _ => L.F("上次整理失敗：%@。改好設定，或按「測試整理工具」成功之後，才會再自動整理。", L.Ui(problem!.Detail.TrimEnd('。', '.', ' '))),
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
            // 名字放在撐滿的那一欄、太長時省略號：英文的「General meeting (copy)」會把右邊的「Default」擠出列表（2026-11-16 英文巡檢）
            var item = new ListViewItem
            {
                Tag = t.Id, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                // 對比主題：「預設」跟著列的前景色（選到時是反白的字，次要文字的顏色在反白的底上看不見）
                Content = Ui.FollowForeground(Ui.Bar(null, Ui.Text(t.DisplayName(), 14, wrap: false), t.Id == S.DefaultTemplate ? Ui.Caption(L.T("預設")) : null)),
            };
            ToolTipService.SetToolTip(item, t.DisplayName());
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, t.DisplayName());
            // 右鍵（R100）：複製、設為預設範本、刪除（自訂的才有）
            var record = t;
            var menu = new MenuFlyout();
            menu.Items.Add(Ui.Item(L.T("複製"), () => CreateTemplate(record, null), icon: Symbol.Copy));
            menu.Items.Add(Ui.Item(L.T("設為預設範本"), () => { S.Set(AppSettings.DefaultTemplateKey, record.Id); Show(); }, S.DefaultTemplate != record.Id));
            if (!record.Builtin)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
                menu.Items.Add(Ui.DangerItem(L.T("刪除"), () => DeleteTemplate(record)));
            }
            item.ContextFlyout = menu;
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

    /// <summary>刪掉一個自訂範本：是預設範本的話預設改回一般會議，不然新會議會指到不存在的範本</summary>
    void DeleteTemplate(TemplateRecord template)
    {
        if (template.Builtin) return;
        if (S.DefaultTemplate == template.Id) S.Set(AppSettings.DefaultTemplateKey, "general");
        host.Controller.DeleteTemplate(template.Id);
        if (templateId == template.Id) templateId = null;
        Show();
    }

    void CreateTemplate(TemplateRecord? source, TemplateDefinition? edited)
    {
        var id = "custom-" + Guid.NewGuid().ToString("N")[..8];
        // 複製內建範本：跟畫面上看到的一樣（簡體介面是簡體，同 Mac 的「複製」按鈕用畫面上的 draft）
        // 新範本預設有議題（R113.1）：沒有議題時播放條上沒有議題帶
        var definition = edited ?? (source is null ? null : Shown(source)) ?? new TemplateDefinition("", ["summary", "decisions", "actionItems", "topics"], [], "");
        host.Controller.SaveTemplate(new TemplateRecord
        {
            Id = id, Name = source is null ? L.T("新範本") : L.F("%@（副本）", source.DisplayName()), Builtin = false, Definition = definition.JsonString(),
        });
        templateId = id;
        Show();
    }

    /// <summary>畫面上顯示的範本內容：內建範本不能改、不會存檔，簡體介面顯示簡體（M§48.2）</summary>
    static TemplateDefinition Shown(TemplateRecord template)
    {
        var definition = template.DefinitionFor(MeetingLanguages.Interface);
        if (!template.Builtin || !L.Simplified) return definition;
        return definition with { Description = L.Ui(definition.Description), Instruction = L.Ui(definition.Instruction),
                                 Custom = [.. definition.Custom.Select(c => c with { Title = L.Ui(c.Title), Instruction = L.Ui(c.Instruction) })] };
    }

    FrameworkElement TemplateEditor(TemplateRecord template)
    {
        var definition = Shown(template);
        var sections = definition.Sections.ToList();
        var custom = definition.Custom.ToList();
        var name = new TextBox { Header = L.T("名稱"), PlaceholderText = L.T("範本名稱"), Text = template.DisplayName(), IsEnabled = !template.Builtin };
        var description = MultiLine(L.T("用途說明"), L.T("例如：週會、專案會議用"), definition.Description, !template.Builtin);
        var instruction = MultiLine(L.T("額外指示（選填）"), L.T("給整理工具的其他要求，例如：金額、數字照原文寫。可以分行。"), definition.Instruction, !template.Builtin);
        var column = Ui.Column(12);
        // 內建範本不能改：說明放最上面、給一顆複製的按鈕（R112.1、design-windows §11.45；原本在最下面）
        if (template.Builtin)
        {
            var copy = Ui.Button(L.T("複製一份來改"), () => CreateTemplate(template, null));
            copy.VerticalAlignment = VerticalAlignment.Center;
            var text = Ui.Text(L.T("內建範本不能修改。按「複製一份來改」，就能改段落、加自訂段落。"), 13, color: Ui.Secondary);
            text.VerticalAlignment = VerticalAlignment.Center;
            var lockIcon = new FontIcon { Glyph = "\uE72E", FontSize = 14, Foreground = Ui.Secondary, VerticalAlignment = VerticalAlignment.Center };
            column.Children.Add(Ui.Card(Ui.Bar(lockIcon, text, copy, 10), 12));
        }
        column.Children.Add(name);
        column.Children.Add(description);
        var toggles = Ui.Column(4, Ui.Text(L.T("要產生的段落"), 14, bold: true));
        foreach (var id in TemplateDefinition.BuiltinSectionIds)
        {
            var box = new CheckBox { Content = L.Ui(TemplateDefinition.SectionLabel(id, MeetingLanguages.Interface)), IsChecked = sections.Contains(id), IsEnabled = !template.Builtin };
            box.Click += (_, _) => { if (box.IsChecked == true) { if (!sections.Contains(id)) sections.Add(id); } else sections.Remove(id); };
            toggles.Children.Add(box);
        }
        toggles.Children.Add(Note(L.T("關掉「議題詳細」時，紀錄裡沒有議題：播放條上沒有議題帶，心智圖改用摘要。")));
        column.Children.Add(toggles);
        // 自訂段落一個一塊（R112.4）：段落名稱、要寫什麼（多行）、右上角移除
        var customs = Ui.Column(8, Ui.Text(L.T("自訂段落"), 14, bold: true),
            Ui.Text(L.T("每個自訂段落是會議紀錄裡的一節，整理工具照「要寫什麼」寫成條列；負責人、期限這類待辦，請打開上面的「待辦」。"), 12, color: Ui.Secondary));
        if (custom.Count == 0) customs.Children.Add(Ui.Text(L.T("還沒有自訂段落。"), 13, color: Ui.Secondary));
        for (var i = 0; i < custom.Count; i++)
        {
            var index = i;
            var title = new TextBox { Header = L.T("段落名稱"), PlaceholderText = L.T("例如：風險"), Text = custom[i].Title, IsEnabled = !template.Builtin };
            title.TextChanged += (_, _) => custom[index] = custom[index] with { Title = title.Text };
            var what = MultiLine(L.T("要寫什麼"), L.T("例如：列出討論中提到的風險、負責人和期限。可以分行，用 - 條列。"), custom[i].Instruction, !template.Builtin);
            what.TextChanged += (_, _) => custom[index] = custom[index] with { Instruction = Lines(what.Text) };
            var delete = Ui.IconButton(Symbol.Delete, L.T("移除這個段落"), () => { custom.RemoveAt(index); Save(); Show(); });
            delete.IsEnabled = !template.Builtin;
            delete.VerticalAlignment = VerticalAlignment.Top;
            customs.Children.Add(Ui.Card(Ui.Bar(null, Ui.Column(8, title, what), delete, 10), 12));
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
            actions.Children.Add(Ui.Button(L.T("刪除"), () => DeleteTemplate(template)));
            actions.Children.Add(Ui.Button(L.T("儲存"), () => { Save(); Show(); }, accent: true));
        }
        column.Children.Add(Ui.Bar(null, null, actions, 12));
        return column;
    }

    /// <summary>
    /// 多行的輸入框（用途說明、要寫什麼、額外指示，R112.3）：Enter 換行，約 3～6 行高、超過的捲動。
    /// AcceptsReturn 要在 Text 之前設：單行的 TextBox 設 Text 時只留第一行（多行的說明會被截掉，存檔就不見了）
    /// </summary>
    static TextBox MultiLine(string header, string placeholder, string text, bool enabled)
    {
        var box = new TextBox
        {
            Header = header, PlaceholderText = placeholder, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MinHeight = 72, MaxHeight = 140, Text = text, IsEnabled = enabled,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        return box;
    }

    /// <summary>WinUI 的 TextBox 把換行存成 \r：範本會放進提示詞，跟 Mac 一樣存成 \n</summary>
    static string Lines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    // MARK: 詞彙表

    // 搜尋、勾選（R94.2、R94.3、design-windows §11.27）：整頁重畫時留著；打字搜尋、勾選只重畫列表那一塊（整頁重畫時焦點會跑掉）
    string glossaryQuery = "";
    readonly HashSet<long> glossaryChecked = [];
    readonly Dictionary<long, CheckBox> glossaryBoxes = [];
    List<GlossaryTerm> glossaryTerms = [];
    StackPanel? glossaryList;
    HyperlinkButton? glossarySelect;
    Button? glossaryDelete;
    /// <summary>剛加完詞彙的說明（同主視窗的提示；設定視窗蓋在主視窗上面時看不到那邊，所以輸入框下面也寫）：顯示一次</summary>
    string? glossaryNotice;
    bool focusGlossaryInput;
    bool settingGlossaryChecks;
    bool confirmingTerms;
    /// <summary>認識的人的重新命名、移除對話框開著（同時只能開一個 ContentDialog）</summary>
    bool confirmingPerson;
    /// <summary>正在就地改的詞（R100）</summary>
    long? editingTerm;

    /// <summary>詞彙表從檔案匯入（R117.2）：.txt、.csv、.tsv，一律合併；結果寫在輸入框下面</summary>
    async Task ImportGlossaryFile()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            foreach (var ext in new[] { ".txt", ".csv", ".tsv" }) picker.FileTypeFilter.Add(ext);
            if (await picker.PickSingleFileAsync() is not { } file) return;
            glossaryNotice = host.Controller.ImportGlossary(file.Path);
        }
        catch (System.Runtime.InteropServices.COMException e) { glossaryNotice = e.Message; }
        Show();
    }

    /// <summary>詞彙表匯出成文字檔（R117.1）</summary>
    async Task ExportGlossaryFile()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = L.T("拾音詞彙表") };
            picker.FileTypeChoices.Add(L.T("純文字（txt）"), [".txt"]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            if (await picker.PickSaveFileAsync() is not { } file) return;
            glossaryNotice = host.Controller.ExportGlossary(file.Path);
        }
        catch (System.Runtime.InteropServices.COMException e) { glossaryNotice = e.Message; }
        Show();
    }

    FrameworkElement Glossary()
    {
        var column = Ui.Column(12, Ui.Text(L.T("系統名、產品名、人名的正確寫法。轉錄時會當成提示（最多約 150 字），整理時也會參考。實驗顯示，有沒有詞彙提示，專有名詞的正確率差很多。"), 13, color: Ui.Secondary));
        // 一次貼上好幾個也可以（R94.1）：換行、逗號、頓號、分號分開。多行的輸入框（最多約 4 行高，貼上好幾行才不會只留第一行），
        // Enter 送出、Shift+Enter 換行（同 Mac 的 Return 送出）
        var input = new TextBox
        {
            Header = L.T("新增詞彙"), PlaceholderText = L.T("產品名、人名；可以一次貼上好幾個"), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MaxHeight = 140,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(input, ScrollBarVisibility.Auto);
        var add = Ui.Button(L.T("新增"), () => { });
        add.IsEnabled = false;
        add.VerticalAlignment = VerticalAlignment.Bottom;
        input.TextChanged += (_, _) => add.IsEnabled = input.Text.Trim().Length > 0;
        void Add(bool focus)
        {
            if (input.Text.Trim().Length == 0) return;
            glossaryNotice = host.Controller.AddTerms(input.Text);
            focusGlossaryInput = focus;
            Show();
        }
        add.Click += (_, _) => Add(focus: false);
        input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter
                || Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
            e.Handled = true;
            Add(focus: true);
        };
        // 按 Enter 加完之後焦點留在輸入框，可以接著打下一個
        if (focusGlossaryInput)
        {
            focusGlossaryInput = false;
            input.Loaded += (_, _) => input.Focus(FocusState.Programmatic);
        }
        column.Children.Add(Ui.Bar(null, input, add));
        // 匯入、匯出（R117、M§91）：匯入一律合併
        var importLink = Ui.Link(L.T("從檔案匯入…"), () => _ = ImportGlossaryFile());
        ToolTipService.SetToolTip(importLink, L.T("加進文字檔裡的詞（.txt、.csv），已經有的跳過，原本的詞不會被刪掉"));
        var exportLink = Ui.Link(L.T("匯出…"), () => _ = ExportGlossaryFile());
        var hasTerms = host.Db.Glossary().Count > 0;
        exportLink.IsEnabled = hasTerms;
        ToolTipService.SetToolTip(exportLink, hasTerms ? L.T("存成文字檔，一行一個詞") : L.T("還沒有詞彙可以匯出"));
        column.Children.Add(Ui.Row(14, importLink, exportLink));
        if (glossaryNotice is { } notice)
        {
            glossaryNotice = null;
            column.Children.Add(Ui.Text(notice, 12, color: Ui.Secondary));
        }
        var suggestions = host.Db.GlossarySuggestions();
        if (suggestions.Count > 0)
        {
            var list = Ui.Column(6, Ui.Caption(L.T("建議加入：從參加者、說話者名字、校正過的字、會議標題找到的")));
            foreach (var s in suggestions)
                list.Children.Add(Ui.Bar(Ui.Row(8, Ui.Text(s.Term, 14), Ui.Caption(s.SourceLabel)), null,
                    Ui.Row(8, Ui.Link(L.T("忽略"), () => { host.Controller.DismissGlossarySuggestion(s.Term); Show(); }), Ui.Link(L.T("加入"), () => { host.Controller.AddTerm(s.Term); Show(); }))));
            column.Children.Add(Ui.Card(new ScrollViewer { Content = list, MaxHeight = 160 }));
        }
        var terms = glossaryTerms = host.Db.Glossary();
        if (terms.Count == 0) column.Children.Add(Ui.Text(L.T("還沒有詞彙。用詞修正時勾選「加進詞彙表」也會加到這裡。"), 13, color: Ui.Secondary));
        // 搜尋（R94.2）：超過 10 個才出現，不分大小寫
        if (terms.Count > 10)
        {
            var search = new TextBox { Header = L.T("搜尋詞彙"), PlaceholderText = L.T("輸入要找的字"), Text = glossaryQuery };
            search.TextChanged += (_, _) =>
            {
                if (search.Text == glossaryQuery) return;
                glossaryQuery = search.Text;
                FillGlossaryList();
            };
            column.Children.Add(search);
        }
        else glossaryQuery = "";
        glossaryList = Ui.Column(6);
        column.Children.Add(glossaryList);
        FillGlossaryList();
        // 公司詞彙表（R53.3）：唯讀，接在自己的詞後面；打開這頁時更新
        var company = host.Enterprise.Glossary.Terms;
        if (company.Count > 0)
        {
            var server = host.Enterprise.Account?.ServerName is { Length: > 0 } name ? name : L.T("公司");
            column.Children.Add(new Expander
            {
                Header = Ui.Column(2, Ui.Text(L.F("公司的詞彙（%lld 個）", company.Count), 14),
                    Ui.Caption(L.F("由%@的管理者維護，接在你的詞後面一起用。", server))),
                Content = Ui.Text(string.Join(L.English ? ", " : "、", company), 13, select: true), // l10n-skip
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            });
        }
        _ = host.Enterprise.RefreshGlossary();
        var prompt = Ui.Text(L.F("目前的轉錄提示：%@", WhisperRules.Prompt(CompanyGlossary.Merge(terms.Select(t => t.Term), company), S.DefaultMeetingLanguage)), 12, color: Ui.Tertiary, select: true);
        column.Children.Add(prompt);
        return column;
    }

    /// <summary>目前列出來的詞（搜尋時只有搜到的，不分大小寫）</summary>
    List<GlossaryTerm> ShownTerms()
    {
        var q = glossaryQuery.Trim();
        return q.Length == 0 ? glossaryTerms : glossaryTerms.Where(t => t.Term.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>勾選裡還在詞彙表的（勾了之後被單獨刪掉的不算，同 Mac）</summary>
    List<long> CheckedTerms() => glossaryTerms.Where(t => t.Id is long id && glossaryChecked.Contains(id)).Select(t => t.Id!.Value).ToList();

    /// <summary>
    /// 詞彙的列表（R94.3）：上面一列「全選」（搜尋時「全選搜到的 N 個」；都勾了變「取消勾選」）和「刪除勾選的 N 個…」，
    /// 每個詞前面是勾選框、右邊是刪除這一個
    /// </summary>
    void FillGlossaryList()
    {
        if (glossaryList is null) return;
        glossaryList.Children.Clear();
        glossaryBoxes.Clear();
        glossarySelect = null;
        glossaryDelete = null;
        var shown = ShownTerms();
        if (shown.Count > 0)
        {
            glossarySelect = new HyperlinkButton { Padding = new Thickness(0) };
            glossarySelect.Click += (_, _) =>
            {
                var ids = ShownTerms().Select(t => t.Id!.Value).ToList();
                SetGlossaryChecks(ids, !ids.All(glossaryChecked.Contains));
            };
            glossaryDelete = Ui.Button("", () => _ = ConfirmDeleteTerms());
            glossaryList.Children.Add(Ui.Bar(glossarySelect, null, glossaryDelete));
            UpdateGlossaryBar();
        }
        foreach (var term in shown)
        {
            var id = term.Id!.Value;
            // 就地改一個詞（R100）：Enter 儲存、Esc 取消；改成已經有的詞時合併
            if (editingTerm == id)
            {
                var edit = new TextBox { Text = term.Term, MinWidth = 200 };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(edit, L.T("詞彙"));
                void Save()
                {
                    editingTerm = null;
                    if (edit.Text.Trim().Length > 0 && edit.Text.Trim() != term.Term) host.Controller.RenameTerm(id, edit.Text);
                    Show();
                }
                void Cancel() { editingTerm = null; FillGlossaryList(); }
                edit.KeyDown += (_, e) =>
                {
                    if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; Save(); }
                    else if (e.Key == Windows.System.VirtualKey.Escape) { e.Handled = true; Cancel(); }
                };
                edit.Loaded += (_, _) => { edit.Focus(FocusState.Programmatic); edit.SelectAll(); };
                glossaryList.Children.Add(Ui.Bar(null, edit, Ui.Row(8, Ui.Link(L.T("取消"), Cancel), Ui.Link(L.T("儲存"), Save))));
                continue;
            }
            var label = Ui.Text(term.Term, 14, wrap: false);
            ToolTipService.SetToolTip(label, L.T("雙擊可以修改"));
            var box = new CheckBox { Content = label, IsChecked = glossaryChecked.Contains(id) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, term.Term);
            box.Checked += (_, _) => CheckTerm(id, true);
            box.Unchecked += (_, _) => CheckTerm(id, false);
            glossaryBoxes[id] = box;
            void StartEditing() { editingTerm = id; FillGlossaryList(); }
            // 雙擊詞改字（兩下點擊各切換一次勾選，剛好切回原本的）。CheckBox 自己處理指標、不會發 DoubleTapped
            // （掛在字上、掛在 CheckBox 加 handledEventsToo 都收不到，2026-11-16 虛擬機上按兩下沒反應），
            // 改成自己看兩次按下的間隔；第二次放開、勾選切回去之後才進入修改
            long lastPress = 0;
            var secondPress = false;
            box.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) =>
            {
                var now = Environment.TickCount64;
                secondPress = now - lastPress <= 500;
                lastPress = secondPress ? 0 : now;
            }), true);
            box.AddHandler(UIElement.PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) =>
            {
                if (!secondPress) return;
                secondPress = false;
                DispatcherQueue.TryEnqueue(StartEditing);
            }), true);
            var row = Ui.Bar(null, box, Ui.Row(4,
                Ui.IconButton(Symbol.Edit, L.F("修改「%@」", term.Term), StartEditing),
                Ui.IconButton(Symbol.Delete, L.F("刪除「%@」", term.Term), () => { host.Controller.DeleteTerm(id); Show(); })));
            // 右鍵（R100）：重新命名…、刪除
            var menu = new MenuFlyout();
            menu.Items.Add(Ui.Item(L.T("重新命名…"), StartEditing, icon: Symbol.Edit));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Ui.DangerItem(L.T("刪除"), () => { host.Controller.DeleteTerm(id); Show(); }));
            row.ContextFlyout = menu;
            row.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);   // 整列的空白處也能右鍵
            glossaryList.Children.Add(row);
        }
        if (shown.Count == 0 && glossaryTerms.Count > 0) glossaryList.Children.Add(Ui.Text(L.F("沒有含「%@」的詞彙", glossaryQuery.Trim()), 13, color: Ui.Secondary));
    }

    /// <summary>勾選改了：只換上面那一列的字（勾選框留著，焦點不會跑掉）</summary>
    void UpdateGlossaryBar()
    {
        if (glossarySelect is null || glossaryDelete is null) return;
        var ids = ShownTerms().Select(t => t.Id!.Value).ToList();
        glossarySelect.Content = ids.All(glossaryChecked.Contains) ? L.T("取消勾選")
            : glossaryQuery.Trim().Length == 0 ? L.T("全選") : L.F("全選搜到的 %lld 個", (long)ids.Count);
        var count = CheckedTerms().Count;
        glossaryDelete.Content = L.F("刪除勾選的 %lld 個…", (long)count);
        glossaryDelete.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void CheckTerm(long id, bool on)
    {
        if (settingGlossaryChecks) return;
        if (on) glossaryChecked.Add(id); else glossaryChecked.Remove(id);
        UpdateGlossaryBar();
    }

    /// <summary>「全選」「取消勾選」：列出來的那幾個一起勾或取消</summary>
    void SetGlossaryChecks(List<long> ids, bool on)
    {
        if (on) glossaryChecked.UnionWith(ids); else glossaryChecked.ExceptWith(ids);
        settingGlossaryChecks = true;
        foreach (var id in ids) if (glossaryBoxes.TryGetValue(id, out var box)) box.IsChecked = on;
        settingGlossaryChecks = false;
        UpdateGlossaryBar();
    }

    /// <summary>「刪除勾選的 N 個…」：確認對話框（沒辦法還原，預設按鈕是「取消」）</summary>
    async Task ConfirmDeleteTerms()
    {
        var ids = CheckedTerms();
        if (ids.Count == 0 || confirmingTerms || Content.XamlRoot is null) return;
        confirmingTerms = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot, Title = Ui.DialogTitle(L.F("刪除勾選的 %lld 個詞彙？", (long)ids.Count)),
                PrimaryButtonText = L.T("刪除"), CloseButtonText = L.T("取消"), DefaultButton = ContentDialogButton.Close,
            };
            if (await Ui.InterfaceLanguage(dialog).ShowAsync() != ContentDialogResult.Primary) return;
            host.Controller.DeleteTerms(ids);
            glossaryChecked.Clear();
            Show();
        }
        finally { confirmingTerms = false; }
    }

    // MARK: 說話者（M§21.4、R19.3）

    FrameworkElement Speakers()
    {
        var column = Ui.Column(12);
        column.Children.Add(Ui.Toggle(L.T("記住說話者的聲音，之後的會議自動帶入名字"), S.Voiceprints, on => S.Set(AppSettings.VoiceprintsKey, on)));
        column.Children.Add(Ui.Text(L.T("替說話者取名時，拾音會記下這個人的聲音特徵。之後的會議認出同一個人，就自動帶入名字並標「自動」，不對可以改。聲音特徵只存在這台電腦的資料庫，不會上傳，也不會放進匯出的 .shiyin 封包。"), 13, color: Ui.Secondary));
        var people = host.Db.PeopleSummaries();
        column.Children.Add(MyVoice(people));
        var deleteAll = Ui.Button(L.T("全部刪除…"), () => { });
        deleteAll.IsEnabled = people.Count > 0;
        deleteAll.Click += async (_, _) =>
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot, Title = Ui.DialogTitle(L.T("刪除所有認識的人的聲音特徵？")), Content = L.T("已經帶入的名字會留著，之後的會議不會再自動帶入。"),
                PrimaryButtonText = L.T("全部刪除"), CloseButtonText = L.T("取消"),
            };
            if (await Ui.InterfaceLanguage(dialog).ShowAsync() == ContentDialogResult.Primary) { host.Controller.DeleteAllPeople(); Show(); }
        };
        column.Children.Add(Ui.Bar(Ui.Text(L.T("認識的人"), 15, bold: true), null, deleteAll));
        if (people.Count == 0) column.Children.Add(Ui.Text(L.T("還沒有認識的人。在逐字稿點「說話者 1」改成名字，拾音就會記住這個人。"), 13, color: Ui.Secondary));
        foreach (var person in people)
        {
            var prints = L.F("%lld 份聲音特徵", person.VoiceprintCount);
            var detail = person.LastMeetingTitle is { } title && person.LastMeetingDate is { } date ? L.F("%@・最近：%@ %@", prints, date, title) : prints;
            // 用詞同人物頁（R100）：重新命名；移除前確認
            async Task Rename()
            {
                if (confirmingPerson) return;
                confirmingPerson = true;
                try
                {
                    var box = new TextBox { Text = person.Name };
                    var dialog = new ContentDialog
                    {
                        XamlRoot = Content.XamlRoot, Title = Ui.DialogTitle(L.T("重新命名")), Content = box, PrimaryButtonText = L.T("儲存"), CloseButtonText = L.T("取消"),
                        DefaultButton = ContentDialogButton.Primary,
                    };
                    if (await Ui.InterfaceLanguage(dialog).ShowAsync() == ContentDialogResult.Primary && box.Text.Trim().Length > 0) { host.Controller.RenamePerson(person.Id, box.Text.Trim()); Show(); }
                }
                finally { confirmingPerson = false; }
            }
            async Task Remove()
            {
                if (confirmingPerson) return;
                confirmingPerson = true;
                try
                {
                    var dialog = new ContentDialog
                    {
                        XamlRoot = Content.XamlRoot, Title = Ui.DialogTitle(L.F("移除「%@」？", person.Name)),
                        Content = Ui.Text(L.T("會從「人物」拿掉，也會刪掉他的聲音特徵。會議裡已經帶入的名字會留著，之後的會議不會再自動認出他。")),
                        PrimaryButtonText = L.T("移除"), CloseButtonText = L.T("取消"), DefaultButton = ContentDialogButton.Close,
                    };
                    if (await Ui.InterfaceLanguage(dialog).ShowAsync() == ContentDialogResult.Primary) { host.Controller.DeletePerson(person.Id); Show(); }
                }
                finally { confirmingPerson = false; }
            }
            var rename = Ui.Link(L.T("重新命名"), () => _ = Rename());
            // 錄過「我的聲音」的人標「（我）」（R80.4）
            var label = person.EnrolledAt is null ? person.Name : L.F("%@（我）", person.Name);
            var row = Ui.Bar(null, Ui.Column(2, Ui.Text(label, 14), Ui.Caption(detail)),
                Ui.Row(8, rename, Ui.IconButton(Symbol.Delete, L.F("移除「%@」（會議裡的名字留著）", person.Name), () => _ = Remove())));
            var menu = new MenuFlyout();
            menu.Items.Add(Ui.Item(L.T("重新命名…"), () => _ = Rename(), icon: Symbol.Edit));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Ui.DangerItem(L.T("移除這個人…"), () => _ = Remove(), icon: null, glyph: Ui.RemovePersonGlyph));
            row.ContextFlyout = menu;
            column.Children.Add(row);
        }
        // 人物頁（R85.5）：一起開過的會議、負責的待辦、改名和合併也在那裡
        if (people.Count > 0) column.Children.Add(Ui.Text(L.T("也可以在側欄的「人物」管理"), 13, color: Ui.Secondary));
        return column;
    }

    // MARK: 我的聲音（R80、design-windows §11.20、M§56.3）

    FrameworkElement MyVoice(List<PersonSummary> people)
    {
        var me = people.FirstOrDefault(p => p.EnrolledAt is not null);
        var recording = host.Recorder.IsRecording;
        var caption = me is { EnrolledAt: { } at } ? L.F("%@・已錄（%@）", me.Name, at[..Math.Min(10, at.Length)])
            : !S.Voiceprints ? L.T("先打開上面的「記住說話者的聲音」")
            : recording ? L.T("錄音中不能錄（麥克風在用）")
            : L.T("錄一段你自己的聲音，第一場會議就認得你。");
        var record = Ui.Button(me is null ? L.T("錄一段我的聲音…") : L.T("重錄…"), () => _ = RecordMyVoice(me?.Name));
        record.IsEnabled = S.Voiceprints && !recording;
        var actions = Ui.Row(8, record);
        if (me is not null)
        {
            var delete = Ui.Link(L.T("刪除"), () => { host.Controller.DeleteEnrollment(); Show(); });
            ToolTipService.SetToolTip(delete, L.T("只刪這份聲音特徵，會議裡的名字照舊"));
            actions.Children.Add(delete);
        }
        return Ui.Bar(null, Ui.Column(2, Ui.Text(L.T("我的聲音"), 14), Ui.Caption(caption)), actions);
    }

    /// <summary>錄音的對話框（M§56.3）：名字、要唸的文字、秒數；「開始」→「完成」（有聲音的部分 10 秒以上才能按）→ 算聲紋</summary>
    async Task RecordMyVoice(string? name)
    {
        var enrollment = new VoiceEnrollment(() => new WasapiSource(WasapiSource.Kind.Microphone, S.InputDevice));
        var nameBox = new TextBox { Header = L.T("名字"), Text = name ?? Environment.UserName };
        var status = Ui.Text("", 13, color: Ui.Secondary);
        var warning = Ui.Text("", 13, color: Ui.Signal);
        var passage = Ui.Tinted(new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(14),
            Child = new TextBlock { Text = MyVoicePassage, TextWrapping = TextWrapping.Wrap, FontSize = 17, LineHeight = 28 },
        }, Ui.Subtle);
        var content = Ui.Column(12,
            Ui.Text(L.T("按「開始」，用平常開會說話的音量唸下面這段（約 20～30 秒），唸完按「完成」。拾音只用這段聲音算出你的聲音特徵，算完就刪掉錄音。"), 13, color: Ui.Secondary),
            nameBox, passage, status, warning);
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot, Title = Ui.DialogTitle(L.T("錄一段我的聲音")), Content = content,
            PrimaryButtonText = L.T("開始"), CloseButtonText = L.T("取消"),
        };
        var computing = false;
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(200);
        timer.Tick += (_, _) =>
        {
            if (computing) return;
            if (enrollment.Recording)
            {
                status.Text = L.F("已錄 %lld 秒・有聲音的部分 %lld 秒（要 10 秒以上）", (long)enrollment.Seconds, (long)enrollment.VoicedSeconds);
                dialog.IsPrimaryButtonEnabled = enrollment.CanFinish && nameBox.Text.Trim().Length > 0;
            }
            else if (dialog.PrimaryButtonText == L.T("完成"))
            {
                // 到 40 秒自動停了：還是可以按「完成」
                dialog.IsPrimaryButtonEnabled = enrollment.CanFinish && nameBox.Text.Trim().Length > 0;
            }
            if (enrollment.MicSilent) warning.Text = L.T("麥克風沒有收到聲音：可能被靜音、被其他 app 佔用，或選錯了麥克風。");
        };
        timer.Start();
        dialog.PrimaryButtonClick += async (_, e) =>
        {
            e.Cancel = true;
            if (dialog.PrimaryButtonText != L.T("完成"))
            {
                warning.Text = "";
                try { enrollment.Start(); }
                catch (Exception ex) when (ex is CaptureException or System.Runtime.InteropServices.COMException)
                {
                    warning.Text = L.F("沒辦法開始錄音：%@", ex.Message);
                    return;
                }
                dialog.PrimaryButtonText = L.T("完成");
                dialog.IsPrimaryButtonEnabled = false;
                return;
            }
            var deferral = e.GetDeferral();
            var samples = enrollment.Stop();
            computing = true;
            dialog.IsPrimaryButtonEnabled = false;
            status.Text = L.T("正在算聲音特徵…第一次要先下載說話者辨識的模型");
            var error = await host.Controller.EnrollVoice(nameBox.Text.Trim(), samples);
            computing = false;
            deferral.Complete();
            if (error is null)
            {
                dialog.Hide();
                App.Current.Notice(L.F("已記住你的聲音：之後的會議聽到像你的聲音，會帶入「%@」或在待確認問你", nameBox.Text.Trim()));
                return;
            }
            warning.Text = error;
            dialog.PrimaryButtonText = L.T("重新開始");
            dialog.IsPrimaryButtonEnabled = true;
        };
        await Ui.InterfaceLanguage(dialog).ShowAsync();
        timer.Stop();
        enrollment.Stop();
        Show();
    }

    /// <summary>要唸的文字（R80.1，同 Mac 的 VoiceEnrollment.passage，「Mac」換成「電腦」）</summary>
    static string MyVoicePassage => L.T("拾音會在這台電腦上把會議錄下來，轉成逐字稿，再整理成會議紀錄。我現在唸這一段，是要讓拾音記住我的聲音。之後開會的時候，拾音聽到這個聲音，就會在逐字稿上標出我的名字。聲音特徵只存在這台電腦裡，不會上傳。下午三點，我們要討論下一季的計畫，請大家先看一下上週的數字。");

    // MARK: 錄音（R1、R108）：錄音來源、提醒與快捷鍵（原本「錄音與轉錄」的第一、四組）

    FrameworkElement RecordingSource()
    {
        var source = Ui.Column(12);
        source.Children.Add(Ui.Combo(new[] { "mic", "both", "system" }.Select(s => (s, MainWindow.SourceLabel(s))), S.RecordSource,
            s => { S.Set(AppSettings.RecordSourceKey, s); Show(); }, L.T("錄音來源")));
        // 同錄音按鈕的下拉選單（R58.1）：系統預設後面寫出現在是哪一支
        var fallback = host.DefaultMicrophone();
        var microphones = host.MicrophoneList().Select(d => (d.Id, d.Name));
        var mic = Ui.Combo(new[] { ("", fallback is null ? L.T("系統預設") : L.F("系統預設（%@）", fallback.Name)) }.Concat(microphones), S.InputDevice,
            id => S.Set(AppSettings.InputDeviceKey, id.Length == 0 ? null : id), L.T("麥克風"));
        mic.IsEnabled = S.RecordSource != "system";
        source.Children.Add(mic);
        source.Children.Add(Note(L.T("錄線上會議時選「麥克風＋電腦聲音」：你的聲音和對方的聲音會分別錄在左右聲道。")));
        source.Children.Add(Note(L.T("錄音按鈕旁的箭頭、錄音畫面的麥克風名稱也可以換麥克風。藍牙耳機連著時，Windows 可能自動改用耳機的麥克風，人不在旁邊就錄不到現場的聲音。")));
        source.Children.Add(Ui.Button(L.T("麥克風權限設定"), Shell.OpenMicrophonePrivacy));
        return source;
    }

    // MARK: 轉錄（R4、R12.4、R108）：正式轉錄、說話者辨識、即時逐字稿；語言（原本「錄音與轉錄」的第三、二組，模型清單搬到「模型與空間」）

    FrameworkElement Languages()
    {
        var language = Ui.Column(12);
        language.Children.Add(Ui.Combo(MeetingLanguages.All.Select(l => (l, l.Name())), S.DefaultMeetingLanguage, l => S.Set(AppSettings.DefaultMeetingLanguageKey, l.Code()), L.T("預設會議語言")));
        language.Children.Add(Note(L.T("新的錄音和匯入預設用這個語言轉錄、整理會議紀錄；錄音選單、匯入時和會議頁都可以改。")));
        // 中文的字（R71、M§47.3）
        language.Children.Add(Ui.Combo(new[] { ("traditional", L.T("繁體（台灣）")), ("simplified", L.T("簡體")) },
            S.ChineseScript, v => S.Set(AppSettings.ChineseScriptKey, v), L.T("中文的字")));
        language.Children.Add(Note(L.T("只影響中文會議的逐字稿、會議紀錄和匯出，介面文字不變。已經做好的會議不會變，重新轉錄或重新整理後才會換。")));
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
        language.Children.Add(Note(L.F("開始錄音後聽約 10 秒，判斷是哪一種語言（%@）；跟會議語言不同而且有把握時會自動切換，錄音畫面可以改回來。需要 60 MB 的語言偵測模型。", MeetingLanguages.UiNames(MeetingLanguages.Detectable))));
        return language;
    }

    FrameworkElement Transcription()
    {
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
            host.Gpu.OfferQwenGpu();
            host.Runner.Wake();
            Show();
        }, L.T("正式轉錄")));
        transcription.Children.Add(Note(engine switch
        {
            "server" => L.F("錄音會送到 %@（公司的伺服器）轉錄，轉完就刪；說話者辨識也在伺服器上做，聲紋只存在這台電腦。會參考詞彙表。連不上時會等，也可以改在這台電腦轉錄。", host.Enterprise.Host),
            "system" => L.T("速度快很多，下載也小；但不參考詞彙表，英文專有名詞、人名比較容易錯。"),
            "qwen" => L.T("會參考詞彙表，多人會議的漏字和同音字比 Breeze 少；阿里巴巴開源的模型，全程在這台電腦上跑、不連網，輸出會轉成台灣繁體；需要下載約 2.5 GB 的模型和 llama.cpp 的執行檔。") + OtherEngines(),
            _ => L.T("會參考詞彙表，中英夾雜、專有名詞比較準；聯發科開源的台灣模型，需要下載約 1.7 GB。") + OtherEngines(),
        }));
        transcription.Children.Add(Ui.Toggle(L.T("轉錄時辨識說話者"), S.Diarize, on => S.Set(AppSettings.DiarizeKey, on)));
        transcription.Children.Add(Ui.Toggle(L.T("錄音中顯示即時逐字稿"), S.LiveTranscription, on => S.Set(AppSettings.LiveTranscriptionKey, on)));
        return transcription;
    }

    FrameworkElement Alerts()
    {
        var alerts = Ui.Column(12);
        // 會議結束時倒數自動停止（R95、M§69）：偵測會議關掉時停用
        var autoStop = Ui.Toggle(L.T("會議結束時自動停止錄音"), S.AutoStopOnMeetingEnd, on => S.Set(AppSettings.AutoStopOnMeetingEndKey, on));
        autoStop.IsEnabled = S.MeetingDetection;
        alerts.Children.Add(Ui.Toggle(L.T("偵測到線上會議時提醒錄音"), S.MeetingDetection, on =>
        {
            S.Set(AppSettings.MeetingDetectionKey, on);
            autoStop.IsEnabled = on;
        }));
        alerts.Children.Add(autoStop);
        alerts.Children.Add(Note(L.T("Zoom、Teams、Webex、Google Meet（瀏覽器）等開始用麥克風、拾音沒在錄音時提醒你，不會自動開始錄音。會議 app 停止用麥克風時倒數 60 秒，沒按「繼續錄」就停止並轉錄；關掉「自動停止」時只問要不要停止。")));
        foreach (var app in host.Detector.Muted)
            alerts.Children.Add(Ui.Bar(null, Ui.Text(L.F("不提醒：%@", MeetingDetector.DisplayName(app)), 13), Ui.Link(L.T("恢復提醒"), () => { host.Detector.Unmute(app); Show(); })));
        // 錄音島（M§57、R81.8、design-windows §11.21）
        alerts.Children.Add(Ui.Toggle(L.T("在螢幕上方顯示錄音島"), S.RecordingIsland, on => S.Set(AppSettings.RecordingIslandKey, on)));
        alerts.Children.Add(Note(L.T("錄音中在螢幕上方正中間顯示錄音的時間和提醒，全螢幕開會、分享畫面時也看得到，分享畫面拍不到它。偵測到線上會議時的「要開始錄音嗎？」也在這裡。")));
        // 忘了停止錄音（M§35、R48.5）
        alerts.Children.Add(Ui.Toggle(L.T("很久沒人說話時提醒，30 分鐘後自動停止"), S.IdleStop, on => S.Set(AppSettings.IdleStopKey, on)));
        alerts.Children.Add(Note(L.T("錄音中 15 分鐘沒人說話時提醒你；再 15 分鐘還是沒人說話就自動停止，只轉錄到最後有人說話的地方，錄音檔完整保留。")));
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
        return alerts;
    }

    // MARK: 模型與空間（R9.4、R10.3、R108）：模型清單、儲存空間

    FrameworkElement ModelRows()
    {
        var transcribing = host.Runner.Running(JobLane.Transcribe) is not null;
        var models = Ui.Column(12);
        foreach (var (model, note) in new[]
                 {
                     (ModelStore.Model.Breeze, ""),
                     (ModelStore.Model.Qwen, MeetingLanguages.UiNames("qwen").Length == 0
                         ? L.T("中文、英文可選的引擎（阿里巴巴開源），約 2.5 GB，含 llama.cpp 的執行檔；「正式轉錄」選了它才會用。")
                         : L.F("中文、英文可選的引擎（阿里巴巴開源），約 2.5 GB，含 llama.cpp 的執行檔；「正式轉錄」選了它才會用，%@會議一律用。", MeetingLanguages.UiNames("qwen"))),
                     (ModelStore.Model.Japanese, L.F("%@會議用，574 MB。第一次轉錄這些語言的會議時會自動下載。", MeetingLanguages.UiNames("turbo"))),
                     (ModelStore.Model.FastRecognition, L.T("即時逐字稿和快速辨識用，約 240 MB。")),
                     (ModelStore.Model.Diarization, L.T("說話者辨識用，約 46 MB。")),
                     (ModelStore.Model.LanguageDetection, L.T("自動偵測會議語言用，60 MB。")),
                 })
        {
            models.Children.Add(ModelRow(model, model == ModelStore.Model.LanguageDetection ? host.Recorder.IsRecording : transcribing));
            if (note.Length > 0) models.Children.Add(Note(note));
        }
        // Qwen 的 CUDA 版（design-windows §8.4）：有 NVIDIA 顯示卡或已經下載過才顯示
        if (host.Gpu.Kind == GpuAcceleration.Pack.Cuda || host.Models.StateOf(ModelStore.Model.QwenCuda) is not ModelStore.State.Missing)
        {
            models.Children.Add(ModelRow(ModelStore.Model.QwenCuda, transcribing));
            models.Children.Add(Note(L.T("有 NVIDIA 顯示卡、選了 Qwen3-ASR 時自動下載，Qwen 的轉錄會快很多。272 MB，解開約 610 MB（llama.cpp 官方的 CUDA 版，要先有上面的 NVIDIA 顯示卡加速）。刪除後不會再自動下載。")));
        }
        // NVIDIA 顯示卡加速（R27 R4.6）：只在有 NVIDIA 顯示卡的 x64 電腦（或已經下載過）顯示
        if (host.Gpu.Kind == GpuAcceleration.Pack.Cuda || host.Models.StateOf(ModelStore.Model.GpuCuda) is not ModelStore.State.Missing)
        {
            var inUse = PackInUse(ModelStore.Model.GpuCuda);
            models.Children.Add(ModelRow(ModelStore.Model.GpuCuda, transcribing || inUse,
                inUse ? L.T("轉錄正在用這個元件，重新開啟拾音後才能刪除") : null));
            models.Children.Add(Note(L.T("有 NVIDIA 顯示卡時自動下載，轉錄會快很多。685 MB，解開約 1.1 GB（whisper.cpp 官方的 CUDA 版）。刪除後不會再自動下載。")));
        }
        // AMD 顯示卡加速（R27 R4.7）：只在有 AMD 顯示卡（沒有 NVIDIA）的 x64 電腦（或已經下載過）顯示；Qwen 的 Intel 版先不做（R4.8）
        if ((host.Gpu.Kind == GpuAcceleration.Pack.Vulkan && !host.Gpu.CheckFirst) || host.Models.StateOf(ModelStore.Model.QwenVulkan) is not ModelStore.State.Missing)
        {
            models.Children.Add(ModelRow(ModelStore.Model.QwenVulkan, transcribing));
            models.Children.Add(Note(L.F("有 AMD 顯示卡、選了 Qwen3-ASR 時自動下載，Qwen 改用顯示卡轉錄。%@，解開約 %@（llama.cpp 官方的 Vulkan 版）。刪除後不會再自動下載。",
                Recorder.ByteCount(ModelStore.QwenVulkan.Size), Recorder.ByteCount(Unpacked(ModelStore.QwenVulkan)))));
        }
        if (host.Gpu.Kind == GpuAcceleration.Pack.Vulkan || host.Models.StateOf(ModelStore.Model.GpuVulkan) is not ModelStore.State.Missing)
        {
            var inUse = PackInUse(ModelStore.Model.GpuVulkan);
            models.Children.Add(ModelRow(ModelStore.Model.GpuVulkan, transcribing || inUse,
                inUse ? L.T("轉錄正在用這個元件，重新開啟拾音後才能刪除") : null));
            // Intel：先檢查才用顯示卡，寫出規則和結果（R4.8）
            models.Children.Add(Note(host.Gpu.CheckFirst
                ? L.F("有 Intel 顯示卡時自動下載。第一次正式轉錄時，先用這場錄音的一段比對顯示卡和 CPU，結果一樣、顯示卡又比較快才改用顯示卡。%@，解開約 %@（拾音用同一版 whisper.cpp 建的 Vulkan 版）。刪除後不會再自動下載。",
                    Recorder.ByteCount(ModelStore.GpuVulkan.Size), Recorder.ByteCount(Unpacked(ModelStore.GpuVulkan)))
                : L.F("有 AMD 顯示卡時自動下載，正式轉錄改用顯示卡。%@，解開約 %@（拾音用同一版 whisper.cpp 建的 Vulkan 版）。刪除後不會再自動下載。",
                    Recorder.ByteCount(ModelStore.GpuVulkan.Size), Recorder.ByteCount(Unpacked(ModelStore.GpuVulkan)))));
            if (host.Gpu.CheckFirst && host.Models.IsInstalled(ModelStore.Model.GpuVulkan))
                models.Children.Add(Note(host.Gpu.CheckState switch
                {
                    GpuAcceleration.CheckGpu => L.T("檢查通過：正式轉錄用顯示卡。"),
                    GpuAcceleration.CheckCpu => L.T("這台電腦的顯示卡沒有通過檢查（沒有比 CPU 快、結果跟 CPU 不一樣，或出錯）：正式轉錄用 CPU。"),
                    GpuAcceleration.CheckRunning => L.T("正在檢查顯示卡。"),
                    _ => L.T("還沒檢查：下一次正式轉錄時檢查。"),
                }));
        }
        return models;
    }

    /// <summary>元件已經載入時刪不掉（DLL 在用），重新開啟拾音之後才能刪</summary>
    bool PackInUse(ModelStore.Model pack) =>
        WhisperEngine.LoadedDirectory is { } loaded && string.Equals(loaded, host.Models.Directory(pack), StringComparison.OrdinalIgnoreCase);

    /// <summary>壓縮檔解開之後的大小（解出來的檔案加起來）</summary>
    static long Unpacked(ModelStore.ModelInfo info) => info.Files.Sum(f => f.Extract?.Sum(e => e.Size) ?? f.Size);

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
            if (await Ui.InterfaceLanguage(dialog).ShowAsync() == ContentDialogResult.Primary) { host.Models.Delete(model); Show(); }
        };
        return button;
    }

    // MARK: 一般（R108）：介面語言、顯示、引導和搜尋索引、匿名使用統計（原本「進階」的一部分）

    FrameworkElement General()
    {
        var column = Ui.Column(16);
        // 介面語言（D57）：Windows 沒有替桌面 app 個別設定語言的地方
        // 語言的名稱用各自的語言寫
        var language = Ui.Combo(new[] { ("", L.T("跟著 Windows")), ("zh", "繁體中文"), ("zh-Hans", "简体中文"), ("en", "English") }, S.String(AppSettings.InterfaceLanguageKey) ?? "",  // l10n-skip
            v => { S.Set(AppSettings.InterfaceLanguageKey, v.Length == 0 ? null : v); App.Current.Notice(L.T("重新打開拾音後換成新的介面語言")); }, L.T("介面語言"));
        column.Children.Add(Section(L.T("介面"), language));
        // 會議內容的文字大小（R59.1）：快捷鍵改的也是這個設定
        var levels = Enumerable.Range(TextSizes.Min, TextSizes.Max - TextSizes.Min + 1);
        var textSize = Ui.Combo(levels.Select(l => (l, TextSizes.Label(l))), S.TextSize, l => S.Set(AppSettings.TextSizeKey, (double)l), L.T("會議內容的文字大小"));
        // 分享畫面時藏起拾音的視窗（R77、design-windows §11.19）
        var hide = Ui.Toggle(L.T("分享畫面時藏起拾音的視窗"), S.HideFromScreenSharing, on => { S.Set(AppSettings.HideFromScreenSharingKey, on); WindowCapture.Set(on); });
        column.Children.Add(Section(L.T("顯示"), textSize, Note(L.T("紀錄、逐字稿、提問、即時逐字稿和我的筆記的文字。也可以用 Ctrl+加號、Ctrl+減號調整，Ctrl+0 回到標準大小。")),
            hide, Note(L.T("打開後，用 Zoom、Teams、Google Meet 分享整個螢幕時，別人看不到拾音（例如其他會議的紀錄）。截圖、螢幕錄影也拍不到拾音。"))));
        column.Children.Add(Section(L.T("其他"),
            Ui.Button(L.T("重新顯示首次使用引導"), () => { S.Set(AppSettings.OnboardingKey, false); App.Current.ShowOnboarding(); }),
            Ui.Button(L.T("重建搜尋索引"), () => { host.Db.RebuildSearchIndex(); App.Current.Notice(L.T("搜尋索引已重建")); })));
        // 匿名使用統計（M§30.5、R40.4）：有統計網址的版本才有。放在最下面、只留一行，開關收在「詳細…」裡
        if (host.Usage.IsOffered)
            column.Children.Add(Ui.Card(Ui.Bar(Ui.Text(L.T("匿名使用統計"), 14), null, Ui.Button(L.T("詳細…"), ShowUsageStats))));
        return column;
    }

    // MARK: 檢查與更新（R10.6、R108）：目前版本、更新；回報問題、關於拾音（原本「進階」的更新、其他）

    FrameworkElement Status()
    {
        var column = Ui.Column(16);
        var update = Ui.Column(8, Ui.Bar(Ui.Text(L.T("目前版本"), 14), null, Ui.Text(host.Version, 14, color: Ui.Secondary)));
        if (host.Updater is { } updater)
        {
            (updateRows, updateLabel, updateBar) = (Ui.Column(8), null, null);
            FillUpdate(updater);
            update.Children.Add(updateRows);
        }
        else update.Children.Add(Note(L.T("這個版本沒有更新金鑰（開發版），不會檢查更新。")));
        column.Children.Add(Section(L.T("更新"), update));
        column.Children.Add(Section(L.T("其他"),
            Ui.Button(L.T("回報問題…"), () => App.Current.ShowReport()),
            Ui.Button(L.T("關於拾音"), () => App.Current.ShowAbout())));
        return column;
    }

    // MARK: 讓 AI 工具讀取會議（本地 MCP，R69、M§44.4、design-windows §11.12）

    FrameworkElement Mcp()
    {
        var blocked = host.Enterprise.Account?.Mcp == false;
        var enabled = S.McpEnabled && !blocked;
        var toggle = Ui.Toggle(L.T("讓 AI 工具讀取會議"), enabled, on => { if (on) S.Set(AppSettings.McpEnabledKey, true); else S.Remove(AppSettings.McpEnabledKey); Show(); });
        toggle.IsEnabled = !blocked;
        var column = Ui.Column(10, toggle, Note(blocked ? L.T("公司的企業伺服器關掉了這個功能。")
            : L.T("Claude Code、Claude Desktop、Codex 等支援 MCP 的 AI 工具可以搜尋、讀你的會議紀錄和逐字稿（只能讀、不能改），拾音不用開著。讀到的內容會交給那個 AI 工具處理，跟整理會議紀錄一樣會離開這台電腦。")));
        if (!enabled) return column;
        var server = McpServerPath();
        var home = Shiyin.Services.Mcp.McpSetup.CustomHome();
        var claude = host.Tools.Find("claude");
        var codex = host.Tools.Find("codex");
        var desktop = host.Locator.HasClaudeDesktop();
        var result = Ui.Text("", 13, color: Ui.Secondary, select: true);
        Button Add(string title, bool available, Func<Task<Shiyin.Services.Mcp.McpSetup.Outcome>> action)
        {
            var button = Ui.Button(title, () => { });
            button.IsEnabled = available;
            button.Click += async (_, _) =>
            {
                button.IsEnabled = false;
                result.Text = "";
                var outcome = await action();
                result.Text = outcome.Message;
                result.Foreground = outcome.Ok ? Ui.Secondary : Ui.Danger;
                button.IsEnabled = true;
            };
            return button;
        }
        // 「複製」在翻譯表是 Duplicate（複製一份範本），這裡是 Copy（同 Mac）
        var copy = new DropDownButton { Content = MeetingLanguages.Ui("複製", "Copy"), Flyout = new MenuFlyout
        {
            Items =
            {
                Ui.Item(L.T("複製 Claude Code 指令"), () => { Copy.Text(Shiyin.Services.Mcp.McpSetup.ClaudeCodeCommand(server, home)); result.Text = L.T("已複製"); }),
                Ui.Item(L.T("複製設定（JSON）"), () => { Copy.Text(Shiyin.Services.Mcp.McpSetup.ConfigJson(server, home)); result.Text = L.T("已複製"); }),
            },
        } };
        column.Children.Add(Ui.Row(8,
            Add(L.T("加到 Claude Code"), claude is not null, () => Shiyin.Services.Mcp.McpSetup.AddToClaudeCode(claude, host.Processes, server, home)),
            Add(L.T("加到 Codex"), codex is not null, () => Shiyin.Services.Mcp.McpSetup.AddToCodex(codex, host.Processes, server, home)),
            Add(L.T("加到 Claude Desktop"), desktop, () => Task.FromResult(Shiyin.Services.Mcp.McpSetup.AddToClaudeDesktop(ClaudeDesktopConfig(), server, home))),
            copy));
        var missing = new List<string>();
        if (claude is null) missing.Add("claude");
        if (codex is null) missing.Add("codex");
        if (!desktop) missing.Add("Claude Desktop");
        if (missing.Count > 0) column.Children.Add(Note(L.F("這台電腦上找不到 %@；其他支援 MCP 的工具可以用「複製」自己貼上設定。", string.Join(L.T("、"), missing))));
        column.Children.Add(result);
        return column;
    }

    /// <summary>shiyin-mcp.exe：安裝版跟 Shiyin.exe 同一個資料夾；DEBUG 可以用 SHIYIN_MCP_SERVER 指到 dotnet build 的輸出</summary>
    static string McpServerPath()
    {
#if DEBUG
        if (Environment.GetEnvironmentVariable("SHIYIN_MCP_SERVER") is { Length: > 0 } debug) return debug;
#endif
        return Shiyin.Services.Mcp.McpSetup.ServerPath(AppContext.BaseDirectory);
    }

    /// <summary>
    /// Claude Desktop 的設定檔：MSIX 版（Store、官網的安裝檔）的 AppData 被虛擬化，只讀套件自己的 LocalCache\Roaming\Claude
    /// （寫到 %APPDATA%\Claude 它讀不到，連它自己的「Edit Config」都開錯檔，anthropics/claude-code#26073）。
    /// 裝了 MSIX 版（還沒開過、資料夾還不在也算）或有那個資料夾就寫那裡，都沒有才寫 %APPDATA%\Claude（舊的安裝方式）。
    /// DEBUG 可以用 SHIYIN_CLAUDE_DESKTOP_CONFIG 指到別的檔
    /// </summary>
    static string ClaudeDesktopConfig()
    {
#if DEBUG
        if (Environment.GetEnvironmentVariable("SHIYIN_CLAUDE_DESKTOP_CONFIG") is { Length: > 0 } debug) return debug;
#endif
        var msix = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", WindowsToolLocator.ClaudeFamily,
            "LocalCache", "Roaming", "Claude");
        var installed = Directory.Exists(msix) || Packages.ByFamily(WindowsToolLocator.ClaudeFamily).Count > 0;
        var folder = installed ? msix : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");
        return Path.Combine(folder, "claude_desktop_config.json");
    }

    // MARK: 企業伺服器（R49、design-windows §12.11）

    /// <summary>登入前：網址、登入（用預設瀏覽器打開伺服器的登入頁，登入完走 shiyin://auth 回來）；登入後：帳號、轉錄和整理要不要用、登出</summary>
    FrameworkElement Enterprise()
    {
        var server = host.Enterprise;
        var column = Ui.Column(10);
        if (server.Account is { } account)
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
            column.Children.Add(Ui.Button(L.T("登出"), SignOut));
        }
        else
        {
            var url = new TextBox { Header = L.T("網址"), PlaceholderText = "https://shiyin.example.com" };
            var error = Ui.Text("", 12, color: Ui.Danger);
            url.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) SignIn(url.Text, error); };
            column.Children.Add(url);
            column.Children.Add(Ui.Row(10, Ui.Button(L.T("登入"), () => SignIn(url.Text, error), accent: true), error));
            column.Children.Add(Note(L.T("公司有拾音的企業伺服器時，填入公司給的網址、按「登入」，在打開的網頁用公司帳號登入。登入後錄音交給公司的伺服器轉錄，沒有 NVIDIA 顯示卡的電腦也很快；整理也可以用公司提供的模型。")));
        }
        return column;
    }

    /// <summary>
    /// 加入連結（R49.7、M§36.2）：讀伺服器的 info，問「加入 主機 的企業伺服器？」（伺服器給的名稱只當成自稱），加入就照「登入」
    /// （瀏覽器 → shiyin://auth）。已經登入同一台：說已經加入；登入的是別台：不先登出，新的登入成功了才換（EnterpriseServer.CompleteSignIn）。
    /// async void：連結誰都做得出來，任何例外都接住（log 只寫型別），不能讓 app 當掉
    /// </summary>
    public async void ConfirmJoin(string text)
    {
        try { await Join(text); }
        catch (Exception e)
        {
            Log.Error($"join failed: {e.GetType().Name}");
            await Message(L.T("沒辦法加入"), EnterpriseException.Of(EnterpriseFailure.NotShiyin).Message);
        }
    }

    async Task Join(string text)
    {
        var server = host.Enterprise;
        if (EnterpriseServer.Normalized(text, server.AllowLocalHttp) is not { } url)
        {
            await Message(L.T("沒辦法加入"), EnterpriseException.Of(EnterpriseFailure.InvalidUrl).Message);
            return;
        }
        var hostName = EnterpriseServer.DisplayHost(url);
        if (server.IsSignedIn && server.SavedUrl is { } saved && EnterpriseServer.SameServer(saved, url) && server.Account is { } account)
        {
            await Message(L.F("已經加入「%@」了", account.ServerName), L.F("這台電腦已經用 %@ 登入 %@。", account.Email, hostName));
            return;
        }
        EnterpriseInfo info;
        try { info = await server.FetchInfo(url); }
        catch (EnterpriseException e)
        {
            await Message(L.T("沒辦法加入"), e.Message);
            return;
        }
        var detail = L.F("這台伺服器自稱「%@」。", info.Name) + "\n\n"
            + L.F("會打開 %@ 的登入頁，用公司帳號登入。登入後錄音交給公司的伺服器轉錄，整理也用公司設定的模型。", hostName);
        if (server.Account is { } current)
            detail += "\n\n" + L.F("登入成功之後，才會登出目前的「%@」（%@）；沒有登入成功就維持原本的登入。", current.ServerName, server.Host);
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot, Title = Ui.DialogTitle(L.F("加入 %@ 的企業伺服器？", hostName)),
            Content = Ui.Text(detail, 14), PrimaryButtonText = L.T("加入"), CloseButtonText = L.T("取消"), DefaultButton = ContentDialogButton.Primary,
        };
        ContentDialogResult answer;
        try { answer = await Ui.InterfaceLanguage(dialog).ShowAsync(); }
        catch (Exception e) { Log.Error($"join dialog failed: {e.GetType().Name}"); return; }
        if (answer != ContentDialogResult.Primary) return;
        try
        {
            var attempt = await server.BeginSignIn(url.ToString());
            await Windows.System.Launcher.LaunchUriAsync(attempt.LoginUrl);
        }
        catch (EnterpriseException e) { await Message(L.T("沒辦法登入"), e.Message); }
    }

    async Task Message(string title, string text)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot, Title = Ui.DialogTitle(title), Content = Ui.Text(text, 14),
            CloseButtonText = L.T("好"), DefaultButton = ContentDialogButton.Close,
        };
        try { await Ui.InterfaceLanguage(dialog).ShowAsync(); }
        catch (Exception e) { Log.Error($"message dialog failed: {e.GetType().Name}"); }
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
        catch (Exception e)
        {
            // async void：不能讓 app 當掉（log 只寫型別）
            Log.Error($"enterprise sign-in failed: {e.GetType().Name}");
            error.Foreground = Ui.Danger;
            error.Text = EnterpriseException.Of(EnterpriseFailure.NotShiyin).Message;
        }
    }

    /// <summary>登出（R49.5）：async void，送不出去、伺服器回得怪也照樣清掉本機的登入，不能讓 app 當掉</summary>
    async void SignOut()
    {
        try { await host.Enterprise.SignOut(); }
        catch (Exception e) { Log.Error($"enterprise sign-out failed: {e.GetType().Name}"); }
        Show();
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
        try { await Ui.InterfaceLanguage(dialog).ShowAsync(); }
        catch (Exception e) { Log.Error($"usage dialog failed: {e.GetType().Name}"); }
    }
}
