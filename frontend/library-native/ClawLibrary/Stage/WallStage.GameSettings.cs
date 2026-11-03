// 游戏库 › 游戏设置（已展开的游戏卡按 Y）。和设置页同一张页（WallStage.SettingsPage.cs 的「游戏模式」）：
// 左栏分类、右栏共用的设置行、同一套触控和打字，标题换成游戏名。所有功能都在这一页里做完，不跳去别的页：
//   通用：启动目录、启动程序（浏览：设置页同一套文件夹浏览，就在右栏里）、这款游戏自己的功耗档（游戏在前台时
//         代替全局的功耗模式），Steam 的启动选项、覆盖层、云同步、收藏、在库中隐藏；
//   画面：Magpie 开关 / 自动缩放 / 超分预设、小黄鸭（Lossless Scaling 给这款游戏一份自己的配置）；
//   手柄：全局 / 游戏配置档、预设（选、存、删）、每个键的映射（WallStage.GameSettingsEmbed.cs，按键选择画在这页上）；
//   封面：封面页的网格直接画在右栏（WallStage.GameSettingsEmbed.cs），换一张就存；
//   已安装文件：占用、浏览本地文件、验证游戏文件完整性（Steam 游戏），程序位置（非 Steam 游戏）。
// Steam 的那几项通过开着的 Steam 自己的界面接口改（后台 game_settings_*），和 Steam 属性页里改一样马上生效。
using System.Text.Json.Nodes;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using static ClawLibrary.Engine.Geometry;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private static readonly string[] GsCats = { "通用", "画面", "手柄", "封面", "已安装文件" };

    /// <summary>设置页现在是哪款游戏的游戏设置（null = 普通设置页）。</summary>
    private LibraryItem? _gsItem;
    private JsonObject? _gsState;
    private bool _gsLoading;

    /// <summary>Y：只有已展开并保持焦点的游戏卡才能打开设置。</summary>
    private void OpenGameSettingsForFocus()
    {
        if (_expandedKey is not { } key || FocusKey != key || _railFocused) return;
        var slot = SlotByKey(key);
        var id = slot != null ? ItemFor(slot) : null;
        var item = id != null ? Lookup(id) : null;
        if (item == null || item.Kind != ItemKind.Game) { Toast("先选一款游戏"); return; }
        if (item.Platform == "folder") { Toast("文件夹里的游戏先在设置里添加成本地游戏"); return; }
        OpenGameSettings(item);
    }

    private void OpenGameSettings(LibraryItem item)
    {
        if (!item.Installed) return;
        _gsItem = item;
        _gsState = null;
        _gsLosslessError = null;
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
        long powerRevision = _gsPowerRevision;
        long losslessRevision = _gsLosslessWrites?.Revision ?? 0;
        _gsLoading = true;
        try
        {
            _agent ??= new AgentClient();
            if (await _agent.CallLong("game_settings_get", 30, GsRequest()) is JsonObject o && _gsItem == item && powerRevision == _gsPowerRevision
                && _gsLosslessWrites?.IsBusy != true && losslessRevision == (_gsLosslessWrites?.Revision ?? 0)) _gsState = (JsonObject)o.DeepClone();
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
            if (key.StartsWith("pad") || key == "launchExe") _gsPadFor = null;   // 布局换了：重新读
            if (key == "launchExe") await LgRefresh();
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

    /// <summary>通用 › 启动：在右栏选择程序或 Windows 快捷方式。</summary>
    private void BuildGsLaunch(JsonObject? s, bool shortcut, string wait, string noExe)
    {
        if (_gsItem is { } game)
        {
            _stRows.Add(new PerfRow { Section = true, Title = "游戏" });
            var nameRow = StInfo("游戏名称", game.Title); nameRow.Skip = true; _stRows.Add(nameRow);
            _stRows.Add(new PerfRow
            {
                Title = "更改游戏名称", Value = "编辑  ›",
                Press = () => StEditText(game.Title, value =>
                {
                    if (_gsItem?.Id != game.Id || value.Trim() == game.Title) return;
                    try
                    {
                        GameNames.Save(game.Id, value);
                        ApplyLibrarySnapshot(_lib.Refilter(), preservePosition: true);
                        _gsItem = _lib.ById.GetValueOrDefault(game.Id) ?? game;
                        Toast("游戏名称已更改");
                    }
                    catch (Exception ex) { Toast($"没有改成：{ex.Message}"); }
                }),
            });
        }
        var launch = s?["launch"] as JsonObject;
        _stRows.Add(new PerfRow { Section = true, Title = "启动" });
        if (s == null) { _stRows.Add(StInfo("启动目录", wait)); return; }
        string? exe = Str(launch?["exe"]);
        string dir = Str(launch?["startDir"]) ?? "";
        // Steam 游戏由 Steam 自己启动：这里换的是工具箱当作这款游戏的程序（Magpie、小黄鸭、手柄、功耗都按它认）
        string what = shortcut ? "启动程序" : "游戏程序";
        var dirRow = StInfo("启动目录", dir.Length > 0 ? dir : noExe); dirRow.Skip = true; _stRows.Add(dirRow);
        var exeRow = StInfo(what, exe != null ? Path.GetFileName(exe) : noExe); exeRow.Skip = true; _stRows.Add(exeRow);
        _stRows.Add(new PerfRow
        {
            Title = "更改" + what, Value = "浏览  ›",
            Press = () =>
            {
                _stBrowseExe = true;
                _stBrowsePick = path =>
                {
                    StBrowseTo(null);
                    if (Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        GsLocal("launch", "exe", JsonValue.Create(path));
                        GsLocal("launch", "startDir", JsonValue.Create(Path.GetDirectoryName(path)));
                    }
                    _ = GsSet("launchExe", JsonValue.Create(path), $"已改成 {Path.GetFileName(path)}");
                };
                string start = exe != null && Path.GetDirectoryName(exe) is { } d && Directory.Exists(d) ? d : Directory.Exists(dir) ? dir : "";
                StBrowseTo(start);
            },
        });
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
        if (_stCat != 3) GsArtReset();
        if (_stCat != 2 && _gsPadFor != null) GsPadReset();
        switch (_stCat)
        {
            case 0:
            {
                BuildGsLaunch(s, shortcut, wait, noExe);
                BuildGsPower(s, wait, noExe);
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
                _stRows.Add(StToggle("在库中隐藏（重新载入游戏库后生效）", Bool(steam["hidden"]), on => { GsLocal("steam", "hidden", JsonValue.Create(on)); _ = GsSet("hidden", JsonValue.Create(on), null); }));
                break;
            }
            case 1:
            {
                var mg = s?["magpie"] as JsonObject;
                var ls = s?["lossless"] as JsonObject;
                // Magpie：工具箱自带（我们编译、随工具箱装的那份），这里是这款游戏的开关和它自己的超分预设；
                // 详细的画面设置（铺满、锐化、捕获方式、帧率……）在 设置 › Magpie。
                _stRows.Add(new PerfRow { Section = true, Title = "Magpie" });
                if (s == null) { _stRows.Add(StInfo("Magpie", wait)); }
                else if (!Bool(mg?["available"])) _stRows.Add(StInfo("Magpie", "没有装"));
                else if (Str(mg?["key"]) == null) _stRows.Add(StInfo("Magpie", noExe));
                else
                {
                    bool on = Bool(mg?["enabled"]);
                    _stRows.Add(StToggle("Magpie", on, v => { GsLocal("magpie", "enabled", JsonValue.Create(v)); _ = GsSet("magpie", JsonValue.Create(v)); }));
                    if (on)
                    {
                        // 自动缩放：Magpie 的叫法（游戏窗口到前台就自动开始缩放）；关掉 = 进游戏后自己按快捷键开
                        _stRows.Add(StToggle("自动缩放", mg?["auto"] is JsonValue av && Bool(av), v => { GsLocal("magpie", "auto", JsonValue.Create(v)); _ = GsSet("magpieAuto", JsonValue.Create(v)); }, true));
                        var presets = (mg?["presets"] as JsonArray)?.Select(Str).OfType<string>().ToArray() ?? new[] { "无" };
                        string cur = Str(mg?["preset"]) ?? "无";
                        _stRows.Add(StPick("超分预设", presets, Math.Max(0, Array.IndexOf(presets, cur)), i =>
                        {
                            GsLocal("magpie", "preset", JsonValue.Create(presets[i]));
                            _ = GsSet("magpiePreset", JsonValue.Create(presets[i]));
                        }, 0));
                    }
                }
                // 小黄鸭（Lossless Scaling）：不是工具箱自带的，是 Steam 上单独买的软件；工具箱只替它给这款游戏建一份配置
                if (Bool(ls?["configured"]))
                {
                    _stRows.Add(new PerfRow { Section = true, Title = "小黄鸭（Lossless Scaling）" });
                    if (Str(s?["exe"]) == null) _stRows.Add(StInfo("小黄鸭", noExe));
                    else BuildGsLossless(ls!);
                }
                break;
            }
            case 2:
                // 手柄档案：这款游戏用全局按键映射，还是自己的一份（软件输入层按 exe 名切换，进游戏自动换）
                BuildGsPad(s, wait, noExe);
                break;
            case 3:
            {
                // 封面：读好之后右栏整块换成封面网格（GsArtMode）
                _stRows.Add(new PerfRow { Section = true, Title = "封面" });
                if (_gsItem is { } it && !_agEmbed) GsArtLoad(it);
                _stRows.Add(StInfo("封面", "正在读取…"));
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
