using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppLifecycle;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Platform;
using Shiyin.Services;
using Shiyin.Text;
using Shiyin.Services.Capture;

namespace Shiyin.App;

public static class Program
{
    /// <summary>
    /// 進入點（design-windows §12.1）：Velopack 的安裝、更新 hook 先跑；已經開著拾音的話，把參數（要開的檔案）轉給它再結束
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
#if DEBUG
        // 驗證用：啟動時丟出的例外都記下來（%TEMP%\shiyin-exceptions.log），WinUI 在啟動中當掉時只看得到 0xc000027b
        var trace = Path.Combine(Path.GetTempPath(), "shiyin-exceptions.log");
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            try { File.AppendAllText(trace, $"{DateTime.Now:HH:mm:ss.fff} {e.Exception.GetType().FullName} 0x{e.Exception.HResult:X8}: {e.Exception.Message}\n{Environment.StackTrace}\n\n"); }
            catch (IOException) { }
        };
#endif
        Velopack.VelopackApp.Build()
            .OnAfterInstallFastCallback(_ =>
            {
                AppHost.UseSavedInterfaceLanguage();
                FileAssociations.Register();
                Shortcuts.Create(L.T("拾音"));
            })
            .OnAfterUpdateFastCallback(_ =>
            {
                AppHost.UseSavedInterfaceLanguage();
                Shortcuts.Rename(L.T("拾音"));
            })
            .OnBeforeUninstallFastCallback(_ =>
            {
                FileAssociations.Unregister();
                Notifier.Unregister();
                Shortcuts.Remove();
            })
            .Run();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        var instance = AppInstance.FindOrRegisterForKey("ShiyinMain");
        if (!instance.IsCurrent)
        {
            // 在別的執行緒等轉送完成（直接在 STA 的主執行緒上 Wait 可能卡住）
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            Task.Run(() => instance.RedirectActivationToAsync(activation).AsTask()).Wait(TimeSpan.FromSeconds(10));
            return 0;
        }
        instance.Activated += (_, e) => App.Current?.Activated(e);
        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            new App(args);
        });
        return 0;
    }
}

/// <summary>
/// app（同 Mac 的 ShiyinApp、AppDelegate）：主視窗、設定、引導、說明、回報問題、問所有會議、系統匣、偵測會議的提醒、結束
/// </summary>
public sealed class App : Application, Microsoft.UI.Xaml.Markup.IXamlMetadataProvider
{
    public static new App Current { get; private set; } = null!;

    // 畫面全部用 C# 建（D59），沒有 XAML 編譯器產生的型別資訊：WinUI 讀控制項的主題（XamlControlsResources）時
    // 要靠這個找到控制項程式庫的型別，不然讀到一半就失敗（「Cannot find a resource with the given key」，啟動就當掉）
    readonly Microsoft.UI.Xaml.XamlTypeInfo.XamlControlsXamlMetaDataProvider xamlTypes = new();
    public Microsoft.UI.Xaml.Markup.IXamlType GetXamlType(Type type) => xamlTypes.GetXamlType(type);
    public Microsoft.UI.Xaml.Markup.IXamlType GetXamlType(string fullName) => xamlTypes.GetXamlType(fullName);
    public Microsoft.UI.Xaml.Markup.XmlnsDefinition[] GetXmlnsDefinitions() => xamlTypes.GetXmlnsDefinitions();

    readonly string[] args;
    AppHost? host;
    MainWindow? main;
    SettingsWindow? settings;
    Window? onboarding, guide, report, about, askAll;
    MeetingPromptWindow? prompt;
    WinUIEx.TrayIcon? tray;

