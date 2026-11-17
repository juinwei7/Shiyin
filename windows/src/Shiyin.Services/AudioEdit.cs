using System.Buffers.Binary;
using NAudio.Dsp;
using Shiyin.IO;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Services.Capture;
using Shiyin.Whisper;

namespace Shiyin.Services;

/// <summary>讀一場會議的錄音（編輯會議的接檔、試聽，M§81.2）：浮點、交錯，可以跳到某個時間</summary>
public interface IPcmReader : IDisposable
{
    int SampleRate { get; }
    int Channels { get; }
    double Seconds { get; }
    /// <summary>接下來從 seconds 秒讀（超出時讀不到東西）</summary>
    void Seek(double seconds);
    /// <summary>讀最多 frames 個音框到 interleaved（交錯，長度至少 frames × Channels）；讀完回傳 0</summary>
    int Read(float[] interleaved, int frames);
}

/// <summary>
/// 編輯會議的錄音（M§81.2、design-windows §11.40）：照片段接成新的錄音、開錄音來讀。Windows 是 Media Foundation（Platform 的
/// MediaFoundationAudioEditor）；沒給（測試）用 AudioEdit.WavOnly
/// </summary>
public interface IAudioEditor
{
    /// <summary>
    /// 照 plan 的片段接起來，寫在 directory（要已經存在）裡；sources 是每場會議的錄音，沒有錄音的那幾段補安靜。全部都沒有錄音時回傳 null
    /// </summary>
    Task<string?> Compose(EditPlan plan, IReadOnlyDictionary<long, string> sources, string directory, CancellationToken cancel);
    IPcmReader Open(string path);
}

public static class AudioEdit
{
    /// <summary>WAV 的格式區段和資料的位置（RIFF 的區段順序不固定；沒有正常結束的錄音用實際的檔案大小，同 WavReader、WAVRepair）</summary>
    sealed record Layout(byte[] Format, long DataStart, long DataLength, int SampleRate, int BlockAlign)
    {
        public long Frames => DataLength / BlockAlign;
    }

