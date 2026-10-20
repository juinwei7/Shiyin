using NAudio.Wave;
using Shiyin.Localization;
using Shiyin.Services;
using Shiyin.Services.Capture;
using Shiyin.Whisper;

namespace Shiyin.Platform;

/// <summary>
/// 解碼（design-windows §8、T-W9）：Media Foundation 依序讀、轉成 16 kHz 單聲道浮點（同 D34 的串流讀法）。
/// WAV 直接讀（不經過 Media Foundation，結果跟 Mac 一樣）；AIFF 用 NAudio 的 AiffFileReader。
/// Windows 的 N 版沒有 Media Foundation，要裝 Media Feature Pack（系統需求）
/// </summary>
public sealed class MediaFoundationDecoder : IAudioDecoder
{
    static readonly WavDecoder Wav = new();

    static bool IsWav(string path) => Importer.Extension(path) == "wav";

    static WaveStream OpenReader(string path)
    {
        var ext = Importer.Extension(path);
        try
        {
            if (ext is "aif" or "aiff") return new AiffFileReader(path);
            return new MediaFoundationReader(path, new MediaFoundationReader.MediaFoundationReaderSettings { RequestFloatOutput = true });
        }
        catch (System.Runtime.InteropServices.COMException e)
        {
            throw new IOException(L.F("沒辦法讀取「%@」（Media Foundation 錯誤代碼 0x%@）", Path.GetFileName(path), e.HResult.ToString("X8")), e);
        }
    }

    public IAudioStream Open(string path) => IsWav(path) ? Wav.Open(path) : new Stream(OpenReader(path));

    public float[] Decode(string path, CancellationToken cancel = default)
    {
        if (IsWav(path)) return Wav.Decode(path, cancel);
        using var stream = new Stream(OpenReader(path));
        var buffer = new List<float>();
        while (!stream.Finished)
        {
            cancel.ThrowIfCancellationRequested();
            stream.Fill(buffer, buffer.Count + 16000 * 60);
        }
        return [.. buffer];
    }

    public float[][] DecodeChannels(string path, CancellationToken cancel = default)
    {
        if (IsWav(path)) return Wav.DecodeChannels(path, cancel);
        using var reader = OpenReader(path);
        var format = reader.WaveFormat;
        var channels = Enumerable.Range(0, format.Channels).Select(_ => new List<float>()).ToArray();
        var resamplers = Enumerable.Range(0, format.Channels).Select(_ => new StreamResampler(format.SampleRate)).ToArray();
        var bytes = new byte[format.AverageBytesPerSecond];
        int n;
        while ((n = reader.Read(bytes, 0, bytes.Length)) > 0)
        {
            cancel.ThrowIfCancellationRequested();
            var frames = n / format.BlockAlign;
            for (var c = 0; c < format.Channels; c++)
            {
                var one = new float[frames];
                for (var f = 0; f < frames; f++) one[f] = Sample(bytes, (f * format.Channels + c) * (format.BitsPerSample / 8), format);
                channels[c].AddRange(resamplers[c].Process(one));
            }
        }
        return channels.Select(c => c.ToArray()).ToArray();
    }

    static float Sample(byte[] data, int offset, WaveFormat format) => format.Encoding == WaveFormatEncoding.IeeeFloat || format.BitsPerSample == 32 && format is WaveFormatExtensible
        ? BitConverter.ToSingle(data, offset)
        : format.BitsPerSample switch
        {
            16 => BitConverter.ToInt16(data, offset) / 32768f,
            24 => ((data[offset + 2] << 24 | data[offset + 1] << 16 | data[offset] << 8) >> 8) / 8388608f,
            _ => 0,
        };

    /// <summary>依序讀出 16 kHz 單聲道（whisper 一段一段讀）</summary>
    sealed class Stream : IAudioStream
    {
        readonly WaveStream reader;
        readonly StreamResampler resampler;
        readonly byte[] bytes;
        readonly bool isFloat;

        public Stream(WaveStream reader)
        {
            this.reader = reader;
            var format = reader.WaveFormat;
            resampler = new StreamResampler(format.SampleRate);
            bytes = new byte[format.AverageBytesPerSecond];
            isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat || format is WaveFormatExtensible && format.BitsPerSample == 32;
            EstimatedCount = (int)Math.Min(int.MaxValue, reader.TotalTime.TotalSeconds * StreamResampler.OutputRate);
        }

        public int EstimatedCount { get; }
        public bool Finished { get; private set; }

        public void Fill(List<float> buffer, int count)
        {
            var format = reader.WaveFormat;
            while (buffer.Count < count && !Finished)
            {
                var n = reader.Read(bytes, 0, bytes.Length - bytes.Length % format.BlockAlign);
                if (n <= 0)
                {
                    Finished = true;
                    return;
                }
                buffer.AddRange(resampler.Process(StreamResampler.Mono(bytes.AsSpan(0, n), format.Channels, format.BitsPerSample, isFloat)));
            }
        }

        public void Dispose() => reader.Dispose();
    }
}

/// <summary>
/// 壓縮成 m4a（M§6.4、design-windows §13）：Media Foundation 的 AAC 編碼器只接受 44.1 或 48 kHz、96～192 kbps，16 kHz 的錄音先升頻到 48 kHz。
/// 每小時約 43 MB（R27 的 R9.4）
/// </summary>
public sealed class MediaFoundationCompressor : IAudioCompressor
{
    public Task Compress(string wavPath, string m4aPath, CancellationToken cancel) => Task.Run(() =>
    {
        using var reader = new WaveFileReader(wavPath);
        using var resampled = new MediaFoundationResampler(reader, new WaveFormat(48000, 16, reader.WaveFormat.Channels)) { ResamplerQuality = 60 };
        var temp = m4aPath + ".tmp.mp4";
        try
        {
            MediaFoundationEncoder.EncodeToAac(new Cancellable(resampled, cancel), temp, 96000);
            cancel.ThrowIfCancellationRequested();
            File.Move(temp, m4aPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }, cancel);

    /// <summary>取消時讓編碼器讀到檔案結尾</summary>
    sealed class Cancellable(IWaveProvider inner, CancellationToken cancel) : IWaveProvider
    {
        public WaveFormat WaveFormat => inner.WaveFormat;
        public int Read(Span<byte> buffer) => cancel.IsCancellationRequested ? 0 : inner.Read(buffer);
    }

    /// <summary>匯出錄音（R7.2）：m4a 是 m4a 時直接複製；wav 解碼成 16 kHz 的 WAV</summary>
    public async Task Export(string source, string format, string destination, CancellationToken cancel)
    {
        var ext = Importer.Extension(source);
        if (ext == format) { File.Copy(source, destination, overwrite: true); return; }
        if (format == "m4a" && ext == "wav") { await Compress(source, destination, cancel); return; }
        if (format == "m4a")
        {
            // 其他格式（匯入的 mp3、mp4…）：先解碼成 WAV 再壓縮
            var temp = destination + ".tmp.wav";
            try
            {
                await Export(source, "wav", temp, cancel);
                await Compress(temp, destination, cancel);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            return;
        }
        if (format == "wav")
        {
            var samples = await Task.Run(() => new MediaFoundationDecoder().Decode(source, cancel), cancel);
            using var writer = new IO.WavWriter(destination, 1);
            writer.Write([samples], samples.Length);
            return;
        }
        throw new IOException(L.T("不支援這個音訊格式"));
    }
}
