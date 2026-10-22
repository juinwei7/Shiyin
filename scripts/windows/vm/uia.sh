#!/bin/bash
# 在虛擬機的桌面用 UI Automation 操作拾音：uia.sh <動作> [視窗] [名稱] [其他]（動作見 ps/uia.ps1）
HERE="$(cd "$(dirname "$0")" && pwd)"
q() { local s="${1//\'/\'\'}"; printf "'%s'" "$s"; }
line="& C:\\src\\scripts\\windows\\vm\\ps\\uia.ps1"
for a in "$@"; do line="$line $(q "$a")"; done
printf '%s\n' "$line" | "$HERE/insession.sh" 60