    static Layout ReadLayout(Stream input)
    {
        Span<byte> header = stackalloc byte[12];
        input.ReadExactly(header);
        if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..12].SequenceEqual("WAVE"u8)) throw new InvalidDataException(L.T("不是 WAV 檔"));
        byte[]? format = null;
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
                var start = input.Position;
                var length = size == 0 || start + size > input.Length ? input.Length - start : size;
                var rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(4));
                var align = (int)BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(12));
                if (align == 0 || rate == 0) throw new InvalidDataException(L.T("WAV 缺少格式區段"));
                return new Layout(format, start, length - length % align, rate, align);
            }
            else input.Seek(size + size % 2, SeekOrigin.Current);
        }
    }

    /// <summary>時間 → 第幾個音框（切點在兩個片段相接的地方要算到同一個音框）</summary>
    static long Frame(double seconds, int rate) => (long)Math.Floor(seconds * rate + 1e-6);

    /// <summary>
    /// 只有一場會議、錄音是 WAV（M§81.2）：照原本的格式接（fmt 區段原樣、data 照位元組複製，聲道、取樣率、位元數都不變，逐個取樣相同）。
    /// 其他區段（LIST 等）不帶；片段超出錄音的部分當成沒有（同 Mac 的 composeWAV）。寫成 directory/edit.wav
    /// </summary>
    public static string ComposeWav(EditPlan plan, string path, string directory)
    {
        using var input = File.OpenRead(path);
        var layout = ReadLayout(input);
        var ranges = new List<(long From, long To)>();
        foreach (var piece in plan.Pieces)
        {
            var from = Frame(piece.Start, layout.SampleRate);
            var to = Math.Min(layout.Frames, Frame(piece.End, layout.SampleRate));
            if (from < to) ranges.Add((from, to));
        }
        var bytes = ranges.Sum(r => (r.To - r.From) * layout.BlockAlign);
        var output = Path.Combine(directory, "edit.wav");
        using var file = new FileStream(output, FileMode.Create, FileAccess.Write);
        var fmt = layout.Format;
        Span<byte> word = stackalloc byte[4];
        file.Write("RIFF"u8);
        BinaryPrimitives.WriteUInt32LittleEndian(word, (uint)Math.Min(uint.MaxValue, 4 + 8 + fmt.Length + fmt.Length % 2 + 8 + bytes + bytes % 2));
        file.Write(word);
        file.Write("WAVE"u8);
        file.Write("fmt "u8);
        BinaryPrimitives.WriteUInt32LittleEndian(word, (uint)fmt.Length);
        file.Write(word);
        file.Write(fmt);
        if (fmt.Length % 2 == 1) file.WriteByte(0);
        file.Write("data"u8);
        BinaryPrimitives.WriteUInt32LittleEndian(word, (uint)Math.Min(uint.MaxValue, bytes));
        file.Write(word);
        var buffer = new byte[1 << 20];
        foreach (var (from, to) in ranges)
        {
            input.Seek(layout.DataStart + from * layout.BlockAlign, SeekOrigin.Begin);
            var remaining = (to - from) * layout.BlockAlign;
            while (remaining > 0)
            {
                var n = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (n <= 0) break;
                file.Write(buffer, 0, n);
                remaining -= n;
            }
        }
        if (bytes % 2 == 1) file.WriteByte(0);
        return output;
    }

    /// <summary>片段用到的會議（照順序、不重複）</summary>
    public static List<long> MeetingsIn(EditPlan plan)
    {
        var used = new List<long>();
        foreach (var p in plan.Pieces) if (!used.Contains(p.MeetingId)) used.Add(p.MeetingId);
        return used;
    }

    /// <summary>
    /// 接好的錄音的格式（M§81.2）：取樣率取高的、聲道取多的（最多 2）。aac：AAC 編碼器只收 44.1、48 kHz，其他的升到 48 kHz（同壓縮、分割，design-windows §13）
    /// </summary>
    public static (int Rate, int Channels) Target(IEnumerable<IPcmReader> readers, bool aac)
    {
        int rate = 0, channels = 1;
        foreach (var r in readers)
        {
            rate = Math.Max(rate, r.SampleRate);
            channels = Math.Max(channels, r.Channels);
        }
        if (rate <= 0) rate = 16000;
        if (aac && rate is not (44100 or 48000)) rate = 48000;
        return (rate, Math.Min(channels, 2));
    }

    /// <summary>編輯畫面的波形（M§81.5）：16 kHz 單聲道讀一遍，每 step 秒一個峰值（0～1，取絕對值的最大）</summary>
    public static float[] Peaks(IAudioStream stream, double step, CancellationToken cancel)
    {
        var bucket = Math.Max(1, (int)(16000 * step));
        var peaks = new List<float>();
        var buffer = new List<float>();
        float current = 0;
        var count = 0;
        while (!stream.Finished)
        {
            cancel.ThrowIfCancellationRequested();
            buffer.Clear();
            stream.Fill(buffer, 16000 * 10);
            foreach (var v in buffer)
            {
                current = Math.Max(current, Math.Abs(v));
                if (++count < bucket) continue;
                peaks.Add(Math.Min(1, current));
                current = 0;
                count = 0;
            }
        }
        if (count > 0) peaks.Add(Math.Min(1, current));
        return [.. peaks];
    }

    /// <summary>WAV 的 IPcmReader（測試、WAV 的錄音）</summary>
    public sealed class WavPcmReader(string path) : IPcmReader
    {
        readonly WavReader reader = new(path);
        public int SampleRate => reader.SampleRate;
        public int Channels => reader.Channels;
        public double Seconds => reader.SampleRate > 0 ? reader.Frames / (double)reader.SampleRate : 0;
        public void Seek(double seconds) => reader.Seek(Frame(seconds, reader.SampleRate));

        public int Read(float[] interleaved, int frames)
        {
            var data = reader.Read(frames);
            var n = data.Length > 0 ? data[0].Length : 0;
            for (var f = 0; f < n; f++)
                for (var c = 0; c < data.Length; c++) interleaved[f * data.Length + c] = data[c][f];
            return n;
        }

        public void Dispose() => reader.Dispose();
    }

    /// <summary>
    /// 只接 WAV（Mac 上的測試、評估）：一場的 WAV 照原格式接（ComposeWav）；好幾場（合併）解碼後寫成 16 位元的 WAV
    ///（Windows 是 m4a，Platform 的 MediaFoundationAudioEditor）。其他格式說不支援
    /// </summary>
    public sealed class WavOnly : IAudioEditor
    {
        public IPcmReader Open(string path) => Importer.Extension(path) == "wav" ? new WavPcmReader(path) : throw new IOException(L.T("不支援這個音訊格式"));

        public Task<string?> Compose(EditPlan plan, IReadOnlyDictionary<long, string> sources, string directory, CancellationToken cancel) => Task.Run(() =>
        {
            var used = MeetingsIn(plan);
            var files = used.Where(sources.ContainsKey).ToDictionary(id => id, id => sources[id]);
            if (files.Count == 0 || plan.Duration <= 0) return null;
            if (used.Count == 1 && files.Count == 1 && Importer.Extension(files.Values.First()) == "wav") return ComposeWav(plan, files.Values.First(), directory);
            var readers = files.ToDictionary(p => p.Key, p => Open(p.Value));
            try
            {
                var (rate, channels) = Target(readers.Values, aac: false);
                var output = Path.Combine(directory, "edit.wav");
                using var writer = new WavWriter(output, channels, rate);
                using var stream = new PlanPcmStream(plan, id => readers.GetValueOrDefault(id), rate, channels);
                var buffer = new float[8192 * channels];
                var split = Enumerable.Range(0, channels).Select(_ => new float[8192]).ToArray();
                int n;
                while ((n = stream.Read(buffer, 8192)) > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    for (var f = 0; f < n; f++)
                        for (var c = 0; c < channels; c++) split[c][f] = buffer[f * channels + c];
                    writer.Write(split, n);
                }
                return (string?)output;
            }
            finally
            {
                foreach (var r in readers.Values) r.Dispose();
            }
        }, cancel);
    }
}

