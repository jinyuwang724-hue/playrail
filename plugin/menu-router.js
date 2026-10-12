function steamAppId(value) {
  const id = Number(value);
  return Number.isInteger(id) && id >= -2147483648 && id <= 4294967295 ? id >>> 0 : 0;
}
export function createMenuRouter(env = window, ui = globalThis.DFL ?? {}, options = {}) {
  const timeout = options.timeout ?? 1200;
  const active = new Map(), deactivations = new Map();
  const browserSubscriptions = [];
  let disposed = false, generation = 0, focus, closingFocus, closePatch, registration,
    wake, runningSubscription, returnRefresh = false, lifecycleChanged = () => wake?.(), queue = Promise.resolve();
  const delay = options.delay ?? (ms => new Promise(resolve => {
    const timer = setTimeout(() => { wake = undefined; resolve(); }, ms);
    wake = () => { clearTimeout(timer); wake = undefined; resolve(); };
  }));
  const app = () => steamAppId(env.SteamUIStore?.MainRunningAppID ?? ui.Router?.MainRunningAppID);
  const store = () => env.SteamUIStore?.WindowStore ?? ui.Router?.WindowStore;
  const key = (pid, appId) => `${Number(pid)}:${steamAppId(appId)}`;
  const fail = reason => ({ ok: false, reason });
  const notify = async (source, visible) => {
    try { await options.onMenuVisibility?.(source, visible); }
    catch (error) { console.warn("[Claw] Scaling menu handoff failed", error); }
  };
  const overlayVisible = () => [...active.values()].some(Boolean);
  // Steam occasionally misses the "overlay hidden" callback (a game that stops
  // presenting mid-animation), which would keep the scaling view on its menu
  // layout. While an overlay counts as visible, confirm it once a second against
  // the overlay windows' own composition state; this never runs while idle.
  let overlayCheck;
  const watchOverlay = () => {
    if (!overlayVisible()) { clearInterval(overlayCheck); overlayCheck = undefined; return; }
    if (overlayCheck) return;
    let misses = 0;
    overlayCheck = setInterval(() => {
      if (disposed || !overlayVisible()) { clearInterval(overlayCheck); overlayCheck = undefined; return; }
      let shown = false;
      try { shown = [...(store()?.OverlayWindows ?? [])].some(w => (w?.CompositionStateStore?.GetCompositionState?.() ?? 0) >= 2); }
      catch { shown = true; }
      misses = shown ? 0 : misses + 1;
      if (misses >= 2) {
        for (const id of active.keys()) active.set(id, false);
        clearInterval(overlayCheck); overlayCheck = undefined;
        void notify("overlay", false); wake?.();
      }
    }, 1000);
  };
  const wait = async (check, cancelled = () => false) => {
    // Bounded waits run only during a button press, never while idle.
    const deadline = performance.now() + timeout;
    while (performance.now() <= deadline) {
      if (disposed || cancelled()) return false;
      if (check()) return true;
      await delay(Math.min(40, Math.max(1, deadline - performance.now())));
    }
    return false;
  };
  try {
    registration = env.SteamClient?.Overlay?.RegisterForOverlayActivated?.((pid, appId, visible) => {
      const id = key(pid, appId);
      active.set(id, visible === true);
      if (!visible) deactivations.set(id, (deactivations.get(id) ?? 0) + 1);
      if (active.size > 32) { const old = active.keys().next().value; active.delete(old); deactivations.delete(old); }
      void notify("overlay", overlayVisible());
      watchOverlay();
      wake?.();
    });
  } catch { }
  // The native RegisterOverlayBrowserInfoChanged slot belongs to Steam itself.
  // Subscribe to Steam's fan-out lists instead; replacing the native callback
  // prevents Steam from creating game overlay windows.
  for (const method of ["RegisterForBrowserCreated", "RegisterForBrowserClosed", "RegisterForBrowserUpdated"]) {
    try {
      const subscription = env.overlayStore?.[method]?.(() => lifecycleChanged());
      if (subscription) browserSubscriptions.push(subscription);
    } catch { }
  }
  const focusRequest = async (action, requestId, appId) => {
    const port = options.port?.() ?? 47991;
    if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error("Playhub 端口无效");
    const response = await env.fetch(`http://127.0.0.1:${port}/qam/focus/${action}`, {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ requestId, appId }), signal: AbortSignal.timeout(1600),
    });
    if (!response.ok) throw new Error(`Playhub 菜单焦点请求失败：${response.status}`);
    return response.json();
  };
  const endFocus = async restore => {
    const previous = focus;
    focus = undefined;
    if (!previous) return { ok: true };
    closePatch?.unpatch?.(); closePatch = undefined;
    try { return await focusRequest(restore && app() === previous.app ? "close" : "release", previous.id, previous.app); }
    finally { await notify("focus", false); }
  };
  const menuOf = instance => instance?.MenuStore ?? instance?.m_MenuStore;
  const mainMenuVisible = () => [1, 2].includes(menuOf(store()?.GamepadUIMainWindowInstance)?.GetOpenSideMenu?.());
  const open = (menu, side) => {
    // Preserve the selected QAM tab instead of overwriting it with undefined.
    if (side === 2 && typeof menu.OpenQuickAccessMenu === "function") menu.OpenQuickAccessMenu(menu.GetQuickAccessTab?.());
    else menu.OpenSideMenu(side);
  };
  const nativeWindow = identity => {
    const candidate = store()?.GetOverlayInstance?.(identity.appID, identity.unPID);
    const windows = candidate ? [candidate] : store()?.OverlayWindows ?? [];
    return [...windows].find(instance => {
      const info = instance?.params?.browserInfo;
      return instance?.IsGamepadUIOverlayWindow?.() === true &&
        steamAppId(info?.m_unAppID) === identity.appID && Number(info?.m_unPID) === identity.unPID &&
        Number(info?.m_nBrowserID) === identity.nBrowserID && String(info?.m_gameID) === identity.gameID &&
        !!(instance.BrowserWindow ?? instance.m_BrowserWindow)?.document?.body;
    });
  };
  const nativeMenu = async (identity, side, runningApp) => {
    const overlay = env.SteamClient.Overlay, id = key(identity.unPID, identity.appID);
    const requestGeneration = generation;
    const cancelledAt = deactivations.get(id) ?? 0;
    const cancelled = () => generation !== requestGeneration || app() !== runningApp || (deactivations.get(id) ?? 0) !== cancelledAt;
    let instance = nativeWindow(identity), activatedHere = false, openedHere = false, completed = false, menu;
    try {
      await notify("overlay", true);
      if (!instance) {
        if (typeof overlay.SetOverlayState !== "function") return fail("Steam 游戏覆盖窗口尚未就绪");
        activatedHere = true;
        await overlay.SetOverlayState(identity.gameID, 2);
        if (!await wait(() => !!(instance = nativeWindow(identity)), cancelled)) return fail("Steam 游戏覆盖窗口未就绪或已关闭");
      }
      menu = menuOf(instance);
      const composition = instance.CompositionStateStore;
      if (!menu || typeof composition?.GetCompositionState !== "function") return fail("Steam 游戏菜单接口不可用");
      const wasVisible = composition.GetCompositionState() === 2;
      if (wasVisible && !active.has(id)) active.set(id, true);
      const closing = wasVisible && menu.GetOpenSideMenu?.() === side;
      if (closing) {
        menu.CloseSideMenus();
        await overlay.SetOverlayState?.(identity.gameID, 0);
        const ok = await wait(() => menu.GetOpenSideMenu?.() === 0 && active.get(id) === false, () => app() !== runningApp);
        completed = ok;
        return { ok, side: menu.GetOpenSideMenu?.(), path: "overlay", reason: ok ? undefined : "Steam 覆盖界面未确认关闭" };
      }
      if (cancelled()) return fail("游戏或覆盖窗口已变化");
      openedHere = !wasVisible;
      open(menu, side);
      const ok = await wait(() => menu.GetOpenSideMenu?.() === side && composition.GetCompositionState() === 2 && active.get(id) === true, cancelled);
      completed = ok;
      return { ok, side: menu.GetOpenSideMenu?.(), path: "overlay", reason: ok ? undefined : "Steam 游戏菜单未确认激活" };
    } finally {
      if (!completed && openedHere && menu?.GetOpenSideMenu?.() === side) menu.CloseSideMenus();
      if (!completed && activatedHere) await overlay.SetOverlayState?.(identity.gameID, 0);
      await notify("overlay", overlayVisible());
    }
  };
  const mainFallback = async (runningApp, side) => {
    const requestGeneration = generation;
    const instance = store()?.GamepadUIMainWindowInstance, menu = menuOf(instance);
    const originalOpen = menu && Object.getPrototypeOf(menu)?.OpenSideMenu;
    if (!menu || instance?.IsGamepadUIOverlayWindow?.() === true || typeof originalOpen !== "function" || typeof ui.afterPatch !== "function")
      return fail("游戏没有可用的 Steam 覆盖界面，Playhub 菜单接口不可用");
    if (focus && (focus.app !== runningApp || focus.menu !== menu)) await endFocus(false);
    if (focus && menu.GetOpenSideMenu?.() === side) {
      menu.CloseSideMenus();
      const result = await closingFocus;
      return { ok: result?.ok === true && menu.GetOpenSideMenu?.() === 0, side: 0, path: "playhub-window", reason: result?.reason };
    }
    if (!focus) {
      const id = `claw-${Date.now()}-${Math.random().toString(36).slice(2)}`;
      let acquired;
      await notify("focus", true);
      try { acquired = await focusRequest("acquire", id, runningApp); }
      catch (error) { await focusRequest("release", id, runningApp).catch(() => {}); await notify("focus", false); throw error; }
      if (!acquired.ok || disposed || generation !== requestGeneration || app() !== runningApp) {
        await focusRequest("release", id, runningApp);
        await notify("focus", false);
        return fail(acquired.reason ?? "Playhub 未能取得游戏菜单焦点");
      }
      focus = { id, app: runningApp, menu, side };
      closePatch = ui.afterPatch(menu, "CloseSideMenus", (_args, result) => {
        if (menu.GetOpenSideMenu?.() === 0) {
          closingFocus = endFocus(true);
          closingFocus.catch(error => console.warn("[Claw] Game focus restore failed", error));
        }
        return result;
      });
    }
    // Native policy granted this transaction. The prototype call avoids another
    // Playhub acquisition through its patched method with a different request ID.
    focus.side = side;
    originalOpen.call(menu, side);
    const ok = await wait(() => menu.GetOpenSideMenu?.() === side, () => app() !== runningApp);
    if (!ok) await endFocus(true);
    return { ok, side: menu.GetOpenSideMenu?.(), path: "playhub-window", reason: ok ? undefined : "Playhub 游戏菜单未确认打开" };
  };
  const execute = async action => {
    if (disposed) return fail("菜单处理已停止");
    if (!["MainMenu", "QuickAccess"].includes(action)) return fail("动作不可用");
    const side = action === "MainMenu" ? 1 : 2, runningApp = app();
    try {
      if (runningApp) {
        // Recheck native identity on each press, without an idle scan or cache.
        const infos = await env.SteamClient?.Overlay?.GetOverlayBrowserInfo?.();
        if (!Array.isArray(infos) || app() !== runningApp) return fail("Steam 当前游戏覆盖信息不可用");
        const matching = infos.filter(info => steamAppId(info?.appID) === runningApp);
        if (matching.length > 1) return fail("Steam 当前游戏有多个覆盖窗口，无法确定目标");
        if (matching.length === 1) {
          const info = matching[0];
          if (!Number.isInteger(Number(info.unPID)) || Number(info.unPID) <= 0 ||
              !Number.isInteger(Number(info.nBrowserID)) || Number(info.nBrowserID) < 0 ||
              !/^\d+$/.test(String(info.gameID ?? "")) || String(info.gameID) === "0" ||
              (typeof info.gameID === "number" && !Number.isSafeInteger(info.gameID))) return fail("Steam 覆盖窗口身份不完整");
          if (focus) await endFocus(false);
          return await nativeMenu({ appID: runningApp, unPID: Number(info.unPID), nBrowserID: Number(info.nBrowserID), gameID: String(info.gameID) }, side, runningApp);
        }
        return await mainFallback(runningApp, side);
      }
      if (focus) await endFocus(false);
      const running = env.SteamUIStore?.m_runningAppIDs ?? ui.Router?.m_runningAppIDs;
      if ((running?.size ?? running?.length ?? 0) > 0) return fail("无法确定正在运行的游戏");
      const instance = store()?.GamepadUIMainWindowInstance, menu = menuOf(instance);
      if (!menu || instance?.IsGamepadUIOverlayWindow?.() === true) return fail("Steam 主页菜单不可用");
      const closing = menu.GetOpenSideMenu?.() === side;
      if (closing) menu.CloseSideMenus(); else open(menu, side);
      const ok = await wait(() => menu.GetOpenSideMenu?.() === (closing ? 0 : side));
      await notify("panel", mainMenuVisible());
      return { ok, side: menu.GetOpenSideMenu?.(), path: "home", reason: ok ? undefined : "Steam 主页菜单未响应" };
    } catch (error) {
      if (focus) await endFocus(true).catch(() => {});
      return fail(String(error));
    }
  };
  const gameStarted = appId => {
    try { void Promise.resolve(options.onGameStarted?.(appId)).catch(() => {}); } catch { }
  };
  let previousApp = app();
  if (previousApp) gameStarted(previousApp);
  lifecycleChanged = () => {
    wake?.();
    if (!disposed) void notify("panel", mainMenuVisible());
    const currentApp = app(), exited = previousApp !== 0 && currentApp === 0;
    if (currentApp !== previousApp) {
      for (const id of active.keys()) if (id.endsWith(`:${previousApp}`)) { active.delete(id); deactivations.delete(id); }
      void notify("overlay", overlayVisible());
      if (currentApp && !disposed) gameStarted(currentApp);
    }
    previousApp = currentApp;
    if (disposed) return;
    const previous = focus;
    if (previous && currentApp !== previous.app) {
      // Release before closing: an exited game must never receive restored focus.
      // Only close the menu this lease opened, leaving other menu owners alone.
      void endFocus(false).catch(error => console.warn("[Claw] Exited game focus release failed", error));
      if (previous.menu.GetOpenSideMenu?.() === previous.side) previous.menu.CloseSideMenus();
    }
    const main = store()?.GamepadUIMainWindowInstance;
    const browser = main?.BrowserWindow ?? main?.m_BrowserWindow;
    if (exited && !returnRefresh && typeof browser?.dispatchEvent === "function" && typeof browser?.Event === "function") {
      // Run after Steam's current state transaction, without using the hidden
      // main window's throttled animation frames or changing foreground focus.
      returnRefresh = true;
      (options.defer ?? queueMicrotask)(() => {
        returnRefresh = false;
        const running = env.SteamUIStore?.m_runningAppIDs ?? ui.Router?.m_runningAppIDs;
        if (disposed || app() || (running?.size ?? running?.length ?? 0)) return;
        try { browser.dispatchEvent(new browser.Event("resize")); } catch { }
      });
    }
  };
  try {
    let reaction = options.reaction;
    if (!reaction) {
      const symbol = Object.getOwnPropertySymbols(env.SteamUIStore ?? {}).find(s => s.description === "mobx administration");
      const module = symbol && ui.findModule?.(m => Object.values(m).includes(symbol));
      // Resolve by the stable MobX reaction signature, never a minified export name.
      const candidates = Object.values(module ?? {}).filter(fn => typeof fn === "function" && fn.length === 3 && /["']Reaction["']/.test(Function.prototype.toString.call(fn)));
      if (candidates.length === 1) reaction = candidates[0];
    }
    if (typeof reaction === "function") runningSubscription = reaction(
      () => `${app()}:${Array.from(env.SteamUIStore?.m_runningAppIDs ?? ui.Router?.m_runningAppIDs ?? []).map(steamAppId).join(",")}:${mainMenuVisible()}`,
      lifecycleChanged, { name: "ClawExitedGameFocus" });
  } catch (error) { console.warn("[Claw] Game lifecycle subscription failed", error); }
  void notify("panel", mainMenuVisible());
  return {
    execute(action) { const requestedAt = generation; const result = queue.then(() => requestedAt === generation ? execute(action) : fail("模式已变化，已取消旧按压")); queue = result.catch(() => {}); return result; },
    modeChanged(mode) { if (mode !== "BigPicture") { generation++; wake?.(); void endFocus(false).catch(() => {}); } },
    dispose() {
      disposed = true;
      returnRefresh = false;
      clearInterval(overlayCheck);
      runningSubscription?.();
      wake?.();
      void endFocus(false).catch(() => {});
      active.clear(); void notify("overlay", false);
      void notify("panel", false);
      if (typeof registration === "function") registration();
      else (registration?.unregister ?? registration?.Unregister)?.call(registration);
      for (const subscription of browserSubscriptions) {
        if (typeof subscription === "function") subscription();
        else (subscription?.unregister ?? subscription?.Unregister)?.call(subscription);
      }
    },
  };
}
