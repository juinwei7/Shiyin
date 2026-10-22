# 打包 Windows 版（design-windows §14.1、D50）：dotnet publish（自帶 .NET 和 Windows App SDK）→ 放進 whisper 的 DLL → vpk pack
# → 安裝檔改名成 Shiyin-<版本>-win-<架構>-Setup.exe（官網認這個名字，docs/website.md §7）→ 寫更新說明並用更新金鑰簽章
#   pwsh scripts/windows/pack.ps1 -Version 3.6.0 -Arch x64 -Whisper build\whisper-x64 -Out dist\win-x64
# 簽章要環境變數 SHIYIN_UPDATE_KEY（更新金鑰的私鑰 PEM）；沒有時不簽（只能自己測，不能發布）
param(
    [Parameter(Mandatory)][string]$Version,
    [ValidateSet('x64', 'arm64')][string]$Arch = 'x64',
    [Parameter(Mandatory)][string]$Whisper,
    [string]$Out = "dist\win-$Arch",
    # 測試 app 內更新用：更新來源、公鑰改從環境變數 SHIYIN_UPDATE_SOURCE、SHIYIN_UPDATE_PUBLIC_KEY 讀（design-windows §14.2）。不能拿去發布
    [switch]$UpdateTest
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$publish = Join-Path ([System.IO.Path]::GetTempPath()) "shiyin-publish-$Arch"
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }

dotnet publish (Join-Path $root 'windows\src\Shiyin.App\Shiyin.App.csproj') -c Release -r "win-$Arch" --self-contained `
    -p:Version=$Version -p:WindowsAppSDKSelfContained=true -p:ShiyinUpdateTest=$($UpdateTest.IsPresent.ToString().ToLowerInvariant()) -o $publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish 失敗' }
if ($UpdateTest) { Write-Warning '測試更新的建置：更新來源、公鑰從環境變數讀，不能拿去發布' }
Copy-Item (Join-Path $Whisper '*.dll') $publish -Force
# 第三方授權（R27 R13.1）跟著 app 一起裝
if (-not (Test-Path (Join-Path $publish 'Third-Party Licenses.txt'))) { throw '安裝檔裡沒有 Third-Party Licenses.txt' }
# app 的資源表：少了它 XAML 一載入就當掉（EnableMsixTooling 沒開時 publish 不會帶）
if (-not (Test-Path (Join-Path $publish 'Shiyin.pri'))) { throw '安裝檔裡沒有 Shiyin.pri' }
# 同名的 onnxruntime.dll 只能有一份（sherpa-onnx 的），Windows App SDK 的 Windows ML 不能帶進來（D42）
$onnx = Get-ChildItem -Recurse $publish -Filter onnxruntime.dll
if ($onnx.Count -ne 1) { throw "onnxruntime.dll 應該只有一份：$($onnx.FullName -join ', ')" }

New-Item -ItemType Directory -Force -Path $Out | Out-Null
dotnet tool update -g vpk --version 1.2.161
# 捷徑不給 Velopack 建：它只能用固定的名稱，app 的安裝 hook 依介面語言建「拾音」或「Shiyin」（Shortcuts.cs）
vpk pack --packId ShiyinApp --packVersion $Version --packDir $publish --mainExe Shiyin.exe --packTitle '拾音' --packAuthors 'juinwei7' --shortcuts None `
    --icon (Join-Path $root 'windows\src\Shiyin.App\Assets\Shiyin.ico') --channel "win-$Arch" --outputDir $Out
if ($LASTEXITCODE -ne 0) { throw 'vpk pack 失敗' }
$setup = Get-ChildItem $Out -Filter '*-Setup.exe' | Select-Object -First 1
Move-Item $setup.FullName (Join-Path $Out "Shiyin-$Version-win-$Arch-Setup.exe") -Force

# 更新說明：版本、架構、每個套件檔的大小和 SHA-256，用更新金鑰簽（app 套用前驗證，§14.2）
# 一定要是陣列：第一版只有一個套件檔時是字串，@packages 會把它拆成一個一個字元
$packages = @(Get-ChildItem $Out -Filter '*.nupkg' | ForEach-Object { $_.FullName })
$manifest = Join-Path $Out "shiyin-update-win-$Arch.json"
dotnet run (Join-Path $root 'scripts\windows\update-key.cs') -- manifest $Version $Arch $manifest @packages
if ($env:SHIYIN_UPDATE_KEY) {
    dotnet run (Join-Path $root 'scripts\windows\update-key.cs') -- sign $manifest
} else {
    Write-Warning '沒有 SHIYIN_UPDATE_KEY：更新說明沒有簽章，這個版本不能發布'
}
Get-ChildItem $Out | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "$hash  $($_.Name)"
}
