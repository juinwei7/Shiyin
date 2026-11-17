using System.Buffers.Binary;
using System.Runtime.InteropServices.WindowsRuntime;
using Shiyin.Model;
using Shiyin.Services;
using Windows.Media.Core;
using Windows.Media.MediaProperties;

namespace Shiyin.Platform;

/// <summary>
/// 編輯畫面的試聽（R107.3、R107.4、M§81.5、design-windows §11.40）：照片段接好的錄音交給 MediaPlayer（同 Mac 用 AVMutableComposition 接）。
/// PlanPcmStream 依序讀每一段（跳過刪掉、裁掉的部分；合併時兩場接在一起），混成單聲道、取樣率取高的（最高 48 kHz），每次送 0.1 秒 16 位元 PCM。
/// 時間是新時間軸上的秒數；跳播時從要求的位置接著讀
/// </summary>
public sealed class EditPreviewSource : IDisposable
{
    readonly object gate = new();
    readonly Dictionary<long, IPcmReader> readers;
    readonly PlanPcmStream stream;
    readonly int rate;
    TimeSpan position;

    public MediaStreamSource Source { get; }

    public EditPreviewSource(EditPlan plan, IReadOnlyDictionary<long, string> sources)
    {
        readers = [];
        foreach (var (id, path) in sources)
        {
            try { readers[id] = new MediaFoundationPcmReader(path); }
            catch (IOException e) { Log.Error($"preview: can't open {e.GetType().Name}"); }
        }
        rate = Math.Min(48000, AudioEdit.Target(readers.Values, aac: false).Rate);
        stream = new PlanPcmStream(plan, id => readers.GetValueOrDefault(id), rate, 1);
        Source = new MediaStreamSource(new AudioStreamDescriptor(AudioEncodingProperties.CreatePcm((uint)rate, 1, 16)))
        {
            CanSeek = true,
            Duration = TimeSpan.FromSeconds(plan.Duration),
        };
        Source.Starting += (_, e) =>
        {
            lock (gate)
            {
                if (e.Request.StartPosition is TimeSpan start)
                {
                    stream.Seek(start.TotalSeconds);
                    position = start;
                }
                e.Request.SetActualStartPosition(position);
            }
        };
        Source.SampleRequested += (_, e) =>
        {
            lock (gate)
            {
                var samples = new float[rate / 10];
                var n = stream.Read(samples, samples.Length);
                if (n <= 0) return;   // 播完：不給樣本就是結束
                var bytes = new byte[n * 2];
                for (var i = 0; i < n; i++)
                    BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), (short)Math.Clamp(samples[i] * 32767f, -32768f, 32767f));
                var sample = MediaStreamSample.CreateFromBuffer(bytes.AsBuffer(), position);
                sample.Duration = TimeSpan.FromSeconds(n / (double)rate);
                position += sample.Duration;
                e.Request.Sample = sample;
            }
        };
    }

    public void Dispose()
    {
        lock (gate)
        {
            stream.Dispose();
            foreach (var r in readers.Values) r.Dispose();
            readers.Clear();
        }
    }
}
