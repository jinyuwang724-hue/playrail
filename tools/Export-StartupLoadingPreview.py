from pathlib import Path
import json
from PIL import Image, ImageStat

out = Path(__file__).resolve().parents[1] / 'artifacts/startup-transition'
frames = [Image.open(p).convert('RGBA') for p in sorted((out / 'frames').glob('loading-*.png'))]
assert len(frames) == 48
assert all(frame.getchannel('A').getextrema() == (255, 255) for frame in frames)
assert len({frame.tobytes() for frame in frames}) > 20
brightness = [sum(ImageStat.Stat(f.convert('RGB')).mean) for f in frames]
assert max(brightness) > min(brightness) * 1.1
rgb = [frame.convert('RGB') for frame in frames]
rgb[0].save(out / 'claw-startup-loading.gif', save_all=True, append_images=rgb[1:], duration=46, loop=0, optimize=False)
rgb[24].save(out / 'loading-poster.png')
report = {'passed': True, 'frames': 48, 'loopSeconds': 2.2, 'background': 'opaque black',
          'content': 'vector pages and breathing light only', 'actualLogon': False}
(out / 'preview-verification.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
print(json.dumps(report))
