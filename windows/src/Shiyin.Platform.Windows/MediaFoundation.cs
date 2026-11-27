using NAudio.Wave;
using Shiyin.Localization;
using Shiyin.Model;
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

    /// <summary>WAV 以外的錄音：Media Foundation 讀成浮點（原本的取樣率、聲道），AIFF 用 AiffFileReader；解不開時說明怎麼辦</summary>
    internal static WaveStream OpenReader(string path)
    {
        var ext = Importer.Extension(path);
        try
        {
            if (ext is "aif" or "aiff") return new AiffFileReader(path);
            return new MediaFoundationReader(path, new MediaFoundationReader.MediaFoundationReaderSettings { RequestFloatOutput = true });
        }
        catch (System.Runtime.InteropServices.COMException e)
        {
            // 錯誤訊息不放檔名：這裡是複製進資料夾後的名字（41.ogg），不是使用者匯入的檔案
            throw new IOException(UnsupportedFormats.Contains((uint)e.HResult)
                ? L.F("Windows 解不開 .%@ 的檔案：請先轉成 m4a、mp3 或 wav 再匯入（ogg、opus 可以從 Microsoft Store 安裝「Web 媒體延伸模組」）", ext)
                : L.F("讀不到這個錄音檔，可能檔案已損壞或不是支援的格式（%@）", $"Media Foundation 0x{e.HResult:X8}"), e);
        }
    }

    /// <summary>
    /// Windows 沒有這種容器或編碼的解碼器（T-W9：UUP 的映像檔沒有「Web 媒體延伸模組」時 ogg、opus 就是這樣）：
    /// MF_E_UNSUPPORTED_BYTESTREAM_TYPE、MF_E_TOPO_CODEC_NOT_FOUND、MF_E_INVALIDMEDIATYPE
    /// </summary>
    static readonly uint[] UnsupportedFormats = [0xC00D36C4, 0xC00D5212, 0xC00D36B4];

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
/// 編輯會議的錄音（R107、M§81.2、design-windows §11.40）：只有一場、錄音是 WAV 時照原格式接（AudioEdit.ComposeWav，逐個取樣相同）；
/// 其他（壓縮好的 m4a、匯入的 mp3、影片，或合併）用 Media Foundation 解碼，照片段接起來（PlanPcmStream：取樣率取高的、聲道取多的，
/// 沒有錄音的片段補安靜），壓成 m4a（AAC 只收 44.1／48 kHz，其他升到 48 kHz；位元率照來源的平均取 96～192 kbps 最接近的，WAV 的來源算 96）
/// </summary>
public sealed class MediaFoundationAudioEditor : IAudioEditor
{
    static readonly int[] Bitrates = [96000, 128000, 160000, 192000];

    public IPcmReader Open(string path) => new MediaFoundationPcmReader(path);

