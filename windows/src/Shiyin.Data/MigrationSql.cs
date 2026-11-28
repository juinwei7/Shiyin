namespace Shiyin.Data;

/// <summary>
/// 資料庫的 migration（M§4、§21.1、§23.2、§24.1），SQL 從 Mac 的 <c>Sources/Shiyin/Store/AppDatabase.swift</c> 一字不改地抽出來
/// （SQLite 會把 CREATE TABLE 的原文存在 sqlite_master，空白不同結構就不同，R28.5）。Mac 加了新的 migration，這裡用同樣的識別字加。
/// </summary>
static class MigrationSql
{
    public const string V1 = """
        CREATE TABLE meetings (
          id               INTEGER PRIMARY KEY AUTOINCREMENT,
          title            TEXT NOT NULL DEFAULT '',
          title_locked     INTEGER NOT NULL DEFAULT 0,
          meeting_date     TEXT NOT NULL,
          participants     TEXT NOT NULL DEFAULT '',
          source_filename  TEXT NOT NULL,
          audio_file       TEXT NOT NULL,
          duration_seconds INTEGER,
          notes            TEXT,
          notes_by         TEXT,
          created_at       TEXT NOT NULL,
          updated_at       TEXT NOT NULL
        );
        CREATE TABLE segments (
          id             INTEGER PRIMARY KEY AUTOINCREMENT,
          meeting_id     INTEGER NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
          seq            INTEGER NOT NULL,
          start          REAL NOT NULL,
          end            REAL NOT NULL,
          text           TEXT NOT NULL,
          low_confidence INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX segments_meeting ON segments(meeting_id, seq);
        CREATE TABLE notes_versions (
          id         INTEGER PRIMARY KEY AUTOINCREMENT,
          meeting_id INTEGER NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
          notes      TEXT NOT NULL,
          reason     TEXT NOT NULL,
          created_at TEXT NOT NULL
        );
        CREATE TABLE jobs (
          id          INTEGER PRIMARY KEY AUTOINCREMENT,
          meeting_id  INTEGER NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
          kind        TEXT NOT NULL,
          status      TEXT NOT NULL,
          payload     TEXT,
          error       TEXT,
          created_at  TEXT NOT NULL,
          started_at  TEXT,
          finished_at TEXT
        );
        CREATE INDEX jobs_status ON jobs(status, id);
        CREATE INDEX jobs_meeting ON jobs(meeting_id, id);
        CREATE TABLE glossary (
          id         INTEGER PRIMARY KEY AUTOINCREMENT,
          term       TEXT NOT NULL UNIQUE,
          created_at TEXT NOT NULL
        );
        """;

    public const string V2 = """
        ALTER TABLE meetings ADD COLUMN template_id TEXT NOT NULL DEFAULT 'general';
        ALTER TABLE meetings ADD COLUMN user_notes  TEXT NOT NULL DEFAULT '';
        ALTER TABLE meetings ADD COLUMN source      TEXT NOT NULL DEFAULT 'import';
        ALTER TABLE meetings ADD COLUMN channels    TEXT NOT NULL DEFAULT 'mic';
        ALTER TABLE meetings ADD COLUMN deleted_at  TEXT;
        ALTER TABLE meetings ADD COLUMN folder      TEXT NOT NULL DEFAULT '';
        ALTER TABLE segments ADD COLUMN speaker INTEGER;
        ALTER TABLE segments ADD COLUMN kind TEXT NOT NULL DEFAULT 'final';
        CREATE TABLE speakers (
          meeting_id INTEGER NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
          idx        INTEGER NOT NULL,
          name       TEXT NOT NULL,
          PRIMARY KEY (meeting_id, idx)
        );
        CREATE TABLE bookmarks (
          id         INTEGER PRIMARY KEY AUTOINCREMENT,
          meeting_id INTEGER NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
          time       REAL NOT NULL,
          label      TEXT NOT NULL DEFAULT ''
        );
        CREATE TABLE templates (
          id         TEXT PRIMARY KEY,
          name       TEXT NOT NULL,
          builtin    INTEGER NOT NULL,
          definition TEXT NOT NULL,
          updated_at TEXT NOT NULL
        );
        CREATE VIRTUAL TABLE search USING fts5(meeting_id UNINDEXED, kind UNINDEXED, ref UNINDEXED, text, tokenize = 'trigram');
        """;

    public const string V3 = """
        ALTER TABLE meetings ADD COLUMN language TEXT NOT NULL DEFAULT 'zh'
        """;

    public const string V4 = """
        ALTER TABLE segments ADD COLUMN channel INTEGER
        """;