    public App(string[] args)
    {
        Current = this;
        this.args = args;
        UnhandledException += (_, e) => { Log.Error($"unhandled: {e.Exception.GetType().Name}"); e.Handled = true; };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs launch)
    {
        // 畫面全部用 C# 建，WinUI 控制項的樣式要自己加進資源（沒有 App.xaml）
        Ui.ApplyAccent(Resources);
        Resources.MergedDictionaries.Add(new XamlControlsResources());
        try { host = new AppHost(DispatcherQueue.GetForCurrentThread(), args); }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            // 遷移在交易裡進行，失敗時原本的資料不會被改到（N3）；說明清楚後結束，不要直接閃退
            ShowFatal(e.Message);
            return;
        }
        host.ShowMain = id => { if (id is long m) ShowMeeting(m); else ShowMain(); };
        host.ShowMeetingPrompt = ShowMeetingPrompt;
        main = new MainWindow(host);
        Pdf.Window = main.Handle;
        main.Activate();
        host.Start();
        BuildTray();
        if (!host.Settings.Flag(AppSettings.OnboardingKey, false)) ShowOnboarding();
        else if (!host.Settings.Flag(AppSettings.GuideKey, false))
        {
            host.Settings.Set(AppSettings.GuideKey, true);
            ShowGuide();
        }
        OpenFiles(args);
#if DEBUG
        Debug.ControlPipe.Start(host, this);
        Debug.DebugMetrics.Start(DispatcherQueue.GetForCurrentThread());
#endif
        if (host.Updater is { } updater) updater.Changed += () => host.Post(() =>
        {
            if (updater.Current is Updater.State.Available a) _ = AskToUpdate(a.Version, a.Notes);
        });
    }

    void ShowFatal(string reason)
    {
        var window = new Window { Title = L.T("拾音沒辦法開啟資料庫") };
        var paths = ShiyinPaths.FromEnvironment();
        var text = Ui.Text(L.F("原因：%@\n\n你的資料沒有被改動。升級前的備份在：%@\n請把這個訊息截圖給維護拾音的人。", reason, paths.Backups), select: true);
        window.Content = Ui.WindowBackground(Ui.Scroll(Ui.Column(16, Ui.Text(L.T("拾音沒辦法開啟資料庫"), 20, bold: true), text, Ui.Button(L.T("結束拾音"), () => Exit(), accent: true))));
        window.Activate();
    }

    /// <summary>第二次啟動轉過來的參數（檔案總管「開啟檔案」、拖到圖示上，R3.1）</summary>
    public void Activated(AppActivationArguments e)
    {
        host?.Post(() =>
        {
            IReadOnlyList<string> arguments = e.Data switch
            {
                Windows.ApplicationModel.Activation.IProtocolActivatedEventArgs protocol => [protocol.Uri.AbsoluteUri],
                Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch => CommandLine.Split(launch.Arguments),
                _ => [],
            };
            // 點通知、按通知的按鈕（shiyin: 網址，D62）：交給 Notifier，按「不要再提醒」時不用打開主視窗
            if (arguments.Any(NotificationLinks.IsLink)) { OpenLinks(arguments); return; }
            ShowMain();
            OpenFiles(arguments);
        });
    }

    void OpenLinks(IEnumerable<string> arguments)
    {
        if (host is null) return;
        foreach (var link in arguments.Where(NotificationLinks.IsLink)) host.Notifier.Open(link);
    }

    void OpenFiles(IEnumerable<string> arguments)
    {
        if (host is null || main is null) return;
        OpenLinks(arguments);
        var files = arguments.Where(a => !a.StartsWith("--") && File.Exists(a) && !a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToList();
        if (files.Count > 0) _ = host.Controller.ImportDirectly(files, main.State.CurrentFolder, main.State.Selection);
    }

    public void Notice(string message) => main?.ShowNotice(message);

    public void ShowMain()
    {
        if (main is null) return;
        main.AppWindow.Show();
        main.Activate();
    }

    public void ShowMeeting(long id)
    {
        main?.Reveal(id);
    }

    static void Bring(Window window, int width, int height)
    {
        WindowPlacement.Center(window, width, height);
        // 同 Mac 的使用說明、回報問題、問所有會議（520～560 × 440～480）
        WindowPlacement.MinimumSize(window, Math.Min(width, 520), Math.Min(height, 440));
        window.Activate();
    }

    public void ShowSettings()
    {
        if (host is null) return;
        if (settings is null)
        {
            settings = new SettingsWindow(host);
            settings.Closed += (_, _) => settings = null;
        }
        settings.Activate();
    }

    public void ShowOnboarding()
    {
        if (host is null) return;
        onboarding ??= OtherWindows.Onboarding(host, () => { onboarding?.Close(); });
        onboarding.Closed += (_, _) => onboarding = null;
        Bring(onboarding, 640, 600);
    }

    public void ShowGuide()
    {
        guide ??= OtherWindows.Guide();
        guide.Closed += (_, _) => guide = null;
        Bring(guide, 720, 800);
    }

    public void ShowReport()
    {
        if (host is null) return;
        report ??= OtherWindows.Report(host);
        report.Closed += (_, _) => report = null;
        Bring(report, 600, 520);
    }

    public void ShowAbout()
    {
        if (host is null) return;
        about ??= OtherWindows.About(host);
        about.Closed += (_, _) => about = null;
        Bring(about, 620, 560);
    }

    public void ShowAskAll()
    {
        if (host is null || main is null) return;
        askAll ??= OtherWindows.AskAll(host, main);
        askAll.Closed += (_, _) => askAll = null;
        Bring(askAll, 720, 740);
    }

    /// <summary>有通知權限就發通知（帶按鈕），沒有就用不搶焦點的小視窗（同 Mac）</summary>
    void ShowMeetingPrompt(MeetingDetector.Prompt p)
    {
        if (host is null) return;
        if (host.Notifier.MeetingPrompt(p)) return;
        prompt?.Close();
        prompt = new MeetingPromptWindow(host, p);
        prompt.Closed += (_, _) => prompt = null;
        prompt.Show();
    }

    async Task AskToUpdate(string version, string? notes)
    {
        if (main?.XamlRoot is null || host?.Updater is not { } updater) return;
        // 更新內容只列前幾項（R10.6、M§12）：tag 的說明中英各一段、每段好幾條，整段放進對話框太長
        var (items, more) = ReleaseNotes.Brief(notes ?? "", L.English);
        var brief = string.Join("\n", items.Select(i => "• " + i).Concat(more > 0 ? [L.F("……還有 %lld 項，完整說明在官網。", more)] : []));
        var text = string.Join("\n\n", new[] { brief, L.F("目前是 %@。更新時拾音會結束並重新打開，會議資料都會保留。", host.Version) }.Where(t => t.Length > 0));
        var dialog = new ContentDialog
        {
            XamlRoot = main.XamlRoot, Title = Ui.DialogTitle(L.F("拾音 %@ 可以更新了", version)), Content = Ui.Text(text), PrimaryButtonText = L.T("下載並更新"), CloseButtonText = L.T("稍後"),
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        main.ShowNotice(L.T("正在下載更新，完成後拾音會自動重新打開"));
        if (await updater.Install(version) is { } reason) main.ShowError(L.F("更新失敗：%@", reason));
    }

    // MARK: 系統匣（R27 R1.7、R11.1）

    void BuildTray()
    {
        if (host is null) return;
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "Shiyin.ico");
        tray = new WinUIEx.TrayIcon(1, icon, L.T("拾音"));
        tray.IsVisible = true;
        tray.Selected += (_, _) => ShowMain();
        tray.ContextMenu += (_, e) => e.Flyout = TrayMenu();
        host.Recorder.Changed += () => host.Post(UpdateTray);
        host.Recorder.HintAppeared += hint => host.Post(() => HintAppeared(hint));
    }

    void UpdateTray()
    {
        if (tray is null || host is null) return;
        var recorder = host.Recorder;
        // 收音提醒還在時系統匣的提示文字跟著變（R42.2；系統匣只有一個圖示，錄音中也只改提示文字）
        tray.Tooltip = recorder.IsRecording
            ? recorder.Hint is not null ? L.F("拾音：錄音中 %@，%@", TimeFormat.String(recorder.ElapsedSeconds), L.T("收音提醒")) : L.F("拾音：錄音中 %@", TimeFormat.String(recorder.ElapsedSeconds))
            : host.Runner.IsBusy ? L.T("拾音：處理中") : L.T("拾音");
    }

    /// <summary>收音提醒第一次出現（R42.3、M§31.5）：拾音的視窗不是前景時送通知，點了打開那場會議；每場每種一次由 Recorder 管</summary>
    void HintAppeared(Recorder.LevelHint hint)
    {
        if (host is null || host.Recorder.MeetingId is not long id) return;
        if (main is { IsForeground: true } && main.AppWindow.IsVisible) return;
        host.Notifier.LevelHint(hint, id);
    }

    MenuFlyout TrayMenu()
    {
        var menu = new MenuFlyout();
        var recorder = host!.Recorder;
        if (recorder.IsRecording)
        {
            menu.Items.Add(new MenuFlyoutItem { Text = (recorder.IsPaused ? L.T("已暫停") : L.T("錄音中")) + " " + TimeFormat.String(recorder.ElapsedSeconds), IsEnabled = false });
            menu.Items.Add(Ui.Item(recorder.IsPaused ? L.T("繼續") : L.T("暫停"), () => { if (recorder.IsPaused) recorder.Resume(); else recorder.Pause(); }));
            menu.Items.Add(Ui.Item(L.T("停止"), recorder.Stop));
            if (recorder.Hint is { } hint)
            {
                // 收音提醒（R42.2）：跟錄音畫面同一句，「知道了」這場不再提示
                menu.Items.Add(new MenuFlyoutItem { Text = hint.Text(), IsEnabled = false });
                menu.Items.Add(Ui.Item(L.T("知道了"), recorder.DismissLevelHint));
            }
        }
        else menu.Items.Add(Ui.Item(L.T("開始錄音"), () => host.Controller.StartRecording(main?.State.CurrentFolder ?? "")));
        menu.Items.Add(new MenuFlyoutSeparator());
        foreach (var lane in Enum.GetValues<JobLane>())
            if (host.Runner.Running(lane) is { } running)
            {
                var title = host.Db.Meeting(running.Job.MeetingId)?.DisplayTitle ?? "";
                var stage = running.Job.Kind switch
                {
                    JobKind.Compress => L.T("壓縮錄音"),
                    JobKind.Revise => L.T("依回答修正"),
                    JobKind.Correct => L.T("檢查可能聽錯的字"),
                    JobKind.NameSpeakers => L.T("猜說話者"),
                    JobKind.Summarize => L.T("整理"),
                    _ => running.Stage.Length == 0 ? L.T("轉錄") : running.Stage,
                };
                var progress = running.Progress is double p ? $" {(int)(p * 100)}%" : "";
                menu.Items.Add(new MenuFlyoutItem { Text = L.F("%@中：%@", stage, title) + progress, IsEnabled = false });
            }
        if (!host.Runner.IsBusy) menu.Items.Add(new MenuFlyoutItem { Text = L.T("目前沒有進行中的工作"), IsEnabled = false });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Ui.Item(L.T("打開拾音"), ShowMain));
        menu.Items.Add(Ui.Item(L.T("使用說明"), ShowGuide));
        menu.Items.Add(Ui.Item(L.T("關於拾音"), ShowAbout));
        menu.Items.Add(Ui.Item(L.T("設定"), ShowSettings));
        menu.Items.Add(Ui.Item(L.T("結束拾音"), () => _ = Quit()));
        return menu;
    }

    /// <summary>錄音中或有工作在跑時先確認（R11.7、R13.4）；結束前讓編輯器存草稿（M§21.15）</summary>
    public async Task Quit(bool confirmed = false)
    {
        if (host is null) { Exit(); return; }
        var recording = host.Recorder.IsRecording;
        var busy = host.Runner.IsBusy;
        if ((recording || busy) && !confirmed && main?.XamlRoot is not null)
        {
            ShowMain();
            var info = new List<string>();
            if (recording) info.Add(L.T("錄音會先存好，下次開啟拾音時開始轉錄。"));
            if (busy) info.Add(L.T("進行中的轉錄或整理會中斷，下次開啟時從頭重跑。"));
            var dialog = new ContentDialog
            {
                XamlRoot = main.XamlRoot, Title = Ui.DialogTitle(recording ? L.T("正在錄音，要結束拾音嗎？") : L.T("還有工作在進行，要結束拾音嗎？")),
                Content = string.Join("\n", info), PrimaryButtonText = L.T("結束"), CloseButtonText = L.T("取消"),
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }
        main?.Refresh();
        await Task.Run(host.StopWork);
        host.Close();
        tray?.Dispose();
        Exit();
    }
}

/// <summary>啟動參數的切法（ILaunchActivatedEventArgs.Arguments 是一整串命令列）</summary>
public static class CommandLine
{
    public static List<string> Split(string line)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var c in line)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }
}
