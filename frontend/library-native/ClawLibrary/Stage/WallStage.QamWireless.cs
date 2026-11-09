using System.Numerics;
using ClawLibrary.Data;
using Direction = ClawLibrary.Engine.Direction;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Windows.Devices.Enumeration;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Motion = ClawLibrary.Engine.Motion;
using Windows.Devices.Radios;
using Windows.Devices.WiFi;
using Windows.Foundation;
using Windows.UI;
using Entry = ClawLibrary.Data.QamWireless.Entry;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private ContainerVisual _qsHome = null!, _qsNetworkClip = null!;
    private SpriteVisual _qsStatusV = null!, _qsNetworkV = null!, _qsKeyboardV = null!;
    private CompositionDrawingSurface? _qsStatusSurface, _qsNetworkSurface, _qsKeyboardSurface;
    private string? _networkPage, _networkBusy, _networkExpand, _networkPin;
    private string _networkFocus = "back", _networkPassword = "";
    private bool _networkScan, _networkMenu, _networkArmed, _networkError, _networkReveal;
    private bool _networkKeyboard, _networkSymbols, _networkShift, _networkProvidePin;
    private int _networkGeneration, _networkKeyRow = 1, _networkKeyCol;
    private float _networkScroll, _networkBottom;
    private CancellationTokenSource? _networkScanCancellation;
    private TaskCompletionSource<string?>? _networkPair;
    private readonly List<Entry> _networkEntries = [];
    private readonly Dictionary<string, GattSession> _networkBtSessions = [];
    private readonly Dictionary<string, int?> _networkBatteries = [];
    private readonly Dictionary<string, long> _networkArrivals = [];
    private long _networkExpandedAt;
    private readonly List<NetworkHit> _networkHits = [];
    private readonly List<NetworkHit> _networkKeys = [];
    private SettingsWriteQueue? _networkWrites;
    private sealed record NetworkHit(string Id, Rect Bounds, Action Action);
    private bool NetworkWifi => _networkPage == "wifi";
    private bool NetworkOn => (NetworkWifi ? _qsWifi : _qsBt) == true;

    private void NetworkVisuals()
    {
        _qsNetworkClip = G.Container(relative: false);
        _qsNetworkClip.Clip = G.C.CreateInsetClip();
        _qsHome = G.Container(relative: false);
        _qsNetworkClip.Children.InsertAtTop(_qsHome);
        _qsSlide.Children.InsertAtTop(_qsNetworkClip);
        _qsToastV = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        _qsToastV.IsVisible = false;
        _qsStatusV = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        _qsNetworkV = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        _qsNetworkV.Opacity = 0;
        _qsNetworkClip.Children.InsertAtTop(_qsNetworkV);
        _qsSlide.Children.InsertAtTop(_qsStatusV);
        _qsSlide.Children.InsertAtTop(_qsToastV);
        _qsKeyboardV = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        _qsKeyboardV.IsVisible = false;
        _qs!.Children.InsertAtTop(_qsKeyboardV);
    }

    private SpriteVisual _qsToastV = null!;
    private CompositionDrawingSurface? _qsToastSurface;
    private int _qsToastGeneration;

    private void QsLocalToast(string message)
    {
        int generation = ++_qsToastGeneration;
        _qsToastV.Offset = new Vector3(32 * QsK, (QsViewH - 64) * QsK, 0);
        QsDrawInto(ref _qsToastSurface, _qsToastV, 352, 44, ds =>
        {
            ds.FillRoundedRectangle(0, 0, 352, 44, 14, 14, QsBg);
            var text = message.Length > 24 ? message[..23] + "…" : message;
            NetworkCenter(ds, text, 14, 600, 176, 22, QsInk);
        });
        _qsToastV.IsVisible = true;
        After(2200, () => { if (generation == _qsToastGeneration) _qsToastV.IsVisible = false; });
    }

    private void NetworkEnter(string page)
    {
        if (_networkPage != null || page is not ("wifi" or "bt")) return;
        QsSetFocus(page);
        _networkPage = page;
        _networkFocus = "back";
        _networkScroll = 0;
        _networkEntries.Clear();
        _networkArrivals.Clear();
        _networkBatteries.Clear();
        NetworkTransition(true);
        NetworkPaint();
        _ = NetworkScan();
    }

    private void NetworkTransition(bool enter, bool instant = false)
    {
        int duration = instant || Reduced ? 1 : (int)Motion.CameraMs;
        NetworkAnimate(_qsHome, enter ? -56 : 0, enter ? 0 : 1, duration);
        NetworkAnimate(_qsNetworkV, enter ? 0 : 56, enter ? 1 : 0, duration);
    }

    private void NetworkAnimate(Visual visual, float x, float opacity, int duration)
    {
        var animation = G.C.CreateVector3KeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(duration);
        var curve = Motion.CameraEase;
        var ease = G.C.CreateCubicBezierEasingFunction(new(curve.X1, curve.Y1), new(curve.X2, curve.Y2));
        animation.InsertKeyFrame(1, new Vector3(x * QsK, 0, 0), ease);
        visual.StartAnimation("Offset", animation);
        var fade = G.C.CreateScalarKeyFrameAnimation();
        fade.Duration = animation.Duration;
        fade.InsertKeyFrame(1, opacity, ease);
        visual.StartAnimation("Opacity", fade);
    }

    private void NetworkLeave(bool instant = false)
    {
        if (_networkPage == null) return;
        NetworkCollapse();
        ++_networkGeneration;
        _networkScanCancellation?.Cancel();
        _networkPage = null;
        _networkScan = false;
        _networkBusy = null;
        NetworkTransition(false, instant);
    }

    private async Task NetworkScan()
    {
        int generation = ++_networkGeneration;
        _networkScanCancellation?.Cancel();
        if (!NetworkOn || _networkPage == null) { _networkScan = false; NetworkPaint(); return; }
        if (NetworkWifi)
        {
            _networkEntries.RemoveAll(e => e.Connected && e.Name != _qsSsid);
            if (_qsSsid is { } ssid && !_networkEntries.Any(e => e.Connected))
            {
                _networkEntries.RemoveAll(e => e.Name == ssid);
                _networkEntries.Insert(0, QamWireless.ConnectedWifiEntry(ssid));
            }
        }
        using var cancellation = new CancellationTokenSource();
        _networkScanCancellation = cancellation;
        _networkScan = true;
        NetworkPaint();
        NetworkTick(generation);
        try
        {
            if (NetworkWifi)
            {
                var entries = await QamWireless.Wifi();
                if (generation != _networkGeneration) return;
                _networkEntries.Clear();
                _networkEntries.AddRange(entries);
                if (_qsSsid is { } current && !entries.Any(e => e.Connected))
                {
                    _networkEntries.RemoveAll(e => e.Name == current);
                    _networkEntries.Insert(0, QamWireless.ConnectedWifiEntry(current));
                }
            }
            else
            {
                var watcher = QamWireless.WatchBluetooth();
                var devices = new Dictionary<string, DeviceInformation>();
                void Dispatch(Action action) => _dq.TryEnqueue(() =>
                {
                    if (generation != _networkGeneration || cancellation.IsCancellationRequested) return;
                    action();
                    NetworkPaint();
                });
                watcher.Added += (_, device) => Dispatch(() => { devices[device.Id] = device; UpdateBluetooth(device); });
                watcher.Updated += (_, update) => Dispatch(() =>
                {
                    if (devices.TryGetValue(update.Id, out var device)) { device.Update(update); UpdateBluetooth(device); }
                });
                watcher.Removed += (_, update) => Dispatch(() =>
                {
                    devices.Remove(update.Id);
                    _networkEntries.RemoveAll(e => e.Id == update.Id && !e.Saved && e.Id != _networkBusy);
                });
                _networkEntries.RemoveAll(e => !e.Saved && e.Id != _networkExpand);
                watcher.Start();
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(12), cancellation.Token);
                    if (generation == _networkGeneration) { _networkScan = false; NetworkPaint(); }
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
                }
                finally { watcher.Stop(); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"QAM wireless scan: {ex}");
            if (generation == _networkGeneration) QsFlash(ex is UnauthorizedAccessException ? "扫描需要系统权限" : "扫描失败");
        }
        finally
        {
            if (ReferenceEquals(_networkScanCancellation, cancellation)) _networkScanCancellation = null;
            if (generation == _networkGeneration) { _networkScan = false; NetworkPaint(); }
        }
    }

    private void NetworkTick(int generation)
    {
        After(100, () =>
        {
            if (_networkPage == null || generation != _networkGeneration || !(_networkScan || _networkBusy != null)) return;
            NetworkPaint();
            NetworkTick(generation);
        });
    }

    private void UpdateBluetooth(DeviceInformation device)
    {
        var entry = QamWireless.Bluetooth(device);
        int i = _networkEntries.FindIndex(e => e.Id == entry.Id);
        if (i < 0)
        {
            _networkEntries.Add(entry);
            if (!entry.Saved && !Reduced)
            {
                _networkArrivals[entry.Id] = NowMs;
                QsStartTicking();
            }
        }
        else _networkEntries[i] = entry;
        if (entry.Connected && entry.Saved && QamWireless.IsLowEnergy(entry) && _networkBatteries.TryAdd(entry.Id, null))
            _ = NetworkReadBattery(entry, _networkGeneration);
    }

    private async Task NetworkReadBattery(Entry entry, int generation)
    {
        try
        {
            var level = await QamWireless.Battery(entry);
            if (generation != _networkGeneration) return;
            _networkBatteries[entry.Id] = level;
            NetworkPaint();
        }
        catch (Exception ex) { Log.Info($"QAM Bluetooth battery unavailable: {ex.HResult:x}"); }
    }

    private void NetworkStartExpansion()
    {
        _networkExpandedAt = NowMs;
        if (!Reduced) QsStartTicking();
    }

    private bool NetworkAnimating()
    {
        if (Reduced || _networkPage == null) return false;
        return _networkExpand != null && NowMs - _networkExpandedAt < Motion.ReflowMs ||
            _networkArrivals.Values.Any(t => NowMs - t < 220);
    }

    private void NetworkBack()
    {
        if (_networkKeyboard) NetworkKeyboard(false);
        else if (_networkArmed) _networkArmed = false;
        else if (_networkExpand != null) NetworkCollapse();
        else { NetworkLeave(); return; }
        NetworkPaint();
    }

    private void NetworkCollapse()
    {
        NetworkKeyboard(false);
        _networkPair?.TrySetResult(null);
        if (_networkExpand != null) _networkFocus = _networkExpand;
        _networkExpand = _networkPin = null;
        _networkPassword = "";
        _networkArmed = _networkError = _networkMenu = _networkReveal = _networkProvidePin = false;
    }

    private void NetworkMove(Direction direction)
    {
        if (_networkKeyboard)
        {
            var rows = NetworkKeyRows();
            if (direction is Direction.Up or Direction.Down)
                _networkKeyRow = Math.Clamp(_networkKeyRow + (direction == Direction.Up ? -1 : 1), 0, rows.Length - 1);
            else _networkKeyCol += direction == Direction.Left ? -1 : 1;
            _networkKeyCol = Math.Clamp(_networkKeyCol, 0, rows[_networkKeyRow].Length - 1);
            NetworkPaintKeyboard();
            return;
        }
        var current = _networkHits.Find(h => h.Id == _networkFocus);
        if (current == null) { _networkFocus = "back"; NetworkPaint(); return; }
        bool horizontal = direction is Direction.Left or Direction.Right;
        bool forward = direction is Direction.Down or Direction.Right;
        var next = horizontal
            ? _networkHits.Where(h => h.Bounds.Y == current.Bounds.Y && (forward ? h.Bounds.X > current.Bounds.X : h.Bounds.X < current.Bounds.X))
                .OrderBy(h => Math.Abs(h.Bounds.X - current.Bounds.X)).FirstOrDefault()
            : _networkHits.Where(h => forward ? h.Bounds.Y > current.Bounds.Y : h.Bounds.Y < current.Bounds.Y)
                .OrderBy(h => Math.Abs(h.Bounds.Y - current.Bounds.Y)).FirstOrDefault();
        if (next == null)
        {
            if (direction == Direction.Left && _networkExpand == null && _networkFocus != "toggle") NetworkBack();
            return;
        }
        if (_networkFocus != next.Id) _networkArmed = false;
        _networkFocus = next.Id;
        if (!horizontal || next.Bounds.Y >= 128)
        {
            if (next.Id is "back" or "toggle") _networkScroll = 0;
            else if (next.Bounds.Y < 128) _networkScroll = Math.Max(0, _networkScroll + (float)next.Bounds.Y - 128);
            else if (next.Bounds.Bottom > QsViewH - 12) _networkScroll += (float)(next.Bounds.Bottom - QsViewH + 12);
        }
        NetworkPaint();
        Cue(Sfx.Nav);
    }

    private void NetworkActivate()
    {
        if (_networkKeyboard) { NetworkKey(NetworkKeyRows()[_networkKeyRow][_networkKeyCol]); return; }
        _networkHits.Find(h => h.Id == _networkFocus)?.Action();
        NetworkPaint();
    }

    private NetworkHit? NetworkKeyAt(Point point) =>
        _networkKeyboard ? _networkKeys.Find(h => h.Bounds.Contains(point)) : null;

    private NetworkHit? NetworkControlAt(Point point) =>
        _networkHits.FindLast(h => h.Bounds.Contains(point) && (point.Y >= 128 || h.Id is "back" or "toggle"));

    private void NetworkClick(float x, float y)
    {
        var point = new Point(x, y);
        var hit = NetworkKeyAt(point);
        if (hit != null) { hit.Action(); return; }
        hit = NetworkControlAt(point);
        if (hit == null) return;
        if (_networkFocus != hit.Id) _networkArmed = false;
        _networkFocus = hit.Id;
        hit.Action();
        NetworkPaint();
    }

    private void NetworkToggle()
    {
        if (_networkBusy != null) return;
        var kind = NetworkWifi ? RadioKind.WiFi : RadioKind.Bluetooth;
        if (_networkWrites?.IsBusy == true) return;
        bool wasOn = NetworkOn;
        NetworkCollapse();
        if (NetworkWifi) _qsWifi = !wasOn; else _qsBt = !wasOn;
        NetworkWrite("toggle", async () =>
        {
            await QsRadio(kind, !wasOn);
            if (!NetworkOn)
            {
                _networkScanCancellation?.Cancel();
                _networkEntries.Clear();
                if (kind == RadioKind.Bluetooth)
                {
                    foreach (var session in _networkBtSessions.Values) session.Dispose();
                    _networkBtSessions.Clear();
                }
            }
        });
    }

    private void NetworkSelect(Entry entry)
    {
        if (_networkBusy != null) return;
        if (_networkExpand != null) { NetworkCollapse(); _networkFocus = entry.Id; }
        if (!NetworkWifi)
        {
            if (entry.Saved)
            {
                if (entry.Connected) NetworkDisconnect(entry);
                else if (QamWireless.IsLowEnergy(entry))
                    NetworkWrite(entry.Id, async () =>
                    {
                        var session = await QamWireless.ConnectBluetooth(entry);
                        if (_networkBtSessions.Remove(entry.Id, out var old)) old.Dispose();
                        _networkBtSessions[entry.Id] = session;
                    }, "正在连接…");
                else QsFlash("此设备由 Windows 管理连接");
                return;
            }
            NetworkWrite(entry.Id, () => NetworkPair(entry));
            return;
        }
        if (entry.Connected) { if (entry.Adapter != null) NetworkOptions(entry); return; }
        if (entry.Secured && !entry.Saved)
        {
            _networkExpand = entry.Id;
            _networkFocus = "password";
            NetworkRevealRow(entry.Id, 114);
            NetworkStartExpansion();
            NetworkKeyboard(true);
        }
        else NetworkConnect(entry, null);
    }

    private void NetworkOptions(Entry entry)
    {
        if (_networkBusy != null) return;
        NetworkCollapse();
        _networkExpand = entry.Id;
        _networkMenu = true;
        _networkFocus = entry.Connected ? "disconnect" : "forget";
        NetworkRevealRow(entry.Id, 52);
        NetworkStartExpansion();
    }

    private void NetworkRevealRow(string id, int extra)
    {
        var hit = _networkHits.Find(h => h.Id == id);
        if (hit != null) _networkScroll += Math.Max(0, (float)(hit.Bounds.Bottom + extra - QsViewH + 12));
    }

    private void NetworkDisconnect(Entry entry)
    {
        if (entry.Adapter == null)
        {
            if (!_networkBtSessions.ContainsKey(entry.Id)) { QsFlash("此设备由 Windows 管理连接"); return; }
            NetworkCollapse();
            NetworkWrite(entry.Id, async () =>
            {
                if (_networkBtSessions.Remove(entry.Id, out var session)) session.Dispose();
                await Task.Delay(750);
                var current = await DeviceInformation.CreateFromIdAsync(entry.Id, ["System.Devices.Aep.IsConnected"], DeviceInformationKind.AssociationEndpoint);
                if (QamWireless.Bluetooth(current).Connected) throw new InvalidOperationException("设备仍由 Windows 使用");
            }, "正在断开…");
            return;
        }
        NetworkCollapse();
        NetworkWrite(entry.Id, async () =>
        {
            await Task.Run(() => entry.Adapter.Disconnect());
            await Task.Delay(450);
        }, "正在断开…");
    }

    private void NetworkForget(Entry entry)
    {
        if (!_networkArmed) { _networkArmed = true; return; }
        NetworkCollapse();
        NetworkWrite(entry.Id, async () =>
        {
            if (entry.Adapter != null)
            {
                if (entry.Connected) entry.Adapter.Disconnect();
                await QamWireless.Forget(entry);
            }
            else
            {
                if (_networkBtSessions.Remove(entry.Id, out var session)) session.Dispose();
                var result = await entry.Device!.Pairing.UnpairAsync();
                if (result.Status is not (DeviceUnpairingResultStatus.Unpaired or DeviceUnpairingResultStatus.AlreadyUnpaired))
                    throw new InvalidOperationException("无法忘记此设备");
                _networkEntries.RemoveAll(e => e.Id == entry.Id);
            }
        });
    }

    private void NetworkConnect(Entry entry, string? password)
    {
        NetworkKeyboard(false);
        NetworkWrite(entry.Id, async () =>
        {
            var status = await QamWireless.Connect(entry, password);
            if (status == WiFiConnectionStatus.Success)
            {
                NetworkCollapse();
                QsFlash($"已连接 {entry.Name}");
            }
            else if (status == WiFiConnectionStatus.InvalidCredential && password != null && _networkExpand == entry.Id)
            {
                _networkError = true;
                _networkFocus = "password";
            }
            else throw new InvalidOperationException(status == WiFiConnectionStatus.AccessRevoked ? "Wi-Fi 访问权限已撤回" : "连接失败");
        }, "正在连接…");
    }

    private async Task NetworkPair(Entry entry)
    {
        int generation = _networkGeneration;
        var custom = entry.Device!.Pairing.Custom;
        Windows.Foundation.TypedEventHandler<DeviceInformationCustomPairing, DevicePairingRequestedEventArgs> handler = async (_, args) =>
        {
            if (args.PairingKind == DevicePairingKinds.ConfirmOnly) { args.Accept(); return; }
            using var deferral = args.GetDeferral();
            try
            {
                var confirmation = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!_dq.TryEnqueue(() =>
                {
                    if (_networkPage != "bt" || generation != _networkGeneration) { confirmation.TrySetResult(null); return; }
                    _networkPair = confirmation;
                    _networkExpand = entry.Id;
                    _networkProvidePin = args.PairingKind == DevicePairingKinds.ProvidePin;
                    _networkPin = args.Pin;
                    _networkFocus = _networkProvidePin ? "password" : "connect";
                    NetworkRevealRow(entry.Id, 145);
                    NetworkStartExpansion();
                    if (_networkProvidePin) NetworkKeyboard(true);
                    NetworkPaint();
                })) return;
                string? answer = await confirmation.Task.WaitAsync(TimeSpan.FromSeconds(45));
                if (answer != null)
                {
                    if (args.PairingKind == DevicePairingKinds.ProvidePin) args.Accept(answer);
                    else args.Accept();
                }
            }
            catch (Exception ex) { Log.Warn($"QAM Bluetooth pairing request: {ex.Message}"); }
            finally { deferral.Complete(); }
        };
        custom.PairingRequested += handler;
        try
        {
            var result = await custom.PairAsync(DevicePairingKinds.ConfirmOnly | DevicePairingKinds.ConfirmPinMatch | DevicePairingKinds.ProvidePin);
            if (generation != _networkGeneration) return;
            bool canceled = _networkPair?.Task is { IsCompletedSuccessfully: true } answer && answer.Result == null;
            NetworkCollapse();
            if (result.Status is DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired)
            {
                UpdateBluetooth(entry.Device);
                QsFlash($"已配对 {entry.Name}");
            }
            else if (!canceled) throw new InvalidOperationException("配对失败");
        }
        finally { custom.PairingRequested -= handler; _networkPair = null; }
    }

    private string _networkBusyText = "";
    private void NetworkWrite(string key, Func<Task> action, string detail = "")
    {
        if (_networkBusy != null || _networkWrites?.IsBusy == true) return;
        string? page = _networkPage;
        int generation = _networkGeneration;
        _networkBusy = key;
        _networkBusyText = detail;
        if (!_networkScan) NetworkTick(generation);
        NetworkPaint();
        _networkWrites ??= new SettingsWriteQueue(ex =>
        {
            Log.Warn($"QAM wireless write: {ex}");
            if (_qsOpen) QsFlash(ex is InvalidOperationException ? ex.Message : "操作失败");
        }, () =>
        {
            _networkBusy = null;
            if (_networkPage != null && _qsOpen) _ = NetworkScan();
            if (_qsOpen) _ = QsRefresh(_qsVersion);
        });
        _ = _networkWrites.Enqueue(key, 0, async () =>
        {
            if (_networkPage == page && generation == _networkGeneration) await action();
        });
    }
}
