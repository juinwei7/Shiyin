#!/bin/bash
# 在虛擬機登入的桌面執行一段 PowerShell（從標準輸入讀），等它跑完、印出輸出：insession.sh [等幾秒，預設 120] <<'PS' … PS
# 從 SSH 開的程式不會出現在桌面、碰不到 UI Automation，所以用排程工作在桌面的工作階段跑（ps/in-session.ps1，§15.4）
HERE="$(cd "$(dirname "$0")" && pwd)"
timeout="${1:-120}"
name="session-$$"
"$HERE/run.sh" <<PS
\$script = 'C:\\xfer\\tmp\\$name.ps1'
\$body = @'
$(cat)
'@
[IO.File]::WriteAllText(\$script, \$body, (New-Object Text.UTF8Encoding \$true))
& C:\\src\\scripts\\windows\\vm\\ps\\in-session.ps1 \$script $timeout
Remove-Item \$script -ErrorAction SilentlyContinue
PS
