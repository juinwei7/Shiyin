#!/bin/bash
# 在背景跑：Mac 的磁碟剩不到 10 GB 就停掉虛擬機（使用者的 Mac 和錄音優先）
STATE="${SHIYIN_VM:-$HOME/Library/Caches/ShiyinBuild-win/vm}"
while true; do
  free=$(df -k /System/Volumes/Data | awk 'NR==2 {print int($4/1048576)}')
  if [ "$free" -lt 10 ]; then
    osascript -e 'tell application "UTM" to stop virtual machine named "Shiyin Windows" by force'
    echo "$(date) stopped: free ${free} GB" >> "$STATE/diskguard.log"
    exit 0
  fi
  sleep 30
done
