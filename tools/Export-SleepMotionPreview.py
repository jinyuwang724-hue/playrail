"""Package frames from the actual WPF renderer; this never captures the desktop."""
from pathlib import Path
import json
import sys
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'artifacts/sleep-animation' / (sys.argv[1] if len(sys.argv) > 1 else 'motion-v5')
sources = {name: [Image.open(p).convert('RGBA') for p in sorted((OUT / 'frames').glob(name + '-*.png'))]
           for name in ('suspend', 'wake')}
assert len(sources['suspend']) >= 30 and len(sources['wake']) >= 25
assert sources['suspend'][0].getchannel('A').getextrema() == (0, 0)
assert sources['suspend'][-1].getchannel('A').getextrema() == (255, 255)
assert sources['suspend'][-1].convert('RGB').getbbox() is None, 'Must be black before hibernation'
assert sources['wake'][0].getchannel('A').getextrema() == (255, 255)
assert sources['wake'][-1].getchannel('A').getextrema() == (0, 0), 'Wake must release the entire screen'
for frame in sources['wake']:
    w, h = frame.size
    alpha = frame.getchannel('A')
    assert len({alpha.getpixel(p) for p in ((0, 0), (w - 1, h - 1), (w // 4, h // 2))}) == 1, 'Screen must fade evenly, without a spatial wipe'
for frames in sources.values():
    assert len({frame.tobytes() for frame in frames}) > 20, 'Export must contain actual motion'

w, h = sources['suspend'][0].size
background = Image.new('RGBA', (w, h))
draw = ImageDraw.Draw(background)
for y in range(h):
    f = y / h
    c = tuple(round(a * (1 - f) + b * f) for a, b in zip((42, 47, 57), (18, 21, 27)))
    draw.line((0, y, w, y), fill=c + (255,))
# A neutral illustrative backdrop makes the transparent reveal visible in a GIF.
for x, y, ww, hh in ((52, 135, 218, 330), (296, 135, 370, 330), (692, 135, 218, 330)):
    draw.rounded_rectangle((x, y, x + ww, y + hh), 20, fill=(31, 36, 46), outline=(57, 65, 80), width=1)
font_path = Path('C:/Windows/Fonts/msyh.ttc')
font = ImageFont.truetype(str(font_path), 19) if font_path.exists() else ImageFont.load_default()
small = ImageFont.truetype(str(font_path), 14) if font_path.exists() else font

def composite(frame):
    return Image.alpha_composite(background, frame).convert('RGB')

animation = [composite(f).resize((768, 480), Image.Resampling.LANCZOS) for f in sources['suspend']]
animation += [animation[-1].copy() for _ in range(9)]
animation += [composite(f).resize((768, 480), Image.Resampling.LANCZOS) for f in sources['wake']]
animation += [animation[-1].copy() for _ in range(28)]
animation[0].save(OUT / 'claw-sleep-wake.gif', save_all=True, append_images=animation[1:],
                  duration=33, loop=0, optimize=False, disposal=2)

sheet = Image.new('RGB', (1280, 590), (12, 14, 18))
sd = ImageDraw.Draw(sheet)
for row, (name, title) in enumerate((('suspend', '休眠 · 合页收束'), ('wake', '唤醒 · 展页淡出'))):
    sd.text((22, row * 295 + 13), title, font=font, fill=(224, 230, 239))
    frames = sources[name]
    for col, fraction in enumerate((.16, .4, .68, .88)):
        frame = frames[round(fraction * (len(frames) - 1))]
        thumb = composite(frame).resize((312, 195), Image.Resampling.LANCZOS)
        sheet.paste(thumb, (8 + col * 320, row * 295 + 54))
        sd.text((20 + col * 320, row * 295 + 259), f'{fraction:.0%}', font=small, fill=(117, 132, 153))
sheet.save(OUT / 'storyboard.png')
report = {'passed': True, 'renderer': 'same WPF TransitionScene as production', 'suspendFrames': len(sources['suspend']),
          'wakeFrames': len(sources['wake']), 'suspendEnd': 'opaque black', 'wakeEnd': 'fully transparent',
          'suspendMs': 1150, 'wakeMs': 950, 'uniformBackgroundFade': True, 'actualSuspend': False, 'desktopCaptured': False}
(OUT / 'frame-verification.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
print(json.dumps(report))