    public Task<string?> Compose(EditPlan plan, IReadOnlyDictionary<long, string> sources, string directory, CancellationToken cancel) => Task.Run(() =>
    {
        var used = AudioEdit.MeetingsIn(plan);
        var files = used.Where(sources.ContainsKey).ToDictionary(id => id, id => sources[id]);
        if (files.Count == 0 || plan.Duration <= 0) return null;
        if (used.Count == 1 && files.Count == 1 && Importer.Extension(files.Values.First()) == "wav") return AudioEdit.ComposeWav(plan, files.Values.First(), directory);
        var readers = files.ToDictionary(p => p.Key, p => (IPcmReader)new MediaFoundationPcmReader(p.Value));
        try
        {
            var (rate, channels) = AudioEdit.Target(readers.Values, aac: true);
            var bitrate = files.Values.Zip(readers.Values).Select(p => Bitrate(p.First, p.Second.Seconds)).Max();
            using var stream = new PlanPcmStream(plan, id => readers.GetValueOrDefault(id), rate, channels);
            var output = Path.Combine(directory, "edit.m4a");
            var temp = output + ".tmp.mp4";
            try
            {
                // Media Foundation 照副檔名選容器：先寫成 .mp4 再改名（同壓縮）
                MediaFoundationEncoder.EncodeToAac(new Pcm16(stream, cancel), temp, bitrate);
                cancel.ThrowIfCancellationRequested();
                File.Move(temp, output, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            return (string?)output;
        }
        finally
        {
            foreach (var r in readers.Values) r.Dispose();
        }
    }, cancel);

    /// <summary>原檔的平均位元率最接近的那一級（影片檔會偏大，最多 192 kbps）；WAV 照壓縮的 96 kbps</summary>
    static int Bitrate(string path, double seconds)
    {
        if (Importer.Extension(path) == "wav" || seconds <= 0) return Bitrates[0];
        var average = new FileInfo(path).Length * 8 / seconds;
        return Bitrates.OrderBy(b => Math.Abs(b - average)).First();
    }

    /// <summary>PlanPcmStream 換成 16 位元 PCM 給編碼器讀（取消時讀到結尾）</summary>
    sealed class Pcm16(PlanPcmStream stream, CancellationToken cancel) : IWaveProvider
    {
        float[] buffer = [];
        public WaveFormat WaveFormat { get; } = new(stream.SampleRate, 16, stream.Channels);

        public int Read(Span<byte> output)
        {
            if (cancel.IsCancellationRequested) return 0;
            var frames = output.Length / WaveFormat.BlockAlign;
            if (buffer.Length < frames * stream.Channels) buffer = new float[frames * stream.Channels];
            var n = stream.Read(buffer, frames);
            for (var i = 0; i < n * stream.Channels; i++)
                System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(output[(i * 2)..], (short)Math.Round(Math.Clamp(buffer[i], -1f, 1f) * 32767f));
            return n * WaveFormat.BlockAlign;
        }
    }
}

/// <summary>
/// Media Foundation 讀一場的錄音（編輯會議的接檔、試聽、長度）：WAV 直接讀、AIFF 用 AiffFileReader、其他用 Media Foundation 解成浮點
///（同 MediaFoundationDecoder.OpenReader），WAVE_FORMAT_EXTENSIBLE 換成一般的格式再轉成浮點
/// </summary>
public sealed class MediaFoundationPcmReader : IPcmReader
{
    readonly WaveStream reader;
    readonly ISampleProvider samples;

    public MediaFoundationPcmReader(string path)
    {
        reader = MediaFoundationDecoder.OpenReader(path);
        samples = new MediaFoundationCompressor.StandardFormat(reader).ToSampleProvider();
    }

    public int SampleRate => reader.WaveFormat.SampleRate;
    public int Channels => reader.WaveFormat.Channels;
    public double Seconds => reader.TotalTime.TotalSeconds;

    public void Seek(double seconds)
    {
        var align = reader.WaveFormat.BlockAlign;
        var frame = (long)Math.Floor(seconds * SampleRate + 1e-6);
        reader.Position = Math.Clamp(frame * align, 0, reader.Length - reader.Length % align);
    }

    public int Read(float[] interleaved, int frames) => samples.Read(interleaved.AsSpan(0, frames * Channels)) / Channels;

    public void Dispose() => reader.Dispose();
}

/// <summary>
/// 壓縮成 m4a（M§6.4、design-windows §13）：Media Foundation 的 AAC 編碼器只接受 44.1 或 48 kHz、96～192 kbps，16 kHz 的錄音先升頻到 48 kHz。
/// 每小時約 43 MB（R27 的 R9.4）
/// </summary>
public sealed class MediaFoundationCompressor : IAudioCompressor
{
    public Task Compress(string wavPath, string m4aPath, CancellationToken cancel) => Compress(wavPath, m4aPath, cancel, null);

    /// <summary>progress：讀了多少 ÷ 原檔長度（匯出的進度，R120）</summary>
    public Task Compress(string wavPath, string m4aPath, CancellationToken cancel, IProgress<double>? progress) => Task.Run(() =>
    {
        using var reader = new WaveFileReader(wavPath);
        using var resampled = new MediaFoundationResampler(new StandardFormat(reader), new WaveFormat(48000, 16, reader.WaveFormat.Channels)) { ResamplerQuality = 60 };
        var temp = m4aPath + ".tmp.mp4";
        try
        {
            MediaFoundationEncoder.EncodeToAac(new Cancellable(resampled, cancel, progress, Expected(reader.TotalTime, resampled.WaveFormat)), temp, 96000);
            cancel.ThrowIfCancellationRequested();
            File.Move(temp, m4aPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }, cancel);

    /// <summary>
    /// WAVE_FORMAT_EXTENSIBLE（很多錄音軟體、afconvert 寫的 WAV）換成一般的 PCM／浮點格式，資料本身不變：
    /// MediaFoundationResampler 只認 PCM 和 IEEE float 的格式標記（2026-10-21 在虛擬機匯入這種 WAV，壓縮失敗）
    /// </summary>
    internal sealed class StandardFormat(IWaveProvider inner) : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } = Standard(inner.WaveFormat);
        public int Read(Span<byte> buffer) => inner.Read(buffer);

        // NAudio 讀出來的不一定是 WaveFormatExtensible 型別（格式標記是 Extensible 的一般 WaveFormat）：32 位元當成浮點（同解碼的判斷）
        static WaveFormat Standard(WaveFormat f) => f.Encoding != WaveFormatEncoding.Extensible ? f
            : f is WaveFormatExtensible extensible ? extensible.ToStandardWaveFormat()
            : f.BitsPerSample == 32 ? WaveFormat.CreateIeeeFloatWaveFormat(f.SampleRate, f.Channels)
            : new WaveFormat(f.SampleRate, f.BitsPerSample, f.Channels);
    }

    /// <summary>照原檔長度估的位元組數（壓縮讀的是重新取樣後的 PCM）</summary>
    static long Expected(TimeSpan duration, WaveFormat format) => (long)(duration.TotalSeconds * format.AverageBytesPerSecond);

    /// <summary>取消時讀不到東西，編碼器當成讀到檔案結尾就停；progress 有給時照讀了多少 ÷ expected 回報（R120）</summary>
    sealed class Cancellable(IWaveProvider inner, CancellationToken cancel, IProgress<double>? progress = null, long expected = 0) : IWaveProvider
    {
        long read;
        public WaveFormat WaveFormat => inner.WaveFormat;
        public int Read(Span<byte> buffer)
        {
            if (cancel.IsCancellationRequested) return 0;
            var n = inner.Read(buffer);
            read += n;
            if (progress is not null && expected > 0) progress.Report(Math.Min(0.99, (double)read / expected));
            return n;
        }
    }

    /// <summary>
    /// 匯出錄音（R7.2）：m4a 是 m4a 時直接複製；wav 解碼成 16 kHz 的 WAV。
    /// mixChannels：「麥克風＋電腦聲音」的錄音（R73.2、design-windows §11.16）：m4a 混成單聲道再壓縮、wav 解碼成單聲道，
    /// 原檔是同一種格式也一樣。progress：完成的比例（R120、design-windows §11.53），直接複製時不回報
    /// </summary>
    public async Task Export(string source, string format, string destination, CancellationToken cancel, bool mixChannels = false,
        IProgress<double>? progress = null)
    {
        var ext = Importer.Extension(source);
        if (format == "m4a" && mixChannels) { await CompressMixed(source, destination, cancel, progress); progress?.Report(1); return; }
        if (ext == format && !mixChannels) { File.Copy(source, destination, overwrite: true); return; }
        if (format == "m4a" && ext == "wav") { await Compress(source, destination, cancel, progress); progress?.Report(1); return; }
        if (format == "m4a")
        {
            // 其他格式（匯入的 mp3、mp4…）：先解碼成 WAV 再壓縮；進度：解碼、寫暫存檔 0～40%，壓縮 40～100%
            var temp = destination + ".tmp.wav";
            try
            {
                await Export(source, "wav", temp, cancel, progress: Scaled(progress, 0, 0.4));
                await Compress(temp, destination, cancel, Scaled(progress, 0.4, 1));
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            progress?.Report(1);
            return;
        }
        if (format == "wav")
        {
            // 進度：解碼完 70%、寫完 100%（解碼、寫檔沒有中間的進度）
            progress?.Report(0.02);
            var samples = await Task.Run(() => new MediaFoundationDecoder().Decode(source, cancel), cancel);
            cancel.ThrowIfCancellationRequested();
            progress?.Report(0.7);
            using (var writer = new IO.WavWriter(destination, 1)) writer.Write([samples], samples.Length);
            progress?.Report(1);
            return;
        }
        throw new IOException(L.T("不支援這個音訊格式"));
    }

    /// <summary>分兩段做時，一段的 0～1 換成整體的 from～to</summary>
    static IProgress<double>? Scaled(IProgress<double>? progress, double from, double to) =>
        progress is null ? null : new Relay(v => progress.Report(from + (to - from) * v));

    sealed class Relay(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    /// <summary>讀原檔、兩個聲道混成單聲道（ChannelMix），照壓縮的做法升頻到 48 kHz、AAC</summary>
    static Task CompressMixed(string source, string m4aPath, CancellationToken cancel, IProgress<double>? progress = null) => Task.Run(() =>
    {
        using var reader = MixedAudioSource.OpenReader(source);
        var mono = new ChannelMix.ToMono(new StandardFormat(reader).ToSampleProvider()).ToWaveProvider16();
        using var resampled = new MediaFoundationResampler(mono, new WaveFormat(48000, 16, 1)) { ResamplerQuality = 60 };
        var temp = m4aPath + ".tmp.mp4";
        try
        {
            MediaFoundationEncoder.EncodeToAac(new Cancellable(resampled, cancel, progress, Expected(reader.TotalTime, resampled.WaveFormat)), temp, 96000);
            cancel.ThrowIfCancellationRequested();
            File.Move(temp, m4aPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }, cancel);
}
