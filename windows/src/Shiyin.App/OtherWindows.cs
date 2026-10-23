using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shiyin.App.Views;
using Shiyin.LLM;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Platform;
using Shiyin.Services;

namespace Shiyin.App;

/// <summary>首次使用引導、使用說明、回報問題、問所有會議（同 Mac 的 OnboardingView、GuideView、ReportProblemView、AskAllView）</summary>
public static class OtherWindows
{
    // MARK: 首次使用引導（R10.2）：歡迎 → 權限 → 下載模型 → 整理工具 → 完成，每一步都可以略過

    public static Window Onboarding(AppHost host, Action close)
    {
        var window = new Window { Title = L.T("歡迎使用拾音") };
        string[] titles = [L.T("歡迎使用拾音"), L.T("權限"), L.T("下載模型"), L.T("整理工具"), L.T("準備好了")];
        var step = 0;
        var body = new Border();
        var progress = new Grid { ColumnSpacing = 6, Margin = new Thickness(0, 0, 0, 24) };
        var title = Ui.Heading("", MeetingLanguage.Zh, 28);
        Button back = null!, skip = null!, next = null!, start = null!;
        back = Ui.Button(L.T("上一步"), () => { step--; Render(); });
        skip = Ui.Button(L.T("略過"), () => { step++; Render(); });
        next = Ui.Button(L.T("下一步"), () => { step++; Render(); }, accent: true);
        start = Ui.Button(L.T("開始使用"), () =>
        {
            host.Settings.Set(AppSettings.OnboardingKey, true);
            close();
            if (!host.Settings.Flag(AppSettings.GuideKey, false))
            {
                host.Settings.Set(AppSettings.GuideKey, true);
                App.Current.ShowGuide();
            }
        }, accent: true);
        for (var i = 0; i < titles.Length; i++) progress.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Render()
        {
            progress.Children.Clear();
            for (var i = 0; i < titles.Length; i++)
            {
                var bar = new Microsoft.UI.Xaml.Shapes.Rectangle { Height = 4, RadiusX = 2, RadiusY = 2, Fill = i <= step ? Ui.Celadon : Ui.Subtle };
                Grid.SetColumn(bar, i);
                progress.Children.Add(bar);
            }
            title.Text = titles[step];
            body.Child = step switch
            {
                0 => Welcome(),
                1 => Permissions(host),
                2 => Downloads(host),
                3 => Providers(host, Render),
                _ => Done(host),
            };
            back.Visibility = step > 0 && step < titles.Length - 1 ? Visibility.Visible : Visibility.Collapsed;
            skip.Visibility = next.Visibility = step < titles.Length - 1 ? Visibility.Visible : Visibility.Collapsed;
            start.Visibility = step == titles.Length - 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        var root = new Grid { Padding = new Thickness(32) };
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            root.RowDefinitions.Add(new RowDefinition { Height = h });
        root.Children.Add(progress);
        Grid.SetRow(title, 1);
        title.Margin = new Thickness(0, 0, 0, 12);
        root.Children.Add(title);
        var scroll = new ScrollViewer { Content = body };
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);
        var buttons = Ui.Bar(back, null, Ui.Row(8, skip, next, start));
        Grid.SetRow(buttons, 3);
        root.Children.Add(buttons);
        window.Content = Ui.WindowBackground(root);
        host.Models.Changed += _ => host.Post(() => { if (step == 2) Render(); });
        Render();
        return window;
    }

    static FrameworkElement Welcome() => Ui.Column(14,
        Ui.Text(L.T("拾音幫你把會議變成逐字稿和會議紀錄：錄音時就能看到即時逐字稿，錄完會在這台電腦上正式轉錄（中文、英文用 Breeze ASR 25，日文用 Whisper）、分辨說話者，再用 Claude、Codex 或 API 整理成摘要、決議和待辦。")),
        Ui.Text("🔒 " + L.T("錄音和轉錄都只在這台電腦上進行，不會上傳。")),
        Ui.Text("💬 " + L.T("整理會議紀錄時，只會把逐字稿文字送到你選的 AI 服務。")),
        Ui.Text(L.T("接下來三個步驟大約 3 分鐘（下載模型的時間另計）。"), color: Ui.Secondary));