    public const string V5 = """
        CREATE TABLE corrections (
          id         INTEGER PRIMARY KEY AUTOINCREMENT,
          meeting_id INTEGER NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
          segment_id INTEGER NOT NULL REFERENCES segments(id) ON DELETE CASCADE,
          original   TEXT NOT NULL,
          suggestion TEXT NOT NULL,
          reason     TEXT NOT NULL DEFAULT '',
          status     TEXT NOT NULL DEFAULT 'pending',
          created_at TEXT NOT NULL
        );
        CREATE INDEX corrections_meeting ON corrections(meeting_id);
        CREATE TABLE glossary_dismissed (term TEXT PRIMARY KEY COLLATE NOCASE);
        """;

    public const string V6 = """
        ALTER TABLE jobs ADD COLUMN crashes INTEGER NOT NULL DEFAULT 0
        """;

    public const string V7 = """
        ALTER TABLE meetings ADD COLUMN notes_draft INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE meetings ADD COLUMN transcribed_with TEXT NOT NULL DEFAULT '';
        ALTER TABLE jobs ADD COLUMN error_kind TEXT;
        CREATE TABLE people (
          id         INTEGER PRIMARY KEY AUTOINCREMENT,
          name       TEXT NOT NULL UNIQUE COLLATE NOCASE,
          created_at TEXT NOT NULL
        );
        ALTER TABLE speakers ADD COLUMN name_source TEXT NOT NULL DEFAULT 'default';
        ALTER TABLE speakers ADD COLUMN person_id INTEGER REFERENCES people(id) ON DELETE SET NULL;
        ALTER TABLE speakers ADD COLUMN embedding BLOB;
        ALTER TABLE speakers ADD COLUMN speech_seconds REAL NOT NULL DEFAULT 0;
        ALTER TABLE speakers ADD COLUMN audio_source TEXT NOT NULL DEFAULT 'mix';
        ALTER TABLE speakers ADD COLUMN suggested_name TEXT;
        ALTER TABLE speakers ADD COLUMN suggestion_reason TEXT;
        ALTER TABLE speakers ADD COLUMN suggestion_time TEXT;
        UPDATE speakers SET name_source = 'manual'
          WHERE name NOT GLOB '說話者 [0-9]*' AND name NOT GLOB 'Speaker [0-9]*' AND name NOT GLOB '話者 [0-9]*';
        CREATE TABLE voiceprints (
          id           INTEGER PRIMARY KEY AUTOINCREMENT,
          person_id    INTEGER NOT NULL REFERENCES people(id) ON DELETE CASCADE,
          embedding    BLOB NOT NULL,
          audio_source TEXT NOT NULL,
          meeting_id   INTEGER REFERENCES meetings(id) ON DELETE SET NULL,
          speaker_idx  INTEGER,
          created_at   TEXT NOT NULL
        );
        CREATE INDEX voiceprints_person ON voiceprints(person_id);
        CREATE TABLE questions (
          id         INTEGER PRIMARY KEY AUTOINCREMENT,
          meeting_id INTEGER REFERENCES meetings(id) ON DELETE CASCADE,
          scope      TEXT NOT NULL DEFAULT '',
          question   TEXT NOT NULL,
          answer     TEXT NOT NULL,
          provider   TEXT NOT NULL,
          created_at TEXT NOT NULL
        );
        CREATE INDEX questions_meeting ON questions(meeting_id, id);
        CREATE TABLE drafts (
          meeting_id INTEGER NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
          key        TEXT NOT NULL,
          text       TEXT NOT NULL,
          updated_at TEXT NOT NULL,
          PRIMARY KEY (meeting_id, key)
        );
        """;

    public const string V8 = """
        ALTER TABLE meetings ADD COLUMN min_speakers INTEGER
        """;

    public const string V9 = """
        ALTER TABLE segments ADD COLUMN speaker_manual INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE meetings ADD COLUMN speaker_check REAL;
        ALTER TABLE meetings ADD COLUMN speaker_hint_dismissed INTEGER NOT NULL DEFAULT 0;
        CREATE TABLE speaker_undo (
          meeting_id INTEGER PRIMARY KEY REFERENCES meetings(id) ON DELETE CASCADE,
          snapshot   TEXT NOT NULL,
          label      TEXT NOT NULL,
          created_at TEXT NOT NULL
        );
        """;

    /// <summary>會議的參考文件（M§39.1、R61）</summary>
    public const string V10 = """
        CREATE TABLE documents (
          id         INTEGER PRIMARY KEY AUTOINCREMENT,
          meeting_id INTEGER NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
          name       TEXT NOT NULL,
          file       TEXT NOT NULL,
          kind       TEXT NOT NULL,
          pages      TEXT NOT NULL,
          ocr        INTEGER NOT NULL DEFAULT 0,
          created_at TEXT NOT NULL
        );
        CREATE INDEX documents_meeting ON documents(meeting_id, id);
        """;

