"""Compare actual game screenshots captured through Computer Use."""
from pathlib import Path
import json
from PIL import Image, ImageChops, ImageStat
folder=Path(__file__).resolve().parents[1]/"artifacts/magpie-real-game"
base=Image.open(folder/"bicubic-1.png").convert("RGB")
result={"size":base.size,"region":[250,400,1000,740],"comparisons":{}}
for name in ("rcas","fsr","anime4k"):
    actual=Image.open(folder/(name+"-1.png")).convert("RGB")
    assert actual.size==base.size
    difference=ImageChops.difference(base.crop(tuple(result["region"])),actual.crop(tuple(result["region"])))
    result["comparisons"][name]={"meanAbsoluteRGB":ImageStat.Stat(difference).mean,"changed":difference.getbbox() is not None}
    assert result["comparisons"][name]["changed"]
(folder/"pixel-comparison.json").write_text(json.dumps(result,indent=2),encoding="utf-8")
print(json.dumps(result))
