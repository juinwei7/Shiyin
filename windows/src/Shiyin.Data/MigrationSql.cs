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

}
