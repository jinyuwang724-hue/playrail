"""Faster gamepad/desktop switching and a remembered user choice (0.17.58.3).

Usage: padmode.py <source dir with original files> <output dir>
"""
import sys
from pathlib import Path

src_dir, out_dir = Path(sys.argv[1]), Path(sys.argv[2])
out_dir.mkdir(parents=True, exist_ok=True)


def edit(name, pairs):
    text = (src_dir / name).read_text(encoding="utf-8")
    for old, new in pairs:
        if text.count(old) != 1:
            raise SystemExit(f"{name}: expected one match for:\n{old}")
        text = text.replace(old, new)
    (out_dir / name).write_text(text, encoding="utf-8")
    print("patched", name)


# ---------------------------------------------------------------- service
edit("claw_service.py", [
    # remembered choice
    ("        self._pad_choice_revision = 0\n",
     "        self._pad_choice_revision = 0\n"
     "        # The mode the user picked last (front ends, the pad's own toggle). Temporary switches\n"
     "        # (Game Bar hold, library in front) change the live mode but not this.\n"
     "        self._pad_choice = None\n"),
    # get_gamepad_state: expose chosen / held
    ("        state = await self._gamepad.read()\n"
     "        if self._input.desktop:\n"
     "            state[\"mode\"] = \"desktop\"\n"
     "        return state\n",
     "        state = await self._gamepad.read()\n"
     "        if self._input.desktop:\n"
     "            state[\"mode\"] = \"desktop\"\n"
     "        return self._with_choice(state)\n"
     "\n"
     "    def _chosen_mode(self, live=None):\n"
     "        if self._pad_choice in (\"xinput\", \"desktop\"):\n"
     "            return self._pad_choice\n"
     "        if self._input._settings.get(\"desktopRequested\"):\n"
     "            return \"desktop\"\n"
     "        return live if live in (\"xinput\", \"desktop\") else (\"desktop\" if self._input.desktop else \"xinput\")\n"
     "\n"
     "    def _with_choice(self, state):\n"
     "        live = state.get(\"mode\")\n"
     "        chosen = self._chosen_mode(live)\n"
     "        state[\"chosen\"] = chosen\n"
     "        state[\"held\"] = live in (\"xinput\", \"desktop\") and live != chosen\n"
     "        return state\n"
     "\n"
     "    async def toggle_gamepad_mode(self):\n"
     "        \"\"\"One call from a front end: switch away from the mode the user picked (not from a temporary hold).\"\"\"\n"
     "        target = \"xinput\" if self._chosen_mode() == \"desktop\" else \"desktop\"\n"
     "        await self._switch_pad_mode(target, True)\n"
     "        return {\"available\": True, \"mode\": \"desktop\" if self._input.desktop else \"xinput\",\n"
     "                \"chosen\": self._chosen_mode(), \"held\": False}\n"),
    # firmware writes from the gamepad page: desktop key cache may be stale now
    ("        await call(*args)\n"
     "        if self._input.desktop:\n"
     "            await self._input.use_desktop(True)\n",
     "        await call(*args)\n"
     "        self._input.forget_desktop_firmware()\n"
     "        if self._input.desktop:\n"
     "            await self._input.use_desktop(True)\n"),
    # set_gamepad_mode -> split into a fast switch + full state
    ("    async def set_gamepad_mode(self, mode, notify=True):\n"
     "        if notify: self._pad_choice_revision += 1\n",
     "    async def set_gamepad_mode(self, mode, notify=True):\n"
     "        await self._switch_pad_mode(mode, notify)\n"
     "        return await self.get_gamepad_state()\n"
     "\n"
     "    async def _switch_pad_mode(self, mode, notify=True):\n"
     "        started = time.perf_counter()\n"
     "        if notify: self._pad_choice_revision += 1\n"),
    ("            await self._gamepad.set_mode(\"xinput\", False)\n"
     "                await self._input.set_pad_mode(\"xinput\")\n"
     "                await self._input.use_desktop(True, True)\n",
     "            await self._gamepad.set_mode(\"xinput\", False)\n"
     "                await self._input.set_pad_mode(\"xinput\")\n"
     "                await self._input.use_desktop(True, True, fast=True)\n"),
    ("        else:\n"
     "            await self._input.use_desktop(False, False)\n"
     "            await self._gamepad.set_mode(\"xinput\", False)\n"
     "            await self._input.set_pad_mode(\"xinput\")\n"
     "        if notify:\n"
     "            await self._notifications.mode(mode)\n"
     "            await self._emit(\"gamepad_mode\", {\"mode\": mode, \"notify\": True})\n"
     "        return await self.get_gamepad_state()\n",
     "        else:\n"
     "            await self._input.use_desktop(False, False, fast=True)\n"
     "            await self._gamepad.set_mode(\"xinput\", False)\n"
     "            await self._input.set_pad_mode(\"xinput\")\n"
     "        if notify:\n"
     "            self._pad_choice = mode\n"
     "            # The notice is cosmetic: do not hold the caller (and its button) for it.\n"
     "            asyncio.create_task(self._notifications.mode(mode))\n"
     "            await self._emit(\"gamepad_mode\", {\"mode\": mode, \"notify\": True, \"chosen\": mode})\n"
     "        logging.info(\"Pad mode %s (notify=%s) in %d ms\", mode, notify, (time.perf_counter() - started) * 1000)\n"),
    # the widget shows the picked mode, not a temporary Game Bar hold
    ("                \"gamepad\": {\"mode\": mode, \"available\": mode in (\"xinput\", \"desktop\")}}\n",
     "                \"gamepad\": {\"mode\": mode, \"available\": mode in (\"xinput\", \"desktop\"),\n"
     "                            \"chosen\": self._chosen_mode(mode)}}\n"),
    # what the user had picked before this agent started
    ("        await self._input.start(pad_mode=mode)\n",
     "        await self._input.start(pad_mode=mode)\n"
     "        if self._pad_choice is None:\n"
     "            self._pad_choice = \"desktop\" if (mode == \"desktop\" or self._input._settings.get(\"desktopRequested\")) else \"xinput\"\n"),
    # the pad's own mode toggle is a user choice too
    ("        if notify:\n"
     "            await self._notifications.mode(shown_mode)\n"
     "        await self._emit(\"gamepad_mode\", {\"mode\": shown_mode, \"previous\": previous, \"notify\": notify,\n",
     "        if notify:\n"
     "            self._pad_choice = shown_mode\n"
     "            await self._notifications.mode(shown_mode)\n"
     "        await self._emit(\"gamepad_mode\", {\"mode\": shown_mode, \"previous\": previous, \"notify\": notify,\n"),
])

