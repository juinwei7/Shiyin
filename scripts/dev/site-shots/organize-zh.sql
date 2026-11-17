-- 官網「整理」截圖用：多幾場會議、分好資料夾（只改拍攝用的複本）
UPDATE meetings SET folder = 'Lumio 新品上市' WHERE id = 2;
UPDATE meetings SET folder = '客戶訪談' WHERE id = 1;
UPDATE meetings SET folder = '設計評審' WHERE id = 3;
INSERT INTO meetings (title, title_locked, meeting_date, participants, source_filename, audio_file, duration_seconds, notes, notes_by, created_at, updated_at, template_id, user_notes, source, channels, deleted_at, folder, language, notes_draft, transcribed_with, min_speakers, speaker_check, speaker_hint_dismissed)
SELECT t.title, 1, t.d, participants, 'demo.m4a', '', t.dur, notes, notes_by, t.d || 'T02:00:00Z', t.d || 'T03:00:00Z', template_id, '', 'import', channels, NULL, t.folder, language, notes_draft, transcribed_with, NULL, NULL, 0
FROM meetings m JOIN (
  SELECT 2 AS src, 'Lumio 通路鋪貨進度確認' AS title, '2026-10-21' AS d, 1520 AS dur, 'Lumio 新品上市' AS folder UNION ALL
  SELECT 2, 'Lumio 發表會彩排檢討', '2026-10-28', 2280, 'Lumio 新品上市' UNION ALL
  SELECT 1, '咖啡店老闆使用回饋訪談', '2026-10-16', 1860, '客戶訪談' UNION ALL
  SELECT 3, '包裝設計第二輪評審', '2026-10-22', 2410, '設計評審' UNION ALL
  SELECT 2, '週一部門例會', '2026-11-03', 1750, '' UNION ALL
  SELECT 1, '新人到職說明', '2026-11-05', 1240, '' UNION ALL
  SELECT 2, '年度預算初步討論', '2026-11-10', 2690, ''
) t ON m.id = t.src;
UPDATE meetings SET notes = json_set(notes, '$.reviewItems', json('[]')) WHERE id > 3;
