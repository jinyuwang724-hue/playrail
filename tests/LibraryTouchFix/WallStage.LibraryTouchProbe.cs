using System.Text.Json.Nodes;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using static ClawLibrary.Engine.Geometry;
using static ClawLibrary.Engine.ViewMath;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    // Runs the real card hit-testing and Activate dispatch. A single click only expands a card;
    // the probe never clicks an expanded card, launches a game, or changes persisted preferences.
    internal async Task LibraryTouchProbeAsync()
    {
        var checks = new JsonArray();
        string? error = null;
        int originalTab = _tabIndex;
        var originalPrefs = _prefs;
        void Check(string name, bool passed)
        {
            checks.Add((JsonNode)new JsonObject { ["name"] = name, ["passed"] = passed });
            if (!passed) throw new InvalidOperationException(name);
        }
        try
        {
            await Task.Delay(2200);
            _prefs = _prefs with { Screensaver = false };
            MarkInput();
            if (_ccOpen) CloseControlCenter();
            await Task.Delay(1000);
            if (MorphBusy) RushMorph();
            foreach (string tabId in new[] { "all", "steam", "local" })
            {
                int tab = _lib.HomeTabs.FindIndex(t => t.Id == tabId);
                Check(tabId + ": category exists", tab >= 0);
                if (_expandedKey != null) Collapse(false);
                _railFocused = false; _railFocus = -1;
                SwitchTab(tab - _tabIndex);
                // Steam categories can fill the first viewport with collections. Keep the real
                // category and item order; anchor an actual game into view for the click fixture.
                if (SlotNearSeam() is { } anchor)
                {
                    int gameIndex = CurrentLayer.Items.FindIndex(item => item.Kind == ItemKind.Game);
                    Check(tabId + ": contains games", gameIndex >= 0);
                    CurrentLayer.Layout = Content.Anchor(CurrentLayer.Layout, anchor.Key, gameIndex);
                    SwapContent(null);
                    Focus(anchor.Key, reveal: false);
                }
                await Task.Delay(900);
                foreach (bool sameFocus in new[] { true, false })
                {
                    MarkInput();
                    if (_expandedKey != null) Collapse(false);
                    await Task.Delay(400);
                    Sync(); Recull(true); RepaintRail();
                    var geo = SeamGeometry(_anchorX, _center, _openWidth, View);
                    var candidates = _tiles.Values.Where(tile =>
                    {
                        if (!HasContent(tile.Slot) || Lookup(ItemFor(tile.Slot) ?? "")?.Kind != ItemKind.Game) return false;
                        var box = TileScreenBox(tile, NowMs);
                        double x = box.X + box.W / 2, y = box.Y + box.H / 2;
                        return x > 50 && x < View.Width - 50 && y > 90 && y < View.Height - 50
                            && Math.Abs(x - geo.ScreenX) > geo.Width / 2 + 10;
                    }).OrderBy(tile => Math.Abs(TileScreenBox(tile, NowMs).X - View.Width / 2)).Take(2).ToList();
                    string name = tabId + (sameFocus ? ": previous game focus" : ": different game focus");
                    Check(name + " has a visible game card", candidates.Count > 0);
                    var target = candidates[0];
                    _railFocused = false;
                    var previous = sameFocus ? target : _tiles.Values.First(tile => tile.Slot.Key != target.Slot.Key && HasContent(tile.Slot));
                    Focus(previous.Slot.Key, reveal: false);
                    int railTab = _rail.Items.FindIndex(i => i.Kind == RailKind.Tab && i.Index == tab);
                    Check(name + " has category target", railTab >= 0);
                    var row = _rail.Items[railTab].Bounds;
                    EnterRail(row.Y + row.Height / 2, railTab);
                    Check(name + " begins with category owning focus", _railFocused && _railFocus == railTab && _expandedKey == null);
                    var hit = TileScreenBox(target, NowMs);
                    ClickAt(hit.X + hit.W / 2, hit.Y + hit.H / 2);
                    Check(name + " transfers focus from category to game", !_railFocused && _railFocus == -1 && FocusKey == target.Slot.Key);
                    Check(name + " expands the tapped game", _expandedKey == target.Slot.Key);
                    Check(name + " preserves selected category", _tabIndex == tab);
                    Collapse(false);
                    await Task.Delay(450);
                }
            }
            if (_expandedKey != null) Collapse(false);
            SwitchTab(originalTab - _tabIndex);
            OpenControlCenter(quiet: true);
            Check("control centre has no back icon hit region", TouchNavigationMode == 0 && !TouchNavigationHit(20 * TouchScale, 48 * TouchScale));
            foreach (var page in new (string Name, Action Open, Action Close)[] {
                ("display", OpenDisplay, () => CloseDisplay(true)),
                ("system", OpenSystem, () => CloseSystem(true)),
                ("performance", OpenPerf, () => ClosePerf(true)),
                ("software", OpenSettingsPage, () => CloseSettingsPage(true)),
                ("gamepad", OpenGamepad, () => CloseGamepad(true)),
                ("screenshots", () => OpenShots(), () => CloseShots(true)),
            })
            {
                page.Open();
                Check(page.Name + " has no back icon hit region", TouchNavigationMode == 0 && !TouchNavigationHit(20 * TouchScale, 48 * TouchScale));
                page.Close();
            }
        }
        catch (Exception ex) { error = ex.Message; Log.Error("library touch probe", ex); }
        finally
        {
            _prefs = originalPrefs;
            if (_expandedKey != null) Collapse(false);
            _railFocused = false; _railFocus = -1;
            CloseShots(true); CloseGamepad(true); CloseDisplay(true); CloseSystem(true); ClosePerf(true); CloseSettingsPage(true);
            if (_ccOpen) CloseControlCenter();
            if (MorphBusy) RushMorph();
            SwitchTab(originalTab - _tabIndex);
            RepaintRail(); MarkInput();
            File.WriteAllText(Path.Combine(App.CacheDir, "library-touch-probe.json"),
                new JsonObject { ["checks"] = checks, ["error"] = error }.ToJsonString());
        }
    }
}
