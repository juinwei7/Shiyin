<img src="../docs/images/icon.png" width="96" alt="Shiyin icon">

[繁體中文](../README.md) | **English** | Website: [shiyin.weibuilds.com](https://shiyin.weibuilds.com/en/)

# Shiyin 拾音

A meeting-notes app for macOS. Record a meeting (in person or online) and watch a live transcript as it happens. Afterwards Shiyin transcribes the recording on your Mac, identifies the speakers, and has Claude, Codex or an API write the meeting notes. Every item in the notes carries a timestamp: click it to hear what was actually said.

- Recording, transcription and speaker identification all run locally; recordings are never uploaded
- No Python, Homebrew or ffmpeg to install: download the DMG and go
- Interface in English and Traditional Chinese (follows your macOS language); meetings can be in English, Chinese or Japanese
- Requires Apple Silicon and macOS 26

![Meeting notes](../docs/images/en/summary.png)

## How it works

1. **Record or import**: click Record in the toolbar (⌘R). For online meetings, set the source to “Microphone + computer audio” so the other side is recorded too. Already have a recording? Drag it into the window.
2. **Follow along and take notes**: the live transcript is on the left. In “My notes” on the right, each new line is stamped with the recording time; click “Bookmark” (⌘B, or ⌃⌥⌘B from any app) at important moments.
3. **Stop and transcribe**: right after you stop, Shiyin drafts notes from the live transcript while it transcribes the recording again on your Mac and identifies the speakers. Click “Speaker 1” to change it to a name; Shiyin remembers the voice and fills in the name next time.
4. **Read the meeting notes**: the transcript is summarized into a summary, decisions, action items and open questions, each with a timestamp you can click to jump back into the recording. Names and terms the summarizer isn’t sure about become questions to confirm: the note in question gets a “?” next to it that you can click to answer right there, and the “N items to confirm” bar at the top of the notes lists them all. The notes are then revised from your answers (a “Yes” confirms on the spot, no waiting). To find out who said what, just ask in the Ask tab.
5. **Export and share**: save the notes as Markdown, PDF or Word, the transcript as subtitles, or package the whole meeting as a .shiyin file for a colleague.

## Features

- **Recording**
  - Sources: microphone only, microphone + computer audio (online meetings), or computer audio only. The other side of Zoom, Meet or Teams is recorded too
  - Live transcript and per-source level meters while recording; pause, bookmark and take notes
  - Sound check: the recording screen tells you when the microphone isn’t picking up any sound, the sound is too quiet, or the room is too noisy; when you are in another app you get a notification and the menu bar icon turns into a warning
  - In a crowded or noisy room, pick roughly how many people there are while recording or when importing, so speakers are told apart better
  - Recording continues with the main window closed; pause or stop from the menu bar, or press ⌃⌥⌘R in any app to start or stop
  - Plugging in or unplugging headphones, or switching output devices, while recording reconnects automatically and keeps recording
  - When Zoom, Teams, Google Meet or another online meeting starts using the microphone, Shiyin asks whether to record, and reminds you to stop when the meeting seems to be over. It only asks; it never records on its own
  - Press ⌃⌥⌘B in any app while recording to bookmark the moment, with a sound and a menu bar cue
  - Shiyin warns you when disk space is low; if the disk fills up, recording stops and everything recorded so far is kept and transcribed
  - If the app crashes or the Mac shuts down, the recording is recovered on next launch
- **Import**: audio and video (m4a, mp3, wav, aac, flac, caf, aiff, mp4, mov), transcripts (srt, vtt, txt), Markdown notes and whole-meeting packages (.shiyin). Drag into the window, press ⌘O, drop on the Dock icon, or use Open With in Finder
- **Local transcription**: whisper.cpp + Breeze ASR 25 (MediaTek), with your glossary as the prompt. In Settings you can switch to llama.cpp + Qwen3-ASR 1.7B (an open model by Alibaba that misses fewer words in meetings with many people; its output is converted to Traditional Chinese as used in Taiwan, and it all runs locally). Japanese meetings use Whisper large-v3-turbo (downloaded the first time you transcribe a Japanese meeting). Or use the transcriber built into macOS (no model download and much faster, but it ignores the glossary). Until the model has downloaded, meetings are transcribed with macOS built-in recognition, and you can transcribe them again with one click afterwards. Long recordings are transcribed in parts, so even a 4-hour meeting doesn’t take much memory
- **Meeting language**: each meeting is English, Chinese or Japanese, and the transcription, the notes and the export headings follow it. Japanese meetings are shown with Japanese glyphs, and PDF and Word exports use Japanese fonts. Set the default in Settings
- **Language detection**: about 10 seconds into a recording, Shiyin works out whether people are speaking Chinese, English or Japanese. If it’s confident and that differs from the meeting language, it switches the live transcript and the meeting language, and you can switch back from the recording screen
- **Transcript correction**: after transcription, the summarizer uses your glossary and the context to find words that were probably misheard (homophones, names) and marks them in the transcript. Nothing changes until you click Accept, and you can revert
- **Glossary suggestions**: terms to add to the glossary, taken from participants, speaker names, corrected words and meeting titles
- **Speaker identification**: every transcript segment is labeled with a speaker, each in its own color; rename speakers or show one speaker only
  - Voiceprints: once you name a speaker, Shiyin remembers that person’s voice and fills in the name in later meetings (marked “Auto”, easy to correct). Voiceprints stay on this Mac; view, delete or turn them off in Settings
  - The summarizer guesses speaker names from the conversation (“thanks for the update, Anna”) and lists them as suggestions; nothing changes until you click Apply, and renaming a speaker also updates the notes
  - Too few speakers found in a crowded, noisy or phone-across-the-table recording? Choose More → Identify Speakers Again… and pick roughly how many people there are; only who is speaking is worked out again, the transcript text stays the same
  - Easy to fix: if one person was split into two, click the speaker’s name and choose Merge Into…; if a few lines went to the wrong person, right-click the transcript and choose Who Said This Line? or Who Said These Lines?. Undo from More → Undo Speaker Changes; your fixes are kept when speakers are identified again
  - When Shiyin suspects different people were treated as one (crowded or noisy rooms), a note above the transcript suggests identifying speakers again
- **Meeting notes**
  - Templates: General meeting, One-on-one, Interview, Lecture / talk, Brainstorm, or your own
  - Summarizers: Claude Code, Codex CLI, Anthropic API, OpenAI API, Gemini API, an OpenAI-compatible API (Groq, OpenRouter, or Ollama and LM Studio on your Mac), or “Don’t summarize” for transcripts only
  - Only have the Codex desktop app (now called ChatGPT on the Mac)? That works too: Shiyin uses the codex bundled with it. If you only have the Claude desktop app, the welcome guide and Settings explain how to install Claude Code as well, with an install command you can copy
  - Right after you stop recording, Shiyin drafts notes from the live transcript (marked “Draft”) and replaces them when the final transcript is ready
  - Edit the notes directly; every change is versioned and can be reverted
- **Export and share**

  | What | Formats |
  |---|---|
  | Meeting notes | Markdown, PDF, Word; or copy as formatted text (for Word, Notion, email), Markdown or plain text |
  | Transcript | txt, Markdown, srt, vtt, Word (with or without timestamps and speakers) |
  | Recording | m4a, wav |
  | Whole meeting | .shiyin package (recording, transcript, speakers, notes, your notes, bookmarks); double-click to import |

  You can also share straight to AirDrop, Mail or Messages when exporting.
- **Ask your meetings**: ask a question in plain language in a meeting’s Ask tab (“What budget did we settle on?”) and get an answer with timestamps you can click. Ask in the toolbar searches all meetings, the current folder or the last 30 days, and tells you which meeting each answer came from
- **Organize**: folders (by project or client), full-text search across all meetings, Trash (restore within 30 days)
- **Also**: playback at 0.75×–2×, Fix Wording (corrects a term in the transcript and the notes at once), recordings compressed to m4a after transcription, notifications when a meeting is done or fails, in-app updates, Help → Report a Problem… (includes the version and setup, never meeting content)

![Transcript and speakers](../docs/images/en/transcript.png)

<sub>The demo meetings in these screenshots are excerpts from the [AMI Meeting Corpus](https://groups.inf.ed.ac.uk/ami/corpus/) (ES2004a, CC BY 4.0).</sub>

![Recording](../docs/images/en/recording.png)

## System requirements

| | Requirement |
|---|---|
| Chip | Apple Silicon (M1 or later) |
| System | macOS 26 or later |
| Memory | 8 GB or more. Transcription plus speaker identification peaks at about 2.9 GB. Long recordings are transcribed in parts, so meeting length makes little difference (a 4-hour meeting: 2.9 GB, measured on M3 Pro) |
| Disk space | About 1.7 GB of models (downloaded on first use; not needed with the macOS transcriber), another 574 MB for Japanese meetings, 60 MB for language detection and about 2.5 GB if you choose Qwen3-ASR, plus recordings: m4a is about 10–30 MB per hour |
| Summarizer | One of: a signed-in [Claude Code](https://claude.com/claude-code) (`claude`), Codex CLI (`codex`; the Codex desktop app alone works too), an Anthropic, OpenAI or Gemini API key, or an OpenAI-compatible API (Groq and OpenRouter need a key; Ollama and LM Studio on your Mac don’t). Or choose “Don’t summarize” |
| Internet | Needed to download the models and to summarize; not needed for recording, transcription or speaker identification |

## Install

### From the DMG

1. Get `Shiyin-<version>.dmg` (from the [website](https://shiyin.weibuilds.com/en/), this repo’s Releases, or from whoever maintains your copy), open it and drag Shiyin to Applications.
2. Shiyin isn’t notarized by Apple, so macOS blocks it the first time: go to System Settings → Privacy & Security and click Open Anyway (once). “Install Guide.pdf” in the DMG has the full steps.
3. On first launch, a guide walks you through allowing the microphone, downloading the transcription model (about 1.7 GB) and choosing a summarizer. You can skip any step and finish it later in Settings.
4. From 3.1.1 on, Shiyin checks this repo’s Releases for new versions and, after asking you, updates itself.

### Build from source

Requires Xcode 26 (Swift 6.2 or later). The first build uses Swift Package Manager to download GRDB, FluidAudio and the whisper.cpp XCFramework.

```bash
git clone https://github.com/juinwei7/Shiyin.git
cd Shiyin
scripts/make-signing-cert.sh --trust   # once: create and trust a self-signed certificate (asks for your login password) so permissions survive app updates
scripts/build-app.sh --install         # build the release app and install it to ~/Applications
```

Without `make-signing-cert.sh --trust` the build still works with an ad-hoc signature, but macOS may ask for permissions again after each rebuild.

## Windows

The Windows version lives in [`windows/`](../windows/) (C#, .NET 10, WinUI 3) and has the same features as Mac 4.2. Download it from the [website](https://shiyin.weibuilds.com/en/windows/) or this repo's Releases (tagged `win-vX.Y.Z`).

| Item | Requirement |
|---|---|
| System | Windows 11 (x64 or ARM64). N editions need the Media Feature Pack first |
| Memory | 8 GB or more |
| Disk space | Downloaded on first use: about 240 MB for Quick recognition, 45 MB for speaker identification, 1.7 GB for the transcription model; plus 574 MB for Japanese meetings, and about 2.5 GB if you choose Qwen3-ASR (including the llama.cpp runtime) |
| Graphics | Not required. On x64 PCs with an NVIDIA graphics card, Shiyin downloads GPU acceleration automatically (about 685 MB) and transcription runs on the GPU (a 40-minute meeting took about 8 minutes on an RTX 3060, about 30 minutes on the CPU alone). With AMD graphics (including Radeon integrated graphics), it downloads the Vulkan version of the acceleration (about 20 MB) and transcribes on the GPU as well. Intel graphics and ARM PCs use the CPU only |
| Performance | Fastest with an NVIDIA card: 4 GB of video memory or more recommended, driver 527 or later. On the CPU alone it's slower, and on a laptop it may take longer than the meeting itself; transcription uses up to 8 threads. In a hurry, set Settings → Recording & Transcription → Transcription to Quick recognition (much faster, less accurate). The live transcript while recording uses about a third of one CPU core |
| Summarizer | Same as on the Mac |

What’s different from the Mac version:

- **Installing**: run `Shiyin-<version>-win-x64-Setup.exe` (or the `win-arm64` one on ARM PCs). No administrator rights needed; it installs to `%LOCALAPPDATA%\ShiyinApp`. The installer isn’t code-signed: if “Windows protected your PC” appears, click “More info” → “Run anyway”. With Smart App Control on, it’s blocked outright; turn it off first in Windows Security → App & browser control. When a new version is out, Shiyin asks you in the app whether to update.
- **Computer audio**: Shiyin records everything played on the Windows default output device (Teams calls included), and follows along when you change the default device or unplug headphones. Playing a recording in Shiyin while recording gets recorded too (the Mac version leaves Shiyin’s own sound out).
- **The notification area** replaces the menu bar: closing the main window keeps Shiyin in the notification area, and recording and transcription carry on. Windows 11 tucks new icons under the “^” on the taskbar by default.
- **Shortcuts**: ⌘ becomes Ctrl. From any other app, Ctrl+Alt+Shift+R starts or stops recording and Ctrl+Alt+Shift+B adds a bookmark.
- **Recognition**: the live transcript and Quick recognition (which stands in for macOS built-in recognition, and is used until the transcription model is downloaded) use SenseVoice; speaker identification uses sherpa-onnx, so voiceprints don’t carry over from the Mac. .shiyin packages import on either side.
- **Data and privacy**: data is stored in `%LOCALAPPDATA%\Shiyin` (readable only by your Windows user), with settings in `settings.json` inside it; API keys are kept in Windows Credential Manager, never in files or logs. Uninstalling doesn’t delete your recordings or database. Everything else is as described in Data and privacy below.
- **Licenses**: it also uses sherpa-onnx, ONNX Runtime, SenseVoice, pyannote, 3D-Speaker, NAudio, Velopack, WinUIEx, the Windows App SDK and more. The full list and license texts are in About Shiyin and in `Third-Party Licenses.txt` in the install folder (generated by `scripts/windows/make-third-party-licenses.py`; rerun it after updating packages).

Building from source has to happen on Windows, with the .NET 10 SDK, Visual Studio Build Tools (C++, including the ARM64 tools), CMake and Git. ARM64 also needs LLVM (clang) and Ninja, and the Vulkan build for AMD graphics (`-Vulkan`) needs the Vulkan SDK:

```powershell
pwsh scripts/windows/build-whisper.ps1 -Arch x64 -Out build\whisper-x64
pwsh scripts/windows/pack.ps1 -Version 4.0.0 -Arch x64 -Whisper build\whisper-x64   # installer goes to dist\win-x64
```

The code that doesn’t touch the UI (database, prompts, summarizers, transcription pipeline) can be tested on a Mac too: `cd windows && dotnet test --project tests/Shiyin.Tests`.

## Enterprise server

A company can run a Shiyin enterprise server on its own machines (a separate project, not in this repo; the API between the app and the server is described in [`docs/design.md`](../docs/design.md) §36). Once employees sign in:

- **Transcription**: final transcription goes to the company’s server (a machine with an NVIDIA graphics card), so laptops don’t have to run the models. While it’s queued, Shiyin shows your place in line; if it can’t reach the server it waits, or you can choose Transcribe on This Computer on the meeting page
- **Summarizing**: choose “Company” as the summarizer to use the model your company set up, so nobody needs their own Claude, Codex or API key
- **Company glossary**: product and customer names maintained by the company, used together with your own glossary for transcription and summarizing (Settings → Speakers & Glossary)
- **Signing in**: in Settings → Advanced → Enterprise Server, enter the URL, click Sign In and use your company account; or open the join link your company sent you. After you sign out, Shiyin works exactly as before

It works on both Mac and Windows. Voiceprints stay on your own computer; the company’s dashboard only sees usage (how many minutes, how many times), never meeting content.

## Releasing

### With GitHub Actions (recommended)

1. Bump `CFBundleShortVersionString` in `Resources/Info.plist`, commit and push.
2. Push an annotated tag; its message becomes the release notes (used by the GitHub Release and the website’s release notes):

   ```bash
   git tag -a vX.Y.Z -F notes.txt
   git push origin vX.Y.Z
   ```

   `notes.txt` has a Chinese paragraph, a blank line and an English paragraph. The version in each title must match the tag, and every item starts with “- ” (the website build fails if the format is off):

   ```
   拾音 X.Y.Z：副標
   - 一行一條

   Shiyin X.Y.Z: Subtitle
   - One item per line, in the app’s own words
   ```

3. [`.github/workflows/release.yml`](workflows/release.yml) builds the DMG on a `macos-26` runner, creates a GitHub Release and uploads the DMG and `shiyin-update.json`. It fails if the tag doesn’t match the version.

You can also run the workflow by hand from the Actions page: it only builds, and the DMG is attached to that run as an artifact.

For Windows, push a `win-vX.Y.Z` tag (titles “拾音 Windows X.Y.Z” and “Shiyin for Windows X.Y.Z”): [`.github/workflows/release-windows.yml`](workflows/release-windows.yml) builds and packages on x64 and ARM64 Windows and creates a Release that is not marked latest (the Mac’s in-app updates read the latest release).

Settings (Settings → Secrets and variables → Actions, all optional):

| Name | Kind | Purpose |
|---|---|---|
| `SHIYIN_SIGNING_P12`, `SHIYIN_SIGNING_P12_PASSWORD` | Secret | Self-signed certificate. Set them from your Mac with `scripts/export-signing-cert.sh --to-github <owner/repo>` so CI signs the app the same way your Mac does; without them the app is signed ad hoc |
| `SHIYIN_UPDATE_URL` | Variable | Update URL built into the app |
| `SHIYIN_USAGE_URL` | Variable | Anonymous usage statistics URL built into the app (optional; no statistics are sent without it) |
| `SHIYIN_RELEASES_REPO`, `SHIYIN_RELEASES_TOKEN` | Variable, Secret | Also publish to a separate public repo (for keeping the code private while updates still work) |

### From your Mac

1. Bump `CFBundleShortVersionString` in `Resources/Info.plist`.
2. Build the DMG with the update URL and release notes:

   ```bash
   SHIYIN_UPDATE_URL=https://example.com/shiyin/shiyin-update.json \
   SHIYIN_RELEASE_NOTES="What changed" scripts/make-dmg.sh
   ```

   `dist/` then holds `Shiyin-<version>.dmg` and `shiyin-update.json` (version, DMG file name, SHA-256, release notes).
3. Put both files at the update URL (https only). Installed copies find the new version within 24 hours and, after asking, download, verify and relaunch.

With GitHub Releases, the update URL can be `https://github.com/juinwei7/Shiyin/releases/latest/download/shiyin-update.json`; upload both files with every release:

```bash
gh release create v3.1.1 dist/Shiyin-3.1.1.dmg dist/shiyin-update.json --notes "What changed"
```

Note: Release files in a private repo require signing in, so in-app updates fail. In that case put the two files somewhere public over https (an internal website, a NAS, etc.).

The app checks that a new version is signed the same way as the running app. After `make-signing-cert.sh --trust`, only versions signed with the same certificate can be installed as updates.

## Data and privacy

| What | Where it lives and goes |
|---|---|
| Recordings, transcripts, database, models | `~/Library/Application Support/Shiyin` (permissions 700, not synced by iCloud) |
| Recording, transcription, speaker identification | Entirely on your Mac, offline |
| Summarizing, revising, asking, guessing speaker names | The transcript text, your notes, the glossary and the meeting details go to the service you chose (Anthropic, OpenAI, Google, or the URL you entered for an OpenAI-compatible API). Asking all meetings only sends the relevant excerpts. Recordings are never sent |
| Voiceprints | Speakers’ voice features (a list of numbers, not audio) are kept only in the local database: never uploaded, never included in exported .shiyin packages. Delete them or turn them off in Settings → Speakers & Glossary |
| Enterprise server (only when signed in) | Recordings go to your company’s own server for transcription, not through any other cloud service, and are deleted once transcribed. With “Company” as the summarizer, the transcript goes to the company’s server and is summarized by the model the company chose. The Mac identifies speakers locally; on Windows the server does it and sends the voiceprints back, which are kept only on your PC. The sign-in token is stored in the Keychain (Credential Manager on Windows). The company’s dashboard only sees usage, never meeting content |
| Report a Problem | Only opens GitHub in your browser when you click “Report on GitHub”; you see the text and submit it yourself. It contains the version, your setup and the kinds of errors (plus the server version when you’re signed in to an enterprise server), never meeting content, file names, API keys, or the server URL or account |
| Anonymous usage statistics | Once a day, Shiyin sends its developer an anonymous summary: version, system, minutes recorded and transcribed, which features were used (including whether the enterprise server was used for transcription or summarizing), and how many tasks failed and why (for example not signed in or a wrong key, without the error messages), with a random anonymous ID. It never includes meeting titles, transcripts, notes, names, voiceprints, server URLs, company names or files. You can turn it off in Settings → Advanced |
| API keys | Stored in the macOS Keychain, never in files or logs |
| “Don’t summarize” with usage statistics turned off | Fully offline |
| OpenAI-compatible API on your Mac (Ollama, LM Studio) | Summarizing happens on this Mac too; nothing leaves your computer |

## Settings (⌘,)

| Tab | Contents |
|---|---|
| Summarizer | Claude, Codex, Anthropic API, OpenAI API, Gemini API, OpenAI-compatible API or Don’t summarize; sign-in status and Test Summarizer; API keys; the OpenAI-compatible API’s URL (with presets); model; whether to summarize and check for misheard words after transcription; templates (built-in and custom, default template) |
| Recording & Transcription | Recording source, microphone, permissions; default meeting language, language detection; transcription engine (Breeze, Qwen3-ASR or macOS), speaker identification, live transcript, download, pause or delete models; online meeting detection and apps not to ask about, global shortcuts (start/stop recording, bookmark) |
| Speakers & Glossary | Remember speakers’ voices (voiceprints), People Shiyin Knows: rename, delete, delete all; glossary: correct spellings of names, systems and terms, used for transcription and summarizing; suggested terms |
| Advanced | Enterprise server (sign in, sign out), update URL and Check for Updates, environment check, compress recordings, disk usage, show the welcome guide again, rebuild the search index, anonymous usage statistics, delete files left by version 2 |

## Development

The project follows spec-driven development: requirements, design and tasks come first, then code, and a task is done only after it has actually been run. The spec documents are written in Traditional Chinese.

| Document | Contents |
|---|---|
| [`docs/requirements.md`](../docs/requirements.md) | Requirements |
| [`docs/design.md`](../docs/design.md) | Architecture, data model, prompts, interface, decision log |
| [`docs/design-windows.md`](../docs/design-windows.md) | How the Windows version differs from the Mac, the golden files, the test setup |
| [`docs/website.md`](../docs/website.md) | Requirements and design of the website |
| [`docs/tasks.md`](../docs/tasks.md) | Tasks still to do |
| [`CLAUDE.md`](../CLAUDE.md) | Things to watch out for when developing and verifying |

### Project layout

```
Sources/Shiyin/
├── App/          entry point, menus, AppDelegate
├── Capture/      recording (microphone, system audio), file writing, live transcript
├── Transcribe/   decoding, whisper.cpp, macOS transcriber, speaker identification
├── Notes/        summarizers (CLI, API), prompts, templates
├── Model/        database records and meeting-notes structures
├── Store/        database (GRDB), AppModel
├── IO/           exports, .shiyin packages, transcript import
├── Services/     job queue, model downloads, updates, Keychain, global shortcut, notifications
├── Views/        SwiftUI views
└── Debug/        control channel for DEBUG builds (shiyinctl)
Sources/QwenShim/ thin C layer over llama.cpp (Qwen3-ASR)
Resources/        Info.plist, Localizable.xcstrings (English translations), icon, OpenCC tables
windows/          Windows version (C#, .NET 10, WinUI 3)
shared/golden/    golden files generated on the Mac; the Windows tests compare against them
site/             website (shiyin.weibuilds.com)
workers/usage/    backend for anonymous usage statistics (Cloudflare Workers)
scripts/          build, DMG, self-signed certificate, icon, dev tools; scripts/windows/ builds and packages the Windows version
docs/             specs
```

### Common commands

```bash
scripts/build-app.sh               # release build, produces build/Shiyin.app (actually in ~/Library/Caches/ShiyinBuild)
scripts/build-app.sh --debug       # DEBUG build with the control channel used for verification
scripts/make-dmg.sh                # release build plus DMG and shiyin-update.json
swift scripts/make-icon.swift Resources/AppIcon.icns   # regenerate the icon

# Control channel in DEBUG builds: trigger features, read state and take screenshots without touching the UI
swiftc scripts/dev/shiyinctl.swift -o ~/Library/Caches/ShiyinBuild/shiyinctl
~/Library/Caches/ShiyinBuild/shiyinctl state /tmp/state.json
~/Library/Caches/ShiyinBuild/shiyinctl snapshot /tmp/summary.png dark detail

scripts/dev/l10n.py check         # after changing UI text: extract strings, list missing English and untranslated Chinese
~/Library/Caches/ShiyinBuild/app/Shiyin.app/Contents/MacOS/Shiyin -AppleLanguages "(en)"   # launch with the English interface

/usr/bin/log stream --predicate 'subsystem == "com.weiwei.shiyin"'      # logs
sqlite3 ~/Library/Application\ Support/Shiyin/shiyin.sqlite             # data
```

Interface strings are written in Traditional Chinese in the code and looked up in `Resources/Localizable.xcstrings`, which holds the English translations.

## Known limitations

- Without an Apple Developer account the app isn’t notarized, so it has to be allowed manually the first time.
- The live transcript uses the system recognizer, which ignores the glossary and often gets technical terms wrong. The final transcription after recording uses the glossary.
- Accuracy: on a public Chinese meeting with real speakers (far-field microphone, 6 speakers), the character error rate is about 29% with Breeze, 25% with the macOS transcriber and 18% with the optional Qwen3-ASR, mostly from missed overlapping speech and filler words; the resulting meeting notes are still accurate. On read sentences in Taiwanese Mandarin mixed with English, both models are at about 2%. Meetings recorded on a laptop or headset microphone should do much better.
- Speaker identification: in short recordings where one person talks most of the time, people who speak little may be merged into someone else. If the speaker changes within a transcript segment, the whole segment gets one speaker; Qwen3-ASR’s segments (up to 15 seconds) are coarser than Breeze’s, so meetings where people talk over each other get a few more lines wrong.
- Summarizing with Claude usually takes 30–70 seconds, occasionally a few minutes.
- English meetings: on a public English meeting excerpt (AMI, 4 speakers), the word error rate is about 36% with Breeze, 34% with Qwen3-ASR and 37% with the macOS transcriber (about 33%, 31% and 34% without filler words), mostly words missed while people talk over each other.
- Japanese meetings: on a public expert hearing in the Japanese Diet (6 speakers), the character error rate with Whisper large-v3-turbo is about 8–9%. The reference is the official Diet record, which drops filler words, so the real error rate is lower.
- Not yet: languages other than English, Chinese and Japanese, a Japanese interface, calendar integration, an iPhone version.

## Open-source projects and models

| Name | Used for | License |
|---|---|---|
| [whisper.cpp](https://github.com/ggml-org/whisper.cpp) | Transcription (Breeze, Japanese), language detection | MIT |
| [Breeze ASR 25](https://huggingface.co/MediaTek-Research/Breeze-ASR-25) ([GGML conversion](https://huggingface.co/shdennlin/breeze-asr-25-ggml)) | Transcription model (Traditional Chinese, Chinese–English code-switching) | Apache-2.0 |
| [Whisper](https://github.com/openai/whisper) large-v3-turbo, base ([GGML conversion](https://huggingface.co/ggerganov/whisper.cpp)) | Japanese transcription, language detection | MIT |
| [Qwen3-ASR 1.7B](https://huggingface.co/Qwen/Qwen3-ASR-1.7B) ([GGUF conversion](https://huggingface.co/ggml-org/Qwen3-ASR-1.7B-GGUF)) | Optional transcription model (Chinese, English; output converted to Traditional Chinese with [OpenCC](https://github.com/BYVoid/OpenCC)’s tables) | Apache-2.0 |
| [llama.cpp](https://github.com/ggml-org/llama.cpp) | Runs Qwen3-ASR | MIT |
| [FluidAudio](https://github.com/FluidInference/FluidAudio) | Speaker identification | Apache-2.0 |
| [GRDB.swift](https://github.com/groue/GRDB.swift) | SQLite database, full-text search | MIT |

The transcription and speaker identification models aren’t in this repo; they are downloaded on first use. The app and the DMG include the full license terms of these packages: see the About Shiyin window, or `Third-Party Licenses.txt` in the DMG (generated by `scripts/dev/make-third-party-licenses.sh`; rerun it after updating dependencies).

## Support

Shiyin is a free, personal open-source project. If you find it useful, you can buy me a coffee on Ko-fi.

[![Buy me a coffee on Ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/weiwei_chang)

## License

[MIT](../LICENSE). The open-source projects and models above are under their own licenses.
