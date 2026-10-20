CREATE INDEX corrections_meeting ON corrections(meeting_id);
CREATE INDEX jobs_meeting ON jobs(meeting_id, id);
CREATE INDEX jobs_status ON jobs(status, id);
CREATE INDEX questions_meeting ON questions(meeting_id, id);
CREATE INDEX segments_meeting ON segments(meeting_id, seq);
CREATE INDEX voiceprints_person ON voiceprints(person_id);
CREATE TABLE bookmarks (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  meeting_id INTEGER NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
  time       REAL NOT NULL,
  label      TEXT NOT NULL DEFAULT ''
);
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
CREATE TABLE drafts (
  meeting_id INTEGER NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
  key        TEXT NOT NULL,
  text       TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  PRIMARY KEY (meeting_id, key)
);
CREATE TABLE glossary (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  term       TEXT NOT NULL UNIQUE,
  created_at TEXT NOT NULL
);
CREATE TABLE glossary_dismissed (term TEXT PRIMARY KEY COLLATE NOCASE);
CREATE TABLE grdb_migrations (identifier TEXT NOT NULL PRIMARY KEY);
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
, crashes INTEGER NOT NULL DEFAULT 0, error_kind TEXT);
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
, template_id TEXT NOT NULL DEFAULT 'general', user_notes  TEXT NOT NULL DEFAULT '', source      TEXT NOT NULL DEFAULT 'import', channels    TEXT NOT NULL DEFAULT 'mic', deleted_at  TEXT, folder      TEXT NOT NULL DEFAULT '', language TEXT NOT NULL DEFAULT 'zh', notes_draft INTEGER NOT NULL DEFAULT 0, transcribed_with TEXT NOT NULL DEFAULT '', min_speakers INTEGER, speaker_check REAL, speaker_hint_dismissed INTEGER NOT NULL DEFAULT 0);
CREATE TABLE notes_versions (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  meeting_id INTEGER NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
  notes      TEXT NOT NULL,
  reason     TEXT NOT NULL,
  created_at TEXT NOT NULL
);
CREATE TABLE people (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  name       TEXT NOT NULL UNIQUE COLLATE NOCASE,
  created_at TEXT NOT NULL
);
CREATE TABLE questions (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  meeting_id INTEGER REFERENCES meetings(id) ON DELETE CASCADE,
  scope      TEXT NOT NULL DEFAULT '',
  question   TEXT NOT NULL,
  answer     TEXT NOT NULL,
  provider   TEXT NOT NULL,
  created_at TEXT NOT NULL
);
CREATE VIRTUAL TABLE search USING fts5(meeting_id UNINDEXED, kind UNINDEXED, ref UNINDEXED, text, tokenize = 'trigram');
CREATE TABLE 'search_config'(k PRIMARY KEY, v) WITHOUT ROWID;
CREATE TABLE 'search_content'(id INTEGER PRIMARY KEY, c0, c1, c2, c3);
CREATE TABLE 'search_data'(id INTEGER PRIMARY KEY, block BLOB);
CREATE TABLE 'search_docsize'(id INTEGER PRIMARY KEY, sz BLOB);
CREATE TABLE 'search_idx'(segid, term, pgno, PRIMARY KEY(segid, term)) WITHOUT ROWID;
CREATE TABLE segments (
  id             INTEGER PRIMARY KEY AUTOINCREMENT,
  meeting_id     INTEGER NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
  seq            INTEGER NOT NULL,
  start          REAL NOT NULL,
  end            REAL NOT NULL,
  text           TEXT NOT NULL,
  low_confidence INTEGER NOT NULL DEFAULT 0
, speaker INTEGER, kind TEXT NOT NULL DEFAULT 'final', channel INTEGER, speaker_manual INTEGER NOT NULL DEFAULT 0);
CREATE TABLE speaker_undo (
  meeting_id INTEGER PRIMARY KEY REFERENCES meetings(id) ON DELETE CASCADE,
  snapshot   TEXT NOT NULL,
  label      TEXT NOT NULL,
  created_at TEXT NOT NULL
);
CREATE TABLE speakers (
  meeting_id INTEGER NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
  idx        INTEGER NOT NULL,
  name       TEXT NOT NULL, name_source TEXT NOT NULL DEFAULT 'default', person_id INTEGER REFERENCES people(id) ON DELETE SET NULL, embedding BLOB, speech_seconds REAL NOT NULL DEFAULT 0, audio_source TEXT NOT NULL DEFAULT 'mix', suggested_name TEXT, suggestion_reason TEXT, suggestion_time TEXT,
  PRIMARY KEY (meeting_id, idx)
);
CREATE TABLE sqlite_sequence(name,seq);
CREATE TABLE templates (
  id         TEXT PRIMARY KEY,
  name       TEXT NOT NULL,
  builtin    INTEGER NOT NULL,
  definition TEXT NOT NULL,
  updated_at TEXT NOT NULL
);
CREATE TABLE voiceprints (
  id           INTEGER PRIMARY KEY AUTOINCREMENT,
  person_id    INTEGER NOT NULL REFERENCES people(id) ON DELETE CASCADE,
  embedding    BLOB NOT NULL,
  audio_source TEXT NOT NULL,
  meeting_id   INTEGER REFERENCES meetings(id) ON DELETE SET NULL,
  speaker_idx  INTEGER,
  created_at   TEXT NOT NULL
);
