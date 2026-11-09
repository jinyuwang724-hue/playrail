using System.Diagnostics;
using System.Text.Json.Nodes;
using ClawLibrary.Data;
using ClawLibrary.Engine;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    internal async Task LibraryFilterProbeAsync()
    {
        string output = Path.Combine(App.CacheDir, "library-filter");
        Directory.CreateDirectory(output);
        var checks = new JsonArray();
        var original = _prefs;
        var flags = (Library.IncludeSteam, Library.ShowHidden, Library.SteamInstalledOnly);
        void Check(string name, bool ok)
        {
            checks.Add((JsonNode)new JsonObject { ["name"] = name, ["passed"] = ok });
            if (!ok) throw new InvalidOperationException("library filter probe: " + name);
        }
        async Task Capture(string name)
        {
            var recorder = new FrameRecorder();
            Check("capture " + name, recorder.Start(_window));
            await Task.Delay(180);
            Check("frames " + name, await recorder.StopAndSaveAsync(Path.Combine(output, name), Stopwatch.GetTimestamp()) > 0);
        }
        try
        {
            _appearanceProbeRunning = true;
            _pad?.Dispose();
            _prefs = _prefs with { Screensaver = false };
            var allSteam = _lib.Games.Where(g => g.Platform == "steam").Select(g => g.Id).ToHashSet();
            var installedSteam = _lib.Games.Where(g => g.Platform == "steam" && g.Installed).Select(g => g.Id).ToHashSet();
            var locals = _lib.Games.Where(g => g.Platform != "steam").Select(g => g.Id).ToHashSet();
            File.WriteAllLines(Path.Combine(output, "all-steam-ids.txt"), _lib.Games.Where(g => g.Platform == "steam").Select(g => g.Id));
            File.WriteAllLines(Path.Combine(output, "installed-steam-ids.txt"), _lib.Games.Where(g => g.Platform == "steam" && g.Installed).Select(g => g.Id));
            File.WriteAllLines(Path.Combine(output, "local-ids.txt"), _lib.Games.Where(g => g.Platform != "steam").Select(g => g.Id));
            OpenSettingsFromLibrary();
            _stCat = 1; Repaint();
            var row = _stRows.Single(r => r.Title == "Steam 游戏显示范围");
            Check("settings offers both Steam scopes", row.Choices!.SequenceEqual(new[] { "所有游戏", "已安装游戏" }));
            Check("settings defaults to all games", row.Choice == 0 && !original.SteamInstalledOnly);
            await Task.Delay(700);
            await Capture("all");
            row.Select!(1);
            Check("installed scope saves without reload", _prefs.SteamInstalledOnly && UiPrefs.Load().SteamInstalledOnly && !_stNeedsReload);
            Check("settings shows installed selection", _stRows.Single(r => r.Title == row.Title).Choice == 1);
            Check("Steam data changes immediately", _lib.Games.Where(g => g.Platform == "steam").Select(g => g.Id).ToHashSet().SetEquals(installedSteam));
            Check("live filter preserves local games", _lib.Games.Where(g => g.Platform != "steam").Select(g => g.Id).ToHashSet().SetEquals(locals));
            Check("wall layer changes immediately", _stack[0].Items.SequenceEqual(_lib.HomeTabs[_tabIndex].Items()));
            Check("visible covers change immediately", _tiles.Values.All(t => t.Shown == ItemFor(t.Slot)));
            Check("settings stays open", _ccOpen && _stOpen && _stCat == 1);
            await Capture("installed");
            CloseSettingsToLibrary();
            await Task.Delay(800);
            await Capture("wall-installed");
            OpenSettingsFromLibrary(); _stCat = 1; Repaint();
            _stRows.Single(r => r.Title == row.Title).Reset!();
            Check("reset restores all games", !_prefs.SteamInstalledOnly && !UiPrefs.Load().SteamInstalledOnly);
            Check("all Steam games return immediately", _lib.Games.Where(g => g.Platform == "steam").Select(g => g.Id).ToHashSet().SetEquals(allSteam));
            Check("restored wall matches data", _stack[0].Items.SequenceEqual(_lib.HomeTabs[_tabIndex].Items()) && _tiles.Values.All(t => t.Shown == ItemFor(t.Slot)));
            Check("control center count updates immediately", _ccBlocks[0].Second == $"{_lib.Games.Count} 款游戏");
            if (_lib.Collections.FirstOrDefault() is { } collection)
            {
                var start = SlotNearSeam();
                int allMembers = collection.Children!.Count;
                _stack.Add(new Layer { Id = collection.Id, Title = collection.Title,
                    Items = collection.Children.Select(Lookup).OfType<LibraryItem>().ToList(),
                    Layout = Content.Infinite(allMembers, start), Home = false });
                SetSteamInstalledOnly(true);
                Check("open collection updates immediately", CurrentLayer.Home || CurrentLayer.Items.All(g => g.Platform != "steam" || g.Installed));
                SetSteamInstalledOnly(false);
                Check("open collection restores members", CurrentLayer.Home || CurrentLayer.Items.Count == allMembers);
                if (_stack.Count > 1) Pop();
            }
            if (SlotNearSeam() is { } searchStart && SearchItems("a").Count > 0)
            {
                var matches = SearchItems("a");
                _stack.Add(new Layer { Id = "search:a", Title = "a", Items = matches,
                    Layout = SearchLayout(matches.Count, searchStart), Home = false });
                SetSteamInstalledOnly(true);
                Check("open search updates immediately", CurrentLayer.Home || (CurrentLayer.Layout is FiniteLayout && CurrentLayer.Items.SequenceEqual(SearchItems("a"))));
                SetSteamInstalledOnly(false);
                Check("open search restores all matches", CurrentLayer.Home || CurrentLayer.Items.Count == matches.Count);
                if (_stack.Count > 1) Pop();
            }

            var games = new List<LibraryItem>
            {
                new() { Id = "installed", Title = "Installed", Platform = "steam", Installed = true },
                new() { Id = "uninstalled", Title = "Uninstalled", Platform = "steam", Installed = false },
                new() { Id = "hidden", Title = "Hidden", Platform = "steam", Installed = true },
                new() { Id = "local", Title = "Local", Platform = "local", Installed = false },
                new() { Id = "shortcut", Title = "Shortcut", Platform = "other", Installed = false },
                new() { Id = "folder", Title = "Folder", Platform = "folder", Installed = false },
            };
            var collections = new List<SteamCollection>
            {
                new("hidden", "Hidden", new() { "hidden" }),
                new("mixed", "Mixed", new() { "installed", "uninstalled" }),
                new("empty", "Only uninstalled", new() { "uninstalled" }),
            };
            Library.IncludeSteam = true; Library.ShowHidden = false;
            var filtered = Library.Build(games, collections);
            foreach (bool installedOnly in new[] { false, true, false })
            {
                Library.SteamInstalledOnly = installedOnly;
                var lib = filtered = filtered.Refilter();
                Check("Steam scope " + installedOnly, lib.Games.Where(g => g.Platform == "steam").Select(g => g.Id).ToHashSet()
                    .SetEquals(installedOnly ? new[] { "installed" } : new[] { "installed", "uninstalled" }));
                Check("all tab applies scope " + installedOnly, lib.HomeTabs[0].Items().SequenceEqual(lib.Games));
                Check("Steam tab applies scope " + installedOnly, lib.HomeTabs[1].Items().Where(g => g.Kind == ItemKind.Game).All(g => !installedOnly || g.Installed));
                Check("local sources preserved " + installedOnly, lib.HomeTabs[2].Items().Select(g => g.Id).ToHashSet().SetEquals(new[] { "local", "shortcut", "folder" }));
                Check("collection count follows scope " + installedOnly, lib.Collections.Single(c => c.Id == "sc-mixed").Children!.Count == (installedOnly ? 1 : 2));
                Check("empty collections omitted " + installedOnly, lib.Collections.Any(c => c.Id == "sc-empty") != installedOnly);
            }
            Library.IncludeSteam = false;
            Check("Steam source switch remains effective", Library.Build(games, collections).Games.All(g => g.Platform != "steam"));
            Library.IncludeSteam = true; Library.SteamInstalledOnly = true; Library.ShowHidden = true;
            Check("hidden toggle respects installed filter", Library.Build(games, collections).Games.Where(g => g.Platform == "steam").Select(g => g.Id).ToHashSet().SetEquals(new[] { "installed", "hidden" }));
            var empty = Library.Build(new() { games[1] }, new());
            Check("no installed games is supported", empty.Games.Count == 0);
            Library.SteamInstalledOnly = false;
            Check("empty installed view restores all sources", empty.Refilter().Games.Count == 1);
            Log.Info("library filter probe passed");
        }
        catch (Exception ex) { Log.Error("library filter probe", ex); }
        finally
        {
            (Library.IncludeSteam, Library.ShowHidden, Library.SteamInstalledOnly) = flags;
            _prefs = original; _prefs.Save();
            _appearanceProbeRunning = false;
            File.WriteAllText(Path.Combine(output, "checks.json"), checks.ToJsonString());
        }
    }
}
