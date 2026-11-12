using System.Buffers.Binary;
using System.Runtime.InteropServices.WindowsRuntime;
using NAudio.Wave;
using Shiyin.Services;
using Shiyin.Services.Capture;
using Windows.Media.Core;
using Windows.Media.MediaProperties;

namespace Shiyin.Platform;

/// <summary>
/// 「麥克風＋電腦聲音」的錄音混成單聲道再交給 MediaPlayer（R73.1、design-windows §11.16）：NAudio 讀原檔，
/// 每次送 0.1 秒 16-bit PCM（原檔的取樣率），跳播時從要求的位置接著讀
/// </summary>
public sealed class MixedAudioSource : IDisposable
{
    readonly object gate = new();
    readonly WaveStream reader;
    readonly ISampleProvider mono;
    readonly int rate;
    TimeSpan position;

    public MediaStreamSource Source { get; }

    public MixedAudioSource(string path)
    {
        reader = OpenReader(path);
        mono = new ChannelMix.ToMono(new MediaFoundationCompressor.StandardFormat(reader).ToSampleProvider());
        rate = reader.WaveFormat.SampleRate;
        Source = new MediaStreamSource(new AudioStreamDescriptor(AudioEncodingProperties.CreatePcm((uint)rate, 1, 16)))
        {
            CanSeek = true,
            Duration = reader.TotalTime,
        };
        Source.Starting += (_, e) =>
        {
            lock (gate)
            {
                if (e.Request.StartPosition is TimeSpan start && start <= reader.TotalTime)
                {
                    reader.CurrentTime = start;
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
                var n = mono.Read(samples);
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

    /// <summary>wav 直接讀（同解碼），其他用 Media Foundation 讀成浮點</summary>
    public static WaveStream OpenReader(string path) => Importer.Extension(path) == "wav"
        ? new WaveFileReader(path)
        : new MediaFoundationReader(path, new MediaFoundationReader.MediaFoundationReaderSettings { RequestFloatOutput = true });

    public void Dispose()
    {
        lock (gate) reader.Dispose();
    }
}
