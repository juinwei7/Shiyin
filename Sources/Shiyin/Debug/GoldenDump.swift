#if DEBUG
import FluidAudio
import Foundation
import GRDB

/// 產生 Windows 版用來比對的標準答案（design-windows §4）：讀 shared/golden/fixtures.json，用 Mac 的程式算出提示詞、範本、
/// 匯出、轉錄流程的規則、資料庫的結果，寫進輸出資料夾。只在 DEBUG 建置，不改任何正式的行為（R26.5）。
/// shiyinctl dump-golden <fixtures.json> <輸出資料夾>；做完寫 <輸出資料夾>/result.json
@MainActor
enum GoldenDump {
    private typealias Object = [String: Any]

    static func run(fixtures: String, output: String) {
        let out = URL(fileURLWithPath: output, isDirectory: true)
        var result: Object = [:]
        do {
            let data = try Data(contentsOf: URL(fileURLWithPath: fixtures))
            guard let root = try JSONSerialization.jsonObject(with: data) as? Object else { throw LLMError.badOutput("fixtures") }
            try? FileManager.default.removeItem(at: out)
            try FileManager.default.createDirectory(at: out, withIntermediateDirectories: true)
            var dump = Writer(root: out)
            let fixtures = Fixtures(root)
            prompts(fixtures, &dump)
            templates(&dump)
            exports(fixtures, &dump)
            utilities(fixtures, &dump)
            pipeline(fixtures, &dump)
            try database(fixtures, &dump)
            result["ok"] = true
            result["files"] = dump.count
        } catch {
            result["ok"] = false
            result["error"] = "\(error)"
        }
        try? FileManager.default.createDirectory(at: out, withIntermediateDirectories: true)
        if let data = try? JSONSerialization.data(withJSONObject: result, options: [.prettyPrinted, .sortedKeys]) {
            try? data.write(to: out.appendingPathComponent("result.json"))
        }
    }

    // MARK: 輸出

    private struct Writer {
        let root: URL
        var count = 0

        mutating func text(_ path: String, _ text: String) {
            let url = root.appendingPathComponent(path)
            try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            try? Data(text.utf8).write(to: url)
            count += 1
        }

