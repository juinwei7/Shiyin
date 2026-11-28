using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Shiyin.Model;
using Shiyin.Text;

namespace Shiyin.Data;

/// <summary>
/// 資料庫（design-windows §5）：跟 Mac 的 AppDatabase 同一份 schema（R28.5）。WAL、外鍵開著；寫入只用一條連線（一次一個交易），
/// 讀取每次開新連線（有連線池），對應 GRDB 的 DatabasePool。也寫 GRDB 的 grdb_migrations，Mac 的資料庫檔案可以直接打開。
/// </summary>
public sealed partial class AppDatabase : IDisposable
{
    readonly string connectionString;
    readonly SqliteConnection writer;
    readonly object writeLock = new();

    public string Path { get; }

    /// <summary>參考文件原始檔的資料夾（M§39.1）；null 時刪除會議、文件不碰檔案（標準答案的測試）</summary>
    public string? DocumentsRoot { get; }

    /// <summary>開啟資料庫；有待執行的 migration 時，先把舊檔備份到 backupsDir（N3）</summary>
    public AppDatabase(string path, string? backupsDir = null, string? documentsRoot = null) : this(path, backupsDir, documentsRoot, migrateUntil: null) { }

    /// <summary>只給測試用：migration 只跑到 migrateUntil（含）為止，用來在舊的結構上放資料再跑後面的 migration（標準答案 db/migrate-v18.json）</summary>
    internal AppDatabase(string path, string? backupsDir, string? documentsRoot, string? migrateUntil)
    {
        Path = path;
        DocumentsRoot = documentsRoot;
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, ForeignKeys = true, Pooling = true, DefaultTimeout = 30,
        }.ToString();
        writer = new SqliteConnection(connectionString);
        writer.Open();
        using (var cmd = writer.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode = WAL";
            cmd.ExecuteNonQuery();
        }
        Migrate(backupsDir, migrateUntil);
    }

    public void Dispose()
    {
        writer.Dispose();
        SqliteConnection.ClearAllPools();
    }

    // MARK: 交易

    /// <summary>寫入：一個交易，失敗就整個退回（對應 GRDB 的 pool.write）</summary>
    public T Write<T>(Func<Db, T> work)
    {
        lock (writeLock)
        {
            using var tx = writer.BeginTransaction(deferred: false);
            var db = new Db(writer, tx);
            var result = work(db);
            tx.Commit();
            return result;
        }
    }

    public void Write(Action<Db> work) => Write(db => { work(db); return 0; });

    /// <summary>讀取：自己的連線，WAL 下不會擋到寫入</summary>
    public T Read<T>(Func<Db, T> work)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var tx = connection.BeginTransaction(deferred: true);
        return work(new Db(connection, tx));
    }

    /// <summary>一個交易裡的操作</summary>
    public sealed class Db(SqliteConnection connection, SqliteTransaction? tx)
    {
        SqliteCommand Command(string sql, object?[] args)
        {
            var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            var sb = new StringBuilder(sql.Length + 8);
            var index = 0;
            var quote = '\0';
            foreach (var c in sql)
            {
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                    sb.Append(c);
                }
                else if (c is '\'' or '"')
                {
                    quote = c;
                    sb.Append(c);
                }
                else if (c == '?')
                {
                    sb.Append("$p").Append(index.ToString(CultureInfo.InvariantCulture));
                    index++;
                }
                else sb.Append(c);
            }
            cmd.CommandText = sb.ToString();
            for (var i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue("$p" + i.ToString(CultureInfo.InvariantCulture), ToDb(args[i]));
            return cmd;
        }

        static object ToDb(object? v) => v switch
        {
            null => DBNull.Value,
            bool b => b ? 1L : 0L,
            int i => (long)i,
            float f => (double)f,
            _ => v,
        };

        public int Execute(string sql, params object?[] args)
        {
            using var cmd = Command(sql, args);
            return cmd.ExecuteNonQuery();
        }

        public List<Row> Query(string sql, params object?[] args)
        {
            using var cmd = Command(sql, args);
            using var reader = cmd.ExecuteReader();
            var rows = new List<Row>();
            while (reader.Read())
            {
                var row = new Row();
                for (var i = 0; i < reader.FieldCount; i++) row.Add(reader.GetName(i), reader.IsDBNull(i) ? null : reader.GetValue(i));
                rows.Add(row);
            }
            return rows;
        }

        public Row? QueryOne(string sql, params object?[] args) => Query(sql, args).FirstOrDefault();

        public object? Scalar(string sql, params object?[] args)
        {
            using var cmd = Command(sql, args);
            var v = cmd.ExecuteScalar();
            return v is DBNull ? null : v;
        }

        public long LastInsertRowId => (long)Scalar("SELECT last_insert_rowid()")!;
    }

    /// <summary>一列資料，保留欄位的順序</summary>
    public sealed class Row
    {
        readonly List<(string Name, object? Value)> columns = [];
        public void Add(string name, object? value) => columns.Add((name, value));
        public IReadOnlyList<(string Name, object? Value)> Columns => columns;

        public object? this[string name] => columns.FirstOrDefault(c => c.Name == name).Value;
        public string Str(string name) => this[name] as string ?? "";
        public string? StrOrNull(string name) => this[name] as string;
        public long Long(string name) => this[name] switch { long l => l, double d => (long)d, _ => 0 };
        public long? LongOrNull(string name) => this[name] switch { long l => l, double d => (long)d, _ => null };
        public int Int(string name) => (int)Long(name);
        public int? IntOrNull(string name) => (int?)LongOrNull(name);
        public double Double(string name) => this[name] switch { double d => d, long l => l, _ => 0 };
        public double? DoubleOrNull(string name) => this[name] switch { double d => d, long l => l, _ => null };
        public bool Bool(string name) => Long(name) != 0;
        public byte[]? Blob(string name) => this[name] as byte[];
    }

    // MARK: migration

    static readonly string[] MigrationIds = ["v1", "v2", "v3", "v4", "v5", "v6", "v7", "v8", "v9", "v10", "v11", "v12", "v13", "v14", "v15", "v16", "v17", "v18", "v19", "v20", "v21"];

    /// <summary>
    /// 這次開資料庫時 migration v14 換掉的定期會議（id → 資料夾名稱，D177）；沒跑 v14 時是空的。
    /// 設定檔裡側欄的鍵（series:id）由 Services 換成 folder:名稱（同 Mac 的 AppDatabase.replaceSeriesKeys）
    /// </summary>
    public IReadOnlyDictionary<long, string> MigratedSeries { get; private set; } = new Dictionary<long, string>();

    /// <summary>
    /// 這次開資料庫時 migration v18 之後的資料夾（名稱 → id，R118、M§92.1）；沒跑 v18、沒有資料夾時是空的。
    /// 設定檔裡側欄的鍵（folder:名稱）由 Services 換成 folderid:id（同 Mac 的 AppDatabase.replaceFolderKeys）
    /// </summary>
    public IReadOnlyDictionary<string, long> MigratedFolders { get; private set; } = new Dictionary<string, long>();

    /// <summary>migration 跑完要交給 Services 的對照（v14 的定期會議、v18 的資料夾），其他是 null</summary>
    sealed record MigrationResult(Dictionary<long, string>? Series = null, Dictionary<string, long>? Folders = null);

    void Migrate(string? backupsDir, string? migrateUntil)
    {
        lock (writeLock)
        {
            var db = new Db(writer, null);
            db.Execute("CREATE TABLE IF NOT EXISTS grdb_migrations (identifier TEXT NOT NULL PRIMARY KEY)");
            var applied = db.Query("SELECT identifier FROM grdb_migrations").Select(r => r.Str("identifier")).ToHashSet();
            var ids = migrateUntil is null ? MigrationIds : MigrationIds[..(Array.IndexOf(MigrationIds, migrateUntil) + 1)];
            var pending = ids.Where(id => !applied.Contains(id)).ToList();
            if (applied.Count > 0 && pending.Count > 0 && backupsDir is not null)
            {
                db.Query("PRAGMA wal_checkpoint(TRUNCATE)");
                Directory.CreateDirectory(backupsDir);
                var stamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
                File.Copy(Path, System.IO.Path.Combine(backupsDir, $"shiyin-{stamp}-before-{pending[0]}.sqlite"));
            }
            foreach (var id in pending)
            {
                // GRDB 跑 migration 時先關外鍵，跑完檢查
                db.Execute("PRAGMA foreign_keys = OFF");
                using (var tx = writer.BeginTransaction(deferred: false))
                {
                    var t = new Db(writer, tx);
                    var migrated = RunMigration(t, id);
                    t.Execute("INSERT INTO grdb_migrations (identifier) VALUES (?)", id);
                    if (t.Query("PRAGMA foreign_key_check").Count > 0) throw new InvalidOperationException($"foreign key violation in {id}");
                    tx.Commit();
                    if (migrated?.Series is { } series) MigratedSeries = series;
                    if (migrated?.Folders is { } folders) MigratedFolders = folders;
                }
                db.Execute("PRAGMA foreign_keys = ON");
            }
        }
    }

    /// <summary>v14 回傳換掉的定期會議（id → 資料夾名稱），v18 回傳資料夾（名稱 → id），其他是 null</summary>
    static MigrationResult? RunMigration(Db db, string id)
    {
        switch (id)
        {
            case "v1": db.Execute(MigrationSql.V1); break;
            case "v2":
                db.Execute(MigrationSql.V2);
                // 會議紀錄轉成 v2 格式（字串陣列 → 帶時間點的條目，時間點先留空）
                foreach (var row in db.Query("SELECT id, notes FROM meetings WHERE notes IS NOT NULL"))
                {
                    if (MeetingNotes.Decode(row.StrOrNull("notes")) is { } notes)
                        db.Execute("UPDATE meetings SET notes = ? WHERE id = ?", notes.JsonString(), row.Long("id"));
                }
                RebuildSearch(db);
                break;
            case "v3": db.Execute(MigrationSql.V3); break;
            case "v4": db.Execute(MigrationSql.V4); break;
            case "v5": db.Execute(MigrationSql.V5); break;
            case "v6":
                db.Execute(MigrationSql.V6);
                RebuildSearch(db);
                break;
            case "v7": db.Execute(MigrationSql.V7); break;
            case "v8": db.Execute(MigrationSql.V8); break;
            case "v9": db.Execute(MigrationSql.V9); break;
            case "v10": db.Execute(MigrationSql.V10); break;
            case "v11": db.Execute(MigrationSql.V11); break;
            case "v12": db.Execute(MigrationSql.V12); break;
            case "v13":
                db.Execute(MigrationSql.V13);
                db.Execute("INSERT OR IGNORE INTO folders (name, created_at) SELECT DISTINCT folder, ? FROM meetings WHERE folder <> ''", Clock.Now());
                db.Execute("INSERT OR IGNORE INTO folders (name, created_at) SELECT folder, created_at FROM folder_rules");
                break;
            case "v14":
            {
                // 拿掉定期會議（R99、M§73、D177）：每個定期會議換成同名的資料夾（已經有就合在一起），裡面不在資料夾的會議移進去
                //（已經在別的資料夾的不動），再做 MigrationSql.V14
                var names = new Dictionary<long, string>();
                foreach (var row in db.Query("SELECT id, name FROM series ORDER BY id"))
                {
                    var name = SwiftText.Trim(row.Str("name"));
                    if (name.Length == 0) continue;
                    var seriesId = row.Long("id");
                    names[seriesId] = name;
                    db.Execute("INSERT OR IGNORE INTO folders (name, created_at) VALUES (?, ?)", name, Clock.Now());
                    db.Execute("UPDATE meetings SET folder = ?, folder_decided = 1 WHERE series_id = ? AND folder = ''", name, seriesId);
                }
                db.Execute(MigrationSql.V14);
                return new MigrationResult(Series: names);
            }
            case "v15": db.Execute(MigrationSql.V15); break;
            case "v16": db.Execute(MigrationSql.V16); break;
            case "v17": foreach (var sql in MigrationSql.V17) db.Execute(sql); break;
            case "v18":
            {
                // 資料夾用 id 辨識（R118、M§92.1、D200）：跑完把名稱 → id 交給側欄的鍵（folder:名稱 → folderid:id，同 Mac 的 replaceFolderKeys）
                db.Execute(MigrationSql.V18);
                var folders = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (var row in db.Query("SELECT id, name FROM folders")) folders[row.Str("name")] = row.Long("id");
                return new MigrationResult(Folders: folders);
            }
            case "v19": db.Execute(MigrationSql.V19); break;
            case "v20": db.Execute(MigrationSql.V20); break;
            case "v21": db.Execute(MigrationSql.V21); break;
        }
        return null;
    }

    // MARK: 對應

    internal static Meeting ToMeeting(Row r) => new()
    {
        Id = r.Long("id"), Title = r.Str("title"), TitleLocked = r.Bool("title_locked"), MeetingDate = r.Str("meeting_date"),
        Participants = r.Str("participants"), SourceFilename = r.Str("source_filename"), AudioFile = r.Str("audio_file"),
        DurationSeconds = r.IntOrNull("duration_seconds"), Notes = r.StrOrNull("notes"), NotesBy = r.StrOrNull("notes_by"), NotesTemplate = r.StrOrNull("notes_template"),
        CreatedAt = r.Str("created_at"), UpdatedAt = r.Str("updated_at"), TemplateId = r.Str("template_id"),
        UserNotes = r.Str("user_notes"), Source = r.Str("source"), Channels = r.Str("channels"), DeletedAt = r.StrOrNull("deleted_at"),
        FolderId = r.LongOrNull("folder_id"), Language = r.Str("language"), NotesDraft = r.Int("notes_draft"), TranscribedWith = r.Str("transcribed_with"),
        MinSpeakers = r.IntOrNull("min_speakers"), SpeakerCheck = r.DoubleOrNull("speaker_check"),
        SpeakerHintDismissed = r.Bool("speaker_hint_dismissed"), FolderDecided = r.Bool("folder_decided"), SharedFrom = r.StrOrNull("shared_from"),
    };

    internal static Segment ToSegment(Row r) => new()
    {
        Id = r.Long("id"), MeetingId = r.Long("meeting_id"), Seq = r.Int("seq"), Start = r.Double("start"), End = r.Double("end"),
        Text = r.Str("text"), LowConfidence = r.Bool("low_confidence"), Speaker = r.IntOrNull("speaker"), Kind = r.Str("kind"),
        Channel = r.IntOrNull("channel"), SpeakerManual = r.Bool("speaker_manual"),
    };

    internal static SpeakerName ToSpeaker(Row r) => new()
    {
        MeetingId = r.Long("meeting_id"), Idx = r.Int("idx"), Name = r.Str("name"), NameSource = r.Str("name_source"),
        PersonId = r.LongOrNull("person_id"), Embedding = r.Blob("embedding"), SpeechSeconds = r.Double("speech_seconds"),
        AudioSource = r.Str("audio_source"), SuggestedName = r.StrOrNull("suggested_name"),
        SuggestionReason = r.StrOrNull("suggestion_reason"), SuggestionTime = r.StrOrNull("suggestion_time"),
    };

    internal static Bookmark ToBookmark(Row r) => new() { Id = r.Long("id"), MeetingId = r.Long("meeting_id"), Time = r.Double("time"), Label = r.Str("label") };

    internal static Correction ToCorrection(Row r) => new()
    {
        Id = r.Long("id"), MeetingId = r.Long("meeting_id"), SegmentId = r.Long("segment_id"), Original = r.Str("original"),
        Suggestion = r.Str("suggestion"), Reason = r.Str("reason"), Status = r.Str("status"), CreatedAt = r.Str("created_at"),
    };

    internal static Job ToJob(Row r) => new()
    {
        Id = r.Long("id"), MeetingId = r.Long("meeting_id"), Kind = JobKinds.Parse(r.Str("kind")) ?? JobKind.Transcribe,
        Status = JobKinds.ParseStatus(r.Str("status")), Payload = r.StrOrNull("payload"), Error = r.StrOrNull("error"),
        CreatedAt = r.Str("created_at"), StartedAt = r.StrOrNull("started_at"), FinishedAt = r.StrOrNull("finished_at"),
        Crashes = r.Int("crashes"), ErrorKind = r.StrOrNull("error_kind"), Priority = r.Int("priority"),
    };

    internal static TemplateRecord ToTemplate(Row r) => new()
    {
        Id = r.Str("id"), Name = r.Str("name"), Builtin = r.Bool("builtin"), Definition = r.Str("definition"), UpdatedAt = r.Str("updated_at"),
    };

    internal static NotesVersion ToVersion(Row r) => new()
    {
        Id = r.Long("id"), MeetingId = r.Long("meeting_id"), Notes = r.Str("notes"), Reason = r.Str("reason"), CreatedAt = r.Str("created_at"),
        TemplateId = r.StrOrNull("template_id"),
    };

    // 整列寫入（對應 GRDB 的 insert、update）

    static readonly string MeetingColumns = "title, title_locked, meeting_date, participants, source_filename, audio_file, duration_seconds, notes, notes_by, "
        + "created_at, updated_at, template_id, user_notes, source, channels, deleted_at, folder_id, language, notes_draft, transcribed_with, "
        + "min_speakers, speaker_check, speaker_hint_dismissed, folder_decided, notes_template, shared_from";

    static object?[] MeetingValues(Meeting m) =>
    [
        m.Title, m.TitleLocked, m.MeetingDate, m.Participants, m.SourceFilename, m.AudioFile, m.DurationSeconds, m.Notes, m.NotesBy,
        m.CreatedAt, m.UpdatedAt, m.TemplateId, m.UserNotes, m.Source, m.Channels, m.DeletedAt, m.FolderId, m.Language, m.NotesDraft,
        m.TranscribedWith, m.MinSpeakers, m.SpeakerCheck, m.SpeakerHintDismissed, m.FolderDecided, m.NotesTemplate, m.SharedFrom,
    ];

    internal static long InsertMeetingRow(Db db, Meeting m)
    {
        db.Execute($"INSERT INTO meetings ({MeetingColumns}) VALUES ({string.Join(", ", Enumerable.Repeat("?", 26))})", MeetingValues(m));
        return db.LastInsertRowId;
    }

    internal static void UpdateMeetingRow(Db db, Meeting m)
    {
        var sets = string.Join(", ", MeetingColumns.Split(", ").Select(c => c + " = ?"));
        db.Execute($"UPDATE meetings SET {sets} WHERE id = ?", [.. MeetingValues(m), m.Id]);
    }

    internal static Meeting? FetchMeeting(Db db, long id) => db.QueryOne("SELECT * FROM meetings WHERE id = ?", id) is { } r ? ToMeeting(r) : null;

    internal static void InsertSegmentRow(Db db, Segment s)
    {
        db.Execute("INSERT INTO segments (meeting_id, seq, start, end, text, low_confidence, speaker, kind, channel, speaker_manual) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
            s.MeetingId, s.Seq, s.Start, s.End, s.Text, s.LowConfidence, s.Speaker, s.Kind, s.Channel, s.SpeakerManual);
        s.Id = db.LastInsertRowId;
    }

    internal static void InsertSpeakerRow(Db db, SpeakerName s) => db.Execute(
        "INSERT INTO speakers (meeting_id, idx, name, name_source, person_id, embedding, speech_seconds, audio_source, suggested_name, suggestion_reason, suggestion_time) "
        + "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
        s.MeetingId, s.Idx, s.Name, s.NameSource, s.PersonId, s.Embedding, s.SpeechSeconds, s.AudioSource, s.SuggestedName, s.SuggestionReason, s.SuggestionTime);
}
