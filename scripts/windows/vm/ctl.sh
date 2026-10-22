#!/bin/bash
# 送指令給虛擬機裡 DEBUG 版的拾音（named pipe shiyin-debug，windows/src/Shiyin.Ctl；named pipe 不分工作階段，SSH 也連得到）
HERE="$(cd "$(dirname "$0")" && pwd)"
args=""
for a in "$@"; do args="$args '${a//\'/\'\'}'"; done
exec "$HERE/ssh.sh" "\$env:DOTNET_ROOT = \"\$env:USERPROFILE\\.dotnet\"; & \"\$env:LOCALAPPDATA\\ShiyinBuild\\bin\\Shiyin.Ctl\\debug\\shiyinctl.exe\" $args"
