"""Only preview_exit is sent. This script never requests shutdown or restart."""
import asyncio
import importlib.util
import json
from pathlib import Path

root = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('animation', root / 'plugin/claw_sleep_animation.py')
animation = importlib.util.module_from_spec(spec); spec.loader.exec_module(animation)

async def main():
    path = animation._state_dir() / 'animation.jsonl'
    before = path.read_text(encoding='utf-8').splitlines()
    results = []
    for action in ('restart', 'shutdown'):
        result = await animation.power_action(action, preview=True)
        assert result == {'handled': True, 'action': action, 'preview': True}
        results.append(result)
        await asyncio.sleep(.8)
    events = [json.loads(line) for line in path.read_text(encoding='utf-8').splitlines()[len(before):]]
    assert not any(event['kind'] == 'power-exit-requested' for event in events)
    assert len([event for event in events if event['kind'] == 'power-exit-black' and event['data']['preview']]) == 2
    report = {'passed': True, 'previewOnly': True, 'osPowerRequests': 0, 'responses': results,
              'events': events, 'finalState': await animation.view()}
    (root / 'artifacts/startup-shield/exit-preview.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps({'passed': True, 'previewOnly': True, 'osPowerRequests': 0, 'events': events}))

asyncio.run(main())
