"""Exercise a harmless native setting through the deployed RPC and restore it in finally.

Does not launch a game or start scaling. If LS is already running, applying each write
briefly reloads it using the same path as the settings page. Run only for live verification.
"""
import importlib.util
import json
import os
from pathlib import Path
import time

spec = importlib.util.spec_from_file_location("deploy", Path(__file__).with_name("Deploy-ScalingSettings.py"))
deploy = importlib.util.module_from_spec(spec); spec.loader.exec_module(deploy)
call = deploy.call


def wait_running(expected):
    deadline = time.monotonic() + 12
    while time.monotonic() < deadline:
        view = call("get_lossless_settings")
        if view["running"] == expected: return view
        time.sleep(.25)
    raise RuntimeError("Background status did not return to its previous state")


if __name__ == "__main__":
    before = call("get_lossless_settings")
    assert before["configured"] and before["editor"]["initialized"]
    target = next(p for p in before["editor"]["profiles"] if p["default"])
    index, original = target["index"], target["values"]["DrawFps"]
    report = Path(__file__).resolve().parents[1] / "artifacts/lossless-audit"
    report.mkdir(parents=True, exist_ok=True)
    native = Path(os.environ["LOCALAPPDATA"]) / "Lossless Scaling/Settings.xml"
    (report/"before-live-test.xml").write_bytes(native.read_bytes())
    try:
        result = call("save_lossless_settings", dict(revision=before["editor"]["revision"], globalValues={}, profiles=[dict(index=index, values={"DrawFps":not original})], remove=[]))
        assert not result.get("warning"), result.get("warning")
        applied = wait_running(before["running"])
        assert applied["editor"]["profiles"][index]["values"]["DrawFps"] is (not original)
        print("Native setting written, read back and background state restored")
    finally:
        current = call("get_lossless_settings")
        if current["editor"]["profiles"][index]["values"]["DrawFps"] != original:
            result = call("save_lossless_settings", dict(revision=current["editor"]["revision"], globalValues={}, profiles=[dict(index=index, values={"DrawFps":original})], remove=[]))
            assert not result.get("warning"), result.get("warning")
        restored = wait_running(before["running"])
        assert [p["values"] for p in restored["editor"]["profiles"]] == [p["values"] for p in before["editor"]["profiles"]]
        assert restored["editor"]["globalValues"] == before["editor"]["globalValues"]
        print("All native profile/global values restored; no game or scaling session started")
    (report/"live-verification.json").write_text(json.dumps(dict(installed=restored["installed"], running=restored["running"], profiles=len(restored["editor"]["profiles"]), profileFields=sum(len(g["fields"]) for g in restored["editor"]["profileSchema"]), applicationFields=sum(len(g["fields"]) for g in restored["editor"]["globalSchema"]), valueRoundtrip=True, originalValuesRestored=True),indent=2),encoding="utf-8")
