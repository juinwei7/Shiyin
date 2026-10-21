#!/bin/bash
# 截虛擬機的畫面（UTM 的「Shiyin Windows」視窗，不會跳到前景）：shot.sh <輸出.png>
out="${1:?shot.sh <輸出.png>}"
id=$(swift - <<'SWIFT'
import CoreGraphics
let list = CGWindowListCopyWindowInfo([.optionAll], kCGNullWindowID) as! [[String: Any]]
if let w = list.first(where: { ($0[kCGWindowOwnerName as String] as? String) == "UTM" && ($0[kCGWindowName as String] as? String) == "Shiyin Windows" }) {
    print(w[kCGWindowNumber as String]!)
}
SWIFT
)
[ -n "$id" ] || { echo "找不到 UTM 的「Shiyin Windows」視窗" >&2; exit 1; }
exec screencapture -x -o -l "$id" "$out"