# ---------------------------------------------------------------- agent method list
edit("claw_agent.py", [
    ("    \"get_gamepad_state\", \"set_gamepad_mode\", ",
     "    \"get_gamepad_state\", \"set_gamepad_mode\", \"toggle_gamepad_mode\", "),
    # A suspended Game Bar widget stops reading; its sends wait EVENT_SEND_TIMEOUT (2 s) before it is closed.
    # Callers (mode switch, input layer publish) used to wait for that too: a pad mode switch took 4 s.
    ("        # A suspended renderer must not stop hardware replies or updates to other clients.\n"
     "        await asyncio.gather(*(send(connection) for connection in list(self.connections)))\n",
     "        # A suspended renderer must not stop hardware replies, updates to other clients, or the caller:\n"
     "        # each send runs on its own, in order per connection (write happens when the task starts).\n"
     "        for connection in list(self.connections):\n"
     "            task = asyncio.create_task(send(connection))\n"
     "            self._event_tasks.add(task)\n"
     "            task.add_done_callback(self._event_tasks.discard)\n"),
    ("        self.token = token or secrets.token_hex(32)\n",
     "        self.token = token or secrets.token_hex(32)\n"
     "        self._event_tasks = set()\n"),
])

# ---------------------------------------------------------------- input layer
edit("claw_input_layer.py", [
    ("    async def use_desktop(self, enabled, remember=None):\n"
     "        previous = self.desktop\n",
     "    def forget_desktop_firmware(self):\n"
     "        self._desktop_firmware_known = False\n"
     "\n"
     "    def _queue_firmware(self):\n"
     "        \"\"\"Firmware tweaks for the new layout (stick dead zones, M1/M2, key maps) after the switch is live.\"\"\"\n"
     "        previous = getattr(self, \"_firmware_task\", None)\n"
     "\n"
     "        async def run():\n"
     "            if previous is not None and not previous.done():\n"
     "                await asyncio.gather(previous, return_exceptions=True)\n"
     "            try:\n"
     "                async with self._lock:\n"
     "                    if self._phase == \"active\" and self._process:\n"
     "                        await self._take_firmware()\n"
     "            except Exception as error:\n"
     "                self._log(\"Input layer firmware update failed: %s\" % error)\n"
     "        self._firmware_task = asyncio.create_task(run())\n"
     "\n"
     "    async def use_desktop(self, enabled, remember=None, fast=False):\n"
     "        previous = self.desktop\n"),
    ("        if enabled and self._gamepad:\n"
     "            state = await self._gamepad.read()\n"
     "            self._desktop_buttons = state.get(\"desktopKeys\") or {}\n"
     "            self._desktop_sticks = state.get(\"desktopSticks\") or {}\n"
     "            self._desktop_maps = state.get(\"desktopStickMappings\") or {}\n"
     "        async with self._lock:\n"
     "            if self._phase == \"active\" and self._process:\n"
     "                await self._call(\"configure\", **self._configure_args())\n"
     "                await self._take_firmware()\n"
     "        await self.reconcile(\"desktop layout\")\n",
     "        if enabled and self._gamepad and not (fast and getattr(self, \"_desktop_firmware_known\", False)):\n"
     "            state = await self._gamepad.read()\n"
     "            self._desktop_buttons = state.get(\"desktopKeys\") or {}\n"
     "            self._desktop_sticks = state.get(\"desktopSticks\") or {}\n"
     "            self._desktop_maps = state.get(\"desktopStickMappings\") or {}\n"
     "            self._desktop_firmware_known = bool(state.get(\"available\", True))\n"
     "        async with self._lock:\n"
     "            if self._phase == \"active\" and self._process:\n"
     "                await self._call(\"configure\", **self._configure_args())\n"
     "                if not fast:\n"
     "                    await self._take_firmware()\n"
     "        if fast:\n"
     "            self._queue_firmware()\n"
     "        await self.reconcile(\"desktop layout\")\n"),
])

