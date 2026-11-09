using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using ClawLibrary.Data;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private const int ScreensaverBrightness = 15;
    private const int ScreensaverLowRefresh = 48;
    private const int ScreensaverFallbackRefresh = 60;
    private const uint ExecutionContinuous = 0x80000000;
    private const uint ExecutionSystemRequired = 0x00000001;
    private const uint ExecutionDisplayRequired = 0x00000002;
    private static readonly Guid BestEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    private bool _screensaverKeepAwake, _screensaverPowerWanted, _screensaverPowerUpdating;
    private long _screensaverPowerRetryAt;
    private bool _screensaverTdpUpdating;
    private long _screensaverTdpRefreshAt;
    private AgentClient? _screensaverTdpAgent;
    private Guid? _screensaverPreviousPowerMode;
    private int? _screensaverPreviousBrightness;
    private (int Previous, int Applied)? _screensaverRefreshChange;

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetUserConfiguredDCPowerMode(out Guid mode);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetUserConfiguredDCPowerMode(in Guid mode);

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    private void UpdateScreensaverPower()
    {
        bool active = _screensaverActive && !_screensaverProbe && !_appearanceProbeRunning;
        if (active != _screensaverKeepAwake)
        {
            _screensaverKeepAwake = active;
            uint flags = ExecutionContinuous;
            if (active) flags |= ExecutionSystemRequired | ExecutionDisplayRequired;
            if (SetThreadExecutionState(flags) == 0) Log.Warn("screensaver execution state request failed");
        }

        bool wanted = false;
        if (active)
        {
            var battery = SystemInfo.Battery();
            wanted = battery.HasBattery && !battery.OnAc;
        }
        bool changed = wanted != _screensaverPowerWanted;
        _screensaverPowerWanted = wanted;
        if (active && changed) CsSaverPowerChanged();
        if (!_screensaverTdpUpdating && (changed || (wanted && NowMs >= _screensaverTdpRefreshAt)))
            _ = UpdateScreensaverTdp();
        bool restorePending = !wanted && NowMs >= _screensaverPowerRetryAt
            && (_screensaverPreviousPowerMode != null || _screensaverPreviousBrightness != null || _screensaverRefreshChange != null);
        if (!_screensaverPowerUpdating && (changed || restorePending)) _ = ApplyScreensaverPower();
    }

    private async Task UpdateScreensaverTdp()
    {
        _screensaverTdpUpdating = true;
        try
        {
            bool wanted;
            do
            {
                wanted = _screensaverPowerWanted;
                _screensaverTdpAgent ??= new AgentClient();
                try { await _screensaverTdpAgent.CallLong("set_screensaver_power", 5, JsonValue.Create(wanted)); }
                catch (Exception ex) { Log.Warn($"screensaver tdp: {ex.Message}"); }
            }
            while (wanted != _screensaverPowerWanted);
        }
        finally
        {
            _screensaverTdpRefreshAt = NowMs + 4_000;
            _screensaverTdpUpdating = false;
            if (!_screensaverPowerWanted)
            {
                _screensaverTdpAgent?.Dispose();
                _screensaverTdpAgent = null;
            }
        }
    }

    // Serialize display writes so a wake during entry always restores after the last write.
    private async Task ApplyScreensaverPower()
    {
        _screensaverPowerUpdating = true;
        using var agent = new AgentClient();
        try
        {
            bool wanted;
            do
            {
                wanted = _screensaverPowerWanted;
                SetScreensaverWindowsPowerMode(wanted);
                if (!wanted && _screensaverRefreshChange == null && _screensaverPreviousBrightness == null) continue;
                try
                {
                    var display = await agent.Call("get_display_state");
                    if (wanted)
                    {
                        if (_screensaverPowerWanted && (int?)display?["brightness"] is int brightness
                            && brightness > ScreensaverBrightness)
                        {
                            _screensaverPreviousBrightness ??= brightness;
                            await SetScreensaverDisplay(agent, "set_brightness", ScreensaverBrightness);
                        }
                        if (_screensaverPowerWanted && (int?)display?["refresh"] is int refresh
                            && display?["refreshRates"] is JsonArray rates)
                        {
                            int target = rates.Any(rate => (int?)rate == ScreensaverLowRefresh)
                                ? ScreensaverLowRefresh : ScreensaverFallbackRefresh;
                            if (refresh > target && rates.Any(rate => (int?)rate == target))
                            {
                                _screensaverRefreshChange = (_screensaverRefreshChange?.Previous ?? refresh, target);
                                await SetScreensaverDisplay(agent, "set_refresh_rate", target);
                            }
                        }
                    }
                    else
                    {
                        if (_screensaverRefreshChange is { } refreshChange && (int?)display?["refresh"] is int currentRefresh
                            && (currentRefresh != refreshChange.Applied || await SetScreensaverDisplay(agent, "set_refresh_rate", refreshChange.Previous)))
                            _screensaverRefreshChange = null;
                        if (_screensaverPreviousBrightness is int oldBrightness && (int?)display?["brightness"] is int currentBrightness
                            && (currentBrightness != ScreensaverBrightness || await SetScreensaverDisplay(agent, "set_brightness", oldBrightness)))
                            _screensaverPreviousBrightness = null;
                    }
                }
                catch (Exception ex) { Log.Warn($"screensaver display power: {ex.Message}"); }
            }
            while (wanted != _screensaverPowerWanted);
        }
        finally
        {
            _screensaverPowerRetryAt = NowMs + 5_000;
            _screensaverPowerUpdating = false;
        }
    }

    private static async Task<bool> SetScreensaverDisplay(AgentClient agent, string method, int value)
    {
        try { await agent.Call(method, JsonValue.Create(value)); return true; }
        catch (Exception ex) { Log.Warn($"screensaver {method}: {ex.Message}"); return false; }
    }

    private void SetScreensaverWindowsPowerMode(bool wanted)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        try
        {
            uint result = PowerGetUserConfiguredDCPowerMode(out Guid current);
            if (result != 0) { Log.Warn($"screensaver read power mode: {result}"); return; }
            if (wanted && current != BestEfficiency)
            {
                result = PowerSetUserConfiguredDCPowerMode(in BestEfficiency);
                if (result == 0) _screensaverPreviousPowerMode ??= current;
            }
            else if (!wanted && _screensaverPreviousPowerMode is Guid previous)
            {
                if (current == BestEfficiency) result = PowerSetUserConfiguredDCPowerMode(in previous);
                if (result == 0) _screensaverPreviousPowerMode = null;
            }
            if (result != 0) Log.Warn($"screensaver write power mode: {result}");
        }
        catch (EntryPointNotFoundException) { }
    }
}
