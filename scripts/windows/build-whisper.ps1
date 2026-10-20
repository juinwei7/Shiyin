# 建 Windows 的 whisper（design-windows §8、D46）：whisper.cpp 固定在 b5454（commit d1be6fd，跟 Mac 的 Package.swift 同一版），
# 加上拾音的 shim（windows/native/whisper），輸出 shiyin_whisper.dll、whisper.dll、ggml*.dll，並印出每個檔案的 SHA-256
#   pwsh scripts/windows/build-whisper.ps1 -Arch x64 -Out build\whisper-x64 [-Vulkan]
#   pwsh scripts/windows/build-whisper.ps1 -Arch arm64 -Out build\whisper-arm64
# 需要 Visual Studio Build Tools（C++，含 ARM64 的工具）、CMake、Git；-Vulkan 要先裝 Vulkan SDK
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
$platform = if ($Arch -eq 'arm64') { 'ARM64' } else { 'x64' }
$vulkanFlag = if ($Vulkan) { 'ON' } else { 'OFF' }
cmake -S (Join-Path $root 'windows\native\whisper') -B $build -A $platform -DWHISPER_SOURCE="$Source" -DSHIYIN_VULKAN=$vulkanFlag
cmake --build $build --config Release --parallel

New-Item -ItemType Directory -Force -Path $Out | Out-Null
$dlls = Get-ChildItem -Recurse -Path $build -Filter *.dll | Where-Object { $_.FullName -match '\\Release\\' }
foreach ($dll in $dlls) { Copy-Item $dll.FullName -Destination $Out -Force }
# ARM64 的 OpenMP（libomp140.aarch64.dll）沒用到：GGML_OPENMP 關掉了
Get-ChildItem $Out -Filter *.dll | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "$hash  $($_.Name)"
}
