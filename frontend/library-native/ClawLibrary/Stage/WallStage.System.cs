// 控制中心 → 系统：和性能、显示与声音页同一套 3:7 两栏（功能点见 docs/system-page-features.md）。
// 左栏只看：电池（电量、充放电功率、剩余时间、健康度）、版本、设备；右栏只改：电池与电源、电源操作、更新与维护、启动。
// 全部是全局设置，没有「当前游戏」。
// 读：工具箱后台 get_system_state；老后台没有它时右栏写「后台还没接上」，电源操作照样能用（前端自己做）。
// 写：每项一个 set_*，本地先改（界面立刻跟着变），写完读回真实值。
// 还原这种收不回来的：第一下只把这一行变成「再按一次确认」，3 秒内再按才做。
// 手柄：↑ ↓ 选项，← → 改值，A 执行 / 开关，B 回控制中心。
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Composition;
using Windows.UI;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private ContainerVisual? _sy;
    private ContainerVisual _syRightView = null!;
    private SpriteVisual _syTitle = null!, _syLeft = null!, _syRight = null!;
    private bool _syOpen, _syBusy, _syAgentDown, _syOldAgent;
    private string? _syError;
    private long _syReadAt;
    private int _syFocus, _syVersion, _syBackupPick;
    private SettingsWriteQueue? _syWrites;
    private float _syScroll, _syScrollY = float.NaN;
    /// <summary>后台的系统状态（get_system_state 的结果）。本地改动直接写在这里。</summary>
    private JsonObject? _syView;
    private readonly List<PerfRow> _syRows = new();
    private DispatcherQueueTimer? _syTimer;
    /// <summary>等第二下确认的那一行（还原、BIOS、全部更新），和第一下的时间。</summary>
    private string? _syArmed;
    private long _syArmedAt;

    private static readonly int[] SyCharge = { 0, 60, 80, 90 };
    private static readonly string[] SyChargeNames = { "关", "60 %", "80 %", "90 %" };
    private static readonly string[] SyButtons ={ "sleep", "hibernate", "shutdown" };
    private static readonly string[] SyButtonNames = { "睡眠", "休眠", "关机" };
    private static readonly int[] SyHibernate = { 0, 1800, 3600, 10800 };
    private static readonly string[] SyHibernateNames = { "不休眠", "30 分钟", "1 小时", "3 小时" };
    private static readonly int[] SyDisplay = { 180, 300, 600, 1800 };
    private static readonly string[] SyDisplayNames = { "3 分钟", "5 分钟", "10 分钟", "30 分钟" };
    private const string SyOwnHome = "ClawToolbox.Library_";

    // ---------------- 开 / 关 ----------------

    private void OpenSystem()
    {
        if (_cc == null) return;
        EnsureSyVisuals();
        _syOpen = true;
        _syArmed = null;
        SettingsResetPointer();
        ++_syVersion;
        BuildSyRows();
        _syFocus = _syRows.FindIndex(r => !r.Section && !r.Skip);
        _syScroll = 0;
        _syScrollY = float.NaN;
        PaintSy();
        foreach (var vis in CcPageParts()) vis.StartAnimation("Opacity", ToScalar(0, 160, EaseOutCss));
        _sy!.IsVisible = true;
        _sy.Opacity = 0;
        _sy.StartAnimation("Opacity", ToScalar(1, 240, EaseOutCss, 80));
        _syTimer ??= MakeSyTimer();
        _syTimer.Start();
        _ = SyRefresh();
        Log.Info("system page open");
    }

    private void CloseSystem(bool instant = false)
    {
        if (!_syOpen || _sy == null) return;
        _syOpen = false;
        SettingsResetPointer();
        _syTimer?.Stop();
        int v = ++_syVersion;
        if (instant || Reduced)
        {
            _sy.StopAnimation("Opacity"); _sy.IsVisible = false; _sy.Opacity = 1;
            foreach (var vis in CcPageParts()) { vis.StopAnimation("Opacity"); vis.Opacity = 1; }
            return;
        }
        _sy.StartAnimation("Opacity", ToScalar(0, 140, EaseOutCss));
        foreach (var vis in CcPageParts()) vis.StartAnimation("Opacity", ToScalar(1, 220, EaseOutCss, 60));
        After(160, () => { if (v == _syVersion && !_syOpen && _sy != null) { _sy.IsVisible = false; _sy.StopAnimation("Opacity"); _sy.Opacity = 1; } });
        Log.Info("system page close");
    }

    private void EnsureSyVisuals()
    {
        if (_sy != null) return;
        _sy = G.Container();
        _sy.IsVisible = false;
        _syTitle = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        _syLeft = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        _syRight = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        _syRightView = G.Container(relative: false);
        _syRightView.Clip = G.C.CreateInsetClip();
        _syRightView.Children.InsertAtTop(_syRight);
        foreach (var s in new Visual[] { _syTitle, _syLeft, _syRightView }) _sy.Children.InsertAtTop(s);
        _cc!.Children.InsertAtTop(_sy);
    }

    /// <summary>
    /// 平时五秒读一次（电量、功率在变，别处改了电源设置要跟上；后台每次都要起 powercfg 查唤醒设备，所以不读太勤）；
    /// 正在检查更新或在下载、安装时每秒读一次，进度跟得上。
    /// </summary>
    private DispatcherQueueTimer MakeSyTimer()
    {
        var t = _dq.CreateTimer();
        t.Interval = TimeSpan.FromSeconds(1);
        t.IsRepeating = true;
        t.Tick += (_, _) =>
        {
            if (_syArmed != null && NowMs - _syArmedAt > 3000) { _syArmed = null; BuildSyRows(); PaintSy(); }
            if (_syOpen && !_settingsGesture.Active && _syWrites?.IsBusy != true && (SyUpdating || NowMs - _syReadAt >= 5000)) _ = SyRefresh();
        };
        return t;
    }

    private bool SyUpdating => _syView?["vendorUpdates"] is JsonObject u && (Str(u["state"]) == "checking" || Str(u["active"]) != null);

    // ---------------- 和 agent 打交道 ----------------

    private async Task SyRefresh()
    {
        if (_syBusy || _settingsGesture.Active || _syWrites?.IsBusy == true) return;
        _syBusy = true;
        long revision = _syWrites?.Revision ?? 0;
        try
        {
            _agent ??= new AgentClient();
            var view = await _agent.Call("get_system_state");
            _syAgentDown = _syOldAgent = false;
            _syError = null;
            if (!_settingsGesture.Active && _syWrites?.IsBusy != true && revision == (_syWrites?.Revision ?? 0) && view is JsonObject o)
                _syView = (JsonObject)o.DeepClone();
        }
        catch (Exception ex) when (ex.Message.Contains("不支持的后台调用")) { _syOldAgent = true; }
        catch (InvalidOperationException ex)
        {
            // 后台连上了但这次读出错：保留上次的读数，左下角写出错原因（不是「连不上」）
            if (_syError != ex.Message) Log.Warn($"system refresh: {ex.Message}");
            _syError = ex.Message;
            _syAgentDown = false;
        }
        catch (Exception ex)
        {
            if (!_syAgentDown) Log.Warn($"system refresh: {ex.Message}");
            _syAgentDown = true;
        }
        finally { _syBusy = false; _syReadAt = NowMs; }
        if (!_syOpen) return;
        if (_syWrites?.IsBusy != true && revision != (_syWrites?.Revision ?? 0)) { _ = SyRefresh(); return; }
        BuildSyRows();
        PaintSy();
    }

    private void SySet(string method, params JsonNode?[] args)
    {
        _syWrites ??= new SettingsWriteQueue(
            ex => Toast(ex.Message.Contains("不支持的后台调用") ? $"这一项{DsLater}" : $"没有改成：{ex.Message}"),
            () => { _ = SyRefresh(); });
        _ = _syWrites.Enqueue(method, 0, async () =>
        {
            _agent ??= new AgentClient();
            await _agent.Call(method, args);
        });
        BuildSyRows();
        PaintSy();
    }

    /// <summary>一次性的动作（备份、还原、检查更新……）：等后台做完，按结果给提示。</summary>
    private async Task SyRun(string method, Func<JsonNode?, string> done, params JsonNode?[] args)
    {
        try
        {
            _agent ??= new AgentClient();
            var result = await _agent.Call(method, args);
            Toast(done(result));
        }
        catch (Exception ex) { Toast(ex.Message.Contains("不支持的后台调用") ? $"这一项{DsLater}" : $"没有做成：{ex.Message}"); }
        if (_syOpen) _ = SyRefresh();
    }

    /// <summary>收不回来的动作：第一下只是「再按一次确认」，3 秒内同一行再按才做。</summary>
    private bool SyConfirm(string key)
    {
        if (_syArmed == key && NowMs - _syArmedAt <= 3000) { _syArmed = null; return true; }
        _syArmed = key;
        _syArmedAt = NowMs;
        BuildSyRows();
        PaintSy();
        return false;
    }

    // ---------------- 右栏的行 ----------------

    private void BuildSyRows()
    {
        _syRows.Clear();
        var v = _syView;
        string Armed(string key, string idle) => _syArmed == key ? "再按一次确认" : idle;
        PerfRow Act(string title, string key, string idle, Action run) => new()
        {
            Title = title, Value = Armed(key, idle), Press = run,
        };
        PerfRow Later(string title, string? why = null) => new() { Title = title, Value = why ?? DsLater, Disabled = true };
        PerfRow Pick(string title, string[] names, int idx, Action<int> set, string note = "")
        {
            idx = Math.Clamp(idx, 0, names.Length - 1);
            return new PerfRow
            {
                Title = title, Choices = names, Choice = idx, Value = note,
                Step = d => { int ni = Math.Clamp(idx + d, 0, names.Length - 1); if (ni != idx) set(ni); },
                Press = () => set((idx + 1) % names.Length),
                Select = set,
            };
        }

        // ---- 电池与电源 ----
        _syRows.Add(new PerfRow { Section = true, Title = "电池与电源" });
        if (v == null)
        {
            string why = _syOldAgent ? DsLater : _syAgentDown ? "连不上工具箱后台" : "正在读取…";
            _syRows.Add(Later("充电上限", why));
            _syRows.Add(Later("按电源键", why));
        }
        else
        {
            // 充电上限：MSI 固件 0xD7 那一个字节（和 ClawTweaks、MSI Center M 写的是同一个）
            if (v["chargeLimit"] is JsonObject limit)
            {
                int cur = limit["percent"] is JsonValue ? Int(limit["percent"]) : 0;
                int li = Array.IndexOf(SyCharge, cur);
                _syRows.Add(Pick("充电上限", SyChargeNames, li < 0 ? 0 : li, i =>
                {
                    if (SyCharge[i] == 0) limit.Remove("percent"); else limit["percent"] = SyCharge[i];
                    SySet("set_charge_limit", JsonValue.Create(SyCharge[i]));
                }, li < 0 ? $"现在是 {cur} %" : ""));
            }
            else _syRows.Add(Later("充电上限", Str(v["chargeLimitError"]) is string ce ? $"读不到：{ce}" : "读不到"));

            var power = v["power"] as JsonObject;
            if (power == null) _syRows.Add(Later("按电源键", Str(v["powerError"]) ?? "读不到电源设置"));
            else
            {
                bool hib = Bool(power["hibernateAvailable"]);
                string[] ids = hib ? SyButtons : new[] { "sleep", "shutdown" };
                string[] names = hib ? SyButtonNames : new[] { "睡眠", "关机" };
                string cur = Str(power["buttonDc"]) ?? "sleep";
                int bi = Array.IndexOf(ids, cur);
                _syRows.Add(Pick("按电源键", names, bi < 0 ? 0 : bi, i => { power["buttonDc"] = power["buttonAc"] = ids[i]; SySet("set_power_button", JsonValue.Create(ids[i])); },
                    Str(power["buttonAc"]) != cur ? "插电时不同" : ""));
                if (hib)
                {
                    int hi = Array.IndexOf(SyHibernate, Int(power["hibernateDc"], -1));
                    _syRows.Add(Pick("睡眠多久后转休眠", SyHibernateNames, hi < 0 ? 2 : hi, i => { power["hibernateDc"] = power["hibernateAc"] = SyHibernate[i]; SySet("set_hibernate_after", JsonValue.Create(SyHibernate[i])); },
                        hi < 0 ? "现在是自定义时间" : ""));
                }
                else _syRows.Add(Later("睡眠多久后转休眠", "这台机器没有开启休眠"));
                int di = Array.IndexOf(SyDisplay, Int(power["displayDc"], -1));
                _syRows.Add(Pick("电池下多久关屏", SyDisplayNames, di < 0 ? 2 : di, i => { power["displayDc"] = SyDisplay[i]; SySet("set_display_off", JsonValue.Create(SyDisplay[i])); },
                    di < 0 ? (Int(power["displayDc"]) == 0 ? "现在是从不关屏" : "现在是自定义时间") : ""));
            }

            if (v["wakeArmed"] is JsonArray armed)
            {
                bool locked = armed.Count == 0;
                bool canUnlock = (v["wakeRestore"] as JsonArray)?.Count > 0;
                void Set(bool to)
                {
                    if (!to && !canUnlock) { Toast("关掉唤醒的设备不是工具箱改的，要在设备管理器里逐个打开"); return; }
                    v["wakeArmed"] = to ? new JsonArray() : new JsonArray(JsonValue.Create("…"));
                    SySet("set_wake_lock", JsonValue.Create(to));
                }
                _syRows.Add(new PerfRow
                {
                    Title = "只允许电源键唤醒", Choices = new[] { "关", "开" }, Choice = locked ? 1 : 0,
                    Value = locked ? "" : $"{armed.Count} 个设备可唤醒",
                    Step = d => { if ((d > 0) != locked) Set(d > 0); },
                    Press = () => Set(!locked),
                    Select = i => Set(i == 1),
                });
            }
            else _syRows.Add(Later("只允许电源键唤醒", Str(v["wakeError"]) ?? "读不到唤醒设备"));
        }

        // ---- 更新与维护 ----
        _syRows.Add(new PerfRow { Section = true, Title = "更新与维护" });
        var upd = v?["updates"] as JsonObject;
        string updText = Str(upd?["state"]) switch
        {
            "done" => Int(upd!["count"]) is int n && n > 0 ? $"{n} 个待安装" : "已是最新",
            "checking" => "正在检查…",
            "error" => "检查失败",
            _ => v == null ? "—" : "还没检查",
        };
        _syRows.Add(Act("Windows 更新", "wu", updText, () => { SystemInfo.Open("ms-settings:windowsupdate"); Toast("正在打开 Windows 更新"); }));
        SyUpdateRows(v);
        if (_syFocus >= _syRows.Count || (_syFocus >= 0 && (_syRows[_syFocus].Skip || _syRows[_syFocus].Section)))
        {
            int f = Math.Min(_syFocus, _syRows.Count - 1);
            while (f > 0 && (_syRows[f].Section || _syRows[f].Skip)) f--;
            _syFocus = f;
        }
    }

    /// <summary>
    /// 驱动、BIOS、手柄固件更新：后台直接从微星和 Intel 官网找（第一次打开系统页时自动查一次）。
    /// 「检查驱动和固件更新」重新查；每个更新一行，A 下载并静默安装；两个以上时有「全部更新」
    /// （BIOS、手柄固件不在里面，要单独按两下确认）。
    /// </summary>
    private PerfRow SyAct(string title, string key, string idle, Action run) =>
        new() { Title = title, Value = _syArmed == key ? "再按一次确认" : idle, Press = run };

    private void SyUpdateRows(JsonObject? v)
    {
        var u = v?["vendorUpdates"] as JsonObject;
        var items = (u?["items"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? Array.Empty<JsonObject>();
        string state = Str(u?["state"]) ?? "";
        string active = Str(u?["active"]) ?? "";
        var queued = (u?["queued"] as JsonArray)?.Select(n => Str(n) ?? "").ToHashSet() ?? new HashSet<string>();
        var pending = items.Where(i => Str(i["result"]) is not ("done" or "restart")).ToArray();
        string errors = (u?["errors"] as JsonArray)?.Count > 0 ? " · " + string.Join("；", (u!["errors"] as JsonArray)!.Select(e => Str(e))) : "";
        string checkText = state switch
        {
            "checking" => "正在从微星和 Intel 官网查…",
            "error" => $"没查成：{Str(u!["error"])}",
            "done" or "partial" => (pending.Length > 0 ? $"{pending.Length} 个可更新" : "已是最新") + $" · {Str(u!["checked"])} 检查" + errors,
            _ => v == null ? "—" : "还没检查",
        };
        _syRows.Add(SyAct("检查驱动和固件更新", "check", checkText, () =>
        {
            if (state == "checking") return;
            if (u != null) { u["state"] = "checking"; BuildSyRows(); PaintSy(); }
            _ = SyRun("check_driver_updates", _ => "正在从微星和 Intel 官网查更新");
        }));
        var batch = pending.Where(i => !Bool(i["confirm"]) && Str(i["id"]) != active && !queued.Contains(Str(i["id"]) ?? "")).ToArray();
        if (batch.Length >= 2)
            _syRows.Add(SyAct($"全部更新（{batch.Length} 项）", "all", "下载并安装，装完自动复查版本", () =>
            {
                if (!SyConfirm("all")) return;
                var ids = new JsonArray(batch.Select(i => (JsonNode?)JsonValue.Create(Str(i["id"]))).ToArray());
                _ = SyRun("install_updates", _ => $"开始更新 {batch.Length} 项", ids);
            }));
        foreach (var item in items)
        {
            string id = Str(item["id"]) ?? "", key = "u:" + id;
            string vendor = Str(item["vendor"]) == "msi" ? "微星" : "Intel";
            string title = $"{Str(item["title"])} · {vendor}";
            string result = Str(item["result"]) ?? "";
            string value;
            if (id == active && u?["job"] is JsonObject job)
            {
                long bytes = (long)(Num(job["bytes"]) ?? 0), total = (long)(Num(job["total"]) ?? 0);
                value = Str(job["state"]) switch
                {
                    "downloading" => total > 0 ? $"下载中 {bytes * 100 / total} % · {bytes >> 20} / {total >> 20} MB" : $"下载中 {bytes >> 20} MB",
                    "verifying" => "校验中…",
                    "installing" => "安装中…",
                    _ => "收尾中…",
                };
            }
            else if (queued.Contains(id)) value = "排队中";
            else if (result == "done") value = "已更新";
            else if (result == "restart") value = "已更新，重启后生效";
            else if (result.StartsWith("failed")) value = "失败：" + result[(result.IndexOf(':') + 1)..].Trim();
            else
            {
                long size = (long)(Num(item["size"]) ?? 0);
                value = $"{Str(item["current"])} → {Str(item["latest"])}" + (size > 0 ? $" · {Math.Max(1, size >> 20)} MB" : "");
                if (_syArmed == key) value = "再按一次确认" + (Str(item["note"]) is string note ? $"（{note}）" : "");
            }
            bool busy = id == active || queued.Contains(id);
            _syRows.Add(new PerfRow
            {
                Title = title, Value = value,
                Press = () =>
                {
                    if (busy || result is "done" or "restart") return;
                    if (Bool(item["confirm"]) && !SyConfirm(key)) return;
                    _ = SyRun("install_updates", _ => $"开始更新{Str(item["title"])}", new JsonArray(JsonValue.Create(id)));
                },
            });
        }
    }

    private static string SyHomeName(string? aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return "—";
        if (aumid.StartsWith(SyOwnHome, StringComparison.OrdinalIgnoreCase)) return "本前端";
        if (aumid.StartsWith("Microsoft.GamingApp", StringComparison.OrdinalIgnoreCase)) return "Xbox";
        string family = aumid.Split('!')[0].Split('_')[0];
        int dot = family.LastIndexOf('.');
        return dot >= 0 ? family[(dot + 1)..] : family;
    }

    // ---------------- 输入 ----------------

    private void SyMove(Direction dir)
    {
        SettingsResumeKeyboard();
        if (dir is Direction.Up or Direction.Down)
        {
            int d = dir == Direction.Up ? -1 : 1;
            for (int i = _syFocus + d; i >= 0 && i < _syRows.Count; i += d)
                if (!_syRows[i].Section && !_syRows[i].Skip)
                {
                    _syFocus = i;
                    if (_syArmed != null) { _syArmed = null; BuildSyRows(); }
                    PaintSy();
                    return;
                }
            return;
        }
        if (_syFocus >= 0 && _syFocus < _syRows.Count && !_syRows[_syFocus].Disabled) _syRows[_syFocus].Step?.Invoke(dir == Direction.Left ? -1 : 1);
    }

    private void SyActivate()
    {
        SettingsResumeKeyboard();
        if (_syFocus < 0 || _syFocus >= _syRows.Count) return;
        var r = _syRows[_syFocus];
        if (r.Disabled) { if (r.Value == DsLater) Toast($"这一项{DsLater}"); return; }
        r.Press?.Invoke();
    }

    private void SyBack() => CloseSystem();

    // ---------------- 绘制 ----------------

    private void PaintSy()
    {
        if (_sy == null) return;
        float k = _ccK, raster = k * Chrome.Dpi;
        float h = RowsHeight(_syRows), vh = PerfViewH;
        using (var tl = TextKit.Layout(G.Device, "系统", TextKit.Ui(700), 24, 400, CanvasHorizontalAlignment.Left))
        {
            var bb = tl.LayoutBounds;
            float w = (float)bb.Width + 4, th = (float)bb.Height + 4;
            CcSet(_syTitle, CcDraw(w, th, raster, ds => ds.DrawTextLayout(tl, -(float)bb.X, -(float)bb.Y, CcText)));
            _syTitle.Size = new Vector2(w * k, th * k);
            _syTitle.Offset = new Vector3(40 * k, 32 * k, 0);
        }
        _settingsTargets.Clear();
        CcSet(_syRight, CcDraw(PerfRightW, h, raster, ds => PaintRowList(ds, _syRows, _syFocus, _settingsTargets)));
        _syRight.Size = new Vector2(PerfRightW * k, h * k);
        _syRightView.Size = new Vector2(PerfRightW * k, vh * k);
        _syRightView.Offset = new Vector3(PerfRightX * k, PerfTop * k, 0);
        RowScrollTo(_syRight, _syRows, _settingsManualScroll ? -1 : _syFocus, ref _syScroll, ref _syScrollY, h, vh, false);
        CcSet(_syLeft, CcDraw(PerfLeftW, vh, raster, ds => PaintSyLeft(ds, vh)));
        _syLeft.Size = new Vector2(PerfLeftW * k, vh * k);
        _syLeft.Offset = new Vector3(PerfLeftX * k, PerfTop * k, 0);
    }

    /// <summary>左栏只放读数，和显示与声音页左栏一个写法：「电池」标题 + 电量、一根电量条、下面几行；「版本」「设备」各一组。</summary>
    private void PaintSyLeft(CanvasDrawingSession ds, float h)
    {
        var ui = TextKit.Ui(400);
        var v = _syView;
        float W = PerfLeftW;
        var accent = PerfAccent;
        var muted = Gfx.WithAlpha(CcText, 0.5);
        var bright = Gfx.WithAlpha(CcText, 0.88);
        const float header = 30, rowH = 28, between = 28;

        void Header(float top, string title, string value, Color vc)
        {
            PText(ds, title, ui, 13, muted, 0, top + header / 2, 300);
            if (value.Length == 0) return;
            float vw = PWidth(ds, value, TextKit.Ui(300), 20);
            PText(ds, value, TextKit.Ui(300), 20, vc, W - vw, top + header / 2, vw + 4);
        }
        float Rows(float top, List<(string k, string v)> rows)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                float cy = top + i * rowH + rowH / 2;
                PText(ds, rows[i].k, ui, 14, muted, 0, cy, 200);
                string val = GpFit(ds, rows[i].v, ui, 14, W - 100);
                float vw = PWidth(ds, val, ui, 14);
                PText(ds, val, ui, 14, bright, W - vw, cy, vw + 4);
            }
            return top + rows.Count * rowH;
        }

        // ---- 电池：电量直接读 Windows（和竖栏同一个来源），其余等后台 ----
        var bat = SystemInfo.Battery();
        var b = v?["battery"] as JsonObject;
        Header(0, "电池", bat.HasBattery ? $"{bat.Percent} %" : "—", accent);
        float barY = header + 14;
        ds.FillRoundedRectangle(0, barY - 3, W, 6, 3, 3, Gfx.WithAlpha(CcText, 0.14f));
        if (bat.HasBattery && bat.Percent > 0)
            ds.FillRoundedRectangle(0, barY - 3, Math.Max(6, W * bat.Percent / 100f), 6, 3, 3, accent);
        var rows = new List<(string, string)>();
        double? watts = Num(b?["watts"]);
        string state = !bat.HasBattery ? "—"
            : bat.OnAc ? (watts is > 0.3 ? $"充电中 · {watts:0.0} W" : bat.Percent >= 99 ? "已充满" : "已接通电源")
            : watts is < -0.3 ? $"放电 · {-watts:0.0} W" : "使用电池";
        rows.Add(("状态", state));
        // 插着电：预计充电时间（到充电上限或充满）；用电池：预计使用时间。固件不报功率，后台要攒十几秒的电量变化才算得出来。
        int minutes = Int(b?[bat.OnAc ? "chargeMinutes" : "minutes"]);
        int cap = v?["chargeLimit"]?["percent"] is JsonValue lim ? Int(lim) : 100;
        string eta = minutes > 0 ? (minutes >= 60 ? $"{minutes / 60} 小时 {minutes % 60:00} 分" : $"{minutes} 分钟")
            : !bat.HasBattery ? "—"
            : bat.OnAc && bat.Percent >= cap - 1 && !(watts is > 0.3) ? (cap < 100 ? $"已到上限 {cap} %" : "已充满")
            : v == null ? "—" : "正在估算…";
        rows.Add((bat.OnAc ? "预计充电时间" : "预计使用时间", eta));
        int design = Int(b?["design"]), full = Int(b?["full"]);
        if (design > 0 && full > 0)
        {
            rows.Add(("健康度", $"{Math.Round(100.0 * full / design)} %"));
            rows.Add(("满充 / 设计", $"{full / 1000.0:0.0} / {design / 1000.0:0.0} Wh"));
        }
        if (Int(b?["cycles"]) > 0) rows.Add(("循环次数", $"{Int(b?["cycles"])} 次"));
        float y = Rows(barY + 14, rows) + between;

        // ---- 版本 ----
        Header(y, "版本", "", accent);
        var ver = v?["versions"] as JsonObject;
        var dev = v?["device"] as JsonObject;
        var vrows = new List<(string, string)> { ("前端", SyOwnVersion()) };
        vrows.Add(("后台", Str(ver?["backend"]) ?? "—"));
        vrows.Add(("Game Bar 小组件", Str(ver?["widget"]) ?? "—"));
        vrows.Add(("BIOS", Str(dev?["bios"]) ?? "—"));
        vrows.Add(("显卡驱动", Str(dev?["gpuDriver"]) ?? "—"));
        vrows.Add(("手柄固件", Str(ver?["padFirmware"]) ?? "—"));
        y = Rows(y + header, vrows) + between;

        // ---- 设备 ----
        Header(y, "设备", "", accent);
        string model = Str(dev?["model"]) ?? "—";
        int pack = model.IndexOf(" Launch Pack", StringComparison.OrdinalIgnoreCase);
        if (pack > 0) model = model[..pack];
        var drows = new List<(string, string)> { ("型号", "MSI " + model), ("处理器", (Str(dev?["cpu"]) ?? "—").Replace("(R)", "").Replace("(TM)", "")) };
        drows.Add(("显卡", Str(dev?["gpu"]) ?? "—"));
        if (Int(dev?["memoryGB"]) > 0) drows.Add(("内存", $"{Int(dev?["memoryGB"])} GB"));
        Rows(y + header, drows);

        string? foot = _syAgentDown ? "连不上工具箱后台" : _syError != null ? $"后台出错：{_syError}"
            : v == null ? (_syOldAgent ? $"系统读数{DsLater}" : "正在读取…") : null;
        if (foot != null) PText(ds, GpFit(ds, foot, ui, 13, W), ui, 13, muted, 0, h - 12, W);
    }

    private static string? _syOwnVersion;
    private static string SyOwnVersion()
    {
        if (_syOwnVersion != null) return _syOwnVersion;
        try
        {
            if (App.Packaged)
            {
                var pv = Windows.ApplicationModel.Package.Current.Id.Version;
                return _syOwnVersion = $"{pv.Major}.{pv.Minor}.{pv.Build}";
            }
        }
        catch { }
        return _syOwnVersion = "开发版";
    }
}
