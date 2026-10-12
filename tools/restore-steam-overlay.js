// One-time repair for the callback overwritten by version 0.2.1.
// Run only after the old plugin has unloaded. Do not call OverlayStore.Init().
(async () => {
  const owner = window.overlayStore;
  if (!(owner?.m_mapBrowserInfo instanceof Map) ||
      typeof owner.OnOverlayBrowserInfoChanged !== "function" ||
      typeof owner.RegisterForBrowserCreated !== "function" ||
      typeof SteamClient.Overlay.RegisterOverlayBrowserInfoChanged !== "function") {
    throw new Error("Steam original overlay owner unavailable; repair was not applied");
  }
  SteamClient.Overlay.RegisterOverlayBrowserInfoChanged(owner.OnOverlayBrowserInfoChanged);
  await owner.OnOverlayBrowserInfoChanged();
  return {
    restored: true,
    owner: "Steam overlayStore.OnOverlayBrowserInfoChanged",
    runningApp: window.SteamUIStore.MainRunningAppID,
    browserCount: owner.m_mapBrowserInfo.size,
    overlayWindowCount: window.SteamUIStore.WindowStore.OverlayWindows.length,
  };
})()
