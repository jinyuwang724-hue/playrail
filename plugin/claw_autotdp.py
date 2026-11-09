"""Claw's FPS feedback controller and durable ownership of its power settings.

Independent implementation of normalized FPS feedback, derivative damping and
steady-state power probing, as used by AutoTDP controllers. No HC source is
copied here. Hardware access stays in ClawPower; this module never launches HC.
"""
import json
import math
import os
from pathlib import Path


class AutoTDPGovernor:
    # PresentMon's entire averaging window must follow a successful power write.
    SETTLE_SECONDS = 3.0
    PROBE_SECONDS = 6.0

    def __init__(self):
        self.context = None
        self.limit = None
        self.applied_limit = None
        self.applied_at = -math.inf
        self.history = []
        self.previous = None
        self.probe_at = -math.inf
        self.probe_from = None
        self.probe_blocked_until = -math.inf
        self.reason = "等待游戏帧率"
        self.phase = "waiting"
        self.fps = None

    def configure(self, session, floor, ceiling, target, now):
        context = (session, floor, ceiling, target)
        if context == self.context:
            return
        self.__init__()
        self.context = context
        self.floor, self.ceiling, self.target = floor, ceiling, target
        self.limit = ceiling
        self.probe_at = now

    def applied(self, watts, now):
        if self.applied_limit != watts:
            self.applied_limit, self.applied_at = watts, now
            self.history = []

    def hold(self, reason):
        self.history, self.previous = [], None
        self.reason, self.phase, self.fps = reason, "waiting", None
        return self.limit

    def update(self, fps, sample_start, sample_end, now):
        if (not all(type(v) in (int, float) and math.isfinite(v) for v in (fps, sample_start, sample_end, now))
                or not 0 < fps <= 500 or not sample_start < sample_end <= now or now - sample_end > 4):
            return self.hold("帧率暂不可用 · 保持当前功耗")
        if self.previous and sample_end <= self.previous[0]:
            return self.hold("等待新帧率样本 · 保持当前功耗")
        self.fps = fps
        if self.applied_limit != self.limit:
            self.reason, self.phase = "等待功耗回读", "settling"
            return self.limit
        if sample_start < self.applied_at or now - self.applied_at < self.SETTLE_SECONDS:
            self.reason, self.phase = "等待功耗生效后的帧率", "settling"
            return self.limit

        slope = 0.0
        if self.previous:
            dt = sample_end - self.previous[0]
            if dt <= 5:
                slope = (fps - self.previous[1]) / max(dt, 0.5)
            else:
                self.history = []
        self.previous = (sample_end, fps)
        self.history = [(t, f) for t, f in self.history if sample_end - t <= 10] + [(sample_end, fps)]
        tolerance = max(1.5, self.target * 0.025)
        error = self.target - fps
        # Normalized error gives the same relative response at different FPS/TDP.
        # A rising frame rate damps the next increase. Recovery is deliberately
        # faster than lowering; no package-utilization gate or learned ceiling.
        correction = self.limit * (0.65 * error / max(fps, 5) - 0.06 * slope / self.target)
        if error > tolerance:
            recovering_probe = self.probe_from is not None and now - self.probe_at <= 12
            requested = self.limit + max(1, min(6, math.ceil(correction)))
            if recovering_probe:
                requested = max(requested, self.probe_from)
                self.probe_blocked_until = now + 30  # slows only down-probing; increases remain unrestricted
            self.probe_from = None
            self.limit = min(self.ceiling, requested)
            self.probe_at = now
            self.history = []
            self.phase = "ceiling" if self.limit == self.ceiling else "tracking"
            self.reason = "已到功耗上限 · 目标帧率暂未达到" if self.phase == "ceiling" else "低于目标 · 提高功耗"
        else:
            stable = (len(self.history) >= 3 and sample_end - self.history[0][0] >= self.PROBE_SECONDS
                      and all(f >= self.target - tolerance for _, f in self.history))
            if stable and now - self.probe_at >= self.PROBE_SECONDS and now >= self.probe_blocked_until:
                self.probe_from = self.limit
                self.limit = max(self.floor, self.limit - 1)
                self.probe_at = now
                self.history = []
                self.reason = "帧率稳定 · 小幅降低功耗" if self.limit > self.floor else "已到功耗下限"
            else:
                self.reason = "帧率达标 · 观察稳定性"
            self.phase = "tracking"
        return self.limit

    def view(self):
        return {"provider": "autotdp", "target": getattr(self, "target", None), "fps": self.fps,
                "limit": self.limit, "floor": getattr(self, "floor", None), "ceiling": getattr(self, "ceiling", None),
                "reason": self.reason, "phase": self.phase}


