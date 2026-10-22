# 在登入的桌面工作階段裡執行一個 .ps1（design-windows §15.4）：從 SSH 開的程式不會出現在桌面、碰不到 UI Automation，
# 所以註冊一個「互動式登入」的排程工作去跑，輸出寫到 C:\xfer\tmp\<名稱>.out，跑完（或超過等待時間）才回來
#   in-session.ps1 <腳本.ps1> [等幾秒]
param([string]$Script, [int]$Timeout = 120)
$tmp = 'C:\xfer\tmp'
New-Item -ItemType Directory -Force $tmp | Out-Null
$name = [IO.Path]::GetFileNameWithoutExtension($Script)
$out = "$tmp\$name.out"; $done = "$tmp\$name.done"; $runner = "$tmp\$name.run.ps1"
Remove-Item $out, $done -ErrorAction SilentlyContinue
Set-Content $runner -Encoding UTF8 -Value "& '$Script' *> '$out'; Set-Content '$done' 1"
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$runner`""
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive
Register-ScheduledTask -TaskName ShiyinInSession -Action $action -Principal $principal -Force | Out-Null
Start-ScheduledTask -TaskName ShiyinInSession
$watch = [Diagnostics.Stopwatch]::StartNew()
while (-not (Test-Path $done) -and $watch.Elapsed.TotalSeconds -lt $Timeout) { Start-Sleep -Milliseconds 300 }
if (Test-Path $out) { Get-Content $out -Encoding UTF8 }
if (-not (Test-Path $done)) { "TIMEOUT" }
