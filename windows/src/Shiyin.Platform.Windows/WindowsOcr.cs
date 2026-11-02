using System.Text;
using Shiyin.IO;
using Shiyin.Model;
using Shiyin.Services;
using Windows.Data.Pdf;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Shiyin.Platform;

/// <summary>
/// 參考文件的文字辨識（design-windows §11.8、M§39.2）：Windows.Media.Ocr。PDF 的頁用 Windows.Data.Pdf 畫成長邊 2000 px（同 Mac），
/// 圖片照 EXIF 方向解碼。這台電腦沒有會議語言的辨識時回傳 null（英文會議可以用使用者的語言設定裡的辨識）。
/// 在背景執行緒呼叫（DocumentStore.Add 在 Task.Run 裡），所以直接等 WinRT 的非同步結果
/// </summary>
public sealed class WindowsOcr : IDocumentOcr
{
    readonly object gate = new();
    string? cachedPath;
    PdfDocument? cachedPdf;

    public string? RecognizeImage(string path, MeetingLanguage language)
    {
        if (Engine(language) is not { } engine) return null;
        return Run(path, async () =>
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            return await Recognize(engine, await BitmapDecoder.CreateAsync(stream));
        });
    }

    public string? RecognizePdfPage(string path, int pageIndex, MeetingLanguage language)
    {
        if (Engine(language) is not { } engine) return null;
        return Run(path, async () =>
        {
            var pdf = await Pdf(path);
            if (pageIndex < 0 || pageIndex >= pdf.PageCount) return "";
            using var page = pdf.GetPage((uint)pageIndex);
            var size = page.Size;
            var longest = Math.Max(size.Width, size.Height);
            if (longest <= 0) return "";
            var scale = 2000 / longest;
            using var stream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, new PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Round(size.Width * scale), DestinationHeight = (uint)Math.Round(size.Height * scale),
                BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
            });
            return await Recognize(engine, await BitmapDecoder.CreateAsync(stream));
        });
    }

    /// <summary>同一份 PDF 連續辨識好幾頁：留著上一份，不要每頁重開</summary>
    async Task<PdfDocument> Pdf(string path)
    {
        lock (gate)
        {
            if (cachedPath == path && cachedPdf is not null) return cachedPdf;
        }
        var pdf = await PdfDocument.LoadFromFileAsync(await StorageFile.GetFileFromPathAsync(path));
        lock (gate) (cachedPath, cachedPdf) = (path, pdf);
        return pdf;
    }

    static string? Run(string path, Func<Task<string>> work)
    {
        try { return work().GetAwaiter().GetResult(); }
        catch (Exception e) when (e is not OutOfMemoryException and not DocumentError)
        {
            Log.Error($"ocr failed: {e.GetType().Name}");
            throw DocumentError.Unreadable(Path.GetFileName(path));
        }
    }

    /// <summary>會議語言的辨識；英文會議找不到時用使用者的語言設定（中文的辨識也認得英文）</summary>
    static OcrEngine? Engine(MeetingLanguage language)
    {
        string[] tags = language switch
        {
            MeetingLanguage.En => ["en-US", "en-GB", "en"],
            MeetingLanguage.Ja => ["ja-JP", "ja"],
            _ => ["zh-Hant-TW", "zh-TW", "zh-Hant", "zh-Hant-HK", "zh-Hans-CN", "zh-CN", "zh-Hans"],
        };
        foreach (var tag in tags)
        {
            try
            {
                var l = new Language(tag);
                if (OcrEngine.IsLanguageSupported(l) && OcrEngine.TryCreateFromLanguage(l) is { } engine) return engine;
            }
            catch (ArgumentException) { }
        }
        return language == MeetingLanguage.En ? OcrEngine.TryCreateFromUserProfileLanguages() : null;
    }

    static async Task<string> Recognize(OcrEngine engine, BitmapDecoder decoder)
    {
        // 太大的圖先縮小（OcrEngine.MaxImageDimension），照 EXIF 的方向轉正
        var max = OcrEngine.MaxImageDimension;
        var (w, h) = (decoder.OrientedPixelWidth, decoder.OrientedPixelHeight);
        var transform = new BitmapTransform();
        if (Math.Max(w, h) > max)
        {
            var scale = (double)max / Math.Max(w, h);
            transform.ScaledWidth = (uint)Math.Floor(decoder.PixelWidth * scale);
            transform.ScaledHeight = (uint)Math.Floor(decoder.PixelHeight * scale);
        }
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        var result = await engine.RecognizeAsync(bitmap);
        return string.Join("\n", result.Lines.Select(Line));
    }

    /// <summary>一行的字接起來：前後兩個字都不是中日文時才加空格（Windows 的中文辨識一個字一個 word，直接用 Line.Text 會變成「會 議 議 程」）</summary>
    static string Line(OcrLine line)
    {
        var sb = new StringBuilder();
        foreach (var word in line.Words)
        {
            var text = word.Text;
            if (text.Length == 0) continue;
            if (sb.Length > 0 && !IsCjk(sb[^1]) && !IsCjk(text[0])) sb.Append(' ');
            sb.Append(text);
        }
        return sb.ToString();
    }

    static bool IsCjk(char c) => c is >= '⺀' and <= '鿿' or >= '豈' and <= '﫿' or >= '︰' and <= '﹏'
        or >= '＀' and <= '￯' or >= '\uD800' and <= '\uDFFF';
}
