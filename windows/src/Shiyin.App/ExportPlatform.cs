using Microsoft.Web.WebView2.Core;
using Shiyin.IO;
using Shiyin.Services;
using Shiyin.Services.Export;
using Windows.ApplicationModel.DataTransfer;

namespace Shiyin.App;

/// <summary>
/// 匯出的平台部分（design-windows §13）：Word 用 DocxWriter（Services）、PDF 用 WebView2 把同一份 HTML 印成 A4（邊界 56 pt）、錄音用 Media Foundation
/// </summary>
public sealed class ExportPlatform(AppHost host) : IExportPlatform
{
    public Task NotesDocx(ExportBundle b, string path, CancellationToken cancel) =>
        Task.Run(() => DocxWriter.Write(NotesExporter.Html(b, ExportFonts.Windows), path, b.Meeting.MeetingLanguage), cancel);

    public Task TranscriptDocx(ExportBundle b, bool times, bool speakers, string path, CancellationToken cancel) =>
        Task.Run(() => DocxWriter.Write(TranscriptExporter.Html(b, times, speakers, ExportFonts.Windows), path, b.Meeting.MeetingLanguage), cancel);

    public Task NotesPdf(ExportBundle b, string path, CancellationToken cancel) => Pdf.Print(NotesExporter.Html(b, ExportFonts.Windows), path, cancel);

    public Task Audio(string source, string format, string path, CancellationToken cancel) => host.Compressor.Export(source, format, path, cancel);
}

/// <summary>
/// HTML 印成 PDF：不放進畫面的 WebView2（用主視窗的 HWND 建 controller、不顯示），PrintToPdfAsync。
/// WebView2 的執行環境 Windows 11 內建；使用者資料放 SHIYIN_HOME\tmp，用完刪掉
/// </summary>
public static class Pdf
{
    public static IntPtr Window { get; set; }

    public static async Task Print(string html, string path, CancellationToken cancel)
    {
        var dir = AppHost.Current.Paths.MakeTempDirectory();
        try
        {
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, dir, new CoreWebView2EnvironmentOptions());
            var controller = await environment.CreateCoreWebView2ControllerAsync(CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)Window));
            try
            {
                controller.IsVisible = false;
                var web = controller.CoreWebView2;
                web.Settings.IsScriptEnabled = false;
                var loaded = new TaskCompletionSource<bool>();
                web.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
                web.NavigateToString(html);
                await loaded.Task.WaitAsync(TimeSpan.FromSeconds(30), cancel);
                var settings = environment.CreatePrintSettings();
                settings.PageWidth = 8.27;
                settings.PageHeight = 11.69;
                // 56 pt = 0.78 英吋
                settings.MarginTop = settings.MarginBottom = settings.MarginLeft = settings.MarginRight = 56 / 72.0;
                settings.ShouldPrintBackgrounds = true;
                settings.ShouldPrintHeaderAndFooter = false;
                if (!await web.PrintToPdfAsync(path, settings)) throw new IOException(Localization.L.T("匯出 PDF 失敗"));
            }
            finally
            {
                controller.Close();
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

/// <summary>複製會議紀錄（R7.3、D54）：純文字、Markdown，或同時放純文字和 HTML（Word、Outlook、Notion 都吃 HTML）</summary>
public static class Copy
{
    public static void Text(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    public static void Rich(string plain, string html)
    {
        var package = new DataPackage();
        package.SetText(plain);
        package.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat(html));
        Clipboard.SetContent(package);
    }
}
