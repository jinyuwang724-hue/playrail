"""Build a reversible, hash-bound local helper patch; no runtime Cecil dependency."""
import base64
import gzip
import hashlib
import json
from pathlib import Path

root = Path(__file__).resolve().parents[1]
original = (root / 'artifacts/repair-20261008-1655/XboxGamingBarHelper.exe').read_bytes()
patched = (root / 'artifacts/XboxGamingBarHelper-home.exe').read_bytes()
digest = lambda data: hashlib.sha256(data).hexdigest()
assert digest(original) == 'dbe62a3ea97a6dc30f069adce3d16bb5ae2bbfd0a44cf09ad45d18c7cf141088'
assert digest(patched) == 'a53c57719fe81888550ba29cc6e85af3c15ffdd50efe0d1b3f0d8c9b47566a83'

def changes(before, after):
    return [[offset, base64.b64encode(after[offset:offset+4096]).decode()]
            for offset in range(0, len(after), 4096)
            if before[offset:offset+4096] != after[offset:offset+4096]]

data = dict(originalHash=digest(original), patchedHash=digest(patched),
            originalLength=len(original), patchedLength=len(patched),
            forward=changes(original, patched), reverse=changes(patched, original))
for before, after, direction in [(original, patched, 'forward'), (patched, original, 'reverse')]:
    result = bytearray(before[:len(after)])
    result.extend(b'\0' * (len(after)-len(result)))
    for offset, encoded in data[direction]:
        block = base64.b64decode(encoded)
        result[offset:offset+len(block)] = block
    assert bytes(result) == after
target = root / 'plugin/resources/clawtweaks-home.patch.gz'
target.parent.mkdir(parents=True, exist_ok=True)
target.write_bytes(gzip.compress(json.dumps(data, separators=(',', ':')).encode(), mtime=0))
print(f'Verified reversible patch: {target.stat().st_size} bytes')