    static FrameworkElement Permissions(AppHost host)
    {
        FrameworkElement Row(string title, string detail, string button, Action action) =>
            Ui.Bar(null, Ui.Column(4, Ui.Text(title, 15, bold: true), Ui.Text(detail, 13, color: Ui.Secondary)), Ui.Button(button, action), 14);
        return Ui.Column(18,
            Row(L.T("麥克風"), L.T("錄現場會議或你自己的聲音。Windows 的「隱私權與安全性 → 麥克風」要允許桌面 app 使用麥克風。"), L.T("打開設定"), Shell.OpenMicrophonePrivacy),
            Row(L.T("電腦聲音"), L.T("錄線上會議（Zoom、Meet、Teams）對方的聲音。Windows 不用另外允許，錄音時選「麥克風＋電腦聲音」就好。"), L.T("知道了"), () => { }),
            Row(L.T("通知"), host.Notifier.Enabled ? L.T("轉錄完成、會議紀錄整理好時通知你。已允許。") : L.T("轉錄完成、會議紀錄整理好時通知你。通知被關掉時，可以在 Windows 的「系統 → 通知」打開。"),
                L.T("打開設定"), () => Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:notifications")).AsTask()));
    }

    static FrameworkElement Downloads(AppHost host)
    {
        var models = host.Models;
        var primary = ModelStore.Transcription(host.Settings.DefaultMeetingLanguage);
        // 快速辨識先下載（小、下載好就能錄音），再下載正式轉錄的模型（design-windows §8）
        models.AutoStart(ModelStore.Model.FastRecognition);
        models.AutoStart(ModelStore.Model.Diarization);
        if (!host.Settings.UseFastTranscriber) models.AutoStart(primary);
        if (host.Settings.AutoDetectLanguage) models.AutoStart(ModelStore.Model.LanguageDetection);
        FrameworkElement Row(ModelStore.Model model, string label)
        {
            var state = models.StateOf(model);
            var status = state switch
            {
                ModelStore.State.Installed => Ui.Text(L.T("已下載"), 12, color: Ui.Celadon),
                ModelStore.State.Downloading { Total: > 0 } d => (UIElement)Ui.Column(2,
                    new ProgressBar { Maximum = d.Total, Value = d.Received, Width = 240, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) },
                    Ui.Caption(L.F("%@／%@", Services.Capture.Recorder.ByteCount(d.Received), Services.Capture.Recorder.ByteCount(d.Total)))),
                ModelStore.State.Failed f => Ui.Text(f.Message, 12, color: Ui.Danger),
                ModelStore.State.Verifying => Ui.Caption(L.T("正在驗證檔案…")),
                _ => Ui.Caption(L.T("還沒下載")),
            };
            FrameworkElement? button = state is ModelStore.State.Missing or ModelStore.State.Paused or ModelStore.State.Failed
                ? Ui.Button(L.T("下載"), () => models.Start(model)) : null;
            return Ui.Bar(null, Ui.Column(4, Ui.Text(label, 14), status), button, 12);
        }
        return Ui.Column(16,
            Ui.Text(L.T("模型只需要下載一次，之後轉錄都不需要網路。可以先按「下一步」，在背景繼續下載；快速辨識的模型下載好就能錄音，正式轉錄的模型下載好之前先用快速辨識。"), color: Ui.Secondary),
            Row(ModelStore.Model.FastRecognition, L.T("快速辨識模型 SenseVoice：即時逐字稿，約 240 MB")),
            primary == ModelStore.Model.Japanese
                ? Row(ModelStore.Model.Japanese, L.F("%@：日文會議的正式轉錄，574 MB", ModelStore.Japanese.Name))
                : Row(ModelStore.Model.Breeze, L.F("%@：正式轉錄，1.66 GB", ModelStore.Breeze.Name)),
            Row(ModelStore.Model.Diarization, L.T("說話者辨識模型：分辨是誰在說話，約 46 MB")),
            // 有 NVIDIA 顯示卡時啟動就自動下載了（GpuAcceleration.Start）
            host.Gpu.Applies ? Row(ModelStore.Model.GpuCuda, L.T("NVIDIA 顯示卡加速：轉錄會快很多，685 MB（解開約 1.1 GB）")) : null);
    }

    static FrameworkElement Providers(AppHost host, Action render)
    {
        var settings = host.Settings;
        var hasClaude = host.Tools.Find("claude") is not null;
        var hasCodex = host.Tools.Find("codex") is not null;
        var noCli = !hasClaude && !hasCodex;
        // 沒選過時顯示自動選到的那一個；找不到 claude、codex 時是「之後再設定」（null，R35.3、M§19.6）。改了才寫進設定
        LLMProvider? selected = settings.DebugProvider ?? settings.ChosenProvider ?? (hasClaude ? LLMProvider.Claude : hasCodex ? LLMProvider.Codex : null);
        var current = selected ?? LLMProvider.None;
        var group = new RadioButtons();
        List<(LLMProvider? Provider, string Label)> options =
        [
            (LLMProvider.Claude, L.T("Claude（claude CLI）") + (hasClaude ? "" : L.T("：沒有安裝（給已經在用 Claude Code 的人）"))),
            (LLMProvider.Codex, L.T("Codex（codex CLI）") + (hasCodex ? "" : L.T("：沒有安裝（給已經在用 Codex 的人）"))),
            (LLMProvider.Anthropic, L.T("Anthropic API（填 API key）")), (LLMProvider.OpenAI, L.T("OpenAI API（填 API key）")),
            (LLMProvider.Gemini, L.T("Gemini API（填 API key）")), (LLMProvider.OpenAICompatible, L.T("OpenAI 相容 API（Groq、OpenRouter、Ollama、LM Studio）")),
            (LLMProvider.None, L.T("不整理，只要逐字稿")),
        ];
        if (noCli) options.Insert(0, (null, L.T("之後再設定（先不整理）")));
        foreach (var (_, label) in options) group.Items.Add(label);
        // RadioButtons 還沒載入前設 SelectedIndex 不會顯示（2026-10-21 在虛擬機看到），載入後再設
        var index = options.FindIndex(o => o.Provider == selected);
        group.Loaded += (_, _) => { if (group.SelectedIndex != index) group.SelectedIndex = index; };
        group.SelectionChanged += (_, _) =>
        {
            if (group.SelectedIndex < 0 || group.SelectedIndex == index) return;
            if (options[group.SelectedIndex].Provider is { } chosen) settings.Set(AppSettings.ProviderKey, chosen.Code());
            else settings.Remove(AppSettings.ProviderKey);
            render();
        };
        var column = Ui.Column(14, Ui.Text(L.T("會議紀錄要用哪個 AI 整理？之後可以在設定裡更改。"), color: Ui.Secondary));
        if (noCli)
            column.Children.Add(Ui.Text(L.T("這台電腦沒有 Claude Code 或 Codex。要用 AI 整理會議紀錄，選一個 API 並貼上 API key（有取得 key 的連結）；也可以之後再設定，錄音和逐字稿照常能用。"), color: Ui.Secondary));
        column.Children.Add(group);
        if (current == LLMProvider.OpenAICompatible)
            column.Children.Add(Ui.Text(L.T("網址、API key 和模型請在完成引導後，到「設定 → 整理工具」填寫。"), color: Ui.Secondary));
        else if (current.UsesApiKey())
        {
            var key = new PasswordBox { PlaceholderText = L.T("貼上 API key") };
            var status = Ui.Text(host.Keys.Exists(current.KeyAccount()) ? L.T("已儲存在認證管理員") : "", 12, color: Ui.Celadon);
            column.Children.Add(Ui.Bar(null, key, Ui.Button(L.T("儲存"), () =>
            {
                try
                {
                    if (host.Keys is CredentialStore store) store.Save(current.KeyAccount(), key.Password.Trim());
                    else if (host.Keys is MemoryKeyStore memory) memory.Keys[current.KeyAccount()] = key.Password.Trim();
                    status.Text = L.T("已儲存在認證管理員");
                    status.Foreground = Ui.Celadon;
                }
                catch (InvalidOperationException e) { status.Text = e.Message; status.Foreground = Ui.Danger; }
            })));
            column.Children.Add(status);
            if (current.ApiKeyUrl() is { } url)
                column.Children.Add(Ui.Link(L.T("取得 API key"), () => _ = Windows.System.Launcher.LaunchUriAsync(new Uri(url))));
        }
        else if (current is LLMProvider.Claude or LLMProvider.Codex)
            column.Children.Add(Ui.Text(L.T("要先安裝並登入對應的 CLI。登入後可以在「設定 → 整理工具」按「測試整理工具」確認。"), 13, color: Ui.Secondary));
        return column;
    }

    static FrameworkElement Done(AppHost host) => Ui.Column(14,
        Ui.Text(L.T("按「開始使用」後就可以錄音或匯入了。")),
        Ui.Text(L.T("・錄音：側欄上方的「錄音」（Ctrl+R），或在其他 app 裡按 Ctrl+Alt+Shift+R")),
        Ui.Text(L.T("・匯入：把錄音檔拖進視窗，或按「匯入」（Ctrl+O）")),
        Ui.Text(L.T("・拾音會常駐在工作列右下角的系統匣，關掉視窗也會繼續錄音和整理。")),
        Ui.Text(L.T("之後隨時可以從系統匣或設定的「進階」再打開使用說明。"), color: Ui.Secondary));

    // MARK: 使用說明（R10.4）：提到的按鈕、分頁、選單名稱都要和介面上的文字一致

    public static Window Guide()
    {
        var window = new Window { Title = L.T("拾音使用說明") };
        (string Title, string Body)[] steps =
        [
            (L.T("錄音或匯入"), L.T("按側欄上方的「錄音」（Ctrl+R）開始錄；點按鈕旁的箭頭可以選錄音來源，線上會議請選「麥克風＋電腦聲音（線上會議）」，對方的聲音也會錄進來。已經有錄音檔的話，拖進視窗、按「匯入」（Ctrl+O），或在檔案總管按右鍵用拾音開啟都可以。")),
            (L.T("邊錄邊看、邊記"), L.T("錄音中畫面左邊是即時逐字稿，講完幾秒內就會出現；右邊「我的筆記」每換一行會自動記下當時的錄音時間。重要的地方按「標記重點」（Ctrl+B），在其他 app 裡也可以按 Ctrl+Alt+Shift+B。需要中斷時按「暫停」（Ctrl+Shift+P），暫停的時間不會錄進去。")),
            (L.T("正式轉錄、分辨說話者"), L.T("按「停止並轉錄」後，拾音會在這台電腦上重新轉錄（中文、英文用 Breeze ASR 25，日文用 Whisper large-v3-turbo），並分辨不同的說話者。等待的時候，會議紀錄會先用即時逐字稿整理一份草稿，正式版好了會自動換掉。逐字稿上點「說話者 1」可以改成人名，整場都會跟著改；拾音會記住這個人的聲音，之後的會議自動帶入名字（標「自動」，認錯了點一下就能改）。")),
            (L.T("看會議紀錄"), L.T("轉錄完會自動整理。「摘要」有重點、決議、待辦和未決問題；「詳細」依議題分段；每一條後面的時間點點一下，就從錄音那裡開始播放。範本選單可以換成一對一、訪談、課堂／演講、腦力激盪等格式，換完從「更多」選「重新整理會議紀錄」。想找某件事，可以在「提問」分頁直接問，回答會附上時間點；側欄上方的提問按鈕可以一次問所有會議。")),
            (L.T("回答待確認、修改"), L.T("「待確認」列出整理時沒把握的人名、專有名詞、負責人和期限。紀錄裡有疑問的那一條旁邊會有「？」，點了就能當場回答；也可以到「待確認」分頁一次看完。能用是或否回答的直接按「是」「不是」（按「是」不用等整理工具），其他的在「你的回答」欄位回答，按「送出」，會議紀錄就會依你的答案修正；看不懂或不重要的按「略過」，或一次「全部略過」。也可以在「摘要」按「編輯」直接改。改錯了可以從「更多」選「復原上一版會議紀錄」。")),
            (L.T("匯出、分享"), L.T("從「更多」選「匯出…」（Ctrl+E）：會議紀錄可以存成 Markdown、PDF、Word；逐字稿可以存成 txt、srt、vtt、Word；錄音可以存成 m4a、wav；「整場會議（.shiyin）」可以傳給同事，對方雙擊就能匯入（Mac 版也可以）。「複製會議紀錄」可以直接貼到 Notion、Slack 或郵件。")),
        ];
        var column = Ui.Column(26, Ui.Column(8, Ui.Heading(L.T("拾音使用說明"), MeetingLanguage.Zh, 30),
            Ui.Text(L.T("拾音把會議變成逐字稿和會議紀錄。錄音、轉錄、說話者辨識都在這台電腦上完成。"), color: Ui.Secondary)));
        for (var i = 0; i < steps.Length; i++)
        {
            var number = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = Ui.Celadon, VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Text = $"{i + 1}", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White) },
            };
            column.Children.Add(Ui.Split(number, Ui.Column(6, Ui.Heading(steps[i].Title, MeetingLanguage.Zh), Ui.Text(steps[i].Body, select: true)), 14));
        }
        // 沒有的項目給 null（例如沒有統計網址的版本不寫匿名使用統計）
        FrameworkElement Section(string title, params string?[] bullets)
        {
            var s = Ui.Column(10, Ui.Heading(title, MeetingLanguage.Zh));
            foreach (var b in bullets.OfType<string>()) s.Children.Add(Ui.Split(Ui.Text("•", color: Ui.Secondary), Ui.Text(b, select: true), 8));
            return s;
        }
        column.Children.Add(Section(L.T("找會議、播放"),
            L.T("側欄上方的「搜尋所有會議」會同時搜標題、逐字稿、會議紀錄和你的筆記，點結果就跳到那一段。"),
            L.T("點任何時間點或視窗底部的時間軸就從那裡播放，空白鍵播放或暫停；播放列右邊可以選 0.75×～2× 的速度。"),
            L.T("逐字稿裡琥珀色的段落辨識信心比較低，值得回頭聽；時間軸上的三角形是待確認題目、圓點是你標的重點。"),
            L.T("會議可以分資料夾：在會議上按右鍵（或「更多」）選「移到資料夾」→「新資料夾…」。正在看某個資料夾時，新錄的和匯入的會議都會放進去。"),
            L.T("刪除的會議會先放進側欄最下面的「垃圾桶」，30 天內都可以還原。"),
            L.T("說話者分得太少（人多、吵雜、手機放在桌上遠距收音）時，從「更多」選「重新辨識說話者…」，告訴拾音大約幾個人。"),
            L.T("說話者分錯了：同一個人被分成兩位，點說話者的名字選「合併到…」；某幾句分錯人，在逐字稿按右鍵選「這句是誰說的」。"),
            L.T("開 Zoom、Teams、Google Meet 等線上會議時，拾音會提醒你開始錄音；會議好像結束時提醒停止。"),
            L.T("遇到問題，可以從設定的「進階」選「回報問題…」，會附上版本和環境（不含會議內容）。")));
        column.Children.Add(Section(L.T("資料與隱私"),
            L.T("錄音、逐字稿和會議紀錄都只存在這台電腦上（%LOCALAPPDATA%\\Shiyin），不會跟著帳號漫遊。"),
            L.T("即時逐字稿、正式轉錄、說話者辨識都在本機進行，不需要網路。"),
            L.T("整理、回答待確認、提問、猜說話者時，會把逐字稿文字、你的筆記和詞彙表送到你選的整理工具，錄音檔不會送出。選「不整理」就完全離線。"),
            L.T("聲紋（說話者的聲音特徵，一組數字，不是錄音）只存在這台電腦，不會放進匯出的 .shiyin 封包。"),
            L.T("API key 存在 Windows 的認證管理員，不會寫進任何檔案。"),
            // 匿名使用統計（M§30.5）：有統計網址的版本才寫
            AppHost.Current.Usage.IsOffered ? L.T("每天送一份匿名的使用統計（版本、系統、錄音和轉錄的分鐘數、用了哪些功能、失敗次數），不含任何會議內容。在設定 → 進階可以關掉。") : null));
        column.Children.Add(Section(L.T("快捷鍵"),
            L.F("%@：%@", "Ctrl+R", L.T("開始或停止錄音")),
            L.F("%@：%@", "Ctrl+Alt+Shift+R", L.T("在其他 app 裡也能開始或停止錄音（可以在設定 → 錄音關掉）")),
            L.F("%@：%@", "Ctrl+Shift+P", L.T("暫停或繼續錄音")),
            L.F("%@：%@", "Ctrl+B", L.T("錄音中標記重點")),
            L.F("%@：%@", "Ctrl+Alt+Shift+B", L.T("錄音中在其他 app 裡也能標記重點")),
            L.F("%@：%@", "Ctrl+O", L.T("匯入")), L.F("%@：%@", "Ctrl+E", L.T("匯出")),
            L.F("%@：%@", L.T("空白鍵"), L.T("播放或暫停")),
            L.F("%@：%@", "Ctrl+1～Ctrl+6", L.T("切換摘要、詳細、逐字稿、待確認、筆記、提問")),  // l10n-skip
            L.F("%@：%@", "Ctrl+F", L.T("搜尋逐字稿（在「逐字稿」）"))));
        window.Content = Ui.WindowBackground(Ui.Scroll(new ReadingColumn(column, 680), 36));
        return window;
    }

    // MARK: 回報問題（M§21.9、R19.7）：先讓使用者看會附上的資訊，按「在 GitHub 回報」才打開瀏覽器

    public static Window Report(AppHost host)
    {
        var window = new Window { Title = L.T("回報問題") };
        var text = ProblemReport.Text(host.Db, host.Settings, SystemInfo.Collect(host.Version, host.Version, Whisper.WhisperEngine.GpuDevice));
        // AcceptsReturn 要在 Text 之前設：單行的 TextBox 設 Text 時只留第一行
        var content = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Text = text, IsReadOnly = true, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas") };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(content, L.T("回報問題"));
        var copied = Ui.Text("", 13, color: Ui.Secondary);
        var buttons = Ui.Row(8,
            Ui.Button(L.T("關閉"), window.Close),
            // 「複製」在範本是 Duplicate，這裡是 Copy（說明文字也寫 "Copy"）
            Ui.Button(MeetingLanguages.Ui("複製", "Copy"), () => { Copy.Text(text); copied.Text = L.T("已複製"); }),
            Ui.Button(L.T("在 GitHub 回報"), () => { Shell.OpenUrl(ProblemReport.Url(text)); window.Close(); }, accent: true));
        var grid = new Grid { Padding = new Thickness(20), RowSpacing = 12 };
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            grid.RowDefinitions.Add(new RowDefinition { Height = h });
        grid.Children.Add(Ui.Heading(L.T("回報問題"), MeetingLanguage.Zh, 20));
        var intro = Ui.Text(L.T("按「在 GitHub 回報」會用瀏覽器打開新的 issue，並帶入下面的內容，你可以在瀏覽器裡修改後再送出。內容只有版本、環境和錯誤的種類，不包含會議內容、檔名或 API key。沒有 GitHub 帳號的話，可以按「複製」用其他方式寄給維護的人。"), 13, color: Ui.Secondary);
        Grid.SetRow(intro, 1);
        grid.Children.Add(intro);
        Grid.SetRow(content, 2);
        grid.Children.Add(content);
        var bar = Ui.Bar(copied, null, buttons);
        Grid.SetRow(bar, 3);
        grid.Children.Add(bar);
        window.Content = Ui.WindowBackground(grid);
        return window;
    }

    // MARK: 關於拾音（R13.1、R27 R13.1）：版本、用到的軟體和模型；完整的授權條款很長（約 80 萬字元），用預設的文字編輯器打開

    /// <summary>scripts/windows/make-third-party-licenses.py 產生，安裝資料夾裡也是這個檔名</summary>
    public static string LicensesPath => Path.Combine(AppContext.BaseDirectory, "Third-Party Licenses.txt");

    public static Window About(AppHost host)
    {
        var window = new Window { Title = L.T("關於拾音") };
        var licenses = File.Exists(LicensesPath) ? File.ReadAllText(LicensesPath) : null;
        // 開頭的清單到第一段條文前（「====」那一行）
        var end = licenses?.IndexOf("\n" + new string('=', 80), StringComparison.Ordinal) ?? -1;
        var summary = licenses is null ? L.T("找不到授權條款的檔案，請重新安裝拾音。") : end > 0 ? licenses[..end].TrimEnd() : licenses;
        var full = Ui.Button(L.T("完整的授權條款"), () => Shell.OpenFile(LicensesPath));
        full.IsEnabled = licenses is not null;
        var grid = new Grid { Padding = new Thickness(24), RowSpacing = 14 };
        foreach (var h in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            grid.RowDefinitions.Add(new RowDefinition { Height = h });
        grid.Children.Add(Ui.Column(4,
            Ui.Heading(L.T("拾音"), MeetingLanguage.Zh, 26),
            Ui.Text(L.F("版本 %@", host.Version), 14, color: Ui.Secondary),
            // 贊助（R13.3，同 Mac 的 Credits.html 第一行）：只放連結，用預設瀏覽器打開
            Ui.Link(L.T("覺得拾音好用的話，可以請我喝杯咖啡。"), () => Shell.OpenUrl(ProblemReport.SponsorUrl)),
            Ui.Text(L.T("拾音是 MIT 授權的開源軟體，用到下列軟體和模型："), 13, color: Ui.Secondary)));
        var list = new ScrollViewer { Content = new TextBlock { Text = summary, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 } };
        Grid.SetRow(list, 1);
        grid.Children.Add(list);
        var buttons = Ui.Bar(Ui.Row(8, Ui.Button("GitHub", () => Shell.OpenUrl(ProblemReport.RepositoryUrl)), full), null,  // l10n-skip
            Ui.Button(L.T("關閉"), window.Close, accent: true));
        Grid.SetRow(buttons, 2);
        grid.Children.Add(buttons);
        window.Content = Ui.WindowBackground(grid);
        return window;
    }

    // MARK: 問所有會議（M§21.8）：可以一邊看會議一邊問；出處點了打開那場會議

    public static Window AskAll(AppHost host, MainWindow main)
    {
        var window = new Window { Title = L.T("問所有會議") };
        var input = new TextBox { PlaceholderText = L.T("問所有會議，例如：寄信的機制最後怎麼決定的？"), TextWrapping = TextWrapping.Wrap };
        var scope = AskScope.All;
        var list = new StackPanel { Spacing = 18, Padding = new Thickness(24) };
        var scroll = new ScrollViewer { Content = list };
        CancellationTokenSource? asking = null;
        var scopes = new SelectorBar();
        foreach (var s in Enum.GetValues<AskScope>())
            if (s != AskScope.Folder || main.State.FolderFilter is not null)
                scopes.Items.Add(new SelectorBarItem { Text = s.Label(), Tag = s, IsSelected = s == scope });
        scopes.SelectionChanged += (_, _) => { if (scopes.SelectedItem?.Tag is AskScope s) scope = s; };
        void Render()
        {
            list.Children.Clear();
            var records = host.Db.Questions(null);
            if (records.Count == 0 && asking is null)
                list.Children.Add(Ui.Text(L.T("例如：「上個月跟客戶談過哪些價格？」「寄信的機制最後怎麼決定的？」拾音會先找出相關的會議片段，再請整理工具回答，回答附上是哪一場會議、哪個時間點。"), color: Ui.Secondary));
            foreach (var record in records)
                list.Children.Add(AskTab.QuestionCard(record, id => main.State.Meetings.FirstOrDefault(m => m.Id == id)?.Meeting.DisplayTitle, _ => { },
                    (id, time) =>
                    {
                        main.Reveal(id);
                        main.State.SetTab(DetailTab.Transcript);
                        if (TimeFormat.Seconds(time) is double s) host.Player.Seek(s, play: false);
                    }));
            if (asking is not null)
                list.Children.Add(Ui.Row(10, new ProgressRing { IsActive = true, Width = 16, Height = 16 },
                    Ui.Text(L.F("正在找相關的會議，再用 %@ 回答…", host.Settings.Provider.ShortLabel()), color: Ui.Secondary), Ui.Link(L.T("取消"), () => asking?.Cancel())));
            scroll.ChangeView(null, double.MaxValue, null, true);
        }
        async void Submit()
        {
            var question = input.Text.Trim();
            if (question.Length == 0 || asking is not null || host.Ask.UnavailableReason is not null) return;
            input.Text = "";
            asking = new CancellationTokenSource();
            Render();
            try { await Task.Run(() => host.Ask.AskAll(question, scope, main.State.FolderFilter, asking.Token)); }
            catch (OperationCanceledException) { }
            catch (Exception e) when (e is LLMException or HttpRequestException or IOException or TimeoutException)
            {
                if (!asking.IsCancellationRequested) main.ShowError(e.Message);
            }
            finally
            {
                asking = null;
                Render();
            }
        }
        input.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { Submit(); e.Handled = true; } };
        var head = Ui.Bar(Ui.Heading(L.T("問所有會議"), MeetingLanguage.Zh, 20), null, scopes);
        head.Padding = new Thickness(24, 14, 24, 14);
        var bar = AskTab.InputBar(input, Submit, () => { host.Db.DeleteQuestions(null); Render(); }, host);
        var grid = new Grid();
        foreach (var h in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            grid.RowDefinitions.Add(new RowDefinition { Height = h });
        grid.Children.Add(head);
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);
        Grid.SetRow(bar, 2);
        grid.Children.Add(bar);
        window.Content = Ui.WindowBackground(grid);
        Render();
        return window;
    }
}

