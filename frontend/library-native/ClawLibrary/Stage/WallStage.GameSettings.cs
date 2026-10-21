// 游戏库 › 游戏设置（墙上焦点游戏按 Y）。和设置页同一张页（WallStage.SettingsPage.cs 的「游戏模式」）：
// 左栏分类、右栏共用的设置行、同一套触控和打字，标题换成游戏名。功能照 Steam 的游戏属性页挑能直接改的：
//   通用：启动选项、游戏中启用 Steam 覆盖层、Steam 云同步、收藏、在库中隐藏；
//   画面：Magpie 自动铺满（进游戏后自动套用这款游戏上次的画面效果）、小黄鸭自动缩放（Lossless Scaling 给这款游戏一份
//         自己的配置，游戏在前台时自己开始缩放 / 补帧）；
//   已安装文件：占用、浏览本地文件、验证游戏文件完整性（Steam 游戏），程序位置（非 Steam 游戏）。
// Steam 的那几项通过开着的 Steam 自己的界面接口改（后台 game_settings_*），和 Steam 属性页里改一样马上生效。
using System.Text.Json.Nodes;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using static ClawLibrary.Engine.Geometry;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private static readonly string[] GsCats = { "通用", "画面", "已安装文件" };

    /// <summary>设置页现在是哪款游戏的游戏设置（null = 普通设置页）。</summary>
    private LibraryItem? _gsItem;
    private JsonObject? _gsState;
    private bool _gsLoading;

    /// <summary>Y：墙上焦点的那款游戏的设置。</summary>
    private void OpenGameSettingsForFocus()
    {
        var slot = FocusKey is { } k ? SlotByKey(k) : null;
        var id = slot != null ? ItemFor(slot) : null;
        var item = id != null ? Lookup(id) : null;
        if (item == null || item.Kind != ItemKind.Game) { Toast("先选一款游戏"); return; }
        if (item.Platform == "folder") { Toast("文件夹里的游戏先在设置里添加成本地游戏"); return; }
        OpenGameSettings(item);
    }

    private void OpenGameSettings(LibraryItem item)
    {
        _gsItem = item;
        _gsState = null;
        _stCat = 0;
        OpenSettingsFromLibrary();
        _ = GsLoad();
        Log.Info($"game settings open {item.Id} {item.Title}");
    }

    private JsonObject GsRequest(bool withKeys = false)
    {
        var item = _gsItem!;
        var o = new JsonObject { ["id"] = item.Id, ["platform"] = item.Platform, ["title"] = item.Title };
        if (withKeys)
        {
            o["exe"] = Str(_gsState?["exe"]);
            o["magpieKey"] = Str(_gsState?["magpie"]?["key"]);
        }
        return o;
    }

    private async Task GsLoad()
    {
        var item = _gsItem;
        if (item == null) return;
        _gsLoading = true;
        try
        {
            _agent ??= new AgentClient();
            if (await _agent.CallLong("game_settings_get", 30, GsRequest()) is JsonObject o && _gsItem == item) _gsState = (JsonObject)o.DeepClone();
        }
        catch (Exception ex) { Log.Warn($"game settings: {ex.Message}"); if (_gsItem == item) Toast(ex.Message.Contains("不支持的后台调用") ? "工具箱后台版本太旧" : "没读到游戏设置"); }
        finally { _gsLoading = false; }
        if (_stOpen && _gsItem == item) Repaint();
    }

    /// <summary>改一项：先在界面上改过来，后台做完再读一次真实状态。</summary>
    private async Task GsSet(string key, JsonNode? value, string? done = null)
    {
        var item = _gsItem;
        if (item == null) return;
        try
        {
            _agent ??= new AgentClient();
            await _agent.CallLong("game_settings_set", 40, GsRequest(withKeys: true), JsonValue.Create(key), value);
            if (done != null) Toast(done);
            if (key == "hidden") _stNeedsReload = true;
        }
        catch (Exception ex) { Toast(ex.Message.Contains("不支持的后台调用") ? "工具箱后台版本太旧" : $"没有改成：{ex.Message}"); }
        if (_gsItem == item) await GsLoad();
    }

    private void GsLocal(string path, string key, JsonNode? value)
    {
        // 界面先改过来（开关立刻有反应），后台结果回来再以它为准
        if (_gsState?[path] is JsonObject o) o[key] = value?.DeepClone();
        else if (path.Length == 0 && _gsState != null) _gsState[key] = value?.DeepClone();
        Repaint();
    }

    private void BuildGsRows()
    {
        var s = _gsState;
        var steam = s?["steam"] as JsonObject;
        bool shortcut = Bool(s?["shortcut"]);
        string wait = _gsLoading || s == null ? "正在读取…" : "";
        // 没装的 Steam 游戏：没有文件夹也没有程序，几项都写「没有安装」
        bool notInstalled = !shortcut && steam != null && !Bool(steam["installed"]);
        string noExe = notInstalled ? "没有安装" : "找不到程序";
        switch (_stCat)
        {
            case 0:
            {
                _stRows.Add(new PerfRow { Section = true, Title = "Steam" });
                if (steam == null)
                {
                    _stRows.Add(StInfo("启动选项", wait.Length > 0 ? wait : "Steam 没开"));
                    break;
                }
                string opts = Str(steam["launchOptions"]) ?? "";
                _stRows.Add(new PerfRow
                {
                    Title = "启动选项", Value = opts.Length > 0 ? opts : "无",
                    Press = () => StEditText(opts, v => { GsLocal("steam", "launchOptions", JsonValue.Create(v.Trim())); _ = GsSet("launchOptions", JsonValue.Create(v.Trim())); }),
                });
                _stRows.Add(StToggle("游戏中启用 Steam 覆盖层", Bool(steam["overlay"]), on => { GsLocal("steam", "overlay", JsonValue.Create(on)); _ = GsSet("overlay", JsonValue.Create(on)); }));
                if (Bool(steam["cloudAvailable"]))
                    _stRows.Add(StToggle("Steam 云同步", Bool(steam["cloud"]), on => { GsLocal("steam", "cloud", JsonValue.Create(on)); _ = GsSet("cloud", JsonValue.Create(on)); }));
                _stRows.Add(new PerfRow { Section = true, Title = "游戏库" });
                _stRows.Add(StToggle("收藏", Bool(steam["favorite"]), on => { GsLocal("steam", "favorite", JsonValue.Create(on)); _ = GsSet("favorite", JsonValue.Create(on)); }));
                _stRows.Add(StToggle("在库中隐藏", Bool(steam["hidden"]), on => { GsLocal("steam", "hidden", JsonValue.Create(on)); _ = GsSet("hidden", JsonValue.Create(on), on ? "已隐藏，重新载入游戏库后不再显示" : null); }));
                break;
            }
            case 1:
            {
                var mg = s?["magpie"] as JsonObject;
                var ls = s?["lossless"] as JsonObject;
                _stRows.Add(new PerfRow { Section = true, Title = "超分" });
                if (s == null) { _stRows.Add(StInfo("Magpie 自动铺满", wait)); break; }
                if (!Bool(mg?["available"])) _stRows.Add(StInfo("Magpie 自动铺满", "没有装"));
                else if (Str(mg?["key"]) == null) _stRows.Add(StInfo("Magpie 自动铺满", noExe));
                else _stRows.Add(StToggle("Magpie 自动铺满", Bool(mg?["enabled"]), on => { GsLocal("magpie", "enabled", JsonValue.Create(on)); _ = GsSet("magpie", JsonValue.Create(on)); }));
                if (!Bool(ls?["installed"])) _stRows.Add(StInfo("小黄鸭自动缩放", "没有装"));
                else if (Str(s["exe"]) == null) _stRows.Add(StInfo("小黄鸭自动缩放", noExe));
                else _stRows.Add(StToggle("小黄鸭自动缩放", Bool(ls?["enabled"]), on =>
                {
                    GsLocal("lossless", "enabled", JsonValue.Create(on));
                    _ = GsSet("lossless", JsonValue.Create(on), on ? "小黄鸭会在这款游戏打开时自动开始" : "已关掉这款游戏的小黄鸭");
                }));
                break;
            }
            default:
            {
                _stRows.Add(new PerfRow { Section = true, Title = shortcut ? "程序" : "已安装文件" });
                string? exe = Str(s?["exe"]);
                string folder = Str(s?["folder"]) ?? "";
                if (shortcut || folder.Length == 0)
                {
                    _stRows.Add(StInfo("程序位置", exe ?? (wait.Length > 0 ? wait : notInstalled ? "没有安装" : "找不到")));
                    if (exe != null)
                        _stRows.Add(StAction("打开所在文件夹", "gsopen", "", () => SystemInfo.Open("explorer.exe", $"/select,\"{exe}\"")));
                    break;
                }
                long size = (long)(steam?["size"]?.GetValue<double>() ?? 0);
                _stRows.Add(StInfo("占用空间", size > 0 ? $"{size / 1073741824.0:0.0} GB" : "—"));
                _stRows.Add(StAction("浏览本地文件", "gsbrowse", "", () => SystemInfo.Open("explorer.exe", $"\"{folder}\"")));
                if (steam != null && !shortcut)
                    _stRows.Add(StAction("验证游戏文件完整性", "gsverify", "", () =>
                    {
                        if (!StConfirm("gsverify")) return;
                        _ = GsSet("verify", null, "已交给 Steam 验证，进度在 Steam 的下载页里");
                    }));
                break;
            }
        }
    }
}
