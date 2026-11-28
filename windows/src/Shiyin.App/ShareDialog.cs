using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Services;
using Windows.ApplicationModel.DataTransfer;

namespace Shiyin.App;

/// <summary>
/// 分享對話框（R125.2～R125.4、M§99.4、design-windows §11.58；照 Sheet 的樣子，同 Mac 的 ShareSheet）：
/// 沒有伺服器的分享時只有「用檔案分享…」；還沒分享時是名單、連結、期限、要帶的內容；分享了之後是名單的狀態、連結、期限、更新、停止。
/// 同時只能開一個 ContentDialog：第一次分享的說明、停止分享的確認在同一個對話框裡換頁
/// </summary>
public sealed class ShareDialog
{
    /// <summary>開著的分享對話框（DEBUG 的 state、控制通道）</summary>
    public static ShareDialog? Current { get; private set; }

    readonly MainWindow window;
    readonly AppHost host;
    readonly long meetingId;
    readonly SheetDialog sheet;
    readonly StackPanel body = new() { Spacing = 16 };
    readonly List<string> emails = [];
    readonly TextBox draftBox;
    readonly FlowPanel chips = new() { Spacing = 6, LineSpacing = 6 };
    readonly StackPanel suggestionList = new() { Spacing = 2 };
    readonly Border suggestions;
    readonly StackPanel recipientsInput;
    List<EnterpriseShares.Person> suggested = [];
    bool withLink;
    string access = "company";
    int? days = 30;
    EnterpriseShares.Contents contents = new();
    bool loaded;
    /// <summary>同一個對話框裡的確認頁：第一次分享的說明、停止分享</summary>
    (string Title, string Message, string Action, bool Destructive, Action Run)? confirm;
    int lookupVersion;

    public long MeetingId => meetingId;
    public string Mode => confirm is { } c ? "confirm:" + c.Title : host.Controller.Sharing is null ? "file" : Detail is not null ? "shared" : loaded ? "new" : "loading";
    public IReadOnlyList<string> Emails => emails;

    EnterpriseShares.Detail? Detail => host.Controller.ShareDetail(meetingId);

    ShareDialog(MainWindow window, long meetingId)
    {
        this.window = window;
        host = window.Host;
        this.meetingId = meetingId;
        var title = L.F("分享「%@」", host.Db.Meeting(meetingId)?.DisplayTitle ?? "");
        sheet = new SheetDialog(window, title, 472, 16);
        draftBox = Sheet.TextField("", L.T("同事的信箱"));
        draftBox.TextChanged += (_, _) => Ui.Guard(DraftChanged);
        suggestions = new Border { Child = suggestionList, Padding = new Thickness(8), CornerRadius = new CornerRadius(8), Background = SidebarLook.Gray(0.04), Visibility = Visibility.Collapsed };
        recipientsInput = Ui.Column(8, Sheet.Label(L.T("傳給同事")), chips, draftBox, suggestions);
        sheet.Form.Children.Add(Sheet.Header(title, sheet.Cancel, Sheet.Subtitle(Subtitle)));
        sheet.Form.Children.Add(body);
        // Enter：欄位裡有字時變成一顆；沒有字時是「分享」（還沒分享時）
        sheet.Submit = () =>
        {
            if (draftBox.Text.Trim().Length > 0) { AddDraft(); return; }
            if (confirm is { } c) { c.Run(); return; }
            if (host.Controller.Sharing is not null && Detail is null && loaded && !host.Controller.ShareBusy) Share();
        };
        sheet.SelectOnFocus = false;
    }

    string Subtitle => host.Controller.Sharing is null
        ? L.T("登入企業伺服器、公司有開分享時，也可以直接傳給同事或建立連結。")
        : L.F("透過 %@（公司的伺服器）分享，收到的人匯入成自己的一份。", host.Enterprise.Host);

    /// <summary>打開這場會議的分享對話框（已經開著時不理）</summary>
    public static async Task Show(MainWindow window, long meetingId)
    {
        if (window.XamlRoot is null || Current is not null) return;
        var dialog = new ShareDialog(window, meetingId);
        Current = dialog;
        if (dialog.host.Controller.Sharing is { MaxDays: int max }) dialog.days = Math.Min(30, max);
        void Changed() => dialog.host.Post(() => { if (Current == dialog) dialog.Rebuild(); });
        dialog.host.Controller.SharesChanged += Changed;
        dialog.Rebuild();
        _ = dialog.Load();
        try { await dialog.sheet.Show(); }
        finally
        {
            dialog.host.Controller.SharesChanged -= Changed;
            if (Current == dialog) Current = null;
        }
    }

