namespace Shiyin.Model;

/// <summary>
/// 編輯會議（R107、M§81.1、D178）：新的時間軸＝依序接起來的片段。裁切、刪掉中間幾段、合併都化成片段；
/// 分成兩場是新時間軸上的一個切點。純邏輯，跟 Mac 的 EditPlan.swift 同一套（標準答案 util/edit-plan.json）
/// </summary>
public sealed record EditPlan(IReadOnlyList<EditPlan.Piece> Pieces)
{
    public sealed record Piece(long MeetingId, double Start, double End)
    {
        public double Length => Math.Max(0, End - Start);
    }

    /// <summary>太短的片段不要（避免留下一兩個取樣的碎片）</summary>
    public const double MinimumPiece = 0.05;

    public double Duration => Pieces.Aggregate(0.0, (sum, p) => sum + p.Length);

    /// <summary>一場會議保留的片段：start～end 之間，去掉 deleted（重疊的先合起來；太短的片段不要）</summary>
    public static List<Piece> Keeping(long meetingId, double start, double end, IEnumerable<(double Lower, double Upper)> deleted)
    {
        var pieces = new List<Piece>();
        var cursor = start;
        foreach (var range in Normalized(deleted).Where(r => r.Upper > start && r.Lower < end))
        {
            if (range.Lower > cursor) pieces.Add(new Piece(meetingId, cursor, range.Lower));
            cursor = Math.Max(cursor, range.Upper);
        }
        if (end > cursor) pieces.Add(new Piece(meetingId, cursor, end));
        return pieces.Where(p => p.Length >= MinimumPiece).ToList();
    }

    /// <summary>依開始排序、重疊或相接的合起來</summary>
    public static List<(double Lower, double Upper)> Normalized(IEnumerable<(double Lower, double Upper)> ranges)
    {
        var output = new List<(double Lower, double Upper)>();
        foreach (var r in ranges.OrderBy(r => r.Lower))
        {
            if (output.Count > 0 && r.Lower <= output[^1].Upper)
                output[^1] = (output[^1].Lower, Math.Max(output[^1].Upper, r.Upper));
            else output.Add(r);
        }
        return output;
    }

    /// <summary>只有這場一段、從 0 開始：時間都不變（逐字稿右鍵分成兩場也走這裡，M§64）</summary>
    public bool KeepsWhole(long meetingId) => Pieces.Count == 1 && Pieces[0].MeetingId == meetingId && Pieces[0].Start == 0;

    /// <summary>片段在新時間軸上的開始（照順序加，跟 Mac 的 reduce 一樣）</summary>
    double Offset(int index)
    {
        var sum = 0.0;
        for (var i = 0; i < index; i++) sum += Pieces[i].Length;
        return sum;
    }

    /// <summary>片段的結尾算在片段裡（最後一句常剛好結束在結尾）；相接的兩段取前面那段</summary>
    int? PieceIndex(long meetingId, double t)
    {
        for (var i = 0; i < Pieces.Count; i++)
            if (Pieces[i].MeetingId == meetingId && t >= Pieces[i].Start && t <= Pieces[i].End) return i;
        return null;
    }

    /// <summary>原本那場的時間 → 新時間軸；不在任何片段裡回傳 null</summary>
    public double? Map(long meetingId, double t) => PieceIndex(meetingId, t) is int i ? Offset(i) + (t - Pieces[i].Start) : null;

    /// <summary>
    /// 不在片段裡時對到「下一個片段的開始」（同一場會議裡、時間在後面的第一段；後面沒有了就對到這場最後一段的結束，
    /// 這場一段都沒有時是 0）。用在我的筆記、會議紀錄的時間（D181）
    /// </summary>
    public double Snap(long meetingId, double t)
    {
        if (Map(meetingId, t) is double mapped) return mapped;
        for (var i = 0; i < Pieces.Count; i++)
            if (Pieces[i].MeetingId == meetingId && Pieces[i].Start >= t) return Offset(i);
        for (var i = Pieces.Count - 1; i >= 0; i--)
            if (Pieces[i].MeetingId == meetingId) return Offset(i) + Pieces[i].Length;
        return 0;
    }

    /// <summary>一句話（start～end）留不留：中點在片段裡就留，開始、結束夾在那個片段裡再對應（D181）</summary>
    public (double Start, double End)? MapSentence(long meetingId, double start, double end)
    {
        var mid = (start + Math.Max(start, end)) / 2;
        if (PieceIndex(meetingId, mid) is not int i) return null;
        var piece = Pieces[i];
        var offset = Offset(i);
        var s = Math.Min(Math.Max(start, piece.Start), piece.End);
        var e = Math.Min(Math.Max(end, s), piece.End);
        return (offset + s - piece.Start, offset + e - piece.Start);
    }

    /// <summary>新時間軸 → 原本那場的時間（試聽、播放頭）；超出時夾在兩端</summary>
    public (long MeetingId, double Time)? Source(double t)
    {
        if (Pieces.Count == 0) return null;
        var rest = Math.Max(0, t);
        foreach (var piece in Pieces)
        {
            if (rest <= piece.Length) return (piece.MeetingId, piece.Start + rest);
            rest -= piece.Length;
        }
        var last = Pieces[^1];
        return (last.MeetingId, last.End);
    }

    /// <summary>新時間軸在 cut 切開：前、後兩份（R107.5）；cut 不在中間時回傳 null</summary>
    public (EditPlan Head, EditPlan Tail)? Split(double cut)
    {
        if (!(cut > MinimumPiece && cut < Duration - MinimumPiece)) return null;
        List<Piece> head = [], tail = [];
        var t = 0.0;
        foreach (var piece in Pieces)
        {
            var end = t + piece.Length;
            if (end <= cut) head.Add(piece);
            else if (t >= cut) tail.Add(piece);
            else
            {
                var at = piece.Start + (cut - t);
                head.Add(piece with { End = at });
                tail.Add(piece with { Start = at });
            }
            t = end;
        }
        return (new EditPlan(head), new EditPlan(tail));
    }

    /// <summary>我的筆記（user_notes）每行開頭的 [mm:ss] 照 Snap 改；一行都不刪（D181）</summary>
    public string RemapUserNotes(string text, long meetingId) => string.Join("\n", text.Split('\n').Select(line =>
    {
        if (!line.StartsWith('[')) return line;
        var close = line.IndexOf(']');
        if (close < 0 || TimeFormat.Seconds(line[1..close]) is not double seconds) return line;
        return "[" + TimeFormat.String(Snap(meetingId, seconds)) + "]" + line[(close + 1)..];
    }));

    /// <summary>內容相同（record 的 List 只比參考）</summary>
    public bool SameAs(EditPlan other) => Pieces.SequenceEqual(other.Pieces);
}
