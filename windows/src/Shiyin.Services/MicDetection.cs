using Shiyin.Data;
using Shiyin.Transcribe;

namespace Shiyin.Services;

/// <summary>
/// 麥克風的「建議收錄」（R119.3、M§93.2）：讀錄音的麥克風那一聲道、偵測、寫進資料庫。
/// 錄音停止後轉錄線的第一步、匯入沒有區塊的封包、時間軸的「重新偵測」；不是「麥克風＋電腦聲音」、沒有錄音時不做
/// </summary>
public static class MicDetection
{
    public static bool Run(long meetingId, AppDatabase db, ShiyinPaths paths, IAudioDecoder decoder, CancellationToken cancel = default)
    {
        if (db.Meeting(meetingId) is not { Channels: "both" } meeting || !paths.HasAudio(meeting)) return false;
        var channels = decoder.DecodeChannels(paths.AudioPath(meeting), cancel);
        if (channels.Length == 0) return false;
        db.SaveMicDetection(meetingId, MicRegions.Detect(MicRegions.Levels(channels[0], 16_000), meeting.MicThresholdUser));
        return true;
    }
}
