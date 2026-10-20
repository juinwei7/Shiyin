#!/usr/bin/env python3
"""產生 windows/src/Shiyin.App/ThirdPartyLicenses.txt（design-windows §2、R27 R13.1）。

Windows 版安裝檔帶的套件、.NET、Windows App SDK、whisper.cpp 的授權條款原文，再列出第一次使用時下載的模型。
「關於拾音」和安裝資料夾的 Third-Party Licenses.txt 都是這份。

用法：python3 scripts/windows/make-third-party-licenses.py   （要有 dotnet；會先 restore app，套件放在 ~/.nuget/packages）
     python3 scripts/windows/make-third-party-licenses.py --check   （CI：只比對清單，不下載條文、不改檔案）
更新套件版本（windows/Directory.Packages.props）、whisper.cpp、onnxruntime 之後要重跑，並 commit 產生的檔案。
app 用到這裡沒列的套件時會停下來：在 GROUPS 加上它的授權從哪裡來。
"""
import json
import os
import re
import shutil
from functools import partial
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PROJECT = ROOT / "windows/src/Shiyin.App/Shiyin.App.csproj"
OUT = ROOT / "windows/src/Shiyin.App/ThirdPartyLicenses.txt"
NUGET = Path(os.environ.get("NUGET_PACKAGES") or Path.home() / ".nuget/packages")
DOTNET = os.environ.get("DOTNET") or shutil.which("dotnet") or str(Path.home() / ".dotnet/dotnet")

# 只在建置時用、不會進安裝檔的套件；sherpa-onnx 其他平台的原生檔（安裝檔只有 win-x64、win-arm64）
NOT_SHIPPED = re.compile(r"^(Microsoft\.Windows\.SDK\.BuildTools(\.MSIX)?|org\.k2fsa\.sherpa\.onnx\.runtime\.(android|linux|osx)-.*|org\.k2fsa\.sherpa\.onnx\.runtime\.win-x86)$")


def run(*args):
    return subprocess.run(list(args), check=True, capture_output=True, text=True).stdout


def fetch(url):
    try:
        return run("curl", "-fsSL", url)
    except subprocess.CalledProcessError:
        sys.exit(f"下載不到 {url}")


def raw(repo, ref, path):
    return fetch(f"https://raw.githubusercontent.com/{repo}/{ref}/{path}")


def packages():
    run(DOTNET, "restore", str(PROJECT))  # CI 上 app 還沒 restore 過時 list package 會失敗
    out = run(DOTNET, "list", str(PROJECT), "package", "--include-transitive", "--format", "json")
    result = {}
    for project in json.loads(out)["projects"]:
        for framework in project.get("frameworks", []):
            for p in framework.get("topLevelPackages", []) + framework.get("transitivePackages", []):
                result[p["id"]] = p["resolvedVersion"]
    return result


def package_dir(pid, version):
    d = NUGET / pid.lower() / version
    if not d.is_dir():
        sys.exit(f"找不到 {d}：先 dotnet restore")
    return d


def repository(pid, version):
    """nuspec 記的原始碼位置和 commit：(owner/repo, commit)"""
    text = next(package_dir(pid, version).glob("*.nuspec")).read_text(encoding="utf-8")
    tag = re.search(r"<repository\b[^>]*>", text)
    attrs = dict(re.findall(r'(\w+)="([^"]*)"', tag.group(0))) if tag else {}
    m = re.match(r"https://github\.com/([^/]+/[^/]+?)(?:\.git)?/?$", attrs.get("url", ""))
    if not m or not attrs.get("commit"):
        sys.exit(f"{pid} {version} 的 nuspec 沒有 GitHub 的 commit，在 GROUPS 改用別的來源")
    return m.group(1), attrs["commit"]


def package_file(pid, version, path):
    return (package_dir(pid, version) / path).read_text(encoding="utf-8-sig")


def whisper_commit():
    text = (ROOT / "scripts/windows/build-whisper.ps1").read_text(encoding="utf-8")
    return re.search(r"checkout --force ([0-9a-f]{40})", text).group(1)


def onnxruntime_version(sherpa_commit):
    text = raw("k2-fsa/sherpa-onnx", sherpa_commit, "cmake/onnxruntime-win-x64.cmake")
    return re.search(r"releases/download/v(\d+\.\d+\.\d+)/", text).group(1)


def dotnet_runtime_version():
    sdk = run(DOTNET, "--version").strip()
    props = Path(DOTNET).resolve().parent / "sdk" / sdk / "Microsoft.NETCoreSdk.BundledVersions.props"
    return re.search(r"<BundledNETCoreAppPackageVersion>([^<]+)<", props.read_text(encoding="utf-8")).group(1)


