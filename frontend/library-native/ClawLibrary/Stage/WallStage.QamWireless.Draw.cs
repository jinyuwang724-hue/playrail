using System.Numerics;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.UI;
using Motion = ClawLibrary.Engine.Motion;
using Entry = ClawLibrary.Data.QamWireless.Entry;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private void NetworkPaint()
    {
        if (_networkPage == null) return;
        QsDrawInto(ref _qsNetworkSurface, _qsNetworkV, QsW, QsViewH, NetworkDraw);
        if (_networkKeyboard) NetworkPaintKeyboard();
    }

    private void NetworkDraw(CanvasDrawingSession ds)
    {
        _networkHits.Clear();
        NetworkHitAt("back", 32, 76, 136, 52, NetworkBack);
        if (_networkFocus == "back") ds.FillRoundedRectangle(28, 88, 28, 28, 9, 9, QsInk);
        QsIcon(ds, "qam.l", 42, 102, 16, _networkFocus == "back" ? QsBg : QsInk, 1.6f);
        QsTextC(ds, NetworkWifi ? "Wi-Fi" : "蓝牙", TextKit.Ui(700), 24, 60, 102, QsInk);
        NetworkHitAt("toggle", 330, 76, 54, 52, NetworkToggle);
        ds.FillRoundedRectangle(338, 88, 46, 28, 9, 9, NetworkOn ? QsInk : QsOffBg);
        ds.FillRoundedRectangle(NetworkOn ? 359 : 341, 91, 22, 22, 7, 7, NetworkOn ? QsBg : QsInk);
        if (_networkFocus == "toggle")
        {
            ds.DrawRoundedRectangle(335, 85, 52, 34, 11, 11, QsInk, 1.5f);
            ds.DrawRoundedRectangle(332, 82, 58, 40, 13, 13, QsInk2, 1);
        }
        if (!NetworkOn) { _networkBottom = 128; return; }
        using var clip = ds.CreateLayer(1, new Rect(0, 128, QsW, Math.Max(1, QsViewH - 128)));
        float y = 128 - _networkScroll;
        bool wifi = NetworkWifi;
        var primary = _networkEntries.Where(e => wifi ? e.Connected : e.Saved);
        if (!wifi || primary.Any())
        {
            NetworkGroup(ds, wifi ? "已连接" : "我的设备", ref y, false);
            foreach (var entry in primary) NetworkDrawRow(ds, entry, ref y);
        }
        NetworkGroup(ds, wifi ? "可用网络" : "其他设备", ref y, _networkScan);
        foreach (var entry in _networkEntries.Where(e => wifi ? !e.Connected : !e.Saved))
            NetworkDrawRow(ds, entry, ref y);
        _networkBottom = y + _networkScroll;
    }

    private void NetworkGroup(CanvasDrawingSession ds, string name, ref float y, bool scan)
    {
        y += 12;
        QsTextC(ds, name, TextKit.Ui(600), 13, 32, y + 18, QsInk2);
        if (scan)
            for (int i = 0; i < 3; i++)
                ds.FillRectangle(364 + i * 8, y + 16, 4, 4, (Environment.TickCount64 / 220) % 3 == i ? QsInk : QsInk2);
        y += 36;
    }

    private void NetworkDrawRow(CanvasDrawingSession ds, Entry entry, ref float y)
    {
        double arrival = _networkArrivals.TryGetValue(entry.Id, out long time) && !Reduced
            ? Motion.CameraEase.Ease((NowMs - time) / 220d) : 1;
        float offset = (float)((1 - arrival) * 8);
        y += offset;
        using var fade = ds.CreateLayer((float)arrival);
        NetworkDrawEntry(ds, entry, ref y);
        y -= offset;
    }

    private void NetworkDrawEntry(CanvasDrawingSession ds, Entry entry, ref float y)
    {
        string more = entry.Id + ":more";
        bool options = entry.Saved || (entry.Connected && (!NetworkWifi || entry.Adapter != null));
        bool busy = _networkBusy == entry.Id;
        string detail = busy && _networkBusyText.Length > 0 ? _networkBusyText : entry.Detail;
        if (!busy && entry.Connected && _networkBatteries.TryGetValue(entry.Id, out var level) && level.HasValue)
            detail += $" · 电量 {level}%";
        NetworkHitAt(entry.Id, 20, y, options ? 322 : 376, 56, () => NetworkSelect(entry));
        if (_networkFocus == entry.Id) ds.FillRoundedRectangle(20, y, 376, 56, 14, 14, Gfx.WithAlpha(QsInk, .09));
        string icon = entry.Icon switch
        {
            "keyboard" => "qam.kbd", "mouse" => "qam.mouse", "controller" => "qam.pad",
            "headphones" => "qam.headphones", "speaker" => "qam.speaker", _ => entry.Icon
        };
        if (NetworkWifi)
        {
            for (int i = 0; i < 4; i++)
            {
                float h = 4 + i * 4;
                ds.FillRoundedRectangle(33 + 6 * i, y + 37 - h, 4, h, 1, 1, i < entry.Signal ? QsInk : Gfx.WithAlpha(QsInk, .25));
            }
        }
        else QsIcon(ds, icon, 44, y + 28, 24, QsInk, 1.5f);
        float limit = options ? 250 : 282;
        string name = entry.Name;
        if (QsTextW(ds, name, TextKit.Ui(600), 15) > limit)
        {
            var starts = System.Globalization.StringInfo.ParseCombiningCharacters(name);
            int i = starts.Length - 1;
            while (i > 0 && QsTextW(ds, name[..starts[i]] + "…", TextKit.Ui(600), 15) > limit) i--;
            name = name[..starts[i]] + "…";
        }
        QsTextC(ds, name, TextKit.Ui(600), 15, 70, y + (detail.Length == 0 ? 28 : 20), QsInk);
        if (detail.Length != 0) QsTextC(ds, detail, TextKit.Ui(400), 12, 70, y + 40, QsInk2);
        if (entry.Secured) QsIcon(ds, "qam.lock", options ? 332 : 375, y + 28, 16, QsInk2, 1.4f);
        if (options) NetworkButton(ds, more, "⋯", 352, y + 12, 32, 32, () => NetworkOptions(entry), quiet: true, navY: y);
        if (busy)
        {
            ds.FillRectangle(70, y + 54, 314, 2, QsOffBg);
            ds.FillRectangle(70 + (Environment.TickCount64 % 1200) * 280 / 1200f, y + 54, 34, 2, QsInk);
        }
        y += 56;
        if (_networkExpand != entry.Id) return;
        float top = y;
        float expandedHeight = _networkMenu ? 62 : NetworkWifi || _networkProvidePin ? (_networkError ? 144 : 122) : 146;
        float progress = Reduced ? 1 : (float)Motion.ReflowEase((NowMs - _networkExpandedAt) / (double)Motion.ReflowMs);
        float visibleHeight = expandedHeight * progress;
        using var expansion = ds.CreateLayer(1, new Rect(0, top, QsW, Math.Max(.1, visibleHeight)));
        y += 8;
        if (_networkMenu)
        {
            if (entry.Connected) NetworkButton(ds, "disconnect", "断开", 32, y, 160, 44, () => NetworkDisconnect(entry));
            if (entry.Saved)
                NetworkButton(ds, "forget", _networkArmed ? "再按一次忘记" : NetworkWifi ? "忘记此网络" : "忘记此设备",
                    entry.Connected ? 202 : 32, y, entry.Connected ? 182 : 352, 44, () => NetworkForget(entry), red: _networkArmed);
        }
        else
        {
            if (NetworkWifi || _networkProvidePin)
            {
                string value = _networkReveal ? _networkPassword : new string('•', _networkPassword.Length);
                while (QsTextW(ds, value, TextKit.Ui(600), 15) > 290 && value.Length > 1) value = value[1..];
                ds.FillRoundedRectangle(32, y, 352, 50, 14, 14, QsOffBg);
                if (_networkFocus == "password") ds.DrawRoundedRectangle(32, y, 352, 50, 14, 14, QsInk, 2.5f);
                QsTextC(ds, value, TextKit.Ui(600), 15, 46, y + 25, QsInk);
                NetworkHitAt("password", 32, y, 308, 50, () => NetworkKeyboard(true));
                NetworkHitAt("reveal", 340, y, 44, 50, () => _networkReveal = !_networkReveal);
                if (_networkFocus == "reveal") ds.FillRoundedRectangle(344, y + 9, 32, 32, 11, 11, QsInk);
                QsIcon(ds, "qam.eye", 360, y + 25, 17, _networkFocus == "reveal" ? QsBg : QsInk2, 1.5f);
                y += 60;
                if (_networkError)
                {
                    QsTextC(ds, "密码不正确", TextKit.Ui(400), 12, 32, y + 6, QsInk2);
                    y += 22;
                }
            }
            else
            {
                NetworkCenter(ds, "确认两边显示的配对码一致", 12, 400, 208, y + 10, QsInk2);
                string pin = _networkPin ?? "";
                if (pin.Length == 6) pin = pin.Insert(3, " ");
                NetworkCenter(ds, pin, 32, 700, 208, y + 54, QsInk);
                y += 84;
            }
            NetworkButton(ds, "cancel", "取消", 32, y, 171, 44, NetworkCollapse);
            NetworkButton(ds, "connect", NetworkWifi ? "连接" : "配对", 213, y, 171, 44, () =>
            {
                if (NetworkWifi && _networkBusy == null && _networkPassword.Length > 0) NetworkConnect(entry, _networkPassword);
                else if (!NetworkWifi && (!_networkProvidePin || _networkPassword.Length > 0))
                {
                    _networkPair?.TrySetResult(_networkPassword);
                    NetworkCollapse();
                }
            });
        }
        y = top + visibleHeight;
    }

    private void NetworkHitAt(string id, float x, float y, float w, float h, Action action) =>
        _networkHits.Add(new NetworkHit(id, new Rect(x, y, w, h), action));

    private void NetworkButton(CanvasDrawingSession ds, string id, string label, float x, float y, float w, float h,
        Action action, bool red = false, bool quiet = false, float? navY = null)
    {
        if (navY.HasValue) NetworkHitAt(id, x, navY.Value, w, 56, action);
        else NetworkHitAt(id, x, y, w, h, action);
        bool focus = id == _networkFocus;
        if (focus || red || !quiet) ds.FillRoundedRectangle(x, y, w, h, 11, 11, red ? QsRed : focus ? QsInk : QsOffBg);
        NetworkCenter(ds, label, 15, 600, x + w / 2, y + h / 2, focus || red ? QsBg : quiet ? QsInk2 : QsInk);
    }

    private static void NetworkCenter(CanvasDrawingSession ds, string text, float size, ushort weight, float x, float y, Color ink)
    {
        var font = TextKit.Ui(weight);
        QsTextC(ds, text, font, size, x - QsTextW(ds, text, font, size) / 2, y, ink);
    }

    private void NetworkKeyboard(bool show)
    {
        if (_networkKeyboard == show || show && (_qsWin == null || NetworkKeyboardWidth < 200)) return;
        _networkKeyboard = show;
        _qsKeyboardV.IsVisible = show;
        if (show)
        {
            _networkKeyRow = 1;
            _networkKeyCol = 0;
            _networkShift = _networkSymbols = false;
            NetworkPaintKeyboard();
        }
        QsPlaceWindow(_qsGame != 0 ? _qsGame : WinRT.Interop.WindowNative.GetWindowHandle(_window));
        if (!show && _networkExpand != null) _networkFocus = "password";
    }

    private string[][] NetworkKeyRows() => _networkSymbols
        ? [NetworkLetters("1234567890"), NetworkLetters(_networkShift ? "~`<>?" : "!@#$%^&*()"),
            NetworkLetters("-_=+[]{};"), ["⇧", "\\", "|", ":", "'", "\"", ",", ".", "/", "⌫"], ["ABC", " ", "完成"]]
        : ["1234567890".Select(c => c.ToString()).ToArray(), NetworkLetters("qwertyuiop"), NetworkLetters("asdfghjkl"),
            ["⇧", .. NetworkLetters("zxcvbnm"), "⌫"], ["?123", " ", "完成"]];

    private string[] NetworkLetters(string s) => (_networkShift ? s.ToUpperInvariant() : s).Select(c => c.ToString()).ToArray();

    private void NetworkKey(string key)
    {
        switch (key)
        {
            case "⇧": _networkShift = !_networkShift; break;
            case "ABC": case "?123": _networkSymbols = !_networkSymbols; _networkShift = false; break;
            case "⌫": if (_networkPassword.Length > 0) _networkPassword = _networkPassword[..^1]; break;
            case "完成": NetworkKeyboard(false); _networkFocus = "connect"; break;
            default:
                if (_networkPassword.Length < 64) _networkPassword += key;
                if (!_networkSymbols) _networkShift = false;
                break;
        }
        _networkError = false;
        NetworkPaint();
    }

    private const float NetworkKeyboardHeight = 322;
    private float NetworkKeyboardTop => Math.Max(0, QsViewH - NetworkKeyboardHeight - 24);
    private float NetworkKeyboardWidth => Math.Max(1, _qsMon.W / (Chrome.Dpi * QsK) - 480);

    private void NetworkPaintKeyboard()
    {
        if (!_networkKeyboard) return;
        float width = NetworkKeyboardWidth, top = NetworkKeyboardTop;
        _qsKeyboardV.Offset = new Vector3(448 * QsK, top * QsK, 0);
        QsDrawInto(ref _qsKeyboardSurface, _qsKeyboardV, width, NetworkKeyboardHeight, ds =>
        {
            _networkKeys.Clear();
            ds.FillRoundedRectangle(0, 0, width, NetworkKeyboardHeight, 24, 24, Gfx.WithAlpha(QsBg, .86));
            var rows = NetworkKeyRows();
            float pitch = (width - 48) / 10;
            for (int row = 0; row < rows.Length; row++)
            {
                float x = (width - (row == 4 ? 8 : rows[row].Length) * pitch) / 2;
                float y = 15 + 60 * row;
                for (int col = 0; col < rows[row].Length; col++)
                {
                    string key = rows[row][col];
                    float w = pitch * (row != 4 ? 1 : col == 1 ? 4 : 2);
                    bool focus = row == _networkKeyRow && col == _networkKeyCol;
                    ds.FillRoundedRectangle(x + 4, y, Math.Max(1, w - 8), 52, 14, 14, focus ? QsInk : QsOffBg);
                    NetworkCenter(ds, key, 17, 600, x + w / 2, y + 26, focus ? QsBg : QsInk);
                    int r = row, c = col;
                    _networkKeys.Add(new NetworkHit(key, new Rect(448 + x, top + y, w, 52),
                        () => { _networkKeyRow = r; _networkKeyCol = c; NetworkKey(key); }));
                    x += w;
                }
            }
        });
    }
}