    /// <summary>
    /// 精簡逐字稿（R96、M§70）：一句一列；句子刪掉（重新轉錄、刪除一句）時跟著刪，
    /// 句子的文字改了（編輯、校正、用詞修正、拆句）時觸發器刪掉那一句的精簡版，畫面上回到原文
    /// </summary>
    public const string V11 = """
        CREATE TABLE clean_lines (
          segment_id INTEGER PRIMARY KEY REFERENCES segments(id) ON DELETE CASCADE,
          text       TEXT NOT NULL
        );
        CREATE TRIGGER clean_lines_text AFTER UPDATE OF text ON segments
        BEGIN
          DELETE FROM clean_lines WHERE segment_id = NEW.id;
        END;
        """;

    /// <summary>
    /// 資料夾自動歸類、定期會議（R98、R99、M§72、§73；定期會議 v14 拿掉）：series（定期會議）、series_rejected（按了「不是定期會議」的那一組）、
    /// folder_rules（資料夾的自動加入條件，keywords、people 是 JSON 陣列）；meetings 加 folder_decided、series_id
    /// </summary>
    public const string V12 = """
        CREATE TABLE series (
          id             INTEGER PRIMARY KEY AUTOINCREMENT,
          name           TEXT NOT NULL,
          weekday        INTEGER NOT NULL,
          minute         INTEGER,
          interval_weeks INTEGER NOT NULL DEFAULT 1,
          people         TEXT NOT NULL DEFAULT '[]',
          created_at     TEXT NOT NULL
        );
        CREATE TABLE series_rejected (
          key        TEXT PRIMARY KEY,
          created_at TEXT NOT NULL
        );
        CREATE TABLE folder_rules (
          folder      TEXT PRIMARY KEY,
          keywords    TEXT NOT NULL DEFAULT '[]',
          people      TEXT NOT NULL DEFAULT '[]',
          template_id TEXT,
          series_id   INTEGER REFERENCES series(id) ON DELETE SET NULL,
          created_at  TEXT NOT NULL
        );
        ALTER TABLE meetings ADD COLUMN folder_decided INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE meetings ADD COLUMN series_id INTEGER REFERENCES series(id) ON DELETE SET NULL;
        CREATE INDEX meetings_series ON meetings(series_id);
        """;

    /// <summary>資料夾自己存在，可以是空的（R104、M§78）；建立時把現有會議的資料夾、有條件的資料夾放進去</summary>
    public const string V13 = """
        CREATE TABLE folders (
          name       TEXT PRIMARY KEY,
          created_at TEXT NOT NULL
        );
        """;

    /// <summary>
    /// 拿掉定期會議（R99、M§73、D177）的後半：每個定期會議換成同名資料夾（AppDatabase.RunMigration 先一個一個做）之後，
    /// 資料夾條件的定期會議拿掉（條件都空了就刪掉那一筆）、會議的 series_id 清掉、清空 series 和 series_rejected。
    /// 表、欄位留著不用（拿掉有外鍵的欄位要重建整張 meetings），同 Mac 的 v14
    /// </summary>
    public const string V14 = """
        UPDATE folder_rules SET series_id = NULL;
        DELETE FROM folder_rules WHERE keywords = '[]' AND people = '[]' AND template_id IS NULL;
        UPDATE meetings SET series_id = NULL WHERE series_id IS NOT NULL;
        DELETE FROM series;
        DELETE FROM series_rejected;
        """;

    /// <summary>編輯會議的快照（R107.8、M§81.3）：第一次編輯前的樣子，最後一次編輯後留 30 天</summary>
    public const string V15 = """
        CREATE TABLE meeting_edits (
          meeting_id  INTEGER PRIMARY KEY REFERENCES meetings(id) ON DELETE CASCADE,
          snapshot    TEXT NOT NULL,
          audio_file  TEXT NOT NULL,
          split_into  TEXT NOT NULL DEFAULT '[]',
          merged_from TEXT NOT NULL DEFAULT '[]',
          created_at  TEXT NOT NULL,
          edited_at   TEXT NOT NULL
        );
        """;

    /// <summary>會議中的畫面（R109、M§83.1）：有錄音時間的文件</summary>
    public const string V16 = "ALTER TABLE documents ADD COLUMN at REAL";

    /// <summary>版本紀錄記下用的範本（R115、M§89）</summary>
    public static readonly string[] V17 = ["ALTER TABLE meetings ADD COLUMN notes_template TEXT", "ALTER TABLE notes_versions ADD COLUMN template_id TEXT"];

