# 建 Windows 的 whisper（design-windows §8、D46）：whisper.cpp 固定在 b5454（commit d1be6fd，跟 Mac 的 Package.swift 同一版），
# 加上拾音的 shim（windows/native/whisper），輸出 shiyin_whisper.dll、whisper.dll、ggml*.dll，並印出每個檔案的 SHA-256
#   pwsh scripts/windows/build-whisper.ps1 -Arch x64 -Out build\whisper-x64 [-Vulkan]
#   pwsh scripts/windows/build-whisper.ps1 -Arch arm64 -Out build\whisper-arm64
# 需要 Visual Studio Build Tools（C++，含 ARM64 的工具）、CMake、Git；ARM64 另外要 LLVM（clang）和 Ninja；-Vulkan 要先裝 Vulkan SDK
param(
    [ValidateSet('x64', 'arm64')][string]$Arch = 'x64',
    [string]$Out = "build\whisper-$Arch",
    [string]$Source = '',
    [switch]$Vulkan
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$work = Join-Path ([System.IO.Path]::GetTempPath()) "shiyin-whisper-$Arch"

if (-not $Source) {
    $Source = Join-Path $work 'whisper.cpp'
    if (-not (Test-Path $Source)) {
        git clone --filter=blob:none https://github.com/ggml-org/whisper.cpp $Source
    }
    # release b5454 的 commit（Mac 的 Package.swift 下載同一版的 XCFramework，W1 時在 Mac 上用同一份原始碼建過 dylib）
    git -C $Source fetch --force origin d1be6fde11ac6e0407606b4e42fe72d34add8037
    git -C $Source checkout --force d1be6fde11ac6e0407606b4e42fe72d34add8037
}

$build = Join-Path $work "build-$Arch"
$vulkanFlag = if ($Vulkan) { 'ON' } else { 'OFF' }
$native = Join-Path $root 'windows\native\whisper'
if ($Arch -eq 'arm64') {
    # ARM64 用 clang 建（同 whisper.cpp 官方的 Windows ARM64 版，design-windows §8）：MSVC 建 ARM64 一定要依建置的機器偵測 CPU 功能，
    # GitHub 的 ARM 機器是伺服器 CPU，偵測出的指令一般的 Snapdragon 筆電沒有。CPU 等級自己指定，只建一個版本
    $vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.ARM64 -property installationPath
    if (-not $vs) { throw '找不到有 ARM64 C++ 工具的 Visual Studio' }
    $hostArch = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'amd64' }
    Import-Module (Join-Path $vs 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll')
    Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments "-arch=arm64 -host_arch=$hostArch" | Out-Null
    $clang = Get-Command clang -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source
    if (-not $clang) { $clang = Join-Path $vs 'VC\Tools\Llvm\ARM64\bin\clang.exe' }
    if (-not (Test-Path $clang)) { throw '找不到 clang（LLVM）' }
    $clangxx = Join-Path (Split-Path $clang) 'clang++.exe'
    Write-Host "clang: $clang"
    cmake -S $native -B $build -G Ninja -DCMAKE_BUILD_TYPE=Release "-DCMAKE_C_COMPILER=$clang" "-DCMAKE_CXX_COMPILER=$clangxx" `
        -DCMAKE_C_COMPILER_TARGET=arm64-pc-windows-msvc -DCMAKE_CXX_COMPILER_TARGET=arm64-pc-windows-msvc `
        "-DWHISPER_SOURCE=$Source" "-DSHIYIN_ARM_ARCH=armv8.2-a+dotprod+fp16"
    if ($LASTEXITCODE -ne 0) { throw 'cmake 設定失敗' }
    cmake --build $build --parallel
} else {
    # -D名稱=$變數 要整串加雙引號：PowerShell 不會展開「-」開頭的參數裡的變數，cmake 拿到「$vulkanFlag」這串字當成開（2026-10-22 CI）
    cmake -S $native -B $build -A x64 "-DWHISPER_SOURCE=$Source" "-DSHIYIN_VULKAN=$vulkanFlag"
    if ($LASTEXITCODE -ne 0) { throw 'cmake 設定失敗' }
    cmake --build $build --config Release --parallel
}
if ($LASTEXITCODE -ne 0) { throw 'cmake 建置失敗' }

New-Item -ItemType Directory -Force -Path $Out | Out-Null
# Visual Studio 的建置放在 Release 資料夾；Ninja（ARM64）只有一種設定
$dlls = Get-ChildItem -Recurse -Path $build -Filter *.dll | Where-Object { $_.FullName -notmatch '\\CMakeFiles\\' -and ($Arch -eq 'arm64' -or $_.FullName -match '\\Release\\') }
foreach ($dll in $dlls) { Copy-Item $dll.FullName -Destination $Out -Force }
# ARM64 的 OpenMP（libomp140.aarch64.dll）沒用到：GGML_OPENMP 關掉了
# 執行階段要靜態連結（CMakeLists 的 CMAKE_MSVC_RUNTIME_LIBRARY）：匯入表裡有 VC++ 的 DLL 就不能發布
Get-ChildItem $Out -Filter *.dll | ForEach-Object {
    $text = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($_.FullName))
    if ($text -imatch '(vcruntime140(_1)?|msvcp140|vcomp140|libomp140[^.]*)\.dll') { throw "$($_.Name) 需要 $($Matches[0])，沒裝 VC++ 可轉散發套件的電腦會載入失敗" }
}
Get-ChildItem $Out -Filter *.dll | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "$hash  $($_.Name)"
}