def check(header):
    """CI 用：commit 的授權檔開頭的清單要跟現在的套件版本一致（.NET 的修訂版跟著 SDK 變，不比）"""
    def listed(text):
        return [l for l in text.split("\n" + "=" * 80)[0].rstrip("\n").split("\n") if not l.startswith("- .NET ")]
    committed = OUT.read_text(encoding="utf-8") if OUT.exists() else ""
    if listed(committed) != listed(header):
        old, new = set(listed(committed)), set(listed(header))
        sys.exit("授權檔跟套件版本不一致，重跑 scripts/windows/make-third-party-licenses.py：\n"
                 + "\n".join([f"  少了：{l}" for l in listed(header) if l not in old] + [f"  多了：{l}" for l in listed(committed) if l not in new]))
    print("授權檔的清單跟套件版本一致")


def main():
    used = packages()
    claimed = set()

    def take(pattern, one_version=True):
        ids = sorted(i for i in used if re.fullmatch(pattern, i))
        if not ids:
            sys.exit(f"app 沒有用到 {pattern}：從 GROUPS 拿掉")
        claimed.update(ids)
        versions = sorted({used[i] for i in ids})
        if one_version and len(versions) != 1:
            sys.exit(f"{pattern} 有好幾個版本：{versions}")
        return ids[0], versions[0]

    groups = []  # (清單上的一行, [(段落標題, 取得條文的函式)])

    commit = whisper_commit()
    groups.append((f"whisper.cpp b5454 ({commit[:7]}) (MIT) https://github.com/ggml-org/whisper.cpp",
                   [(f"whisper.cpp {commit[:7]}: LICENSE", partial(raw, "ggml-org/whisper.cpp", commit, "LICENSE"))]))

    pid, version = take(r"org\.k2fsa\.sherpa\.onnx(\.runtime\.win-(x64|arm64))?")
    repo, sherpa = repository(pid, version)
    groups.append((f"sherpa-onnx {version} (Apache-2.0) https://github.com/k2-fsa/sherpa-onnx",
                   [(f"sherpa-onnx {version}: LICENSE", partial(raw, repo, sherpa, "LICENSE"))]))
    ort = onnxruntime_version(sherpa)
    groups.append((f"ONNX Runtime {ort} (MIT), included in sherpa-onnx https://github.com/microsoft/onnxruntime",
                   [(f"ONNX Runtime {ort}: LICENSE", partial(raw, "microsoft/onnxruntime", f"v{ort}", "LICENSE")),
                    (f"ONNX Runtime {ort}: ThirdPartyNotices.txt", partial(raw, "microsoft/onnxruntime", f"v{ort}", "ThirdPartyNotices.txt"))]))

    runtime = dotnet_runtime_version()
    take(r"System\.(Formats\.Cbor|IO\.Packaging|Numerics\.Tensors)", one_version=False)  # .NET 的套件，條文同 .NET
    groups.append((f".NET {runtime} (MIT) https://github.com/dotnet/runtime",
                   [(f".NET {runtime}: LICENSE.TXT", partial(raw, "dotnet/runtime", f"v{runtime}", "LICENSE.TXT")),
                    (f".NET {runtime}: THIRD-PARTY-NOTICES.TXT", partial(raw, "dotnet/runtime", f"v{runtime}", "THIRD-PARTY-NOTICES.TXT"))]))

    pid, version = take(r"Microsoft\.Data\.Sqlite(\.Core)?")
    repo, ref = repository(pid, version)
    groups.append((f"Microsoft.Data.Sqlite {version} (MIT) https://github.com/dotnet/efcore",
                   [(f"Microsoft.Data.Sqlite {version}: LICENSE.txt", partial(raw, repo, ref, "src/efcore/LICENSE.txt"))]))

    # SQLitePCLRaw 的 nuspec 沒有記 commit，原始碼庫也沒有 2.1.13 的 tag；套件標的是 Apache-2.0，附 Apache 的原文
    _, version = take(r"SQLitePCLRaw\..*")
    groups.append((f"SQLitePCLRaw {version} (Apache-2.0) https://github.com/ericsink/SQLitePCL.raw, with SQLite (public domain) https://sqlite.org/copyright.html",
                   [(f"SQLitePCLRaw {version}: Apache License 2.0", partial(fetch, "https://www.apache.org/licenses/LICENSE-2.0.txt"))]))

    for pattern, name, license_name, url, path in [
        (r"NAudio\.(Core|Wasapi)", "NAudio", "MIT", "https://github.com/naudio/NAudio", "LICENSE"),
        (r"DocumentFormat\.OpenXml(\.Framework)?", "Open XML SDK", "MIT", "https://github.com/dotnet/Open-XML-SDK", "LICENSE"),
        (r"Velopack", "Velopack", "MIT", "https://github.com/velopack/velopack", "LICENSE"),
        (r"WinUIEx", "WinUIEx", "MIT", "https://github.com/dotMorten/WinUIEx", "LICENSE"),
        (r"ZstdSharp\.Port", "ZstdSharp", "MIT", "https://github.com/oleg-st/ZstdSharp", "LICENSE"),
    ]:
        pid, version = take(pattern)
        repo, ref = repository(pid, version)
        groups.append((f"{name} {version} ({license_name}) {url}", [(f"{name} {version}: {path}", partial(raw, repo, ref, path))]))

    pid, version = take(r"OpenccNetLib")
    repo, ref = repository(pid, version)
    groups.append((f"OpenccNet {version} (MIT) https://github.com/laisuk/OpenccNet, with OpenCC dictionaries (Apache-2.0) https://github.com/BYVoid/OpenCC",
                   [(f"OpenccNet {version}: LICENSE", partial(raw, repo, ref, "LICENSE")),
                    (f"OpenccNet {version}: dicts/LICENSE.txt (OpenCC)", partial(package_file, pid, version, "contentFiles/any/any/dicts/LICENSE.txt"))]))

    pid, version = take(r"Microsoft\.Web\.WebView2")
    groups.append((f"Microsoft Edge WebView2 SDK {version} (Microsoft) https://aka.ms/webview2",
                   [(f"WebView2 SDK {version}: LICENSE.txt", partial(package_file, pid, version, "LICENSE.txt")),
                    (f"WebView2 SDK {version}: NOTICE.txt", partial(package_file, pid, version, "NOTICE.txt"))]))

    # Windows App SDK 分成好幾個套件，授權條款相同，NOTICE 有兩種：一樣的只附一次
    take(r"Microsoft\.WindowsAppSDK\..*", one_version=False)
    sdk = sorted(i for i in used if i.startswith("Microsoft.WindowsAppSDK."))
    texts, seen = [], {}
    for name in ("license.txt", "NOTICE.txt"):
        for i in sdk:
            path = package_dir(i, used[i]) / name
            if path.exists():
                text = path.read_text(encoding="utf-8-sig")
                if text not in seen:
                    seen[text] = True
                    texts.append((f"Windows App SDK: {i} {used[i]} {name}", lambda text=text: text))
    groups.append(("Windows App SDK (" + ", ".join(f"{i.removeprefix('Microsoft.WindowsAppSDK.')} {used[i]}" for i in sdk) + ") (Microsoft) https://github.com/microsoft/WindowsAppSDK", texts))

    unknown = sorted(i for i in used if i not in claimed and not NOT_SHIPPED.match(i))
    if unknown:
        sys.exit("這些套件還沒列授權從哪裡來，加進 GROUPS：" + ", ".join(f"{i} {used[i]}" for i in unknown))

    models = [
        "Breeze ASR 25 (Apache-2.0) https://huggingface.co/MediaTek-Research/Breeze-ASR-25, GGML q8_0 conversion https://huggingface.co/shdennlin/breeze-asr-25-ggml",
        "Whisper large-v3-turbo, base (MIT) https://github.com/openai/whisper, GGML conversions https://huggingface.co/ggerganov/whisper.cpp",
        "SenseVoice-Small (FunASR Model License, below) https://github.com/FunAudioLLM/SenseVoice, fine-tuned as WSYue-ASR sensevoice_small_yue (Apache-2.0) https://huggingface.co/ASLP-lab/WSYue-ASR, ONNX int8 conversion by sherpa-onnx",
        "Silero VAD (MIT) https://github.com/snakers4/silero-vad",
        "pyannote segmentation-3.0 (MIT) https://huggingface.co/pyannote/segmentation-3.0",
        "3D-Speaker ERes2Net base (Apache-2.0) https://www.modelscope.cn/models/iic/speech_eres2net_base_sv_zh-cn_3dspeaker_16k",
    ]
    funasr = "58830eca4012644aac0c3218c3ccc7d98f003fda"  # modelscope/FunASR 最後一次改 MODEL_LICENSE 的 commit（2026-10-21 查）
    model_texts = [(f"FunASR Model License ({funasr[:7]})", partial(raw, "modelscope/FunASR", funasr, "MODEL_LICENSE"))]

    bar = "=" * 80
    lines = [
        "拾音 Shiyin（Windows 版）使用了下列軟體，以下是各軟體附的授權條款原文。",
        "Shiyin for Windows uses the following software. Their license terms follow, verbatim.",
        "",
    ]
    lines += [f"- {title}" for title, _ in groups]
    lines += [
        "",
        "拾音第一次用到時會下載下列模型（不在安裝檔裡）：",
        "Shiyin downloads the following models when first needed (not included in the installer):",
        "",
    ]
    lines += [f"- {m}" for m in models]
    body = "\n".join(lines) + "\n"
    if "--check" in sys.argv:
        check(body)
        return
    for title, get in [t for _, texts in groups for t in texts] + model_texts:
        text = get().lstrip("\ufeff").replace("\r\n", "\n").rstrip("\n")
        body += f"\n\n{bar}\n{title}\n{bar}\n\n{text}\n"
    OUT.write_text(body, encoding="utf-8")
    print(f"已產生 {OUT.relative_to(ROOT)}（{body.count(chr(10))} 行）")


if __name__ == "__main__":
    main()