    /// <summary>
    /// 資料夾用 id 辨識（R118、M§92.1、D200）：folders 有自己的 id，會議、條件記 folder_id，meetings.folder 整欄拿掉。
    /// id 的順序兩平台一樣：先資料夾表，再會議用到的、條件的（照建立時間、名字）。ALTER TABLE … DROP COLUMN 要 SQLite 3.35 以上
    /// </summary>
    public const string V18 = """
        ALTER TABLE folders RENAME TO folders_old;
        ALTER TABLE folder_rules RENAME TO folder_rules_old;
        CREATE TABLE folders (
          id         INTEGER PRIMARY KEY AUTOINCREMENT,
          name       TEXT NOT NULL UNIQUE,
          created_at TEXT NOT NULL
        );
        -- id 的順序要兩平台一樣：先資料夾表，再會議用到的、條件的（照建立時間、名字）；用 NOT IN 跳過已經有的（INSERT OR IGNORE 會把 id 用掉，留下空號）
        INSERT INTO folders (name, created_at) SELECT name, created_at FROM folders_old ORDER BY created_at, name;
        INSERT INTO folders (name, created_at)
          SELECT folder, MIN(created_at) FROM meetings WHERE folder <> '' AND folder NOT IN (SELECT name FROM folders)
          GROUP BY folder ORDER BY MIN(created_at), folder;
        INSERT INTO folders (name, created_at)
          SELECT folder, created_at FROM folder_rules_old WHERE folder NOT IN (SELECT name FROM folders) ORDER BY created_at, folder;
        CREATE TABLE folder_rules (
          folder_id   INTEGER PRIMARY KEY REFERENCES folders(id) ON DELETE CASCADE,
          keywords    TEXT NOT NULL DEFAULT '[]',
          people      TEXT NOT NULL DEFAULT '[]',
          template_id TEXT,
          series_id   INTEGER REFERENCES series(id) ON DELETE SET NULL,
          created_at  TEXT NOT NULL
        );
        INSERT INTO folder_rules (folder_id, keywords, people, template_id, series_id, created_at)
          SELECT f.id, r.keywords, r.people, r.template_id, r.series_id, r.created_at FROM folder_rules_old r JOIN folders f ON f.name = r.folder;
        ALTER TABLE meetings ADD COLUMN folder_id INTEGER REFERENCES folders(id) ON DELETE SET NULL;
        UPDATE meetings SET folder_id = (SELECT id FROM folders WHERE name = meetings.folder) WHERE folder <> '';
        ALTER TABLE meetings DROP COLUMN folder;
        DROP TABLE folders_old;
        DROP TABLE folder_rules_old;
        CREATE INDEX meetings_folder ON meetings(folder_id);
        """;

    /// <summary>先轉錄這場（R122、M§96.1、D205）：排隊的順序，大的先、一樣的照 id（同 Mac 的 AppDatabase.jobPrioritySQL）</summary>
    public const string V19 = "ALTER TABLE jobs ADD COLUMN priority INTEGER NOT NULL DEFAULT 0";

    /// <summary>詞彙表的群組（R126、M§100.1、D213）：像標籤，一個詞可以在好幾組（同 Mac 的 AppDatabase.glossaryGroupsSQL）</summary>
    public const string V20 = """
        CREATE TABLE glossary_groups (
          id         INTEGER PRIMARY KEY AUTOINCREMENT,
          name       TEXT NOT NULL UNIQUE COLLATE NOCASE,
          created_at TEXT NOT NULL
        );
        CREATE TABLE glossary_group_terms (
          group_id INTEGER NOT NULL REFERENCES glossary_groups(id) ON DELETE CASCADE,
          term_id  INTEGER NOT NULL REFERENCES glossary(id) ON DELETE CASCADE,
          PRIMARY KEY (group_id, term_id)
        );
        CREATE INDEX glossary_group_terms_term ON glossary_group_terms(term_id);
        """;

    /// <summary>透過企業伺服器分享會議（R125、M§99.3、D212）：匯入的分享是誰分享的、這場分享過的 id（同 Mac 的 AppDatabase.meetingSharesSQL）</summary>
    public const string V21 = """
        ALTER TABLE meetings ADD COLUMN shared_from TEXT;
        CREATE TABLE meeting_shares (
          meeting_id INTEGER NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
          server     TEXT NOT NULL,
          share_id   TEXT NOT NULL,
          created_at TEXT NOT NULL,
          PRIMARY KEY (server, share_id)
        );
        CREATE INDEX meeting_shares_meeting ON meeting_shares(meeting_id);
        """;

    /// <summary>v22（M§99.3、R125.5）：匯入的是哪一個分享，再匯入時找之前那份；同 Mac 的 sharedIdSQL</summary>
    public const string V22 = "ALTER TABLE meetings ADD COLUMN shared_id TEXT;";
}
