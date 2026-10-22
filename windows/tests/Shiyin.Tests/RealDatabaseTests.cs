using Shiyin.Data;
using Xunit;

namespace Shiyin.Tests;

/// <summary>
/// 用使用者資料庫的複本測（W1.4，CLAUDE.md：用 sqlite3 .backup 複製、刪掉錄音中的會議）。只在設定了 SHIYIN_REAL_DB 時跑，
/// 結果只印筆數，不印內容。SHIYIN_REAL_DB_COUNTS 給「表=筆數,…」時逐表比對
/// </summary>
public class RealDatabaseTests
{
    [Fact]
    public void OpensMacDatabaseCopy()
    {
        var path = Environment.GetEnvironmentVariable("SHIYIN_REAL_DB");
        if (string.IsNullOrEmpty(path)) return;
        var backups = Path.Combine(Path.GetDirectoryName(path)!, "real-backups-" + Guid.NewGuid().ToString("N"));
        using var db = new AppDatabase(path, backups);
        // 已經是 v9，不應該再跑 migration、也不用備份
        Assert.False(Directory.Exists(backups));
        Assert.Equal("ok", db.Read(d => d.Scalar("PRAGMA integrity_check")) as string);
        var tables = new[] { "meetings", "segments", "speakers", "bookmarks", "notes_versions", "jobs", "glossary", "corrections", "people",
            "voiceprints", "questions", "drafts", "templates", "speaker_undo", "search" };
        var counts = tables.ToDictionary(t => t, t => (long)db.Read(d => d.Scalar($"SELECT count(*) FROM {t}"))!);
        Console.WriteLine(string.Join(" ", counts.Select(p => $"{p.Key}={p.Value}")));
        if (Environment.GetEnvironmentVariable("SHIYIN_REAL_DB_COUNTS") is { Length: > 0 } expected)
            foreach (var pair in expected.Split(','))
            {
                var parts = pair.Split('=');
                Assert.True(long.Parse(parts[1]) == counts[parts[0]], $"{parts[0]}：{parts[1]} ≠ {counts[parts[0]]}");
            }
        // 所有會議讀得出來：會議紀錄解得開、逐字稿和說話者讀得到；每場拿一段的文字搜尋得到那一段
        var rows = db.MeetingRows().Concat(db.MeetingRows(trash: true)).ToList();
        Assert.Equal(counts["meetings"], rows.Count);
        var searched = 0;
        foreach (var row in rows)
        {
            var m = row.Meeting;
            if (m.Notes is not null) Assert.NotNull(m.DecodedNotes);
            var segments = db.Segments(m.Id!.Value);
            db.Speakers(m.Id!.Value);
            if (m.DeletedAt is not null) continue;
            var probe = segments.FirstOrDefault(s => s.Text.Trim().Length >= 6);
            if (probe is null) continue;
            var q = probe.Text.Trim()[..6];
            Assert.Contains(db.Search(q, 1000), h => h.Kind == SearchKind.Segment && h.Ref == probe.Id);
            searched++;
        }
        Console.WriteLine($"meetings read: {rows.Count}, searched: {searched}");
    }
}
