# 重建 DEBUG 版的 app（先結束正在跑的），放進 whisper 的 DLL（C:\xfer\whisper-arm64，§15.2）。用 run.sh 或 ssh.sh 跑
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'; $env:Path = "$env:USERPROFILE\.dotnet;$env:Path"
Stop-Process -Name Shiyin -Force -ErrorAction SilentlyContinue
Set-Location C:\src\windows
dotnet build src\Shiyin.App\Shiyin.App.csproj -c Debug -r win-arm64 2>&1 | Select-String -Pattern ': error |個錯誤|個警告' | Select-Object -Last 8
dotnet build src\Shiyin.Ctl\Shiyin.Ctl.csproj -c Debug 2>&1 | Select-String -Pattern ': error ' | Select-Object -Last 4
Copy-Item C:\xfer\whisper-arm64\*.dll "$env:LOCALAPPDATA\ShiyinBuild\bin\Shiyin.App\debug_win-arm64\" -Force
