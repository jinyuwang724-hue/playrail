# Performance fixes, 2026-11-14

Applied in place to the installed backend 0.17.58.2 (hot-patched, differs from `plugin/`), backup in
`%LOCALAPPDATA%\ClawToolbox\repair-backups\perf-20261114`. Restart the agent with
`Stop-ScheduledTask` / `Start-ScheduledTask \ClawToolbox\Agent`.

- `claw_agent.py`: raise the agent to NORMAL priority at start. The scheduled task runs at priority 7
  (BELOW_NORMAL) and every helper (ClawPower, ClawInput, overlay) inherited it, so a full-CPU game starved
  power control: `save_power` took 80–301 s and failed 3 of 4 times. After: 0.03–5.5 s, 4 of 4.
- `claw_power.py`: while a user `save_power` waits for the policy lock, the 1 s control loop skips its own
  policy work instead of queueing ahead of it.

Library 0.1.269 (`WallStage.Services.cs`): pause the banner drift clock when the library goes to the tray
(hidden library 22.6 % → 5.5 % of a core). Desktop quick-settings route reuses one agent connection.

ClawShell 1.0.19: cache the explorer check and `state.json` read in the 100 ms watch loop (5.7 % → 0.2 %).

ClawPerformanceOverlay (`native/PerformanceOverlay.cs`): pace the scrolling frame-time graph at 30 Hz instead
of 60 Hz (it cost ~14 % of a core in game). Installed over `app\bin\ClawPerformanceOverlay.exe`.

Input engine `ClawInput-0.17.58-linger.exe` (source here in `InputLayer/`: the deployed overlay-r2 source from
`artifacts/overlay-focus-fix-191/src/native/InputLayer` + `docs/notice-r3-Osd.cs.txt`, plus the linger change):
when the agent disappears without sending `stop` (crash, kill, restart) the elevated engine keeps the virtual
pad and current layout running for 30 s and waits on `\.\pipe\claw-input-resume-<user SID>`; the next agent's
bridge resumes it instead of starting a new engine, so games no longer show "controller disconnected" on an
agent restart. A graceful disable still sends `stop` and tears down at once; a different build is told to stop.
`claw_input_layer.py` prefers the `-linger` exe. Verified: agent killed, Decky restarted it in 4 s, same
engine pid, virtual pad never left its XInput slot.