    async Task Load()
    {
        await host.Controller.LoadShareDetail(meetingId);
        loaded = true;
        Rebuild();
        // 還沒分享：打開時游標在信箱欄位（欄位要等問完伺服器才放進來，不能交給 SheetDialog.Focus）
        if (Mode == "new") FocusDraft();
    }

    void FocusDraft() => draftBox.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Ui.Guard(() =>
    {
        if (draftBox.XamlRoot is not null) draftBox.Focus(FocusState.Programmatic);
    }));

    /// <summary>出錯時寫在對話框裡（同時只能開一個 ContentDialog，錯誤的對話框要等這個關掉才出得來）</summary>
    public void ShowError(string message)
    {
        error = message;
        Rebuild();
    }

    string? error;

    public void Close() => sheet.Close();

    void Rebuild()
    {
        // 重建時信箱欄位會先拿下來再放回去：打字中的焦點留在欄位裡
        var typing = draftBox.FocusState != FocusState.Unfocused;
        body.Children.Clear();
        if (error is { } message)
        {
            var text = Ui.Text(message, 13, color: Ui.Danger, select: true);
            AutomationProperties.SetLiveSetting(text, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);
            body.Children.Add(text);
        }
        try { Build(); }
        finally { if (typing) FocusDraft(); }
    }

    void Build()
    {
        suggestions.Visibility = suggested.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RebuildChips();
        if (confirm is { } c)
        {
            body.Children.Add(Ui.Column(8, Ui.Text(c.Title, 16, bold: true), Sheet.Subtitle(c.Message)));
            var action = Sheet.Pill(c.Action, c.Destructive ? Sheet.Kind.Destructive : Sheet.Kind.Primary, click: c.Run);
            sheet.Primary = action;
            body.Children.Add(Sheet.Buttons(null, Sheet.Pill(L.T("取消"), click: () => { confirm = null; Rebuild(); }), action));
            return;
        }
        var controller = host.Controller;
        if (controller.Sharing is null)
        {
            body.Children.Add(Ui.Text(L.T("整場會議（錄音、逐字稿、說話者、紀錄、筆記）存成 .shiyin，傳給同事後點兩下就能匯入。"), 13, color: Ui.Secondary));
        }
        else if (Detail is { } detail) SharedView(detail);
        else if (loaded) NewShareView();
        else body.Children.Add(new ProgressRing { IsActive = true, Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Center });
        if (controller.ShareBusy && controller.ShareProgress is double progress)
        {
            var bar = new ProgressBar { Value = progress * 100, Maximum = 100, VerticalAlignment = VerticalAlignment.Center };
            var percent = Ui.Text($"{(int)(progress * 100)}%", 12, color: Ui.Secondary, wrap: false);
            body.Children.Add(Ui.Bar(null, bar, Ui.Row(10, percent, Ui.Link(L.T("取消"), controller.CancelShare)), 10));
        }
        body.Children.Add(Footer());
    }

    // MARK: 還沒分享

    void NewShareView()
    {
        body.Children.Add(recipientsInput);
        var link = new CheckBox { Content = L.T("同時建立連結"), IsChecked = withLink };
        link.Checked += (_, _) => { withLink = true; Rebuild(); };
        link.Unchecked += (_, _) => { withLink = false; Rebuild(); };
        body.Children.Add(Ui.Column(8, link, withLink ? AccessPicker(access, value => access = value) : null));
        body.Children.Add(ExpiryPicker());
        var meeting = host.Db.Meeting(meetingId);
        var hasAudio = meeting is not null && host.Paths.HasAudio(meeting);
        var audio = Check(AudioLabel(meeting, hasAudio), contents.Audio && hasAudio, on => contents = contents with { Audio = on });
        audio.IsEnabled = hasAudio;
        body.Children.Add(Ui.Column(6,
            Sheet.Label(L.T("要帶的內容")),
            Ui.Text(L.T("會議資訊、逐字稿、說話者名字、會議紀錄、重點標記一定會帶。"), 12, color: Ui.Tertiary),
            audio,
            Check(L.T("我的筆記"), contents.UserNotes, on => contents = contents with { UserNotes = on }),
            Check(L.T("參考文件和會議中的畫面"), contents.Documents, on => contents = contents with { Documents = on })));
    }

    static CheckBox Check(string label, bool on, Action<bool> changed)
    {
        var box = new CheckBox { Content = label, IsChecked = on };
        box.Checked += (_, _) => changed(true);
        box.Unchecked += (_, _) => changed(false);
        return box;
    }

    string AudioLabel(Meeting? meeting, bool hasAudio)
    {
        if (meeting is null || !hasAudio) return L.T("錄音");
        try { return L.F("錄音（%@）", Shiyin.Services.Capture.Recorder.ByteCount(new FileInfo(host.Paths.AudioPath(meeting)).Length)); }
        catch (IOException) { return L.T("錄音"); }
    }

    /// <summary>信箱：打字時從員工名單補完，Enter 或逗號變成一顆，可以刪</summary>
    void RebuildChips()
    {
        chips.Children.Clear();
        chips.Visibility = emails.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var email in emails)
        {
            var remove = SidebarLook.HeaderIcon(new Button(), "");
            remove.Padding = new Thickness(2);
            ToolTipService.SetToolTip(remove, L.F("移除 %@", email));
            AutomationProperties.SetName(remove, L.F("移除 %@", email));
            remove.Click += (_, _) => { emails.Remove(email); Rebuild(); };
            var chip = new Border
            {
                Child = Ui.Row(4, Ui.Text(email, 13, wrap: false), remove), CornerRadius = new CornerRadius(12), Padding = new Thickness(8, 2, 4, 2),
                Background = SidebarLook.Gray(0.08),
            };
            foreach (var child in ((StackPanel)chip.Child).Children.OfType<FrameworkElement>()) child.VerticalAlignment = VerticalAlignment.Center;
            chips.Children.Add(chip);
        }
    }

    void DraftChanged()
    {
        var text = draftBox.Text;
        if (text.Contains(',') || text.Contains('，')) { AddDraft(); return; }   // l10n-skip
        Lookup(text);
    }

    void AddDraft()
    {
        foreach (var part in draftBox.Text.Split([',', '，', ';', '；', '\n', ' '], StringSplitOptions.RemoveEmptyEntries)) Add(part);   // l10n-skip
        draftBox.Text = "";
        suggested = [];
        Rebuild();
    }

    void Add(string email)
    {
        var trimmed = email.Trim().ToLowerInvariant();
        if (!trimmed.Contains('@') || emails.Contains(trimmed)) return;
        emails.Add(trimmed);
    }

    /// <summary>員工名單補完（打字停 0.25 秒才問；字改了就不用舊的結果）</summary>
    async void Lookup(string text)
    {
        var query = text.Trim();
        var version = ++lookupVersion;
        if (query.Length == 0)
        {
            if (suggested.Count > 0) { suggested = []; ShowSuggestions(); }
            return;
        }
        await Task.Delay(250);
        if (version != lookupVersion) return;
        List<EnterpriseShares.Person> people;
        try { people = await new EnterpriseShares(host.Enterprise).People(query); }
        catch (Exception e) when (e is EnterpriseException or HttpRequestException) { people = []; }
        if (version != lookupVersion) return;
        suggested = people.Where(p => !emails.Contains(p.Email.ToLowerInvariant())).ToList();
        ShowSuggestions();
    }

    void ShowSuggestions()
    {
        suggestionList.Children.Clear();
        foreach (var person in suggested)
        {
            var button = new HyperlinkButton
            {
                Content = Ui.Row(8, Ui.Text(person.Display, 13, wrap: false), person.Name is { Length: > 0 } ? Ui.Text(person.Email, 13, color: Ui.Secondary, wrap: false) : null),
                Padding = new Thickness(4, 2, 4, 2), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                Foreground = Ui.Primary,
            };
            AutomationProperties.SetName(button, person.Display);
            button.Click += (_, _) =>
            {
                Add(person.Email);
                draftBox.Text = "";
                suggested = [];
                Rebuild();
            };
            suggestionList.Children.Add(button);
        }
        suggestions.Visibility = suggested.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    FrameworkElement AccessPicker(string value, Action<string> changed)
    {
        var options = new List<(string, string)> { ("company", L.T("公司裡登入的人")) };
        if (host.Controller.Sharing?.PublicLinks == true) options.Add(("public", L.T("知道連結的任何人")));
        var box = Sheet.Combo(Ui.Combo(options, value, changed));
        box.HorizontalAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetName(box, L.T("誰打得開"));
        var label = Ui.Text(L.T("誰打得開"), 14, wrap: false);
        label.VerticalAlignment = VerticalAlignment.Center;
        return Ui.Row(10, label, box);
    }

    /// <summary>有效期限的選項：7、30 天（公司的上限以內）、公司的上限、公司沒有上限時「不限」</summary>
    List<(int? Days, string Label)> ExpiryOptions()
    {
        var max = host.Controller.Sharing?.MaxDays;
        var options = new List<(int?, string)>();
        foreach (var n in new[] { 7, 30 }.Where(n => max is null || n <= max)) options.Add((n, L.F("%lld 天", n)));
        if (max is int m && m != 7 && m != 30) options.Add((m, L.F("%lld 天", m)));
        if (max is null) options.Add((null, L.T("不限")));
        return options;
    }

    FrameworkElement ExpiryPicker()
    {
        var box = Sheet.Combo(Ui.Combo(ExpiryOptions(), days, value => days = value));
        box.HorizontalAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetName(box, L.T("有效期限"));
        var label = Ui.Text(L.T("有效期限"), 14, wrap: false);
        label.VerticalAlignment = VerticalAlignment.Center;
        return Ui.Row(10, label, box);
    }

    // MARK: 分享了之後

    void SharedView(EnterpriseShares.Detail detail)
    {
        var list = Ui.Column(6, Sheet.Label(detail.Recipients.Count == 0 ? L.T("還沒有傳給任何人") : L.F("已傳給 %lld 人", detail.Recipients.Count)));
        foreach (var r in detail.Recipients)
        {
            var name = Ui.Column(1, Ui.Text(r.Name is { Length: > 0 } n ? n : r.Email, 14, wrap: false),
                r.Name is { Length: > 0 } ? Ui.Text(r.Email, 12, color: Ui.Secondary, wrap: false) : null);
            var status = Ui.Text(r.ImportedAt is not null ? L.T("已匯入") : r.OpenedAt is not null ? L.T("已打開") : L.T("還沒打開"), 12, color: Ui.Secondary, wrap: false);
            status.VerticalAlignment = VerticalAlignment.Center;
            var email = r.Email;
            var remove = SidebarLook.HeaderIcon(new Button(), "");
            ToolTipService.SetToolTip(remove, L.F("從名單移除 %@", email));
            AutomationProperties.SetName(remove, L.F("從名單移除 %@", email));
            remove.Click += (_, _) => _ = host.Controller.RemoveShareRecipient(meetingId, email);
            list.Children.Add(Ui.Bar(null, name, Ui.Row(8, status, remove), 8));
        }
        body.Children.Add(list);
        body.Children.Add(recipientsInput);
        if (emails.Count > 0)
            body.Children.Add(Sheet.Pill(L.T("加入名單"), click: async () =>
            {
                var added = emails.ToList();
                emails.Clear();
                error = null;
                Rebuild();
                var back = await host.Controller.AddShareRecipients(meetingId, added);
                foreach (var email in back) if (!emails.Contains(email)) emails.Add(email);
                Rebuild();
            }));
        body.Children.Add(new Border { Height = 1, Background = Ui.Divider });
        if (detail.Link is { } link)
        {
            var url = Ui.Text(link.Url, 13, wrap: false, select: true);
            url.FontFamily = new FontFamily("Consolas");
            url.VerticalAlignment = VerticalAlignment.Center;
            var copy = Sheet.Pill(L.T("拷貝"), compact: true, click: () =>
            {
                var package = new DataPackage();
                package.SetText(link.Url);
                Clipboard.SetContent(package);
                window.ShowNotice(L.T("已拷貝連結"));
            });
            var stop = Ui.Link(L.T("停止連結"), () => _ = host.Controller.SetShareLink(meetingId, null));
            stop.VerticalAlignment = VerticalAlignment.Center;
            body.Children.Add(Ui.Column(6,
                Sheet.Label(L.T("連結")),
                Ui.Bar(null, url, copy, 8),
                Ui.Bar(AccessPicker(link.Access, value => _ = host.Controller.SetShareLink(meetingId, value)), null, stop, 8)));
        }
        else
        {
            body.Children.Add(Ui.Row(10, AccessPicker(access, value => access = value),
                Sheet.Pill(L.T("建立連結"), compact: true, click: () => _ = host.Controller.SetShareLink(meetingId, access))));
        }
        var expiry = Ui.Text(detail.ExpiresAt is { } stamp ? L.F("到期：%@", Day(stamp)) : L.T("不限期"), 13, color: Ui.Secondary, wrap: false);
        expiry.VerticalAlignment = VerticalAlignment.Center;
        var menu = new MenuFlyout();
        var max = host.Controller.Sharing?.MaxDays;
        foreach (var n in new[] { 7, 30 }.Where(n => max is null || n <= max))
            menu.Items.Add(Ui.Item(L.F("從今天起 %lld 天", n), () => _ = host.Controller.SetShareExpiry(meetingId, n)));
        if (max is int m && m != 7 && m != 30) menu.Items.Add(Ui.Item(L.F("從今天起 %lld 天", m), () => _ = host.Controller.SetShareExpiry(meetingId, m)));
        if (max is null) menu.Items.Add(Ui.Item(L.T("不限期"), () => _ = host.Controller.SetShareExpiry(meetingId, null)));
        var change = Ui.Borderless(new DropDownButton { Content = L.T("改期限"), Flyout = MenuLook.Apply(menu) });
        AutomationProperties.SetName(change, L.T("改期限"));
        body.Children.Add(Ui.Bar(expiry, null, change, 8));
    }

    /// <summary>伺服器給的時間（ISO 8601、UTC）換成這台電腦的日期</summary>
    public static string Day(string stamp) =>
        DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? DateText.String(DateOnly.FromDateTime(date.ToLocalTime().DateTime)) : stamp;

    // MARK: 按鈕

    FrameworkElement Footer()
    {
        var controller = host.Controller;
        var busy = controller.ShareBusy;
        var file = Ui.Link(L.T("用檔案分享…"), () =>
        {
            sheet.Close();
            _ = Dialogs.Export(window, meetingId, ExportItem.Package);
        });
        file.VerticalAlignment = VerticalAlignment.Center;
        FrameworkElement left = file;
        var buttons = new List<Button>();
        if (controller.Sharing is not null && Detail is { } detail)
        {
            var stop = Ui.Link(L.T("停止分享…"), () =>
            {
                confirm = (L.T("停止分享這場會議？"), L.T("公司的伺服器會刪掉這場，名單上的人和連結都打不開；別人已經匯入的那份不受影響。"), L.T("停止分享"), true, () =>
                {
                    confirm = null;
                    Rebuild();
                    _ = controller.StopSharing(meetingId);
                });
                Rebuild();
            });
            if (!Ui.HighContrast) stop.Foreground = Ui.Danger;
            stop.IsEnabled = !busy;
            stop.VerticalAlignment = VerticalAlignment.Center;
            left = Ui.Row(16, file, stop);
            var update = Sheet.Pill(L.T("更新內容"), click: () => _ = controller.UpdateShareContents(meetingId));
            update.IsEnabled = !busy && !detail.Uploading;
            ToolTipService.SetToolTip(update, L.T("改過會議之後重新上傳（名單、連結不變），收到的人會看到「已更新」"));
            buttons.Add(update);
        }
        var newShare = Detail is null && controller.Sharing is not null;
        buttons.Add(Sheet.Pill(newShare ? L.T("取消") : L.T("完成"), click: sheet.Close));
        if (newShare)
        {
            var share = Sheet.Pill(L.T("分享"), Sheet.Kind.Primary, click: () =>
            {
                AddDraft();
                if (host.Settings.Flag(AppController.ShareNoticeShownKey, false)) Share();
                else
                {
                    confirm = (L.T("透過公司的伺服器分享？"),
                        L.F("這場會議會存在 %@（公司的伺服器），直到你停止分享或到期。收到的人匯入後是他自己的一份，停止分享不會刪掉別人已經匯入的。", host.Enterprise.Host),
                        L.T("分享"), false, () =>
                        {
                            host.Settings.Set(AppController.ShareNoticeShownKey, true);
                            confirm = null;
                            Share();
                        });
                    Rebuild();
                }
            });
            share.IsEnabled = !busy && loaded;
            sheet.Primary = share;
            buttons.Add(share);
        }
        else sheet.Primary = null;
        return Sheet.Buttons(left, [.. buttons]);
    }

    /// <summary>送出：信箱先拿出來，伺服器沒收下的放回欄位</summary>
    public async void Share()
    {
        error = null;
        var (list, link) = (emails.ToList(), withLink ? access : null);
        emails.Clear();
        Rebuild();
        var back = await host.Controller.ShareMeeting(meetingId, list, link, days, contents);
        foreach (var email in back) if (!emails.Contains(email)) emails.Add(email);
        Rebuild();
    }

    // MARK: DEBUG

    /// <summary>控制通道：信箱放進欄位（同打字後按 Enter）</summary>
    public void DebugAddEmails(IEnumerable<string> list)
    {
        foreach (var email in list) Add(email);
        Rebuild();
    }
}