# ---------------------------------------------------------------- firmware
edit("claw_gamepad_fw.py", [
    # one quick retry when the pad misses a request
    ("            channel.send(read_request(self._rumble, at, part))\n"
     "            # Replies for other addresses (stale, or another reader on the pad) are skipped.\n"
     "            reply = self._await(channel, lambda frame: (parsed := parse_read_reply(frame))\n"
     "                                and parsed[0] == at and len(parsed[1]) == part and parsed[1] or None)\n",
     "            accept = (lambda frame, at=at, part=part: (parsed := parse_read_reply(frame))\n"
     "                      and parsed[0] == at and len(parsed[1]) == part and parsed[1] or None)\n"
     "            # Replies for other addresses (stale, or another reader on the pad) are skipped.\n"
     "            for attempt in (0, 1):\n"
     "                channel.send(read_request(self._rumble, at, part))\n"
     "                try:\n"
     "                    reply = self._await(channel, accept)\n"
     "                    break\n"
     "                except TimeoutError:\n"
     "                    if attempt:\n"
     "                        raise\n"),
    ("            channel.send(write_request(self._rumble, address + offset, data[offset:offset + _CHUNK]))\n"
     "            self._await(channel, lambda frame: True if is_reply(frame, _ACK) else None)\n",
     "            for attempt in (0, 1):\n"
     "                channel.send(write_request(self._rumble, address + offset, data[offset:offset + _CHUNK]))\n"
     "                try:\n"
     "                    self._await(channel, lambda frame: True if is_reply(frame, _ACK) else None)\n"
     "                    break\n"
     "                except TimeoutError:\n"
     "                    if attempt:\n"
     "                        raise\n"),
    # same firmware mode: nothing to re-apply
    ("            mode, mkeys = await self._run(self._firmware.read_mode)\n"
     "            if mode != target:\n"
     "                await self._run(self._firmware.switch_mode, target, mkeys)\n"
     "        await self.enforce(\"mode\")\n",
     "            mode, mkeys = await self._run(self._firmware.read_mode)\n"
     "            changed = mode != target\n"
     "            if changed:\n"
     "                await self._run(self._firmware.switch_mode, target, mkeys)\n"
     "        if changed:\n"
     "            await self.enforce(\"mode\")\n"),
])