/// <summary>
/// 偵測到會議、沒有通知權限時的提醒（同 Mac 的 MeetingPromptPanel）：不搶焦點的小視窗，右下角，30 秒後自己收起
/// </summary>
public sealed class MeetingPromptWindow : Window
{
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(30) };
    public MeetingDetector.Prompt Prompt { get; }

    public MeetingPromptWindow(AppHost host, MeetingDetector.Prompt prompt)
    {
        Prompt = prompt;
        Title = L.T("拾音");
        string message;
        var buttons = Ui.Row(8);
        void Do(Action action) { Close(); action(); }
        if (prompt is MeetingDetector.Prompt.Start)
        {
            message = L.F("%@ 正在開會，要開始錄音嗎？", prompt.App.Name);
            buttons.Children.Add(Ui.Button(L.T("這次不用"), () => Do(() => { })));
            buttons.Children.Add(Ui.Button(L.T("不要再提醒這個 app"), () => Do(() => host.Detector.Mute(prompt.App.Id))));
            buttons.Children.Add(Ui.Button(L.T("開始錄音"), () => Do(() => host.Controller.StartRecording("")), accent: true));
        }
        else
        {
            message = L.T("會議好像結束了，要停止錄音嗎？");
            buttons.Children.Add(Ui.Button(L.T("繼續錄"), () => Do(() => { })));
            buttons.Children.Add(Ui.Button(L.T("停止並轉錄"), () => Do(host.Recorder.Stop), accent: true));
        }
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        Content = Ui.WindowBackground(new Border { Padding = new Thickness(16), Child = Ui.Column(12, Ui.Text(message, 14), buttons) });
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
            presenter.IsResizable = false;
        }
        timer.Tick += (_, _) => Close();
        Closed += (_, _) => timer.Stop();
    }

    /// <summary>右下角、不搶焦點</summary>
    public void Show()
    {
        var area = DisplayArea.Primary.WorkArea;
        var scale = WindowPlacement.Scale(this);
        WindowPlacement.FollowTheme(this);
        int w = (int)(400 * scale), h = (int)(160 * scale), margin = (int)(20 * scale);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + area.Width - w - margin, area.Y + area.Height - h - margin, w, h));
        AppWindow.Show(activateWindow: false);
        timer.Start();
    }
}