        mutating func json(_ path: String, _ object: Any) {
            let options: JSONSerialization.WritingOptions = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes, .fragmentsAllowed]
            // 不能轉成 JSON 的值（例如推論錯的型別）會丟 Objective-C 例外，先檢查
            guard object is NSNull || JSONSerialization.isValidJSONObject(object) else {
                text(path + ".error", "invalid JSON object: \(object)")
                return
            }
            guard let data = try? JSONSerialization.data(withJSONObject: object, options: options) else {
                text(path + ".error", "cannot encode")
                return
            }
            text(path, String(decoding: data, as: UTF8.self) + "\n")
        }
    }

    // MARK: 測試資料

    private struct MeetingFixture {
        var key: String
        var raw: Object
        var meeting: Meeting
        var segments: [Segment]
        var speakers: [Int: String]
        var voices: [Int: SpeakerVoice]
        var bookmarks: [Bookmark]
        var glossary: [String]
        var notes: MeetingNotes?
        var answers: [ReviewAnswer]
        var history: [(question: String, answer: String)]
        var question: String
        var knownPeople: [String]
        var language: MeetingLanguage { meeting.meetingLanguage }

        /// 資料庫裡的說話者表：1 到最大編號，沒改名的是預設名稱（跟 saveTranscript 一樣）
        var speakerTable: [Int: String] {
            let count = segments.compactMap(\.speaker).max() ?? 0
            var table: [Int: String] = [:]
            for idx in stride(from: 1, through: count, by: 1) {
                table[idx] = speakers[idx] ?? language.text(zh: "說話者 \(idx)", en: "Speaker \(idx)", ja: "話者 \(idx)")
            }
            return table
        }
    }

    private struct Fixtures {
        let root: Object
        let meetings: [MeetingFixture]

        init(_ root: Object) {
            self.root = root
            meetings = (root["meetings"] as? [Object] ?? []).map(GoldenDump.meetingFixture)
        }

        func meeting(_ key: String) -> MeetingFixture { meetings.first { $0.key == key }! }
        subscript(_ key: String) -> Any? { root[key] }
    }

    nonisolated private static func meetingFixture(_ o: Object) -> MeetingFixture {
        let m = o["meeting"] as? Object ?? [:]
        var meeting = Meeting(meetingDate: m["meetingDate"] as? String ?? "", sourceFilename: m["sourceFilename"] as? String ?? "")
        meeting.title = m["title"] as? String ?? ""
        meeting.titleLocked = m["titleLocked"] as? Bool ?? false
        meeting.participants = m["participants"] as? String ?? ""
        meeting.durationSeconds = m["durationSeconds"] as? Int
        meeting.templateId = m["templateId"] as? String ?? "general"
        meeting.userNotes = m["userNotes"] as? String ?? ""
        meeting.language = m["language"] as? String ?? "zh"
        meeting.folder = m["folder"] as? String ?? ""
        meeting.channels = m["channels"] as? String ?? "mic"
        meeting.createdAt = "2026-10-20T00:00:00Z"
        meeting.updatedAt = "2026-10-20T00:00:00Z"
        let segments = (o["segments"] as? [Object] ?? []).enumerated().map { i, s in
            Segment(meetingId: 0, seq: i, start: double(s["start"]), end: double(s["end"]), text: s["text"] as? String ?? "",
                    lowConfidence: s["lowConfidence"] as? Bool ?? false, speaker: s["speaker"] as? Int)
        }
        var speakers: [Int: String] = [:]
        for (k, v) in o["speakers"] as? Object ?? [:] { if let idx = Int(k), let name = v as? String { speakers[idx] = name } }
        var voices: [Int: SpeakerVoice] = [:]
        for (k, v) in o["voices"] as? Object ?? [:] {
            guard let idx = Int(k), let v = v as? Object else { continue }
            voices[idx] = SpeakerVoice(embedding: (v["embedding"] as? [Any] ?? []).map { Float(double($0)) },
                                       seconds: double(v["seconds"]), source: v["source"] as? String ?? "mix")
        }
        let bookmarks = (o["bookmarks"] as? [Object] ?? []).map {
            Bookmark(meetingId: 0, time: double($0["time"]), label: $0["label"] as? String ?? "")
        }
        let notes = (o["notes"] as? Object).flatMap { decodeNotes($0) }
        let answers = (o["answers"] as? [Object] ?? []).map {
            ReviewAnswer(time: $0["time"] as? String ?? "", question: $0["question"] as? String ?? "", answer: $0["answer"] as? String ?? "")
        }
        let history: [(question: String, answer: String)] = (o["history"] as? [Object] ?? []).map {
            (question: $0["question"] as? String ?? "", answer: $0["answer"] as? String ?? "")
        }
        return MeetingFixture(key: o["key"] as? String ?? "", raw: o, meeting: meeting, segments: segments, speakers: speakers,
                              voices: voices, bookmarks: bookmarks, glossary: o["glossary"] as? [String] ?? [], notes: notes,
                              answers: answers, history: history, question: o["question"] as? String ?? "",
                              knownPeople: o["knownPeople"] as? [String] ?? [])
    }

    nonisolated private static func double(_ value: Any?) -> Double {
        if let n = value as? NSNumber { return n.doubleValue }
        return 0
    }

    nonisolated private static func decodeNotes(_ object: Any) -> MeetingNotes? {
        guard let data = try? JSONSerialization.data(withJSONObject: object) else { return nil }
        return try? JSONDecoder().decode(MeetingNotes.self, from: data)
    }

    // MARK: 提示詞（R28.1）

    private static func prompts(_ f: Fixtures, _ w: inout Writer) {
        for fx in f.meetings {
            let l = fx.language
            let dir = "prompts/\(fx.key)/"
            w.text(dir + "notes-system.txt", NotesPrompt.system(l))
            func context(_ template: TemplateDefinition) -> NotesContext {
                NotesContext(meeting: fx.meeting, segments: fx.segments, speakers: fx.speakers, bookmarks: fx.bookmarks,
                             glossary: fx.glossary, template: template)
            }
            for builtin in BuiltinTemplates.all {
                let record = TemplateRecord(id: builtin.id, name: builtin.name, builtin: true, definition: builtin.definition.jsonString())
                w.text(dir + "summarize-\(builtin.id).txt", NotesPrompt.summarize(context(record.definition(for: l))))
            }
            if let custom = f["customTemplate"], let data = try? JSONSerialization.data(withJSONObject: custom),
               let json = String(data: data, encoding: .utf8) {
                let record = TemplateRecord(id: "custom-1", name: "客戶會議", builtin: false, definition: json)  // l10n-skip
                w.text(dir + "summarize-custom.txt", NotesPrompt.summarize(context(record.definition(for: l))))
            }
            let general = TemplateRecord(id: "general", name: "", builtin: true, definition: BuiltinTemplates.all[0].definition.jsonString())
            if let notes = fx.notes {
                w.text(dir + "revise.txt", NotesPrompt.revise(context(general.definition(for: l)), notes: notes, answers: fx.answers))
            }

            // 逐字稿校正：跟 JobRunner.correct 一樣組 Context
            let names = fx.speakerTable
            let speakerName = { (idx: Int?) -> String? in idx.map { names[$0] ?? l.text(zh: "說話者 \($0)", en: "Speaker \($0)", ja: "話者 \($0)") } }
            let correction = TranscriptCorrection.Context(
                language: l, title: fx.meeting.title, participants: fx.meeting.participantList,
                speakerNames: Array(Set(names.values)).sorted(), glossary: fx.glossary,
                segments: fx.segments.map { ($0.text, speakerName($0.speaker)) })
            w.text(dir + "correction-system.txt", TranscriptCorrection.system(l))
            w.text(dir + "correction-prompt.txt", TranscriptCorrection.prompt(correction, range: 0..<fx.segments.count))
            if let r = f["correctionRange"] as? [Int], r.count == 2, r[1] <= fx.segments.count {
                w.text(dir + "correction-prompt-range.txt", TranscriptCorrection.prompt(correction, range: r[0]..<r[1]))
            }

            // 猜說話者：跟 JobRunner.nameSpeakers 一樣
            let unnamed = names.keys.sorted().filter { AppDatabase.isDefaultSpeakerName(names[$0]!) }
            let naming = SpeakerNaming.Context(
                language: l, meetingDate: fx.meeting.meetingDate, title: fx.meeting.title, participants: fx.meeting.participantList,
                knownPeople: fx.knownPeople, glossary: fx.glossary, unnamed: unnamed,
                speakerName: { names[$0] ?? l.text(zh: "說話者 \($0)", en: "Speaker \($0)", ja: "話者 \($0)") },
                segments: fx.segments.map { ($0.start, $0.speaker, $0.text) })
            w.text(dir + "naming-system.txt", SpeakerNaming.system(l))
            w.text(dir + "naming-prompt.txt", SpeakerNaming.prompt(naming))

            // 對一場會議提問：跟 AppModel.askMeeting 一樣組逐字稿
            let lines = fx.segments.map { s -> String in
                let speaker = s.speaker.map { (names[$0] ?? l.text(zh: "說話者 \($0)", en: "Speaker \($0)", ja: "話者 \($0)")) + l.text(zh: "：", en: ": ", ja: "：") } ?? ""
                return "[\(TimeFormat.string(s.start))] \(speaker)\(s.text)"
            }
            var meeting = fx.meeting
            meeting.notes = fx.notes?.jsonString()
            w.text(dir + "ask-meeting-system.txt", AskPrompt.meetingSystem(l))
            w.text(dir + "ask-meeting-prompt.txt", AskPrompt.meetingPrompt(meeting: meeting, notes: meeting.decodedNotes, lines: lines,
                                                                           history: Array(fx.history.suffix(3)), question: fx.question))
            w.text(dir + "ask-all-system.txt", AskPrompt.allSystem(l))
            let blocks = (f["askAllBlocks"] as? [Object] ?? []).map {
                AskPrompt.MeetingBlock(id: Int64($0["id"] as? Int ?? 0), title: $0["title"] as? String ?? "", date: $0["date"] as? String ?? "",
                                       summary: $0["summary"] as? [String] ?? [], lines: $0["lines"] as? [String] ?? [])
            }
            w.text(dir + "ask-all-prompt.txt", AskPrompt.allPrompt(l, blocks: blocks, question: f["askAllQuestion"] as? String ?? ""))
            w.text(dir + "keywords-system.txt", AskPrompt.keywordsSystem(l))
            w.text(dir + "keywords-prompt.txt", AskPrompt.keywordsPrompt(l, question: fx.question))
        }

        w.text("schemas/notes.json", MeetingNotes.jsonSchema)
        w.text("schemas/ask-meeting.json", AskPrompt.meetingSchema)
        w.text("schemas/ask-all.json", AskPrompt.allSchema)
        w.text("schemas/keywords.json", AskPrompt.keywordsSchema)
        w.text("schemas/naming.json", SpeakerNaming.jsonSchema)
        w.text("schemas/correction.json", TranscriptCorrection.jsonSchema)
        let formats: [(String, LLMClient.OutputFormat)] = [
            ("notes", .meetingNotes), ("askMeeting", AskPrompt.meetingFormat), ("askAll", AskPrompt.allFormat),
            ("keywords", AskPrompt.keywordsFormat), ("naming", SpeakerNaming.format), ("correction", TranscriptCorrection.format),
        ]
        w.json("schemas/formats.json", Dictionary(uniqueKeysWithValues: formats.map {
            ($0.0, ["toolName": $0.1.toolName, "toolDescription": $0.1.toolDescription, "schema": $0.1.schema])
        }))

        // whisper 的提示詞（design §16.4、§18.2）
        let glossaries = f["whisperGlossaries"] as? [[String]] ?? []
        var whisper: [Object] = []
        for language in MeetingLanguage.allCases {
            for (i, glossary) in glossaries.enumerated() {
                whisper.append(["language": language.rawValue, "glossary": i, "prompt": WhisperEngine.prompt(glossary: glossary, language: language)])
            }
        }
        w.json("whisper/prompts.json", whisper)
        var chunk: [Object] = []
        for fx in f.meetings {
            let transcribed = fx.segments.map { TranscribedSegment(start: $0.start, end: $0.end, text: $0.text, lowConfidence: $0.lowConfidence) }
            for count in [0, 1, transcribed.count] {
                let base = WhisperEngine.prompt(glossary: fx.glossary, language: fx.language)
                chunk.append(["key": fx.key, "previous": count,
                              "prompt": WhisperEngine.debugChunkPrompt(base: base, previous: Array(transcribed.prefix(count)), language: fx.language.whisperCode)])
            }
            // 很長的前一段：超過 120 字（英文 400 字）要截斷
            let long = (0..<40).map { TranscribedSegment(start: Double($0), end: Double($0) + 1, text: "第\($0)句 sentence number \($0)", lowConfidence: false) }  // l10n-skip
            chunk.append(["key": fx.key, "previous": "long",
                          "prompt": WhisperEngine.debugChunkPrompt(base: "P", previous: long, language: fx.language.whisperCode)])
        }
        w.json("whisper/chunk-prompts.json", chunk)
        w.json("whisper/hallucinations.json", WhisperEngine.hallucinationPhrases)
        let ratios = ["短", "this is a normal sentence without much repetition at all",  // l10n-skip
                      String(repeating: "重複", count: 40), String(repeating: "ha ", count: 30), "一二三四五六七八九十一二三四五六七八九十"]  // l10n-skip
        w.json("whisper/compression.json", ratios.map { ["text": $0, "ratio": WhisperEngine.compressionRatio($0)] })
    }

    /// 沒有 sortedKeys 的 JSONEncoder 每次啟動鍵的順序都不同（範本的定義、說話者的復原資料）：重新排序後再寫，Windows 比對解析後的內容
    private static func canonical(_ json: String) -> String {
        guard let object = try? JSONSerialization.jsonObject(with: Data(json.utf8), options: [.fragmentsAllowed]),
              let data = try? JSONSerialization.data(withJSONObject: object, options: [.sortedKeys, .withoutEscapingSlashes, .fragmentsAllowed])
        else { return json }
        return String(decoding: data, as: UTF8.self)
    }

    // MARK: 範本

    private static func templates(_ w: inout Writer) {
        var list: [Object] = []
        for t in BuiltinTemplates.all {
            var item: Object = ["id": t.id, "name": t.name, "jsonString": canonical(t.definition.jsonString())]
            if let english = BuiltinTemplates.english[t.id] {
                item["englishName"] = english.name
                item["englishJSON"] = canonical(english.definition.jsonString())
            }
            if let japanese = BuiltinTemplates.japanese[t.id] { item["japaneseJSON"] = canonical(japanese.jsonString()) }
            list.append(item)
        }
        w.json("templates/builtin.json", list)
        w.json("templates/section-labels.json", MeetingLanguage.allCases.map { l in
            ["language": l.rawValue, "labels": TemplateDefinition.builtinSectionIDs.map { TemplateDefinition.sectionLabel($0, language: l) }] as Object
        })
        w.json("templates/decode.json", ["{}", "{\"description\":\"d\"}", "not json",
                                         "{\"description\":\"d\",\"sections\":[\"summary\"],\"custom\":[],\"instruction\":\"i\"}"].map {
            ["input": $0, "output": canonical(TemplateDefinition.decode($0).jsonString())]
        })
    }

    // MARK: 匯出（R28.2、R28.3）

    private static func exports(_ f: Fixtures, _ w: inout Writer) {
        for fx in f.meetings {
            var meeting = fx.meeting
            meeting.notes = fx.notes?.jsonString()
            let bundle = ExportBundle(meeting: meeting, segments: fx.segments, speakers: fx.speakerTable, bookmarks: fx.bookmarks, notes: fx.notes)
            let dir = "export/\(fx.key)/"
            w.text(dir + "notes.md", NotesExporter.markdown(bundle))
            w.text(dir + "notes.txt", NotesExporter.plain(bundle))
            w.text(dir + "notes.html", NotesExporter.html(bundle))
            for (times, speakers) in [(true, true), (true, false), (false, true), (false, false)] {
                let suffix = "\(times ? "t" : "f")\(speakers ? "t" : "f")"
                w.text(dir + "transcript-\(suffix).txt", TranscriptExporter.text(bundle, format: "txt", times: times, speakers: speakers))
                w.text(dir + "transcript-\(suffix).md", TranscriptExporter.text(bundle, format: "md", times: times, speakers: speakers))
                w.text(dir + "transcript-\(suffix).html", TranscriptExporter.html(bundle, times: times, speakers: speakers))
            }
            w.text(dir + "transcript-t.srt", TranscriptExporter.text(bundle, format: "srt", times: true, speakers: true))
            w.text(dir + "transcript-f.srt", TranscriptExporter.text(bundle, format: "srt", times: true, speakers: false))
            w.text(dir + "transcript-t.vtt", TranscriptExporter.text(bundle, format: "vtt", times: true, speakers: true))
            w.text(dir + "transcript-f.vtt", TranscriptExporter.text(bundle, format: "vtt", times: true, speakers: false))
            // 沒有會議紀錄、空的會議紀錄
            var empty = bundle
            empty.notes = MeetingNotes(title: "x")
            w.text(dir + "notes-empty.md", NotesExporter.markdown(empty))
            w.text(dir + "notes-empty.html", NotesExporter.html(empty))

            // .shiyin 封包的 JSON（design §10）：跟 MeetingPackage.export 一樣的編碼
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
            let m = meeting
            let info = MeetingPackage.MeetingJSON(
                title: m.title, titleLocked: m.titleLocked, meetingDate: m.meetingDate, participants: m.participants,
                sourceFilename: m.sourceFilename, durationSeconds: m.durationSeconds, templateId: m.templateId,
                userNotes: m.userNotes, channels: m.channels, folder: m.folder, language: m.language, notes: fx.notes, notesBy: "claude",
                speakers: bundle.speakers.map { MeetingPackage.SpeakerJSON(idx: $0.key, name: $0.value) }.sorted { $0.idx < $1.idx },
                bookmarks: fx.bookmarks.map { MeetingPackage.BookmarkJSON(time: $0.time, label: $0.label) })
            if let data = try? encoder.encode(info) { w.text("package/\(fx.key)/meeting.json", String(decoding: data, as: UTF8.self)) }
            let segments = fx.segments.map {
                MeetingPackage.SegmentJSON(start: $0.start, end: $0.end, text: $0.text, lowConfidence: $0.lowConfidence, speaker: $0.speaker)
            }
            if let data = try? encoder.encode(segments) { w.text("package/\(fx.key)/transcript.json", String(decoding: data, as: UTF8.self)) }
            let manifest = MeetingPackage.Manifest(format: MeetingPackage.formatVersion, app: "3.6.0", exportedAt: "2026-10-20T00:00:00Z",
                                                   audio: "audio.m4a")
            if let data = try? encoder.encode(manifest) { w.text("package/\(fx.key)/manifest.json", String(decoding: data, as: UTF8.self)) }
        }
    }

    // MARK: 小工具

    private static func utilities(_ f: Fixtures, _ w: inout Writer) {
        // 會議紀錄的寬鬆解碼、JSON 字串、純文字、改名
        var decode: [Object] = []
        for input in f["notesDecode"] as? [String] ?? [] {
            let notes = try? JSONDecoder().decode(MeetingNotes.self, from: Data(input.utf8))
            decode.append(["input": input, "jsonString": notes?.jsonString() ?? NSNull(), "plainText": notes?.plainText ?? NSNull(),
                           "isEmpty": notes?.isEmpty ?? NSNull()])
        }
        for fx in f.meetings {
            if let notes = fx.notes {
                decode.append(["input": fx.key, "jsonString": notes.jsonString(), "plainText": notes.plainText, "isEmpty": notes.isEmpty])
            }
        }
        w.json("util/notes-decode.json", decode)
        var renames: [Object] = []
        for item in f["speakerRenames"] as? [Object] ?? [] {
            let old = item["old"] as? String ?? "", new = item["new"] as? String ?? ""
            guard let notes = f.meeting(item["notesKey"] as? String ?? "zh").notes else { continue }
            renames.append(["old": old, "new": new,
                            "speakerName": notes.replacingSpeakerName(old, with: new, isDefaultName: AppDatabase.isDefaultSpeakerName(old)).jsonString(),
                            "replacing": notes.replacing(old, with: new).jsonString()])
        }
        w.json("util/renames.json", renames)

        w.json("util/time-string.json", (f["timeStrings"] as? [Any] ?? []).map { ["seconds": $0, "text": TimeFormat.string(double($0))] })
        w.json("util/time-parse.json", (f["timeParse"] as? [String] ?? []).map {
            ["text": $0, "clean": TimeFormat.clean($0), "seconds": TimeFormat.seconds($0) ?? NSNull()] as Object
        })
        w.json("util/durations.json", (f["durations"] as? [Any] ?? []).map { value -> Object in
            let seconds = value as? Int
            return ["seconds": value, "zh": TimeFormat.duration(seconds, language: .zh), "en": TimeFormat.duration(seconds, language: .en),
                    "ja": TimeFormat.duration(seconds, language: .ja)]
        })
        w.json("util/subtitle.json", (f["subtitleTimes"] as? [Any] ?? []).map {
            ["seconds": $0, "srt": TimeFormat.subtitle(double($0), separator: ","), "vtt": TimeFormat.subtitle(double($0), separator: ".")]
        })
        w.json("util/weekday.json", (f["weekdayDates"] as? [String] ?? []).map { date -> Object in
            ["date": date, "zh": DateText.weekday(date, language: .zh), "en": DateText.weekday(date, language: .en),
             "ja": DateText.weekday(date, language: .ja),
             "dateLine": MeetingLanguage.allCases.map { ExportText($0).dateLine(date) }]
        })
        let today = DateText.date(f["today"] as? String ?? "2026-10-20") ?? Date()
        w.json("util/infer-date.json", (f["inferDates"] as? [Object] ?? []).map { item -> Object in
            let modified = (item["modified"] as? String).flatMap(DateText.date)
            return ["filename": item["filename"] ?? "", "modified": item["modified"] ?? NSNull(),
                    "date": Importer.inferDate(filename: item["filename"] as? String ?? "", modified: modified, today: today)]
        })
        w.json("util/safe-extension.json", (f["safeExtensions"] as? [String] ?? []).map {
            ["name": $0, "ext": Importer.safeExtension(URL(fileURLWithPath: "/tmp/" + $0))]
        })
        w.json("util/display-title.json", (f["displayTitles"] as? [[String]] ?? []).map { pair -> Object in
            let m = Meeting(title: pair[0], meetingDate: "2026-10-20", sourceFilename: pair[1])
            return ["title": pair[0], "sourceFilename": pair[1], "displayTitle": m.displayTitle]
        })
        w.json("util/participants.json", (f["participants"] as? [String] ?? []).map {
            ["text": $0, "list": Meeting(meetingDate: "", participants: $0, sourceFilename: "").participantList] as Object
        })
        w.json("util/default-speaker-names.json", (f["defaultSpeakerNames"] as? [String] ?? []).map {
            ["name": $0, "isDefault": AppDatabase.isDefaultSpeakerName($0)] as Object
        })
        w.json("util/transcript-import.json", (f["transcriptImports"] as? [Object] ?? []).map { item -> Object in
            let parsed = TranscriptImporter.parse(item["text"] as? String ?? "", ext: item["ext"] as? String ?? "")
            return ["ext": item["ext"] ?? "", "segments": parsed.segments.map { ["start": $0.start, "end": $0.end, "text": $0.text] }]
        })
        w.json("util/openai-url.json", (f["openaiURLs"] as? [String] ?? []).map { text -> Object in
            let url = OpenAICompatibleAPI.normalized(text)
            return ["text": text, "normalized": url?.absoluteString ?? NSNull(), "isLocal": url.map(OpenAICompatibleAPI.isLocal) ?? NSNull()]
        })
        w.json("util/extract-json.json", (f["extractJSON"] as? [String] ?? []).map { ["text": $0, "json": OpenAICompatibleAPI.extractJSON($0)] })
        let rejects = [(400, "response_format is not supported"), (422, "json_schema invalid"), (400, "bad request"), (500, "schema"), (400, "Schema mismatch")]
        w.json("util/rejects-schema.json", rejects.map {
            ["status": $0.0, "message": $0.1, "rejects": OpenAICompatibleAPI.rejectsSchema(APIRequest.Response(status: $0.0, object: [:], message: $0.1))] as Object
        })
        w.json("util/login-problem.json", (f["loginProblems"] as? [String] ?? []).map { ["text": $0, "isLogin": LLMError.isLoginProblem($0)] as Object })
        let vectors: [([Float], [Float])] = [([1, 0], [1, 0]), ([1, 0], [0, 1]), ([1, 2, 3], [3, 2, 1]), ([0, 0], [1, 1]), ([1], [1, 2]), ([], [])]
        w.json("util/similarity.json", vectors.map { ["a": $0.0, "b": $0.1, "similarity": Voiceprint.similarity($0.0, $0.1)] as Object })
        w.json("util/voiceprint-data.json", [["vector": [0.5, -1.25, 3.0], "base64": Voiceprint.data([0.5, -1.25, 3.0]).base64EncodedString()]])

        // 回傳結果的檢查
        if let item = f["validateAnswers"] as? Object, let answer = item["answer"],
           let data = try? JSONSerialization.data(withJSONObject: answer),
           let decoded = try? JSONDecoder().decode(AskPrompt.Answer.self, from: data) {
            let ids = Set((item["meetingIds"] as? [Int] ?? []).map(Int64.init))
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
            w.json("util/validate-answer.json", [
                "single": String(decoding: (try? encoder.encode(AskPrompt.validate(decoded, meetingIds: nil))) ?? Data(), as: UTF8.self),
                "all": String(decoding: (try? encoder.encode(AskPrompt.validate(decoded, meetingIds: ids))) ?? Data(), as: UTF8.self),
            ])
        }
        let zh = f.meeting("zh")
        let names = zh.speakerTable
        let unnamed = names.keys.sorted().filter { AppDatabase.isDefaultSpeakerName(names[$0]!) }
        let naming = SpeakerNaming.Context(language: .zh, meetingDate: "", title: "", participants: [], knownPeople: [], glossary: [],
                                           unnamed: unnamed, speakerName: { names[$0] ?? "" }, segments: [])
        let suggestions = (f["validateNames"] as? [Object] ?? []).map {
            SpeakerNaming.Suggestion(speaker: $0["speaker"] as? Int ?? 0, name: $0["name"] as? String ?? "",
                                     reason: $0["reason"] as? String ?? "", time: $0["time"] as? String ?? "")
        }
        w.json("util/validate-names.json", SpeakerNaming.validate(suggestions, context: naming).map {
            ["speaker": $0.speaker, "name": $0.name, "reason": $0.reason, "time": $0.time] as Object
        })
        let correctionContext = TranscriptCorrection.Context(language: .zh, title: "", participants: [], speakerNames: [], glossary: [],
                                                             segments: zh.segments.map { ($0.text, nil) })
        let raw = (f["validateCorrections"] as? [Object] ?? []).map {
            CorrectionSuggestion(segment: $0["segment"] as? Int ?? 0, original: $0["original"] as? String ?? "",
                                 suggestion: $0["suggestion"] as? String ?? "", reason: $0["reason"] as? String ?? "")
        }
        let valid = TranscriptCorrection.validate(raw, context: correctionContext, range: 0..<4)
        w.json("util/validate-corrections.json", valid.map { s -> Object in
            let text = zh.segments[s.segment].text
            let applied = TranscriptCorrection.apply(s, to: text)
            return ["segment": s.segment, "original": s.original, "suggestion": s.suggestion, "reason": s.reason,
                    "applied": applied ?? NSNull(), "reverted": applied.flatMap { TranscriptCorrection.revert(s, in: $0) } ?? NSNull()]
        })
    }

    // MARK: 轉錄流程的規則

    /// 跟 Windows 一樣的亂數：線性同餘，取高 16 位當 Int16
    private struct LCG {
        var state: UInt32
        mutating func next() -> Float {
            state = state &* 1_664_525 &+ 1_013_904_223
            return Float(Int16(truncatingIfNeeded: state >> 16)) / 32768
        }
    }

    private static func pipeline(_ f: Fixtures, _ w: inout Writer) {
        // 長錄音分段（design §21.16）
        if let c = f["chunkSignal"] as? Object {
            let rate = Int(AudioDecoder.sampleRate)
            var lcg = LCG(state: UInt32(c["seed"] as? Int ?? 1))
            let quietAt = (c["quietAt"] as? [Any] ?? []).map(double)
            let loud = Float(double(c["loud"])), quiet = Float(double(c["quiet"]))
            let count = (c["seconds"] as? Int ?? 0) * rate
            var samples = [Float](repeating: 0, count: count)
            for i in 0..<count {
                let t = Double(i) / Double(rate)
                let amplitude = quietAt.contains { abs(t - $0) < 0.4 } ? quiet : loud
                samples[i] = lcg.next() * amplitude
            }
            let chunks = WhisperEngine.chunks(samples, seconds: double(c["chunkSeconds"]))
            let none = WhisperEngine.chunks(samples, seconds: 0)
            let pairs: [[Int]] = chunks.map { [$0.lowerBound, $0.upperBound] }
            let nonePairs: [[Int]] = none.map { [$0.lowerBound, $0.upperBound] }
            let first: [Double] = samples.prefix(8).map { Double($0) }
            w.json("pipeline/chunks.json", ["chunks": pairs, "noChunks": nonePairs, "first": first] as Object)
        }
        // 語言偵測收集有聲音的片段（design §18.8）
        var collectors: [Object] = []
        for c in f["collectorSeeds"] as? [Object] ?? [] {
            let rate = Int(CaptureFormat.sampleRate)
            var lcg = LCG(state: UInt32(c["seed"] as? Int ?? 1))
            let from = double(c["voicedFrom"]), to = double(c["voicedTo"])
            let loud = Float(double(c["loud"])), quiet = Float(double(c["quiet"]))
            let count = (c["seconds"] as? Int ?? 0) * rate
            var samples = [Float](repeating: 0, count: count)
            for i in 0..<count {
                let t = Double(i) / Double(rate)
                samples[i] = lcg.next() * ((t >= from && t < to) ? loud : quiet)
            }
            final class Box: @unchecked Sendable { var value: [Float]? }
            let box = Box()
            var fed = 0
            let collector = LanguageDetector.Collector { box.value = $0 }
            var position = 0
            while position < samples.count && box.value == nil {
                let end = min(samples.count, position + 1234)
                collector.feed(Array(samples[position..<end]))
                fed = end
                position = end
            }
            let ready = box.value
            var sum = 0.0
            for s in ready ?? [] { sum += Double(s) }
            collectors.append(["seed": c["seed"] ?? 0, "ready": ready != nil, "count": ready?.count ?? 0, "fed": fed, "sum": sum])
        }
        w.json("pipeline/collector.json", collectors)

        // 簡體轉台灣繁體（design §32.5）：Windows 讀同一份字表、同一套最長匹配，要逐字相同
        w.json("util/s2tw.json", (f["s2twCases"] as? [String] ?? []).map { ["in": $0, "out": ChineseScript.toTaiwan($0)] as Object })

        // 更新說明的摘要（R10.6、design §12）：詢問更新的視窗只列前幾項，取段落、取條目的規則兩邊相同
        w.json("util/release-notes.json", (f["releaseNotesCases"] as? [Object] ?? []).map { c -> Object in
            let r = ReleaseNotes.brief(c["notes"] as? String ?? "", english: c["english"] as? Bool ?? false)
            return ["notes": c["notes"] as? String ?? "", "english": c["english"] as? Bool ?? false, "items": r.items, "more": r.more] as Object
        })

        w.json("pipeline/similar.json", (f["similarPairs"] as? [[String]] ?? []).map {
            ["a": $0[0], "b": $0[1], "similar": TranscriptionPipeline.similar($0[0], $0[1])] as Object
        })
        func segment(_ o: Object) -> TranscribedSegment {
            TranscribedSegment(start: double(o["start"]), end: double(o["end"]), text: o["text"] as? String ?? "", lowConfidence: false)
        }
        w.json("pipeline/echo.json", (f["echoCases"] as? [Object] ?? []).map { c -> Object in
            let s = segment(c["segment"] as? Object ?? [:])
            return ["isEcho": TranscriptionPipeline.isEcho(s, of: (c["system"] as? [Object] ?? []).map(segment))]
        })
        w.json("pipeline/assign.json", (f["assignCases"] as? [Object] ?? []).map { c -> Object in
            let turns = (c["turns"] as? [Object] ?? []).map {
                Diarizer.Turn(speaker: $0["speaker"] as? Int ?? 0, start: double($0["start"]), end: double($0["end"]))
            }
            let segments = (c["segments"] as? [Object] ?? []).map(segment)
            let (speakers, numbering) = Diarizer.assignNumbering(turns, to: segments)
            let speakerList: [Any] = speakers.map { (s: Int?) -> Any in s ?? NSNull() }
            return ["speakers": speakerList,
                    "numbering": Dictionary(uniqueKeysWithValues: numbering.map { (String($0.key), $0.value) })]
        })
        w.json("pipeline/quality.json", (f["qualityCases"] as? [Object] ?? []).map { c -> Object in
            let chunks = (c["chunks"] as? [Object] ?? []).map {
                ChunkEmbedding(speakerId: $0["speakerId"] as? String ?? "", chunkIndex: $0["chunkIndex"] as? Int ?? 0,
                               speakerIndex: $0["speakerIndex"] as? Int ?? 0, startTimeSeconds: 0, endTimeSeconds: 0,
                               embedding256: ($0["embedding"] as? [Any] ?? []).map { Float(double($0)) })
            }
            let q = Diarizer.quality(chunks)
            return ["collisionRate": q.collisionRate, "multiSpeakerChunks": q.multiSpeakerChunks, "cohesion": q.cohesion,
                    "clusters": q.clusters, "chunks": q.chunks, "mergeRisk": q.mergeRisk]
        })
        let risks: [Diarizer.Quality] = [
            .init(collisionRate: 0.6, multiSpeakerChunks: 12, cohesion: 0.9, clusters: 1, chunks: 40),
            .init(collisionRate: 0, multiSpeakerChunks: 0, cohesion: 0.48, clusters: 2, chunks: 40),
            .init(collisionRate: 0, multiSpeakerChunks: 0, cohesion: 0.48, clusters: 2, chunks: 29),
            .init(collisionRate: 0, multiSpeakerChunks: 0, cohesion: 1.2, clusters: 3, chunks: 100),
        ]
        w.json("pipeline/merge-risk.json", risks.map { ["mergeRisk": $0.mergeRisk, "hint": $0.mergeRisk >= Diarizer.Quality.hintThreshold] as Object })
        w.json("pipeline/rediarize.json", (f["rediarizeCases"] as? [Object] ?? []).map { c -> Object in
            let old = (c["old"] as? [Object] ?? []).map { o -> SpeakerName in
                var s = SpeakerName(meetingId: 1, idx: o["idx"] as? Int ?? 0, name: o["name"] as? String ?? "")
                s.nameSource = o["nameSource"] as? String ?? "default"
                s.personId = (o["personId"] as? Int).map(Int64.init)
                return s
            }
            let segments = (c["segments"] as? [Object] ?? []).enumerated().map { i, o in
                Segment(meetingId: 1, seq: i, start: double(o["start"]), end: double(o["end"]), text: "", lowConfidence: false,
                        speaker: o["speaker"] as? Int, speakerManual: o["manual"] as? Bool ?? false)
            }
            let newSpeakers = (c["newSpeakers"] as? [Any] ?? []).map { $0 as? Int }
            let mapping = JobRunner.oldToNew(segments: segments, newSpeakers: newSpeakers)
            let kept = zip(segments, newSpeakers).filter { !$0.0.speakerManual }
            let inherited = JobRunner.inheritNames(old: old, segments: kept.map(\.0), newSpeakers: kept.map(\.1))
            return ["oldToNew": Dictionary(uniqueKeysWithValues: mapping.map { (String($0.key), $0.value) }),
                    "inherited": Dictionary(uniqueKeysWithValues: inherited.map {
                        (String($0.key), ["name": $0.value.name, "source": $0.value.source, "personId": $0.value.personId ?? NSNull() as Any] as Object)
                    })]
        })
    }

    // MARK: 資料庫（R28.5）

    private static func database(_ f: Fixtures, _ w: inout Writer) throws {
        let dir = try Paths.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        let db = try AppDatabase(path: dir.appendingPathComponent("golden.sqlite").path)
        var ids: [String: Int64] = [:]
        func id(_ op: Object) -> Int64 { ids[op["key"] as? String ?? ""] ?? 0 }
        func segmentId(_ meetingId: Int64, seq: Int) throws -> Int64 {
            try db.segments(meetingId).first { $0.seq == seq }?.id ?? 0
        }
        for op in f["db"] as? [Object] ?? [] {
            let file = op["file"] as? String
            switch op["op"] as? String ?? "" {
            case "dumpSchema":
                let sql = try db.pool.read { d in
                    try String.fetchAll(d, sql: "SELECT sql FROM sqlite_master WHERE sql IS NOT NULL ORDER BY type, name")
                }
                w.text(file ?? "db/schema.sql", sql.joined(separator: ";\n") + ";\n")
            case "importFixture":
                let fx = f.meeting(op["key"] as? String ?? "")
                let meeting = try db.insertMeeting(fx.meeting, audioExtension: "m4a")
                let mid = meeting.id!
                ids[fx.key] = mid
                let segments = fx.segments.map { s -> Segment in var s = s; s.meetingId = mid; return s }
                try db.saveTranscript(mid, segments: segments, duration: fx.meeting.durationSeconds ?? 0,
                                      speakerCount: segments.compactMap(\.speaker).max() ?? 0, voices: fx.voices, transcribedWith: "breeze")
                for (idx, name) in fx.speakers.sorted(by: { $0.key < $1.key }) { try db.renameSpeaker(mid, idx: idx, name: name) }
                for b in fx.bookmarks { try db.addBookmark(mid, time: b.time, label: b.label) }
                if let notes = fx.notes { try db.saveNotes(mid, notes: notes, by: "claude", reason: "整理", source: .generated) }  // l10n-skip
                try db.updateUserNotes(mid, fx.meeting.userNotes)
                for term in fx.glossary { try db.addTerm(term) }
            case "dumpTables":
                var tables: Object = [:]
                for table in op["tables"] as? [String] ?? [] { tables[table] = try dumpTable(db, table) }
                w.json(file!, tables)
            case "search":
                var results: Object = [:]
                for query in op["queries"] as? [String] ?? [] {
                    results[query] = try db.search(query).map { hit -> Object in
                        ["meetingId": hit.meetingId, "kind": hit.kind.rawValue, "ref": hit.ref, "snippet": hit.snippet]
                    }
                }
                w.json(file!, results)
            case "glossarySuggestions":
                w.json(file!, try db.glossarySuggestions().map { s -> Object in
                    let source: String
                    switch s.source {
                    case .participant: source = "participant"
                    case .speaker: source = "speaker"
                    case .correction: source = "correction"
                    case .title: source = "title"
                    }
                    return ["term": s.term, "source": source]
                })
            case "learnVoiceprint":
                try db.learnVoiceprint(id(op), idx: op["idx"] as? Int ?? 0, name: op["name"] as? String ?? "")
            case "matchVoiceprints":
                w.json(file!, ["matched": try db.matchVoiceprints(id(op))])
            case "mergeSpeakers":
                let r = try db.mergeSpeakers(id(op), from: op["from"] as? Int ?? 0, into: op["into"] as? Int ?? 0, label: op["label"] as? String ?? "")
                w.json(file!, r.map { ["from": $0.from, "into": $0.into] as Object } ?? NSNull())
            case "undoSpeakerEdit":
                w.json(file!, ["undone": try db.undoSpeakerEdit(id(op))])
            case "reassignSegments":
                let mid = id(op)
                let segmentIds = try (op["seqs"] as? [Int] ?? []).map { try segmentId(mid, seq: $0) }
                let target = try db.reassignSegments(mid, ids: segmentIds, to: op["to"] as? Int, label: op["label"] as? String ?? "")
                w.json(file!, ["speaker": target])
            case "replace":
                let r = try db.replace(id(op), find: op["find"] as? String ?? "", with: op["with"] as? String ?? "")
                w.json(file!, ["segmentsChanged": r.segmentsChanged, "notesChanged": r.notesChanged])
            case "saveNotes":
                let source: AppDatabase.NotesSource = switch op["source"] as? String ?? "" {
                case "draft": .draft
                case "generated": .generated
                case "revised": .revised
                default: .edit
                }
                let notes = decodeNotes(op["notes"] ?? [:]) ?? MeetingNotes()
                let replaced = try db.saveNotes(id(op), notes: notes, by: "claude", reason: op["reason"] as? String ?? "", source: source)
                if let file { w.json(file, ["replacedEditedDraft": replaced]) }
            case "undoNotes":
                w.json(file!, ["undone": try db.undoNotes(id(op))])
            case "dismissReviewItems":
                try db.dismissReviewItems(id(op), indexes: Set(op["indexes"] as? [Int] ?? []))
            case "resolveReviewItems":   // 確認（是）和略過一起移除（R41.4，design §31.3）
                try db.resolveReviewItems(id(op), confirmed: Set(op["confirmed"] as? [Int] ?? []), dismissed: Set(op["dismissed"] as? [Int] ?? []))
            case "reviewLinks":   // 待確認的題目對應到哪一條紀錄（R41.1，design §31.1）：段落、位置、對到的題號
                let notes = try db.meeting(id(op))?.decodedNotes ?? MeetingNotes()
                let links = notes.reviewLinks().map { ref, questions -> (key: (Int, Int, Int), object: Object) in
                    (ref.sortKey, ["section": ref.sectionName, "index": ref.sortKey.1, "sub": ref.sortKey.2, "questions": questions.sorted()])
                }.sorted { $0.key < $1.key }.map(\.object)
                w.json(file!, links)
            case "saveCorrections":
                let mid = id(op)
                let items = try (op["items"] as? [Object] ?? []).map {
                    Correction(meetingId: mid, segmentId: try segmentId(mid, seq: $0["seq"] as? Int ?? 0), original: $0["original"] as? String ?? "",
                               suggestion: $0["suggestion"] as? String ?? "", reason: $0["reason"] as? String ?? "")
                }
                try db.saveCorrections(mid, items)
            case "acceptCorrection":
                let list = try db.corrections(id(op))
                w.json(file!, ["accepted": try db.acceptCorrection(list[op["index"] as? Int ?? 0].id!)])
            case "acceptCorrections":
                let list = try db.corrections(id(op))
                w.json(file!, ["notApplied": try db.acceptCorrections((op["indexes"] as? [Int] ?? []).map { list[$0].id! })])
            case "revertCorrection":
                let list = try db.corrections(id(op))
                w.json(file!, ["reverted": try db.revertCorrection(list[op["index"] as? Int ?? 0].id!)])
            case "rejectCorrections":
                let list = try db.corrections(id(op))
                try db.rejectCorrections((op["indexes"] as? [Int] ?? []).map { list[$0].id! })
            case "addTerm":
                try db.addTerm(op["term"] as? String ?? "")
            case "dismissGlossary":
                try db.dismissGlossarySuggestion(op["term"] as? String ?? "")
            case "enqueue":
                try db.enqueue(id(op), kind: JobKind(rawValue: op["kind"] as? String ?? "") ?? .transcribe, payload: op["payload"] as? String)
            case "claimNext":
                let job = try db.claimNext(lane: JobLane(rawValue: op["lane"] as? String ?? "") ?? .transcribe)
                w.json(file!, job.map { ["id": $0.id ?? 0, "kind": $0.kind.rawValue, "meetingId": $0.meetingId] as Object } ?? NSNull())
            case "requeueRunning":
                try db.requeueRunning()
            case "cancelJobs":
                w.json(file!, ["running": try db.cancelJobs(id(op))])
            case "setTrashed":
                try db.setTrashed(id(op), op["trashed"] as? Bool ?? false)
            case "rebuildSearchIndex":
                try db.rebuildSearchIndex()
            case "deleteMeeting":
                try db.deleteMeeting(id(op))
            default:
                throw LLMError.badOutput("unknown op \(op["op"] ?? "")")
            }
        }
    }

    /// 一張表的所有列：時間欄位（每次跑都不同）有值時寫成 "<time>"，BLOB 寫成 base64
    private static func dumpTable(_ db: AppDatabase, _ table: String) throws -> [Object] {
        let timeColumns: Set<String> = ["created_at", "updated_at", "started_at", "finished_at", "deleted_at"]
        let sql = table == "search" ? "SELECT rowid, meeting_id, kind, ref, text FROM search ORDER BY rowid"
            : "SELECT * FROM \(table) ORDER BY rowid"
        return try db.pool.read { d in
            try Row.fetchAll(d, sql: sql).map { row in
                var object: Object = [:]
                for (column, value) in row {
                    switch value.storage {
                    case .null: object[column] = NSNull()
                    case .int64(let n): object[column] = timeColumns.contains(column) ? "<time>" : n
                    case .double(let x): object[column] = x
                    case .string(let s):
                        object[column] = timeColumns.contains(column) ? "<time>" : ["snapshot", "definition"].contains(column) ? canonical(s) : s
                    case .blob(let data): object[column] = data.base64EncodedString()
                    }
                }
                return object
            }
        }
    }
}
#endif
