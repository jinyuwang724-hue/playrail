"""每个游戏的封面套件：从竖封面（600x900）和大背景（hero，无字）裁出墙上各种卡片要用的图。

用法：python3 -I art-kit.py <清单.json> <输出目录>
清单：[{"id": "...", "portrait": "竖封面路径", "hero": "背景图路径"}, ...]（缺哪张就留空）
输出：<输出目录>/<id>/{p,t,h,s,x}.webp 和 kit.json（焦点、主色）。

  p  竖卡（2x3、3x4）：竖封面 400x600
  t  细长竖卡（2x4，约 1:2）：竖封面在上，下方用封面底边模糊延展，标题写在延展区
  h  横卡（3x2 到 6x2）：背景图 1600 宽，前端按焦点决定 object-position
  s  小方卡（2x2）：背景图按焦点裁方 360
  x  大方卡（4x4、展开的 6x6）：同上 900
"""
import json, os, sys
from PIL import Image, ImageFilter, ImageOps, ImageStat

Q = 78


def focal_x(img: Image.Image) -> float:
    """背景图里"有东西"的水平位置：边缘强度 + 饱和度的列和，取最强的方窗中心，略偏向中间。"""
    w = 400
    small = img.convert('RGB').resize((w, max(1, round(img.height * w / img.width))))
    h = small.height
    edges = small.convert('L').filter(ImageFilter.FIND_EDGES)
    sat = small.convert('HSV').split()[1]
    e, s = edges.load(), sat.load()
    col = [sum(e[x, y] + 0.35 * s[x, y] for y in range(0, h, 2)) for x in range(w)]
    win = min(w, h)
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


def square(img, fx, size):
    side = min(img.width, img.height)
    left = min(max(0, round(fx * img.width - side / 2)), img.width - side)
    top = (img.height - side) // 2
    return img.crop((left, top, left + side, top + side)).resize((size, size), Image.LANCZOS)


def tall(portrait, w=320, h=650):
    top = portrait.resize((w, round(portrait.height * w / portrait.width)), Image.LANCZOS)
    ext_h = h - top.height
    out = Image.new('RGB', (w, h))
    out.paste(top, (0, 0))
    if ext_h > 0:
        strip = top.crop((0, top.height - max(24, ext_h // 3), w, top.height))
        ext = ImageOps.flip(strip).resize((w, ext_h + 40), Image.BILINEAR).filter(ImageFilter.GaussianBlur(16))
        ext = Image.blend(ext, Image.new('RGB', ext.size, (0, 0, 0)), 0.35)
        mask = Image.linear_gradient('L').resize((w, ext_h + 40))  # 上透明 → 下不透明
        mask = mask.point(lambda v: min(255, v * 4))
        out.paste(ext, (0, top.height - 40), mask)
    return out


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
        tall(p).save(f'{out}/t.webp', quality=Q, method=6)
        kit['p'] = kit['t'] = True
    if entry.get('hero'):
        h = Image.open(entry['hero']).convert('RGB')
        fx = focal_x(h)
        h.resize((1600, round(h.height * 1600 / h.width)), Image.LANCZOS).save(f'{out}/h.webp', quality=74, method=6)
        square(h, fx, 360).save(f'{out}/s.webp', quality=Q, method=6)
        square(h, fx, 900).save(f'{out}/x.webp', quality=76, method=6)
        kit.update(h=True, s=True, x=True, fx=fx, ha=round(h.width / h.height, 3), c=accent(h))
    elif kit:
        kit['c'] = accent(p)
    json.dump(kit, open(f'{out}/kit.json', 'w'))
    return kit


if __name__ == '__main__':
    entries = json.load(open(sys.argv[1]))
    for e in entries:
        print(e['id'], build(e, sys.argv[2]))
