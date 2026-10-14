"""每个游戏的封面套件：从竖封面（600x900）和大背景（hero，无字）裁出墙上各种卡片要用的图。

用法：python3 -I art-kit.py <清单.json> <输出目录>
清单：[{"id": "...", "portrait": "竖封面路径", "hero": "背景图路径"}, ...]（缺哪张就留空）
输出：<输出目录>/<id>/{v,n,h,s,x,p}.webp 和 kit.json（焦点、主色）。

哪种卡用哪张图见 game-library-ui/封面尺寸规范.md：竖卡（宽高比 < 0.8）用竖封面，其余用无字的大背景。
  p  竖卡（2x3、3x4、2x4）：竖封面 400x600，前端 object-fit 裁
  h  横卡（3x2 到 6x2）：背景图 1600 宽，前端按焦点决定 object-position
  s  小方卡（2x2）：背景图按焦点裁方 360
  x  大方卡（4x4、展开的 6x6）：同上 900
  v / n  没有竖封面时的兜底：背景图按焦点裁 2:3（440x660）和约 1:2（320x650）
"""
import json, os, sys
from PIL import Image, ImageFilter

Q = 78


def focal_x(img: Image.Image, aspect: float = 1.0) -> float:
    """背景图里"有东西"的水平位置：边缘强度 + 饱和度的列和，取最强的窗口（宽 = 高 x aspect）中心，略偏向中间。"""
    w = 400
    small = img.convert('RGB').resize((w, max(1, round(img.height * w / img.width))))
    h = small.height
    edges = small.convert('L').filter(ImageFilter.FIND_EDGES)
    sat = small.convert('HSV').split()[1]
    e, s = edges.load(), sat.load()
    col = [sum(e[x, y] + 0.35 * s[x, y] for y in range(0, h, 2)) for x in range(w)]
    win = max(1, min(w, round(h * aspect)))
    best, bx = -1.0, 0
    run = sum(col[:win])
    for x in range(0, w - win + 1):
        if x:
            run += col[x + win - 1] - col[x - 1]
        cx = (x + win / 2) / w
        score = run * (1 - 0.25 * abs(cx - 0.5))
        if score > best:
            best, bx = score, x
    return round((bx + win / 2) / w, 3)


def crop(img, w, h):
    """按焦点从背景图裁出 w:h 的整块（高度用满），缩到 w x h。"""
    aspect = w / h
    cw = min(img.width, round(img.height * aspect))
    ch = round(cw / aspect)
    fx = focal_x(img, aspect)
    left = min(max(0, round(fx * img.width - cw / 2)), img.width - cw)
    top = (img.height - ch) // 2
    return img.crop((left, top, left + cw, top + ch)).resize((w, h), Image.LANCZOS)


def accent(img):
    c = img.convert('RGB').resize((24, 24)).convert('HSV')
    pixels = sorted(c.getdata(), key=lambda p: p[1] * p[2], reverse=True)[:60]
    hh = sorted(p[0] for p in pixels)[len(pixels) // 2]
    rgb = Image.new('HSV', (1, 1), (hh, 150, 150)).convert('RGB').getpixel((0, 0))
    return '#%02x%02x%02x' % rgb


def build(entry, out_root):
    out = os.path.join(out_root, entry['id'])
    os.makedirs(out, exist_ok=True)
    kit = {}
    if entry.get('portrait'):
        p = Image.open(entry['portrait']).convert('RGB')
        p.resize((400, 600), Image.LANCZOS).save(f'{out}/p.webp', quality=Q, method=6)
        kit['p'] = True
    if entry.get('hero'):
        h = Image.open(entry['hero']).convert('RGB')
        fx = focal_x(h)
        h.resize((1600, round(h.height * 1600 / h.width)), Image.LANCZOS).save(f'{out}/h.webp', quality=74, method=6)
        crop(h, 360, 360).save(f'{out}/s.webp', quality=Q, method=6)
        crop(h, 900, 900).save(f'{out}/x.webp', quality=76, method=6)
        crop(h, 440, 660).save(f'{out}/v.webp', quality=Q, method=6)
        crop(h, 320, 650).save(f'{out}/n.webp', quality=Q, method=6)
        kit.update(h=True, s=True, x=True, v=True, n=True, fx=fx, ha=round(h.width / h.height, 3), c=accent(h))
    elif kit:
        kit['c'] = accent(p)
    json.dump(kit, open(f'{out}/kit.json', 'w'))
    return kit


if __name__ == '__main__':
    entries = json.load(open(sys.argv[1]))
    for e in entries:
        print(e['id'], build(e, sys.argv[2]))
