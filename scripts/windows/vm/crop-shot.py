# 用法：python3 scripts/windows/vm/crop-shot.py <snapwin 截的 png> <輸出.png> [縮放，預設 1.5]
# 虛擬機的截圖：裁掉四周的桌面（150% 時上下左右 2 px、125% 時 1 px），長寬湊成偶數，
# 四角照 Windows 11 的圓角（8 DIP）改成透明（website.md §16.1）
import sys
from PIL import Image, ImageDraw
src, dst = sys.argv[1], sys.argv[2]
scale = float(sys.argv[3]) if len(sys.argv) > 3 else 1.5
im = Image.open(src).convert('RGBA')
w, h = im.size
edge = 2 if scale >= 1.5 else 1
box = [edge, edge, w - edge, h - edge]
if (box[2] - box[0]) % 2: box[2] -= 1
if (box[3] - box[1]) % 2: box[3] -= 1
# 縮成 960 寬時高度也要是偶數（make-images.sh 的規則）：不是的話上下各再裁 1 px
while ((box[3] - box[1]) * 960 + (box[2] - box[0]) // 2) // (box[2] - box[0]) % 2:
    box[1] += 1; box[3] -= 1
im = im.crop(tuple(box))
w, h = im.size
r = round(8 * scale)
s = 4
mask = Image.new('L', (w * s, h * s), 0)
ImageDraw.Draw(mask).rounded_rectangle((0, 0, w * s - 1, h * s - 1), radius=r * s, fill=255)
mask = mask.resize((w, h), Image.LANCZOS)
im.putalpha(Image.composite(im.getchannel('A'), Image.new('L', (w, h), 0), mask))
im.save(dst)
print(dst, im.size)
