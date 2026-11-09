"""Stage the OSD changes against the 0.17.58 Agent shipped in the native package."""
import argparse
import os
import py_compile
import shutil
import subprocess
from pathlib import Path


def replace_once(text, old, new):
    if text.count(old) != 1:
        raise ValueError(f"Unsupported Agent source near {old.splitlines()[0]!r}")
    return text.replace(old, new, 1)


def update_power(text):
    text = replace_once(
        text,
        "        self.overlay = _overlay_module.FixedOverlay(self.root)\n",
        "        self.overlay = _overlay_module.FixedOverlay(self.root)\n"
        "        self.osd_frame_cadence = _overlay_module.FrameCadence()\n",
    )
    text = replace_once(
        text,
        '                if self.settings["osd"]["enabled"] and self.settings["osd"]["renderer"] == "gamebar":\n'
        "                    result = await self._fast_trace_snapshot() or await self._frame_snapshot(fast=True)\n",
        '                if self.settings["osd"]["enabled"] and self.settings["osd"]["renderer"] == "gamebar":\n'
        "                    if not self.osd_frame_cadence.due():\n"
        "                        await asyncio.sleep(.05)\n"
        "                        continue\n"
        "                    result = await self._fast_trace_snapshot() or await self._frame_snapshot(fast=True)\n"
        "                    self.osd_frame_cadence.finish(self.frame_sample if result else {})\n",
    )
    text = replace_once(
        text,
        "                    await _sleep_module.wait_awake()\n"
        "                    continue\n"
        '                if self.settings["osd"]["enabled"] and self.settings["osd"]["renderer"] == "gamebar":\n',
        "                    self.osd_frame_cadence.reset()\n"
        "                    await _sleep_module.wait_awake()\n"
        "                    continue\n"
        '                if self.settings["osd"]["enabled"] and self.settings["osd"]["renderer"] == "gamebar":\n',
    )
    text = replace_once(
        text,
        "                await self.overlay.update(self.monitor_view())\n"
        "                await asyncio.sleep(.05)\n",
        "                settings = self.settings['osd']\n"
        "                now = time.monotonic()\n"
        "                target = self._osd_target_key()\n"
        "                frame = self.frame_sample if (self.monitor_key == target and self.frame_sample_at\n"
        "                    and now - self.frame_sample_at < FRAME_HOLD_SECONDS) else None\n"
        "                hardware = self.hardware_sequence if now - self.hardware_at < HARDWARE_HOLD_SECONDS else None\n"
        "                await self.overlay.refresh(settings, (settings, target, hardware, frame), self.monitor_view)\n"
        "                await asyncio.sleep(.05 if settings['enabled'] else .25)\n",
    )
    return replace_once(
        text,
        "    async def _sync_osd_display(self, previous, current):\n"
        "        effective = self.effective_osd()\n",
        "    async def _sync_osd_display(self, previous, current):\n"
        "        if any(previous.get(key) != current.get(key) for key in ('enabled', 'renderer', 'level')):\n"
        "            self.osd_frame_cadence.reset()\n"
        "        effective = self.effective_osd()\n",
    )


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("app", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    if args.app.resolve() == args.output.resolve():
        parser.error("stage to a separate directory")
    repo = Path(__file__).resolve().parents[1]
    args.output.mkdir(parents=True, exist_ok=True)
    power = args.output / "claw_power.py"
    power.write_text(update_power((args.app / "claw_power.py").read_text(encoding="utf-8")), encoding="utf-8")
    overlay = args.output / "claw_overlay.py"
    shutil.copyfile(repo / "plugin" / overlay.name, overlay)
    for path in (power, overlay):
        py_compile.compile(str(path), doraise=True)
    compiler = Path(os.environ["WINDIR"]) / "Microsoft.NET" / "Framework64" / "v4.0.30319" / "csc.exe"
    subprocess.run([
        str(compiler), "/nologo", "/codepage:65001", "/optimize+", "/unsafe",
        "/target:winexe", "/platform:x64", "/reference:System.Drawing.dll",
        "/reference:System.Windows.Forms.dll", "/reference:System.Web.Extensions.dll",
        f"/out:{args.output / 'ClawPerformanceOverlay.exe'}", str(repo / "native" / "PerformanceOverlay.cs"),
    ], check=True)


if __name__ == "__main__":
    main()
