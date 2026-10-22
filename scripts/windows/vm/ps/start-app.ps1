# 在桌面開 DEBUG 版的拾音，資料放測試用的 C:\ShiyinTest（insession.sh 裡用）。DEBUG 版依賴框架，.NET 在使用者資料夾
#   start-app.ps1 [-ExtraArgs '--set interfaceLanguage=en']
param([string]$ExtraArgs = '')
$env:SHIYIN_HOME = 'C:\ShiyinTest'
$env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet"
$exe = "$env:LOCALAPPDATA\ShiyinBuild\bin\Shiyin.App\debug_win-arm64\Shiyin.exe"
if ($ExtraArgs) { Start-Process $exe -ArgumentList $ExtraArgs } else { Start-Process $exe }
"started $exe $ExtraArgs"
