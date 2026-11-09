using ClawLibrary.Data;
using ClawLibrary.Engine;
using static ClawLibrary.Engine.Geometry;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private bool _libraryArtBuilding, _libraryArtBuildAgain;

    private void SetSteamInstalledOnly(bool installedOnly)
    {
        if (_prefs.SteamInstalledOnly == installedOnly) return;
        SaveUi(_prefs with { SteamInstalledOnly = installedOnly });
        Library.SteamInstalledOnly = installedOnly;
        ApplyLibrarySnapshot(_lib.Refilter(_steamRoot != null ? Steam.ReadInstalled(_steamRoot) : null));
        Log.Info($"Steam library scope applied immediately: installed only={installedOnly}, {_lib.Games.Count} games");
    }

    private void ApplyLibrarySnapshot(Library next, bool preservePosition = false)
    {
        Sync();
        var start = SlotNearSeam();
        var previousLayer = CurrentLayer;
        var previousFocus = FocusKey;
        var previousExpanded = _expandedKey;
        string? MatchId(string? id)
        {
            if (id == null || next.ById.ContainsKey(id)) return id;
            // Steam 接受快捷方式时可能分配新的编号；仍把它当作刚才那款本地游戏。
            string? exe = Lookup(id)?.LocalExePath;
            return exe == null ? id : next.Games.FirstOrDefault(g => string.Equals(g.LocalExePath, exe, StringComparison.OrdinalIgnoreCase))?.Id ?? id;
        }
        var previousGame = MatchId(FocusKey is { } focused && SlotByKey(focused) is { } focusedSlot ? ItemFor(focusedSlot) : null);
        bool railFocused = _railFocused;
        int railFocus = _railFocus;
        var anchors = _stack.ToDictionary(layer => layer, layer =>
        {
            var key = layer == CurrentLayer ? FocusKey ?? start?.Key : layer.Saved?.focus ?? start?.Key;
            int index = key is { } k ? Content.ItemAt(layer.Layout, k) : -1;
            return (Key: key, Id: MatchId(index >= 0 && index < layer.Items.Count ? layer.Items[index].Id : null));
        });
        void ReplaceItems(Layer layer, List<LibraryItem> items, bool search = false)
        {
            bool unchanged = layer.Items.Select(g => g.Id).SequenceEqual(items.Select(g => g.Id));
            layer.Items = items;
            if (preservePosition && unchanged) return;
            layer.Layout = search && start != null ? SearchLayout(items.Count, start) : Content.Infinite(items.Count, start);
            if (preservePosition && anchors[layer] is ({ } key, { } id))
                layer.Layout = Content.Anchor(layer.Layout, key, items.FindIndex(g => g.Id == id));
        }
        string tabId = _lib.HomeTabs[_tabIndex].Id;
        if (preservePosition) _ccGameId = MatchId(_ccGameId);
        _lib = next;
        if (_ccGameId != null && !_lib.Games.Any(g => g.Id == _ccGameId)) { _ccGameId = null; _ccGameArt = null; }
        _allTabs = null;
        _tabIndex = Math.Max(0, _lib.HomeTabs.FindIndex(t => t.Id == tabId));
        ApplyTabPrefs(keepCurrent: true);

        var tab = _lib.HomeTabs[_tabIndex];
        var home = _stack[0];
        home.Id = "home:" + tab.Id;
        ReplaceItems(home, tab.Items());
        // 合集保持在原层；如果筛选后已经为空，则返回上一层。
        // 搜索结果重新查询完整的已筛选库，切回「所有」时也能恢复之前的匹配项。
        for (int i = 1; i < _stack.Count; i++)
        {
            var layer = _stack[i];
            bool search = layer.Id.StartsWith("search:", StringComparison.Ordinal);
            var items = search ? SearchItems(layer.Title)
                : (Lookup(layer.Id)?.Children ?? new()).Select(Lookup).OfType<LibraryItem>().ToList();
            if (items.Count == 0) { _stack.RemoveRange(i, _stack.Count - i); break; }
            ReplaceItems(layer, items, search);
        }

        bool keepFocus = preservePosition && CurrentLayer == previousLayer && previousFocus is { } key
            && SlotByKey(key) is { } slot && previousGame != null && ItemFor(slot) == previousGame;
        _expandedKey = keepFocus ? previousExpanded : null;
        _drawnOverride.Clear();
        _reflowEnds.Clear();
        if (_expandedKey is { } expanded)
            foreach (var expandedSlot in Geometry.FocusedBlockSlots(expanded.Column, expanded.Row, expanded.Index)) _drawnOverride[expandedSlot.Key] = expandedSlot.Rect;
        foreach (var tile in _tiles.Values)
        {
            tile.SetExpanded(tile.Slot.Key == _expandedKey);
            tile.Place(Drawn(tile.Slot));
            tile.Show(ItemFor(tile.Slot));
        }
        Recull(true);
        _railFocused = preservePosition && railFocused; _railFocus = preservePosition ? railFocus : -1;
        Focus(keepFocus ? previousFocus : CurrentLayer.Items.Count > 0 ? start?.Key : null, reveal: false);
        if (_ccBlocks.Count > 0) { CcReadData(); CcPaintCard(_ccBlocks[0]); CcPaintLabels(); }
        TweenSeam(LibrarySeamWidth);
        RenderSeamContent();
        RenderEdgeTabs();
        var covers = FilmCoverPaths();
        _backdrop.SetFilmCovers(covers, a => _dq.TryEnqueue(() => { a(); EnsureBackdropLoop(); }));
        _ccBack?.SetFilmCovers(covers, a => _dq.TryEnqueue(() => { a(); CcBackLoop(); }));
        _ = BuildMissingArtAsync();
        Repaint();
        CsRebuild();
    }

    // 切换范围可能补进没有封面的游戏；把补图请求串行合并，避免和启动补图并发写同一套文件。
    private async Task BuildMissingArtAsync()
    {
        if (_libraryArtBuilding) { _libraryArtBuildAgain = true; return; }
        _libraryArtBuilding = true;
        try
        {
            do { _libraryArtBuildAgain = false; await BuildMissingArtBatchAsync(); }
            while (_libraryArtBuildAgain);
        }
        finally { _libraryArtBuilding = false; }
    }
}
