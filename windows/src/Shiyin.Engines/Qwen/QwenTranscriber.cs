using Shiyin.Model;
using Shiyin.Text;
using Shiyin.Transcribe;
using Shiyin.Whisper;

namespace Shiyin.Qwen;

/// <summary>一次解碼的取樣參數（同 Mac 薄層的 qwen_params）：懲罰 1.0 等於不加，溫度 0 是 greedy</summary>
public sealed record QwenSampling(float RepeatPenalty = 1.0f, int RepeatLastN = 64, float Temperature = 0, uint Seed = 0)
{
    public static QwenSampling Greedy { get; } = new();
}

/// <summary>一次解碼的結果：文字（還沒整理）、是不是到達 max_tokens、生成了幾個 token</summary>
public sealed record QwenDecoded(string Text, bool Truncated, int Tokens);

/// <summary>把一段 16 kHz 單聲道的取樣轉成文字（真的是 llama-server，測試換成假的）</summary>
public interface IQwenDecoder
{
    QwenDecoded Decode(ReadOnlySpan<float> samples, string context, string languageName, int maxTokens, QwenSampling sampling, CancellationToken cancel);
}

/// <summary>每段的結果：文字和是不是經過修剪（低信心）</summary>
public sealed record QwenPiece(int Start, int End, string Text, bool Trimmed);

/// <summary>
/// Qwen3-ASR 的轉錄流程（同 Mac 的 QwenEngine.transcribe／run／decode，M§32.4、D107）：逐段轉錄，段落的時間就是分段的時間。
/// 跳針（同一個字重複到截斷）時先加重複懲罰重試，再切半，最後修剪並標成低信心（R43.5）；幻覺防線（§32.4 第 4 步）
/// </summary>
public static class QwenTranscriber
{
    public static List<TranscribedSegment> Transcribe(float[] samples, IReadOnlyList<(int Start, int End)> chunks, IQwenDecoder decoder,
                                                      MeetingLanguage language, IReadOnlyList<string> glossary, WhisperControl control,
                                                      bool retry = true, bool guard = true)
    {
        var context = QwenRules.Context(glossary, language);
        var languageName = QwenRules.LanguageName(language);
        double? median = guard ? QwenRules.MedianFrameEnergy(samples) : null;
        var total = (double)Math.Max(samples.Length, 1);
        var segments = new List<TranscribedSegment>();
        foreach (var (start, end) in chunks)
        {
            if (control.IsCancelled) throw new WhisperException(WhisperErrorKind.Cancelled);
            foreach (var piece in Run(samples, start, end, context, languageName, decoder, control, retry, median, 0))
            {
                var text = language == MeetingLanguage.Zh ? ChineseScript.ToTaiwan(piece.Text) : piece.Text;
                if (text.Length == 0) continue;
                segments.Add(new TranscribedSegment(piece.Start / WhisperRules.SampleRate, piece.End / WhisperRules.SampleRate, text, piece.Trimmed));
            }
            control.OnProgress(Math.Min(1, end / total));
        }
        if (segments.Count == 0) throw new WhisperException(WhisperErrorKind.Empty);
        return segments;
    }

    /// <summary>一段：壞掉（到達 max_tokens、壓縮比 > 2.4、同一片段連續重複 5 次以上）就重試、切半、修剪</summary>
    static List<QwenPiece> Run(float[] samples, int start, int end, string context, string language, IQwenDecoder decoder, WhisperControl control,
                               bool retry, double? guardMedian, int depth)
    {
        var rate = (int)WhisperRules.SampleRate;
        var seconds = (double)(end - start) / rate;
        var maxTokens = QwenRules.MaxTokens(seconds);
        // 幻覺防線：輸出比有聲音的秒數能講的還多，就加溫度重解一次；兩次差很多就是編的，丟掉；差不多就留短的。
        // 重解用的參數接在目前的參數後面（重試過就帶著重複懲罰，同 Mac 的閉包）
        QwenPiece Guarded(string text, QwenSampling current)
        {
            if (guardMedian is not double median || !QwenRules.TooLong(text, language, QwenRules.SpeechSeconds(samples, start, end, median)))
                return new QwenPiece(start, end, text, false);
            var second = Decode(samples, start, end, context, language, maxTokens, current with { Temperature = 0.3f, Seed = 7 }, decoder, control);
            if (QwenRules.Similarity(text, second.Text) < 0.6) return new QwenPiece(start, end, "", true);
            return new QwenPiece(start, end, QwenRules.Units(text, language) <= QwenRules.Units(second.Text, language) ? text : second.Text, true);
        }
        var sampling = QwenSampling.Greedy;
        var first = Decode(samples, start, end, context, language, maxTokens, sampling, decoder, control);
        if (!retry || !QwenRules.LooksBad(first.Text, first.Truncated)) return [Guarded(first.Text, sampling)];
        // 1. 加重複懲罰和一點溫度再來一次
        sampling = new QwenSampling(RepeatPenalty: 1.3f, RepeatLastN: 128, Temperature: 0.2f, Seed: 1);
        var second = Decode(samples, start, end, context, language, maxTokens, sampling, decoder, control);
        if (!QwenRules.LooksBad(second.Text, second.Truncated)) return [Guarded(second.Text, sampling)];
        // 2. 夠長就切成兩半各自再來
        if (seconds >= 6 && depth < 3)
        {
            var half = QwenRules.Chunks(samples.AsSpan(start, end - start), [], seconds / 2);
            if (half.Count >= 2)
            {
                var pieces = new List<QwenPiece>();
                foreach (var (a, b) in half)
                    pieces.AddRange(Run(samples, start + a, start + b, context, language, decoder, control, true, guardMedian, depth + 1));
                return pieces;
            }
        }
        // 3. 留壓縮比較低的那次，重複的片段縮成 3 次，標成低信心
        var best = WhisperRules.CompressionRatio(first.Text) <= WhisperRules.CompressionRatio(second.Text) ? first.Text : second.Text;
        return [new QwenPiece(start, end, QwenRules.TrimRepeats(best), true)];
    }

    static QwenDecoded Decode(float[] samples, int start, int end, string context, string language, int maxTokens, QwenSampling sampling,
                              IQwenDecoder decoder, WhisperControl control)
    {
        if (control.IsCancelled) throw new WhisperException(WhisperErrorKind.Cancelled);
        var decoded = decoder.Decode(samples.AsSpan(start, end - start), context, language, maxTokens, sampling, control.Cancel);
        if (control.IsCancelled) throw new WhisperException(WhisperErrorKind.Cancelled);
        return decoded with { Text = QwenRules.Clean(decoded.Text) };
    }
}
