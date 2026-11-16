using System.Buffers.Binary;
using Shiyin.Localization;

namespace Shiyin.Services;

/// <summary>分割會議的錄音（R90、M§64、design-windows §11.23、D172）：WAV 直接切位元組，其他格式在 Platform 用 Media Foundation 重新壓成 m4a</summary>
public interface IAudioSplitter
{
    /// <summary>從 seconds 切成前段、後段兩個檔，放在 directory（要已經存在）；切點不在錄音裡面時回傳 null</summary>
    Task<AudioSplit.Parts?> Split(string path, double seconds, string directory, CancellationToken cancel);
}

public static class AudioSplit
{
    /// <summary>前段、後段的檔案，Extension 是兩個檔的副檔名（小寫、不含點）</summary>
    public sealed record Parts(string Head, string Tail, string Extension);

    /// <summary>只切 WAV（Mac 上的測試、評估）；其他格式丟 IOException</summary>
    public sealed class WavOnly : IAudioSplitter
    {
        public Task<Parts?> Split(string path, double seconds, string directory, CancellationToken cancel) =>
            Importer.Extension(path) == "wav" ? Task.Run(() => SplitWav(path, seconds, directory), cancel)
                : throw new IOException(L.T("不支援這個音訊格式"));
    }

    /// <summary>
    /// WAV 從第 floor(seconds × 取樣率) 個音框切開：fmt 區段原樣複製，data 照位元組分成兩段，格式、聲道、位元數都不變（逐個取樣相同）。
    /// 其他區段（LIST 等）不帶。沒有正常結束的錄音（data 的大小是 0 或超過檔案）用實際的檔案大小（同 WavReader、WAVRepair）
    /// </summary>
    public static Parts? SplitWav(string path, double seconds, string directory)
    {
        using var input = File.OpenRead(path);
        Span<byte> header = stackalloc byte[12];
        input.ReadExactly(header);
        if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..12].SequenceEqual("WAVE"u8)) throw new InvalidDataException(L.T("不是 WAV 檔"));
        byte[]? format = null;
        long dataStart, dataLength;
        Span<byte> chunk = stackalloc byte[8];
        while (true)
        {
            if (input.Read(chunk) < 8) throw new InvalidDataException(L.T("找不到音訊資料區段"));
            var id = System.Text.Encoding.ASCII.GetString(chunk[..4]);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            if (id == "fmt ")
            {
                format = new byte[size];
                input.ReadExactly(format);
                if (size % 2 == 1) input.Seek(1, SeekOrigin.Current);
            }
            else if (id == "data")
            {
                if (format is null || format.Length < 16) throw new InvalidDataException(L.T("WAV 缺少格式區段"));
                dataStart = input.Position;
                dataLength = size == 0 || dataStart + size > input.Length ? input.Length - dataStart : size;
                break;
            }
            else input.Seek(size + size % 2, SeekOrigin.Current);
        }
        var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(4));
        var blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(12));
        if (blockAlign == 0 || sampleRate == 0) throw new InvalidDataException(L.T("WAV 缺少格式區段"));
        var frames = dataLength / blockAlign;
        var cut = (long)Math.Floor(seconds * sampleRate);
        if (cut <= 0 || cut >= frames) return null;
        var head = Path.Combine(directory, "head.wav");
        var tail = Path.Combine(directory, "tail.wav");
        input.Seek(dataStart, SeekOrigin.Begin);
        Write(head, format, input, cut * blockAlign);
        Write(tail, format, input, (frames - cut) * blockAlign);
        return new Parts(head, tail, "wav");
    }

    /// <summary>寫一個 WAV：RIFF、原本的 fmt 區段、從 input 目前的位置複製 bytes 個位元組的 data</summary>
    static void Write(string path, byte[] format, Stream input, long bytes)
    {
        using var output = new FileStream(path, FileMode.Create, FileAccess.Write);
        var fmtPadded = format.Length + format.Length % 2;
        var riffSize = 4 + 8 + fmtPadded + 8 + bytes + bytes % 2;
        Span<byte> word = stackalloc byte[4];
        output.Write("RIFF"u8);
        BinaryPrimitives.WriteUInt32LittleEndian(word, (uint)Math.Min(uint.MaxValue, riffSize));
        output.Write(word);
        output.Write("WAVE"u8);
        output.Write("fmt "u8);
        BinaryPrimitives.WriteUInt32LittleEndian(word, (uint)format.Length);
        output.Write(word);
        output.Write(format);
        if (format.Length % 2 == 1) output.WriteByte(0);
        output.Write("data"u8);
        BinaryPrimitives.WriteUInt32LittleEndian(word, (uint)Math.Min(uint.MaxValue, bytes));
        output.Write(word);
        var buffer = new byte[1 << 20];
        var remaining = bytes;
        while (remaining > 0)
        {
            var n = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (n <= 0) break;
            output.Write(buffer, 0, n);
            remaining -= n;
        }
        if (bytes % 2 == 1) output.WriteByte(0);
    }
}
