"""Live non-elevated round-trip checks. Never suspends the machine."""
import asyncio
import importlib.util
import json
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("animation_client", ROOT / "plugin/claw_sleep_animation.py")
client = importlib.util.module_from_spec(spec)
spec.loader.exec_module(client)


def policy():
    text = subprocess.check_output(["powercfg", "/qh", "SCHEME_CURRENT", "SUB_BUTTONS", "PBUTTONACTION"], creationflags=subprocess.CREATE_NO_WINDOW).decode("mbcs")
    import re
    return [int(value, 16) for value in re.findall(r"0x([0-9a-fA-F]{8})", text)]


async def main():
    before = await client.view()
    assert before["available"] and before["effectiveButtonDc"] == "hibernate", before
    checks = []
    try:
        disabled = await client.set_enabled("suspend", False)
        assert not disabled["suspendEnabled"] and not disabled["active"] and policy() == [2, 2]
        checks.append("disable-restores-hibernate")
        enabled = await client.set_enabled("suspend", True)
        assert enabled["suspendEnabled"] and enabled["active"] and policy() == [0, 0]
        checks.append("enable-reclaims-button")
        wake = await client.set_enabled("resume", False)
        assert not wake["resumeEnabled"] and wake["suspendEnabled"] and policy() == [0, 0]
        checks.append("wake-switch-independent")
        wake = await client.set_enabled("resume", True)
        assert wake["resumeEnabled"]
        checks.append("wake-switch-enabled")
        for value in (0, 1, "true", None):
            try:
                await client.set_enabled("suspend", value)
            except ValueError:
                pass
            else:
                raise AssertionError("non-boolean value accepted")
        checks.append("invalid-values-rejected")
    finally:
        await client.set_enabled("suspend", before["suspendEnabled"])
        await client.set_enabled("resume", before["resumeEnabled"])
    result = {"passed": True, "checks": checks, "state": await client.view(), "actualSuspend": False}
    (ROOT / "artifacts/sleep-animation/settings-v2/control-verification.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps(result))


asyncio.run(main())
