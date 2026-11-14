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
