using System.Text.Json.Nodes;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using static ClawLibrary.Engine.Geometry;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    internal async Task LocalLibraryProbeAsync()
    {
        var checks = new JsonArray();
        string output = Path.Combine(App.CacheDir, "local-library-probe");
        Directory.CreateDirectory(output);
        string exe = Path.Combine(output, "fixture.exe");
        if (!File.Exists(exe)) File.Copy(Path.Combine(AppContext.BaseDirectory, "ClawLibrary.exe"), exe);
        string title = "库内刷新验证 " + Guid.NewGuid().ToString("N")[..8];
        string? ident = null;
        var original = _prefs;
        void Check(string name, bool ok)
        {
            checks.Add((JsonNode)new JsonObject { ["name"] = name, ["passed"] = ok });
            if (!ok) throw new InvalidOperationException("local library probe: " + name);
        }
        async Task Until(Func<bool> done)
        {
            for (int i = 0; i < 240 && !done(); i++) await Task.Delay(250);
            Check("background operation completed", done());
        }
        try
        {
            _appearanceProbeRunning = true;
            _pad?.Dispose();
            _prefs = _prefs with { Screensaver = false };
            await Task.Delay(1800);
            if (_ccOpen) CloseControlCenter();
            await Task.Delay(500);
            await LgRefresh();
            int pid = Environment.ProcessId;
            nint window = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            string tab = _lib.HomeTabs[_tabIndex].Id;
            var platforms = _lib.Games.Where(g => g.Platform is "steam" or "xbox").ToDictionary(g => g.Id);
            var focus = FocusKey;
            string? selected = focus is { } k && SlotByKey(k) is { } focused ? ItemFor(focused) : null;
            OpenSettingsFromLibrary();
            _stCat = 1; Repaint();
            Check("add and artwork rows have no reload requirement", _stRows.Where(r => r.Title == "添加游戏" || r.Title.StartsWith("补全封面"))
                .All(r => !r.Title.Contains("载入")));
            _agFromLibrary = true;
            var draft = new AgDraft { Name = title, Exe = exe, Steam = false };
            foreach (var (kind, _) in AgKinds) { draft.Cands[kind] = new(); draft.Pick[kind] = -1; }
            string? portrait = Directory.EnumerateFiles(ArtRoot, "p.jpg", SearchOption.AllDirectories).FirstOrDefault();
            if (portrait != null) { draft.Cands["p"].Add(("file:" + portrait, portrait)); draft.Pick["p"] = 0; }
            // 真实“完成”入口和真实后台保存，测试条目仅保存在本地，不写入 Steam。
            AgShow(draft, AgPage.Art);
            AgFinish(draft);
            await Until(() => _lib.Games.Any(g => g.LocalExePath == exe));
            ident = _lib.Games.Single(g => g.LocalExePath == exe).Id;
            Check("add returns to library in same process and window", !_ccOpen && !_stOpen && Environment.ProcessId == pid
                && WinRT.Interop.WindowNative.GetWindowHandle(_window) == window && !_stNeedsReload);
            Check("add preserves tab and selected game", _lib.HomeTabs[_tabIndex].Id == tab && FocusKey == focus
                && (focus == null || ItemFor(SlotByKey(focus.Value)!) == selected));
            Check("add preserves Steam and Xbox data", platforms.All(p => ReferenceEquals(Lookup(p.Key), p.Value)));
            Check("selected cover is ready without reload", portrait == null || Lookup(ident)?.OwnArt is { P: true } kit
                && kit.Dir.Contains("local-") && File.Exists(kit.File('p')));
            Check("startup uses the same cover cache", portrait == null || Lookup(ident)?.OwnArt?.Dir == LocalArtDirectory(LocalGames.Read().Single(g => g.Id == ident)));
            string? addedCover = Lookup(ident)?.OwnArt?.Dir;

            OpenSettingsFromLibrary();
            _stCat = 1; _lgPage = LgPage.List; Repaint();
            AgOpenEdit(LgGames().Single(g => Str(g["id"]) == ident));
            _agDraft!.Name = title + " 已编辑";
            if (portrait != null)
            {
                _agDraft.Cands["p"].Add(("file:" + portrait, portrait));
                _agDraft.Pick["p"] = _agDraft.Cands["p"].Count - 1;
            }
            AgFinish(_agDraft);
            await Until(() => Lookup(ident)?.Title == title + " 已编辑");
            Check("editing stays on local game list", _stOpen && _lgPage == LgPage.List && !_agOpen && !_stNeedsReload);
            Check("cover edit replaces decoded image address", portrait == null || Lookup(ident)?.OwnArt is { P: true } editedArt && editedArt.Dir != addedCover);
            CloseSettingsToLibrary();
            await Task.Delay(400);

            // 构造 Steam 同步完成时的来源快照，验证启动路径、去重及重新编号；不改用户 Steam 文件。
            var slot = SlotNearSeam()!;
            CurrentLayer.Layout = Content.Anchor(CurrentLayer.Layout, slot.Key, CurrentLayer.Items.FindIndex(g => g.Id == ident));
            Recull(true); Focus(slot.Key, reveal: false); Expand(slot);
            await Task.Delay(500); Sync();
            var center = _center;
            string syncedId = "4294967201";
            var local = LocalGames.Read().Select(g => g.Id == ident ? g with { Id = syncedId } : g).ToList();
            var shortcuts = Steam.ShortcutTargets(_steamUser);
            shortcuts.Add((syncedId, title + " 已编辑", exe, output));
            var snapshot = Library.Load(Path.Combine(AppContext.BaseDirectory, "Assets", "library.json"), new(),
                id => ArtKitBuilder.TryLoad(Path.Combine(ArtRoot, id)), Steam.ReadCollections(_steamUser), shortcuts: shortcuts, local: local);
            ApplyLibrarySnapshot(_lib.ReplaceLocalSources(snapshot), preservePosition: true);
            Check("Steam sync changes launch route without duplicate", Lookup(syncedId)?.ExePath == null && _lib.Games.Count(g => g.LocalExePath == exe) == 1);
            Check("Steam reassigned id retains focus and expanded card", FocusKey == slot.Key && ItemFor(slot) == syncedId && _expandedKey == slot.Key);
            Check("sync leaves camera and window in place", _center == center && !_ccOpen && Environment.ProcessId == pid
                && WinRT.Interop.WindowNative.GetWindowHandle(_window) == window);
            Check("sync preserves cloud and Xbox sources", platforms.All(p => ReferenceEquals(Lookup(p.Key), p.Value)));
            await RefreshLocalLibraryAsync();

            OpenSettingsFromLibrary(); _stCat = 1; _lgPage = LgPage.List; Repaint();
            AgOpenEdit(LgGames().Single(g => Str(g["id"]) == ident));
            await AgRemove(_agDraft!);
            await Until(() => Lookup(ident) == null);
            Check("removal takes effect without reload", _stOpen && !_agOpen && !_stNeedsReload && Environment.ProcessId == pid);
        }
        catch (Exception ex) { Log.Error("local library probe", ex); checks.Add((JsonNode)new JsonObject { ["error"] = ex.Message }); }
        finally
        {
            try
            {
                _agent ??= new AgentClient();
                var fixture = LocalGames.Read().FirstOrDefault(g => string.Equals(g.Exe, exe, StringComparison.OrdinalIgnoreCase));
                if (fixture != null) await _agent.Call("local_games_remove", JsonValue.Create(fixture.Id));
                await LgRefresh();
                if (_agOpen) AgClose();
                if (_stOpen) CloseSettingsToLibrary();
                Collapse(false);
                _prefs = original;
                _appearanceProbeRunning = false;
                checks.Add((JsonNode)new JsonObject { ["name"] = "temporary game removed", ["passed"] = !LocalGames.Read().Any(g => g.Exe == exe) });
            }
            catch (Exception ex) { checks.Add((JsonNode)new JsonObject { ["cleanupError"] = ex.Message }); }
            File.WriteAllText(Path.Combine(output, "checks.json"), checks.ToJsonString());
        }
    }
}
