"""Own Intel Endurance Gaming for one toolbox session; never govern FPS/TDP ourselves."""
import json
import os
from pathlib import Path

PROFILES = ("performance", "balanced", "battery")


class IntelEndurance:
    def __init__(self, call, data, ensure_worker):
        self.call, self.ensure_worker = call, ensure_worker
        self.path = Path(data) / "intel-endurance-session.json"
        self.original = Path(data) / "power-backup.json"
        self.state = {}

    def session(self):
        if not self.path.exists():
            return None
        try:
            record = json.loads(self.path.read_text(encoding="utf-8"))
            if not record["power"]["raw"] or record["driver"]["control"] not in ("off", "auto", "on"):
                raise ValueError("invalid session")
            return record
        except (KeyError, OSError, ValueError) as e:
            raise RuntimeError("Intel 功耗交接记录无法读取，已暂停调节") from e

    def persist(self, record):
        temp = self.path.with_suffix(".tmp")
        temp.write_text(json.dumps(record, ensure_ascii=False), encoding="utf-8")
        os.replace(temp, self.path)

    async def read(self):
        self.state = await self.call("endurance_read")
        return self.state

    async def restore_frequency(self, power):
        freq = power.get("freq") or {}
        current = await self.call("read")
        # Never apply saved caps to a different Windows power plan.
        if freq.get("scheme") and (current.get("freq") or {}).get("scheme") == freq["scheme"]:
            await self.call("set_freq", scheme=freq["scheme"],
                            **{k: freq[k] for k in ("ac", "dc", "ac1", "dc1") if freq.get(k) is not None})

    async def apply(self, profile):
        if profile not in PROFILES:
            raise ValueError("Intel 智能功耗档位无效")
        state = await self.read()
        if not state.get("available"):
            raise RuntimeError(state.get("error") or "本机不支持 Intel 智能功耗")
        if state.get("control") not in ("off", "auto"):
            raise RuntimeError("原 Intel 强制开启配置无法通过当前接口恢复，未做修改")
        if profile not in [p["id"] for p in state.get("profiles", [])]:
            raise RuntimeError("当前驱动不支持这个 Intel 智能功耗档位")
        record = self.session()
        if record and not record.get("ready"):
            # A failed/interrupted entry must recover before a fresh handoff.
            await self.release(restore_driver=True)
            state = await self.read()
            record = None
        if record is None:
            power = await self.call("read")
            if not power.get("raw") or not (power.get("freq") or {}).get("scheme"):
                raise RuntimeError("无法备份当前功耗设置，未启用 Intel 调节")
            record = {"power": power, "driver": state, "profile": profile, "ready": False}
            self.persist(record)  # durable before the first hardware/configuration write
            try:
                baseline = json.loads(self.original.read_text(encoding="utf-8")) if self.original.exists() else power
                await self.call("restore_tdp", raw=baseline["raw"])
                await self.call("set_freq", scheme=power["freq"]["scheme"], ac=0, dc=0, ac1=0, dc1=0)
                if state.get("framePacing") and state.get("conflicts", 0) & 1:
                    await self.call("frame_pacing_set", value=0)
                self.state = await self.call("endurance_set", control="auto", profile=profile)
                await self.ensure_worker()
                record["ready"] = True
                self.persist(record)
            except Exception as error:
                try:
                    await self.release(restore_driver=True)
                except Exception as recovery:
                    raise RuntimeError(f"Intel 功耗启用失败：{error}；恢复未完成：{recovery}") from error
                raise
        else:
            if state.get("control") != "auto":
                raise RuntimeError("Intel 自动调节已被其他程序关闭，请重新选择功耗模式")
            if record["profile"] != profile:
                self.state = await self.call("endurance_set", control="auto", profile=profile)
                record["profile"] = profile
                self.persist(record)
            elif state.get("profile") != profile:
                raise RuntimeError("Intel 功耗档位已被其他程序修改，请重新选择档位")
            await self.ensure_worker()
        return self.state

    async def release(self, restore_driver=False):
        record = self.session()
        if record is None:
            return
        state = await self.read()
        if not state.get("available"):
            raise RuntimeError(state.get("error") or "无法退出 Intel 智能功耗")
        # Stop Intel's automatic policy before restoring manual MSR/frequency values.
        if state.get("control") != "off":
            await self.call("endurance_set", control="off", profile=state["profile"])
        await self.call("restore_tdp", raw=record["power"]["raw"])
        await self.restore_frequency(record["power"])
        previous = record["driver"]
        if previous.get("framePacing") is not None:
            await self.call("frame_pacing_set", value=previous["framePacing"])
        if restore_driver:
            # This device exposes Off/Auto. Never turn an unsupported forced-On into Auto.
            if previous["control"] not in ("off", "auto"):
                raise RuntimeError("原 Intel 强制开启配置无法通过当前接口恢复")
            await self.call("endurance_set", control=previous["control"], profile=previous["profile"])
        self.path.unlink()
        await self.read()