class AutoTDPSession:
    """Journal before any write; restore CPU caps/power across mode changes/crashes."""
    def __init__(self, call, data):
        self.call = call
        self.path = Path(data) / "autotdp-session.json"

    def record(self):
        if not self.path.exists():
            return None
        try:
            value = json.loads(self.path.read_text(encoding="utf-8"))
            if not value["power"]["raw"] or not isinstance(value["frequencies"], dict):
                raise ValueError("invalid backup")
            return value
        except (KeyError, TypeError, OSError, ValueError) as error:
            raise RuntimeError("Auto 功耗交接记录无法读取，已暂停调节") from error

    def persist(self, value):
        temp = self.path.with_suffix(".tmp")
        temp.write_text(json.dumps(value, ensure_ascii=False), encoding="utf-8")
        os.replace(temp, self.path)

    async def acquire(self):
        record = self.record()
        if record and not record.get("ready"):
            await self.release(restore_driver=True)
            record = None
        power = await self.call("read")
        driver = await self.call("endurance_read")
        if driver.get("available") and driver.get("control") not in ("off", "auto"):
            raise RuntimeError("Intel 强制开启配置无法安全交接到 Auto")
        if record and driver.get("available") and driver.get("control") != "off":
            raise RuntimeError("Intel 策略被其他程序开启，Auto 已暂停")
        freq = power.get("freq") or {}
        scheme = freq.get("scheme")
        if not power.get("raw") or not scheme or any(freq.get(k) is None for k in ("ac", "dc", "ac1", "dc1")):
            raise RuntimeError("无法备份功耗和 CPU 频率，未启用 Auto")
        entering = record is None
        if entering:
            record = {"power": power, "driver": driver, "frequencies": {}, "ready": False}
        # Back up each encountered power plan before clearing its caps.
        if scheme not in record["frequencies"]:
            record["frequencies"][scheme] = freq.copy()
            self.persist(record)
        try:
            if driver.get("available") and driver.get("control") != "off":
                await self.call("endurance_set", control="off", profile=driver["profile"])
            if any(freq[k] != 0 for k in ("ac", "dc", "ac1", "dc1")):
                cleared = await self.call("set_freq", scheme=scheme, ac=0, dc=0, ac1=0, dc1=0)
                if any((cleared.get("freq") or {}).get(k) != 0 for k in ("ac", "dc", "ac1", "dc1")):
                    raise RuntimeError("CPU 频率解除后回读不一致，未启用 Auto")
            if not record.get("ready"):
                record["ready"] = True
                self.persist(record)
        except Exception:
            if entering:
                await self.release(restore_driver=True)
            raise

    async def release(self, restore_driver=False):
        record = self.record()
        if record is None:
            return
        driver = await self.call("endurance_read")
        if driver.get("available") and driver.get("control") != "off":
            await self.call("endurance_set", control="off", profile=driver["profile"])
        await self.call("restore_tdp", raw=record["power"]["raw"])
        for scheme, original in record["frequencies"].items():
            restored = await self.call("restore_freq", scheme=scheme,
                                       **{k: original[k] for k in ("ac", "dc", "ac1", "dc1")})
            check = restored.get("restoredFreq") or {}
            if check.get("scheme") != scheme or any(check.get(k) != original[k] for k in ("ac", "dc", "ac1", "dc1")):
                raise RuntimeError("Auto 原 CPU 频率尚未恢复，已保留交接记录")
        previous = record["driver"]
        if restore_driver and previous.get("available"):
            if not driver.get("available"):
                raise RuntimeError("Intel 原配置尚未恢复，已保留 Auto 交接记录")
            if previous.get("control") != "off":
                await self.call("endurance_set", control=previous["control"], profile=previous["profile"])
        self.path.unlink()  # failed recovery retains the journal
