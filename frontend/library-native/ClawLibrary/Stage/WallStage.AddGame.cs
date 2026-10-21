// 设置 › 游戏库 › 本地游戏 这一节：添加游戏（先在文件夹浏览里选 exe，然后进 WallStage.AddGamePage.cs 的整页）、
// 本地游戏清单（点一款进同一个整页改）、文件夹里的游戏一次收进来、补全封面、同步到 Steam、SteamGridDB 密钥（可不填）。
// 写进 Steam 要先关 Steam：添加只进游戏库，「同步到 Steam」一次写完所有待同步的游戏，Steam 只重启一次。
using System.Text.Json.Nodes;
using ClawLibrary.Data;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private enum LgPage { None, List }

    private LgPage _lgPage;
    private bool _stBrowseExe, _lgPolling, _lgFolderTaken;
    private JsonObject? _lgState;
    private readonly Dictionary<string, CanvasBitmap?> _lgBitmaps = new();
    private TextBox? _stEditBox;
    private Action<string>? _stEditDone;
    /// <summary>正在打字的那一行（它的值先不画，免得和输入框里的字叠在一起）。</summary>
    private string? _stEditTitle;

    private bool StEditing => _stEditBox?.Visibility == Visibility.Visible;

    // ---------------- 后台 ----------------

    private async Task LgRefresh()
    {
        try
        {
            _agent ??= new AgentClient();
            bool wasBusy = Bool(_lgState?["busy"]);
            if (await _agent.Call("local_games_state") is JsonObject o) _lgState = (JsonObject)o.DeepClone();
            if (wasBusy && !Bool(_lgState?["busy"]))
            {
                _stNeedsReload = true;
                if (Str(_lgState?["error"]) is { Length: > 0 } err) Toast(err);
            }
        }
        catch (Exception ex) { Log.Warn($"local games state: {ex.Message}"); }
        if (_stOpen && !_agOpen && _stCat == 1 && _stBrowse == null) Repaint();
        if (Bool(_lgState?["busy"]) && !_lgPolling) _ = LgPoll();
    }

    /// <summary>后台在补封面 / 写 Steam 的时候，一秒读一次进度。</summary>
    private async Task LgPoll()
    {
        _lgPolling = true;
        try
        {
            while (_stOpen && Bool(_lgState?["busy"]))
            {
                await Task.Delay(1000);
                await LgRefresh();
            }
        }
        finally { _lgPolling = false; }
    }

    private async Task LgJob(string method, params JsonNode?[] args)
    {
        try
        {
            _agent ??= new AgentClient();
            await _agent.Call(method, args);
        }
        catch (Exception ex) { Toast(ex.Message.Contains("不支持的后台调用") ? "工具箱后台版本太旧" : ex.Message); }
        await LgRefresh();
    }

    private IEnumerable<JsonObject> LgGames() => (_lgState?["games"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>();

    private static bool LgHasArt(JsonObject g, string kind) => g["art"] is JsonObject a && Str(a[kind]) is { Length: > 0 };

    // ---------------- 游戏库分类里的几行 ----------------

    private void BuildLgEntryRows()
    {
        var s = _lgState;
        var job = s?["job"] as JsonObject;
        string phase = Str(job?["phase"]) ?? "";
        bool busy = Bool(s?["busy"]);
        var games = LgGames().ToList();
        _stRows.Add(new PerfRow { Section = true, Title = "本地游戏" });
        _stRows.Add(StAction("添加游戏…", "lgadd", "", () => { _stBrowseExe = true; StBrowseTo(""); }));
        _stRows.Add(new PerfRow { Title = "本地游戏", Value = s == null ? "" : $"{games.Count} 款", Disabled = s != null && games.Count == 0, Press = () => LgOpenList() });
        int folder = _lgFolderTaken ? 0 : Library.FolderGames.Count;
        if (folder > 0)
            _stRows.Add(StAction("收进文件夹里的游戏", "lgfolder", $"{folder} 款", () =>
            {
                if (!StConfirm("lgfolder")) return;
                var items = new JsonArray(Library.FolderGames.Select(g => (JsonNode)new JsonObject { ["name"] = g.Title, ["exe"] = g.Exe }).ToArray());
                _lgFolderTaken = true;
                _ = LgJob("local_games_add_many", items);
            }));
        int missing = games.Count(g => !LgHasArt(g, "p"));
        string artValue = busy && phase == "补封面" ? $"{Int(job?["done"])}/{Int(job?["total"])}" : missing > 0 ? $"{missing} 款" : "完整";
        _stRows.Add(new PerfRow { Title = "补全封面", Value = artValue, Disabled = s == null || (missing == 0 && !busy), Press = () => { if (!busy) _ = LgJob("local_games_fill_art"); } });
        int pending = Int(s?["pending"]);
        string syncValue = busy && phase is "关闭 Steam" or "写入 Steam" or "启动 Steam" ? phase
            : _stArmed == "lgsync" ? "再按一次确认"
            : pending > 0 ? $"{pending} 款" : "已同步";
        _stRows.Add(new PerfRow
        {
            Title = "同步到 Steam", Value = syncValue, Disabled = s == null || (pending == 0 && !busy),
            Press = () => { if (!busy && StConfirm("lgsync")) _ = LgJob("local_games_sync"); },
        });
        _stRows.Add(new PerfRow
        {
            Title = "SteamGridDB 密钥", Value = s == null ? "" : Bool(s["key"]) ? "已填" : "不填也能用",
            // 空着确认 = 不改（清掉密钥要输入「清除」）
            Press = () => StEditText("", key =>
            {
                key = key.Trim();
                if (key.Length == 0) return;
                _ = LgJob("set_steamgriddb_key", JsonValue.Create(key == "清除" ? "" : key));
            }),
        });
    }

    // ---------------- 本地游戏清单 ----------------

    private void LgOpenList()
    {
        _lgPage = LgPage.List;
        StPageReset();
    }

    private void StPageReset()
    {
        _stZone = 1;
        _stArmed = null;
        BuildStRows();
        _stFocus = FirstStRow();
        _stScroll = 0; _stScrollY = float.NaN;
        PaintSt();
    }

    /// <summary>B：清单回游戏库分类。</summary>
    private bool LgBack()
    {
        if (_lgPage != LgPage.List) return false;
        _lgPage = LgPage.None;
        StPageReset();
        return true;
    }

    private void BuildLgRows()
    {
        _stRows.Add(new PerfRow { Section = true, Title = "本地游戏" });
        foreach (var g in LgGames())
        {
            string state = !Bool(g["steam"]) ? "仅游戏库"
                : !Bool(g["inSteam"]) || g["artSynced"] is JsonValue { } sv && !Bool(sv) || g["collectionSynced"] is JsonValue { } cv && !Bool(cv) ? "待同步"
                : !Bool(g["collectionOk"]) ? "分类未保存"
                : !LgHasArt(g, "p") ? "缺封面" : "";
            var game = g;
            _stRows.Add(new PerfRow { Title = Str(g["name"]) ?? "", Value = state, Press = () => AgOpenEdit(game) });
        }
    }

    /// <summary>浏览里选中了一个 exe：已经加过的直接去改，没加过的进「选定游戏」。</summary>
    private void LgNewFromExe(string exe)
    {
        var existing = LgGames().FirstOrDefault(g => string.Equals(Str(g["exe"]), exe, StringComparison.OrdinalIgnoreCase));
        _stBrowse = null;
        _stBrowseExe = false;
        BuildStRows();
        _stFocus = FirstStRow();
        if (existing != null) AgOpenEdit(existing); else AgOpenNew(exe);
    }

    // ---------------- 选 exe（文件夹浏览的另一种用法） ----------------

    private void BuildStBrowseExes(string path)
    {
        string[] exes;
        try { exes = Directory.GetFiles(path, "*.exe").OrderBy(f => f, StringComparer.CurrentCulture).Take(200).ToArray(); }
        catch { return; }
        foreach (var exe in exes)
        {
            long mb = 0;
            try { mb = new FileInfo(exe).Length >> 20; } catch { }
            var target = exe;
            _stRows.Add(new PerfRow { Title = Path.GetFileName(exe), Value = $"{mb} MB", Press = () => LgNewFromExe(target) });
        }
    }

    // ---------------- 画图 ----------------

    private static void DrawCovered(CanvasDrawingSession ds, CanvasBitmap bmp, Rect dst)
    {
        var b = bmp.Size;
        double s = Math.Max(dst.Width / b.Width, dst.Height / b.Height);
        double sw = dst.Width / s, sh = dst.Height / s;
        ds.DrawImage(bmp, dst, new Rect((b.Width - sw) / 2, (b.Height - sh) / 2, sw, sh));
    }

    private static void DrawContained(CanvasDrawingSession ds, CanvasBitmap bmp, Rect dst)
    {
        var b = bmp.Size;
        double s = Math.Min(dst.Width / b.Width, dst.Height / b.Height);
        double w = b.Width * s, h = b.Height * s;
        ds.DrawImage(bmp, new Rect(dst.X + (dst.Width - w) / 2, dst.Y + (dst.Height - h) / 2, w, h), new Rect(0, 0, b.Width, b.Height));
    }

    // ---------------- 设置行里打字（密钥）：XAML TextBox 盖在焦点行的值上，那一行的值先不画 ----------------

    private void StEditText(string text, Action<string> done)
    {
        if (_stEditBox == null)
        {
            _stEditBox = new TextBox
            {
                MinHeight = 0, MinWidth = 0, BorderThickness = new Thickness(0), RequestedTheme = ElementTheme.Dark,
                IsSpellCheckEnabled = false, IsTextPredictionEnabled = false, TextAlignment = TextAlignment.Right,
                VerticalContentAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
                FontFamily = new FontFamily("Microsoft YaHei UI"),
            };
            var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            var ink = new SolidColorBrush(Theme.Ink);
            foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused" })
                _stEditBox.Resources[key] = clear;
            foreach (var key in new[] { "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused" })
                _stEditBox.Resources[key] = ink;
            _stEditBox.KeyDown += (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; StEditEnd(true); }
                else if (e.Key == Windows.System.VirtualKey.Escape) { e.Handled = true; StEditEnd(false); }
            };
            _stEditBox.LostFocus += (_, _) => { if (StEditing) StEditEnd(true); };
            _stEditBox.Loaded += (_, _) =>
            {
                if (FindChild(_stEditBox, "DeleteButton") is FrameworkElement x) { x.MaxWidth = 0; x.Opacity = 0; x.IsHitTestVisible = false; }
            };
            _rootEl.Overlay.Children.Add(_stEditBox);
        }
        if (_stFocus < 0 || _stFocus >= _stRows.Count) return;
        float top = 0;
        for (int i = 0; i < _stFocus; i++) top += _stRows[i].Section ? PerfSecH : PerfRowH;
        float k = _ccK;
        _stEditDone = done;
        _stEditTitle = _stRows[_stFocus].Title;
        Repaint();
        _stEditBox.Text = text;
        _stEditBox.FontSize = 17 * k;
        _stEditBox.Width = (PerfRightW - 320) * k;
        _stEditBox.Height = PerfRowH * k;
        _stEditBox.Padding = new Thickness(0);
        Canvas.SetLeft(_stEditBox, (PerfRightX + 320) * k);
        Canvas.SetTop(_stEditBox, (PerfTop + top - _stScroll) * k);
        _stEditBox.Visibility = Visibility.Visible;
        _stEditBox.Focus(FocusState.Programmatic);
        _stEditBox.SelectAll();
        ShowTouchKeyboard();
    }

    private void StEditEnd(bool commit)
    {
        if (_stEditBox == null || !StEditing) return;
        var done = _stEditDone;
        string text = _stEditBox.Text;
        _stEditDone = null;
        _stEditTitle = null;
        _stEditBox.Visibility = Visibility.Collapsed;
        _rootEl.Focus(FocusState.Programmatic);
        if (commit) done?.Invoke(text);
        if (_stOpen) Repaint();
    }

    /// <summary>手柄上没有键盘：打字时把触摸键盘叫出来（叫不出来就算了，接了键盘照样能打）。</summary>
    private void ShowTouchKeyboard()
    {
        try
        {
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            Windows.UI.ViewManagement.InputPaneInterop.GetForWindow(hwnd)?.TryShow();
        }
        catch (Exception ex) { Log.Warn($"touch keyboard: {ex.Message}"); }
    }
}