/// <summary>
/// 照片段依序讀出（M§81.2）：每段從那場會議的錄音讀、換成同一個取樣率和聲道數（聲道少的複製到其他聲道，target 是單聲道時混在一起），
/// 錄音比片段短、沒有錄音的那幾段補安靜，所以長度一定是每段 round(長度 × 取樣率) 加起來（新的錄音跟時間軸一致，同 Mac 的 append）。
/// 接檔、試聽都用這個；readers 由呼叫的人關
/// </summary>
public sealed class PlanPcmStream : IDisposable
{
    readonly EditPlan plan;
    readonly Func<long, IPcmReader?> reader;
    readonly int rate, channels;
    readonly long[] pieceFrames;
    int piece;
    long into;
    bool started;
    // 目前這段的來源
    IPcmReader? source;
    long sourceLeft;
    WdlResampler? resampler;
    readonly Queue<float> pending = new();
    float[] raw = [];

    public PlanPcmStream(EditPlan plan, Func<long, IPcmReader?> reader, int rate, int channels)
    {
        (this.plan, this.reader, this.rate, this.channels) = (plan, reader, rate, channels);
        pieceFrames = plan.Pieces.Select(p => (long)Math.Round(p.Length * rate, MidpointRounding.AwayFromZero)).ToArray();
        TotalFrames = pieceFrames.Sum();
    }

    public int SampleRate => rate;
    public int Channels => channels;
    public long TotalFrames { get; }
    /// <summary>新時間軸上讀到哪裡（秒）</summary>
    public double Position => (pieceFrames.Take(piece).Sum() + into) / (double)rate;

