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
    /// <summary>游戏设置中选中程序或快捷方式之后做什么；null = 添加游戏（只选 exe）。</summary>
    private Action<string>? _stBrowsePick;
    private JsonObject? _lgState;
    private readonly Dictionary<string, CanvasBitmap?> _lgBitmaps = new();
    private TextBox? _stEditBox;
    private PasswordBox? _stSecretBox;
    private Action<string>? _stEditDone;
    /// <summary>正在打字的那一行（它的值先不画，免得和输入框里的字叠在一起）。</summary>
    private string? _stEditTitle;

    private bool StEditing => _stEditBox?.Visibility == Visibility.Visible || _stSecretBox?.Visibility == Visibility.Visible;

    // ---------------- 后台 ----------------

    private async Task LgRefresh()
    {
        try
        {
            _agent ??= new AgentClient();
            bool wasBusy = Bool(_lgState?["busy"]);
            if (await _agent.Call("local_games_state") is JsonObject o) _lgState = (JsonObject)o.DeepClone();
            if (_servicesDisposed) return;
            string stamp = $"{_lgState?["games"]?.ToJsonString()}|{_lgState?["collections"]?.ToJsonString()}|{_lgState?["lastSync"]?.ToJsonString()}";
            if (_lgState != null && (_localLibraryStamp != stamp || wasBusy && !Bool(_lgState["busy"])))
            {
                _localLibraryStamp = stamp;
                await RefreshLocalLibraryAsync();
            }
            if (wasBusy && !Bool(_lgState?["busy"]))
            {
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
            while (!_servicesDisposed && Bool(_lgState?["busy"]))
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
        _stRows.Add(StAction("添加游戏", "lgadd", "", () => { _stBrowseExe = true; StBrowseTo(""); }));
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
            Title = "同步到 Steam（将重启 Steam）", Value = syncValue, Disabled = s == null || (pending == 0 && !busy),
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
        _agFromLibrary = _stBrowseFromLibrary;
        _stBrowseFromLibrary = false;
        BuildStRows();
        _stFocus = FirstStRow();
        if (existing != null) AgOpenEdit(existing); else AgOpenNew(exe);
    }

    // ---------------- 选 exe（文件夹浏览的另一种用法） ----------------

    private void BuildStBrowseExes(string path)
    {
        string[] exes;
        try { exes = Directory.EnumerateFiles(path).Where(f => Path.GetExtension(f).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || _stBrowsePick != null && Path.GetExtension(f).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.CurrentCulture).Take(200).ToArray(); }
        catch { return; }
        foreach (var exe in exes)
        {
            long mb = 0;
            try { mb = new FileInfo(exe).Length >> 20; } catch { }
            var target = exe;
            bool shortcut = Path.GetExtension(exe).Equals(".lnk", StringComparison.OrdinalIgnoreCase);
            _stRows.Add(new PerfRow { Title = Path.GetFileName(exe), Value = shortcut ? "快捷方式" : $"{mb} MB",
                Press = () => { if (_stBrowsePick is { } pick) pick(target); else LgNewFromExe(target); } });
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

    /// <summary>打字时列表多往上推的量（设计 px）；打字的那一行底边不低于 StKbLimit（屏幕键盘在这台掌机上占下面 400）。</summary>
    private float _stKbLift;
    private const float StKbLimit = 360;

    private void StEditText(string text, Action<string> done)
    {
        if (_stEditBox == null)
        {
            _stEditBox = new TextBox
            {
                MinHeight = 0, MinWidth = 0, BorderThickness = new Thickness(0), RequestedTheme = ElementTheme.Dark,
                IsSpellCheckEnabled = false, IsTextPredictionEnabled = false, TextAlignment = TextAlignment.Right,
                VerticalContentAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
                FontFamily = new FontFamily(TextKit.XamlUi),
            };
            StyleInlineInput(_stEditBox);
            _stEditBox.KeyDown += (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; StEditEnd(true); }
                else if (e.Key == Windows.System.VirtualKey.Escape) { e.Handled = true; StEditEnd(false); }
            };
            _stEditBox.LostFocus += (_, _) => { if (_stEditBox.Visibility == Visibility.Visible) StEditEnd(true); };
            _stEditBox.Loaded += (_, _) =>
            {
                if (FindChild(_stEditBox, "DeleteButton") is FrameworkElement x) { x.MaxWidth = 0; x.Opacity = 0; x.IsHitTestVisible = false; }
            };
            HookTouchKeyboard(_stEditBox);
            _rootEl.Overlay.Children.Add(_stEditBox);
        }
        _stEditBox.Text = text;
        PositionInlineInput(_stEditBox, done);
        FocusTextBox(_stEditBox, () => StEditing);
    }

    private void StEditEnd(bool commit)
    {
        if (!StEditing) return;
        var done = _stEditDone;
        string text = _stSecretBox?.Visibility == Visibility.Visible ? _stSecretBox.Password : _stEditBox?.Text ?? "";
        _stEditDone = null;
        _stEditTitle = null;
        _stKbLift = 0;
        if (_stEditBox != null) { _stEditBox.Visibility = Visibility.Collapsed; _stEditBox.Text = ""; }
        if (_stSecretBox != null) { _stSecretBox.Visibility = Visibility.Collapsed; _stSecretBox.Password = ""; }
        _rootEl.Focus(FocusState.Programmatic);
        if (commit) done?.Invoke(text);
        if (_stOpen) Repaint();
    }

    private void StEditSecret(Action<string> done)
    {
        if (_stFocus < 0 || _stFocus >= _stRows.Count) return;
        _stSecretBox ??= CreateSecretBox();
        _stSecretBox.Password = "";
        PositionInlineInput(_stSecretBox, done);
        _dq.TryEnqueue(() => { if (_stSecretBox.Visibility == Visibility.Visible) { (_window as MainWindow)?.RequestTextInputFocus(); _stSecretBox.Focus(FocusState.Programmatic); } });
    }

    // All settings inputs share the existing inline editor's type, colours and placement.
    // PasswordBox only supplies masking; it does not introduce another visual component.
    private static void StyleInlineInput(Control box)
    {
        box.FontFamily = new FontFamily(TextKit.XamlUi);
        box.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        box.Foreground = new SolidColorBrush(Theme.Ink);
        box.BorderThickness = new Thickness(0);
        box.Padding = new Thickness(0);
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBackgroundDisabled",
            "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused", "TextControlBorderBrushDisabled" }) box.Resources[key] = clear;
        foreach (var key in new[] { "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused" }) box.Resources[key] = box.Foreground;
    }

    private void PositionInlineInput(Control box, Action<string> done)
    {
        if (_stFocus < 0 || _stFocus >= _stRows.Count) return;
        float top = 0;
        for (int i = 0; i < _stFocus; i++) top += _stRows[i].Section ? PerfSecH : PerfRowH;
        _stEditDone = done; _stEditTitle = _stRows[_stFocus].Title;
        _stKbLift = Math.Max(0, PerfTop + top - _stScroll + PerfRowH - StKbLimit);
        _stScroll += _stKbLift; Repaint();
        box.Width = (StRightW - 320) * _ccK; box.Height = PerfRowH * _ccK; box.FontSize = 17 * _ccK;
        Canvas.SetLeft(box, (StRightX + 320) * _ccK); Canvas.SetTop(box, (PerfTop + top - _stScroll) * _ccK);
        box.Visibility = Visibility.Visible;
    }

    private PasswordBox CreateSecretBox()
    {
        var box = new PasswordBox { MinHeight = 0, MinWidth = 0, MaxLength = 32, PasswordRevealMode = PasswordRevealMode.Hidden,
            RequestedTheme = ElementTheme.Dark, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Right,
            Visibility = Visibility.Collapsed };
        StyleInlineInput(box);
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; StEditEnd(true); }
            else if (e.Key == Windows.System.VirtualKey.Escape) { e.Handled = true; StEditEnd(false); }
        };
        box.GotFocus += (_, _) => ShowTouchKeyboard();
        box.LostFocus += (_, _) => { if (box.Visibility == Visibility.Visible) StEditEnd(false); HideTouchKeyboardSoon(); };
        _rootEl.Overlay.Children.Add(box);
        return box;
    }

    // ---------------- 从游戏库添加 ----------------

    /// <summary>添加游戏页是从游戏库竖条的 ＋ 进来的：完成 / 退出后直接回游戏库。</summary>
    private bool _agFromLibrary;

    /// <summary>选 exe 的那一页是从游戏库竖条的 ＋ 进来的：在最上一层按 B 直接回游戏库，选好 exe 后添加完也回游戏库。</summary>
    private bool _stBrowseFromLibrary;

    /// <summary>
    /// 竖条 ＋：照 folia 的进场跳到我们自己的选 exe 页（设置 › 游戏库 › 添加游戏… 那一页，磁盘 → 文件夹 → exe），
    /// 选好直接进添加游戏页；已经加过的进编辑。
    /// </summary>
    private async Task AddGameFromLibrary()
    {
        _ = LgRefresh();
        _backgroundSettingsOpen = false;
        if (!_ccOpen) OpenSettingsFromLibrary();
        else if (!_stOpen) OpenSettingsPage();
        _stCat = 1;
        _stZone = 1;
        _stBrowseFromLibrary = true;
        _stBrowseExe = true;
        StBrowseTo("");
        await Task.CompletedTask;
    }

    // ---------------- 屏幕键盘 ----------------

    private bool _kbPending;

    /// <summary>
    /// 输入框拿到焦点就叫触摸键盘（所有输入框都挂在 GotFocus 上，手柄 A、鼠标、手指点都走这里）：先请系统出，
    /// 0.6 秒后还没挡住窗口（手柄被 Windows 当成硬件键盘时可能这样）就用 TabTip 打开。接了键盘照样能打。
    /// </summary>
    private void ShowTouchKeyboard()
    {
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        bool asked = TouchKeyboard.TryShow(hwnd);
        if (_kbPending) return;
        _kbPending = true;
        After(600, () =>
        {
            _kbPending = false;
            if (!TextInputFocused() || TouchKeyboard.Visible(hwnd)) return;
            // 游戏模式没有 explorer，系统触摸键盘（TextInputHost）不在：用经典屏幕键盘 osk
            if (ShellMode.Available && !ShellMode.ExplorerRunning())
            {
                if (System.Diagnostics.Process.GetProcessesByName("osk").Length == 0) SystemInfo.Open("osk.exe");
                Log.Info("touch keyboard: game mode, opening osk");
                return;
            }
            Log.Info($"touch keyboard: system pane {(asked ? "did not show" : "refused")}, opening TabTip");
            TouchKeyboard.Toggle();
        });
    }

    /// <summary>输入框失去焦点：稍等一下（焦点可能只是换到另一个输入框），没有输入框在打字就收起键盘。</summary>
    private void HideTouchKeyboardSoon()
    {
        After(150, () =>
        {
            if (TextInputFocused()) return;
            TouchKeyboard.Hide(WinRT.Interop.WindowNative.GetWindowHandle(_window));
        });
    }

    private bool TextInputFocused() =>
        _rootEl.XamlRoot != null && Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(_rootEl.XamlRoot) is TextBox or PasswordBox;

    /// <summary>
    /// 给输入框焦点：等这次点按 / 按键处理完再给。手指、鼠标点下去那一下舞台会拿走焦点，松手时同步给输入框的话
    /// 马上又被抢回去，输入框一失焦就收掉了（点了没反应就是这个）。
    /// </summary>
    private void FocusTextBox(TextBox box, Func<bool> stillWanted)
    {
        _dq.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!stillWanted() || box.Visibility != Visibility.Visible) return;
            (_window as MainWindow)?.RequestTextInputFocus();
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        });
    }

    /// <summary>每个输入框建好时挂上：拿到焦点叫键盘，失去焦点收键盘；打字时一下一下的按键声（只在有焦点时，程序改字不响）。</summary>
    private void HookTouchKeyboard(TextBox box)
    {
        box.GotFocus += (_, _) => ShowTouchKeyboard();
        box.LostFocus += (_, _) => HideTouchKeyboardSoon();
        box.TextChanging += (s, e) => { if (e.IsContentChanging && s.FocusState != Microsoft.UI.Xaml.FocusState.Unfocused) SoundFx.Play(Sfx.Typing); };
    }
}
