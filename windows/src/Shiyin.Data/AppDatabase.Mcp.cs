using Microsoft.Data.Sqlite;
using Shiyin.Model;

namespace Shiyin.Data;

/// <summary>資料庫的版本跟這版程式不同（本地 MCP 的唯讀連線，M§44.1）</summary>
public sealed class ReadOnlyDatabaseException(bool needsUpgrade) : Exception(needsUpgrade ? "needs upgrade" : "newer database")
{
    /// <summary>true：還有沒跑的 migration（更新後還沒開過拾音）；false：資料庫比這版程式新</summary>
    public bool NeedsUpgrade { get; } = needsUpgrade;
}

public sealed partial class AppDatabase
{
    /// <summary>
    /// 只讀（本地 MCP，M§44.1）：不跑 migration、不備份；版本不同丟出 <see cref="ReadOnlyDatabaseException"/>。
    /// 旁邊沒有 -wal、-shm 時唯讀連線打不開（SQLite 14），改用一般連線：一樣只讀，SQLite 自己建那兩個檔
    /// </summary>
    public static AppDatabase OpenReadOnly(string path)
    {
        try { return new AppDatabase(path, SqliteOpenMode.ReadOnly); }
        catch (SqliteException e) when (e.SqliteErrorCode == 14) { return new AppDatabase(path, SqliteOpenMode.ReadWrite); }
    }

    AppDatabase(string path, SqliteOpenMode mode)
    {
        Path = path;
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = mode, ForeignKeys = true, Pooling = true, DefaultTimeout = 30,
        }.ToString();
        // 不打開：唯讀時不寫（Write 會失敗）
        writer = new SqliteConnection(connectionString);
        var applied = Read(db =>
            db.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'grdb_migrations'") is long n && n > 0
                ? db.Query("SELECT identifier FROM grdb_migrations").Select(r => r.Str("identifier")).ToHashSet()
                : []);
        if (applied.Any(id => !MigrationIds.Contains(id))) throw new ReadOnlyDatabaseException(needsUpgrade: false);
        if (MigrationIds.Any(id => !applied.Contains(id))) throw new ReadOnlyDatabaseException(needsUpgrade: true);
    }

    /// <summary>給 AI 讀的會議（M§44.2）：不在垃圾桶、不是正在錄的，新的在前（同列表）；日期含頭尾</summary>
    public List<Meeting> MeetingsForAi(string? from, string? to, string? folder) => Read(db =>
    {
        var sql = "SELECT * FROM meetings WHERE deleted_at IS NULL AND NOT (source = 'recording' AND audio_file = '')";
        var args = new List<object?>();
        if (from is not null) { sql += " AND substr(meeting_date, 1, 10) >= ?"; args.Add(from); }
        if (to is not null) { sql += " AND substr(meeting_date, 1, 10) <= ?"; args.Add(to); }
        if (folder is not null) { sql += " AND folder = ?"; args.Add(folder); }
        sql += " ORDER BY meeting_date DESC, id DESC";
        return db.Query(sql, args.ToArray()).Select(ToMeeting).ToList();
    });
}