    /// <summary>從新時間軸的 seconds 秒接著讀</summary>
    public void Seek(double seconds)
    {
        var frame = Math.Clamp((long)Math.Round(seconds * rate), 0, TotalFrames);
        piece = 0;
        while (piece < pieceFrames.Length && frame >= pieceFrames[piece])
        {
            frame -= pieceFrames[piece];
            piece++;
        }
        into = frame;
        started = false;
    }

    /// <summary>讀最多 frames 個音框（交錯）；讀完回傳 0</summary>
    public int Read(float[] interleaved, int frames)
    {
        var written = 0;
        while (written < frames && piece < pieceFrames.Length)
        {
            if (into >= pieceFrames[piece])
            {
                piece++;
                into = 0;
                started = false;
                continue;
            }
            if (!started) StartPiece();
            var want = (int)Math.Min(frames - written, pieceFrames[piece] - into);
            var got = Take(interleaved.AsSpan(written * channels, want * channels));
            // 錄音讀完了（比片段短）：這段剩下的補安靜
            if (got < want)
            {
                interleaved.AsSpan((written + got) * channels, (want - got) * channels).Clear();
                got = want;
            }
            written += got;
            into += got;
        }
        return written;
    }

    void StartPiece()
    {
        started = true;
        pending.Clear();
        var p = plan.Pieces[piece];
        source = reader(p.MeetingId);
        resampler = null;
        sourceLeft = 0;
        if (source is null || source.SampleRate <= 0) return;
        var from = p.Start + into / (double)rate;
        if (from >= source.Seconds) { source = null; return; }
        source.Seek(from);
        sourceLeft = (long)Math.Round((p.End - from) * source.SampleRate);
        if (source.SampleRate != rate)
        {
            resampler = new WdlResampler();
            resampler.SetMode(true, 2, false);
            resampler.SetFilterParms();
            resampler.SetFeedMode(true);
            resampler.SetRates(source.SampleRate, rate);
        }
    }

    /// <summary>從目前這段的來源拿 span.Length / channels 個音框，不夠時回傳拿到的</summary>
    int Take(Span<float> span)
    {
        var want = span.Length / channels;
        while (pending.Count < span.Length && source is not null && sourceLeft > 0)
        {
            var n = (int)Math.Min(4096, sourceLeft);
            if (raw.Length < n * source.Channels) raw = new float[n * source.Channels];
            var got = source.Read(raw, n);
            if (got <= 0) { sourceLeft = 0; break; }
            sourceLeft -= got;
            var mapped = Map(raw.AsSpan(0, got * source.Channels), source.Channels, got);
            foreach (var v in Resample(mapped, got)) pending.Enqueue(v);
        }
        var frames = Math.Min(want, pending.Count / channels);
        for (var i = 0; i < frames * channels; i++) span[i] = pending.Dequeue();
        return frames;
    }

    /// <summary>來源的聲道 → 目標的聲道：目標是單聲道時混在一起（同播放的 ChannelMix），其他照 Mac：第 c 個聲道取來源的 min(c, 來源聲道數 − 1)</summary>
    float[] Map(ReadOnlySpan<float> input, int sourceChannels, int frames)
    {
        var output = new float[frames * channels];
        if (channels == 1)
        {
            if (sourceChannels == 1) input[..frames].CopyTo(output);
            else ChannelMix.Mix(input, sourceChannels, output);
            return output;
        }
        for (var f = 0; f < frames; f++)
            for (var c = 0; c < channels; c++) output[f * channels + c] = input[f * sourceChannels + Math.Min(c, sourceChannels - 1)];
        return output;
    }

    ReadOnlySpan<float> Resample(float[] input, int frames)
    {
        if (resampler is null) return input;
        var accepted = Math.Min(frames, resampler.ResamplePrepare(frames, channels, out Span<float> buffer));
        input.AsSpan(0, accepted * channels).CopyTo(buffer);
        var capacity = (int)Math.Ceiling(frames * (double)rate / source!.SampleRate) + 32;
        var output = new float[capacity * channels];
        var produced = resampler.ResampleOut(output, accepted, capacity, channels);
        return output.AsSpan(0, produced * channels);
    }

    public void Dispose() => pending.Clear();
}
