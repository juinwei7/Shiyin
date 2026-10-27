using Shiyin.IO;

namespace Shiyin.Services;

/// <summary>錄音的前段複製成暫存檔（M§35.3）：沒人說話太久而自動停止的錄音，只轉錄到最後說話的地方</summary>
public static class AudioTrim
{
    /// <summary>WAV 的前 seconds 秒寫成新的 WAV（聲道、取樣率不變），放在 tmp 底下新的資料夾；
    /// 錄音本來就沒有比這長、或不是 WAV（已經壓縮）時回傳 null（照常轉錄全部）</summary>
    public static string? Prefix(string path, double seconds, string tmp)
    {
        if (!path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) return null;
        using var reader = new WavReader(path);
        var frames = (long)(seconds * reader.SampleRate);
        if (frames <= 0 || frames >= reader.Frames) return null;
        var dir = Path.Combine(tmp, "until-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, "until.wav");
        try
        {
            using var writer = new WavWriter(output, reader.Channels, reader.SampleRate);
            var remaining = frames;
            while (remaining > 0)
            {
                var data = reader.Read((int)Math.Min(65536, remaining));
                var count = data.Length > 0 ? data[0].Length : 0;
                if (count == 0) break;
                writer.Write(data, count);
                remaining -= count;
            }
        }
        catch
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
            throw;
        }
        return output;
    }
}
