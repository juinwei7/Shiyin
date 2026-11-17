UPDATE meetings SET folder = 'Remote control project' WHERE id IN (1, 2);
UPDATE meetings SET folder = 'Pricing' WHERE id = 3;
INSERT INTO meetings (title, title_locked, meeting_date, participants, source_filename, audio_file, duration_seconds, notes, notes_by, created_at, updated_at, template_id, user_notes, source, channels, deleted_at, folder, language, notes_draft, transcribed_with, min_speakers, speaker_check, speaker_hint_dismissed)
SELECT t.title, 1, t.d, participants, 'demo.m4a', '', t.dur, notes, notes_by, t.d || 'T02:00:00Z', t.d || 'T03:00:00Z', template_id, '', 'import', channels, NULL, t.folder, language, notes_draft, transcribed_with, NULL, NULL, 0
FROM meetings m JOIN (
  SELECT 2 AS src, 'Prototype review' AS title, '2026-10-23' AS d, 2140 AS dur, 'Remote control project' AS folder UNION ALL
  SELECT 3, 'Supplier quote follow-up', '2026-10-20', 1630, 'Pricing' UNION ALL
  SELECT 1, 'Customer interviews: living-room habits', '2026-10-14', 1980, 'Customer research' UNION ALL
  SELECT 1, 'Customer interviews: older users', '2026-10-21', 1720, 'Customer research' UNION ALL
  SELECT 2, 'Weekly team sync', '2026-11-03', 1750, '' UNION ALL
  SELECT 3, 'New hire onboarding', '2026-11-05', 1240, '' UNION ALL
  SELECT 2, 'Budget planning for 2027', '2026-11-10', 2690, ''
) t ON m.id = t.src;
UPDATE meetings SET notes = json_set(notes, '$.reviewItems', json('[]')) WHERE id > 3;
