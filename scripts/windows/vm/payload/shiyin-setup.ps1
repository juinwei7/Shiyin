# 拾音 Windows 版的驗證用虛擬機：第一次登入時執行（autounattend.xml 的 FirstLogonCommands）
# 參數：這張光碟的磁碟機代號（例如 E:）
param([string]$Drive)
$ErrorActionPreference = 'Continue'
Start-Transcript -Path C:\shiyin-setup.log -Append

# 1. virtio 驅動程式：網路、亂數、序列埠、記憶體氣球、輸入、共用資料夾。顯示（viogpudo、qxl）不裝：UTM 說會黑畫面
foreach ($name in 'NetKVM', 'viorng', 'vioserial', 'Balloon', 'vioinput', 'viofs', 'fwcfg', 'qemufwcfg', 'pvpanic', 'smbus') {
    $dir = Join-Path $Drive "Drivers\$name"
    if (Test-Path $dir) { pnputil /add-driver "$dir\*.inf" /install }
}

# 2. OpenSSH Server（從 Mac 用金鑰登入，預設的 shell 是 PowerShell）
Start-Process msiexec.exe -ArgumentList '/i', "$Drive\OpenSSH-ARM64.msi", '/qn', '/norestart' -Wait
Set-Service sshd -StartupType Automatic
Start-Service sshd
New-Item -Path HKLM:\SOFTWARE\OpenSSH -Force | Out-Null
New-ItemProperty -Path HKLM:\SOFTWARE\OpenSSH -Name DefaultShell -Value "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -PropertyType String -Force | Out-Null
Copy-Item "$Drive\authorized_keys" C:\ProgramData\ssh\administrators_authorized_keys -Force
# 群組名稱會翻譯，用 SID：Administrators、SYSTEM
icacls C:\ProgramData\ssh\administrators_authorized_keys /inheritance:r /grant '*S-1-5-32-544:F' /grant '*S-1-5-18:F'
if (-not (Get-NetFirewallRule -Name 'shiyin-sshd' -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -Name 'shiyin-sshd' -DisplayName 'OpenSSH (Shiyin VM)' -Enabled True -Direction Inbound -Protocol TCP -LocalPort 22 -Action Allow -Profile Any | Out-Null
}
Restart-Service sshd

# 3. 不睡眠、不關螢幕、不鎖定（桌面的工作階段要一直在，才能開 app、截圖、按按鈕）
powercfg /hibernate off
powercfg /change standby-timeout-ac 0
powercfg /change monitor-timeout-ac 0
powercfg /change disk-timeout-ac 0
reg add 'HKLM\SOFTWARE\Policies\Microsoft\Windows\Personalization' /v NoLockScreen /t REG_DWORD /d 1 /f
reg add 'HKCU\Control Panel\Desktop' /v ScreenSaveActive /t REG_SZ /d 0 /f

# 4. 不自動更新（Mac 的磁碟空間不夠讓它下載好幾 GB）、不自動重開機
reg add 'HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU' /v NoAutoUpdate /t REG_DWORD /d 1 /f
reg add 'HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU' /v NoAutoRebootWithLoggedOnUsers /t REG_DWORD /d 1 /f
Stop-Service wuauserv -Force -ErrorAction SilentlyContinue
Set-Service wuauserv -StartupType Disabled

# 5. 建置用：長路徑；原始碼、套件的資料夾不即時掃毒（建置快很多）
reg add 'HKLM\SYSTEM\CurrentControlSet\Control\FileSystem' /v LongPathsEnabled /t REG_DWORD /d 1 /f
Add-MpPreference -ExclusionPath 'C:\src', "$env:USERPROFILE\.nuget", "$env:USERPROFILE\.dotnet" -ErrorAction SilentlyContinue

# 6. UTM 的工具（QEMU guest agent：SSH 連不上時 UTM 還能在裡面執行指令）
$tools = Get-ChildItem "$Drive\utm-guest-tools-*.exe" | Select-Object -First 1
if ($tools) { Start-Process $tools.FullName -ArgumentList '/S' -Wait }

Set-Content C:\shiyin-setup.done (Get-Date -Format o)
Stop-Transcript
