using System.Buffers.Binary;

namespace Shiyin.IO;

/// <summary>WAV 的讀寫（M§5.2、§8）：16 kHz 的錄音、評估用的檔案。PCM 16/24/32 位元、32 位元浮點，任意聲道數</summary>
public sealed class WavReader : IDisposable
{
    readonly FileStream file;
    readonly long dataStart;
    readonly long dataLength;
    long position;

    public int Channels { get; }
    public int SampleRate { get; }
    public int BitsPerSample { get; }
    public bool IsFloat { get; }
    public long Frames => dataLength / BlockAlign;
    int BlockAlign => Channels * BitsPerSample / 8;

    public WavReader(string path)
    {
        file = File.OpenRead(path);
        Span<byte> header = stackalloc byte[12];
        file.ReadExactly(header);
        if (header[..4].SequenceEqual("RIFF"u8) == false || header[8..12].SequenceEqual("WAVE"u8) == false)
            throw new InvalidDataException(Localization.L.T("不是 WAV 檔"));
        Span<byte> chunk = stackalloc byte[8];
        var foundFormat = false;
        while (true)
        {
            if (file.Read(chunk) < 8) throw new InvalidDataException(Localization.L.T("找不到音訊資料區段"));
            var id = System.Text.Encoding.ASCII.GetString(chunk[..4]);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            if (id == "fmt ")
            {
                var fmt = new byte[size];
                file.ReadExactly(fmt);
                var format = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                Channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2));
                SampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt.AsSpan(4));
                BitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14));
                if (format == 0xFFFE && fmt.Length >= 26) format = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(24));
                IsFloat = format == 3;
                if (format is not (1 or 3)) throw new InvalidDataException(Localization.L.F("不支援的 WAV 格式 %lld", format));
                foundFormat = true;
                if (size % 2 == 1) file.Seek(1, SeekOrigin.Current);
            }
            else if (id == "data")
            {
                if (!foundFormat) throw new InvalidDataException(Localization.L.T("WAV 缺少格式區段"));
                dataStart = file.Position;
                // 沒有正常結束的錄音：大小欄位是 0 或超過檔案，用實際的檔案大小（同 WAVRepair）
                dataLength = size == 0 || dataStart + size > file.Length ? file.Length - dataStart : size;
                dataLength -= dataLength % BlockAlign;
                break;
            }
            else file.Seek(size + size % 2, SeekOrigin.Current);
        }
    }

    public void Dispose() => file.Dispose();

    /// <summary>讀最多 frames 個音框，每個聲道一個陣列（-1～1）；讀完回傳空的</summary>
    public float[][] Read(int frames)
    {
        var remaining = (dataLength - position) / BlockAlign;
        var count = (int)Math.Min(frames, remaining);
        var result = new float[Channels][];
        for (var c = 0; c < Channels; c++) result[c] = new float[count];
        if (count <= 0) return result;
        var bytes = new byte[count * BlockAlign];
        file.Seek(dataStart + position, SeekOrigin.Begin);
        file.ReadExactly(bytes);
        position += bytes.Length;
        var width = BitsPerSample / 8;
        for (var i = 0; i < count; i++)
        {
            for (var c = 0; c < Channels; c++)
            {
                var o = i * BlockAlign + c * width;
                result[c][i] = (BitsPerSample, IsFloat) switch
                {
                    (32, true) => BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(o)),
                    (16, false) => BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(o)) / 32768f,
                    (24, false) => (bytes[o] | bytes[o + 1] << 8 | (sbyte)bytes[o + 2] << 16) / 8388608f,
                    (32, false) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(o)) / 2147483648f,
                    (8, false) => (bytes[o] - 128) / 128f,
                    _ => throw new InvalidDataException(Localization.L.F("不支援 %lld 位元", BitsPerSample)),
                };
            }
        }
        return result;
    }
}

/// <summary>16 位元 PCM 的 WAV 寫入（匯出 wav、錄音），標頭每次寫入後都更新（意外結束時用實際大小就能讀）</summary>
public sealed class WavWriter : IDisposable
{
    readonly FileStream file;
    readonly int channels;
    long dataBytes;

    public WavWriter(string path, int channels, int sampleRate = 16000)
    {
        this.channels = channels;
        file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        var header = new byte[44];
        "RIFF"u8.CopyTo(header);
        "WAVE"u8.CopyTo(header.AsSpan(8));
        "fmt "u8.CopyTo(header.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), (uint)(sampleRate * channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32), (ushort)(channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(34), 16);
        "data"u8.CopyTo(header.AsSpan(36));
        file.Write(header);
    }

    /// <summary>寫入對齊好的聲道（每個聲道一樣長）</summary>
    public void Write(IReadOnlyList<float[]> channelData, int count)
    {
        var bytes = new byte[count * channels * 2];
        for (var i = 0; i < count; i++)
            for (var c = 0; c < channels; c++)
            {
                var v = Math.Clamp(channelData[c][i], -1f, 1f);
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan((i * channels + c) * 2), (short)Math.Round(v * 32767f));
            }
        file.Write(bytes);
        dataBytes += bytes.Length;
    }

    /// <summary>更新 RIFF、data 的大小</summary>
    public void Flush()
    {
        var position = file.Position;
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)Math.Min(uint.MaxValue, 36 + dataBytes));
        file.Seek(4, SeekOrigin.Begin);
        file.Write(size);
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)Math.Min(uint.MaxValue, dataBytes));
        file.Seek(40, SeekOrigin.Begin);
        file.Write(size);
        file.Seek(position, SeekOrigin.Begin);
        file.Flush(flushToDisk: false);
    }

    public long Frames => dataBytes / (channels * 2);

    public void Dispose()
    {
        Flush();
        file.Dispose();
    }
}
