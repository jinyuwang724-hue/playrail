// Small, user-driven Steam shortcut flow. Browsing never creates games or collections.
export function createGameAdder(env, ui, api, React = env.SP_REACT) {
  const h = React.createElement;
  const NONE = "claw:none", NEW = "claw:new";
  const listeners = new Set();
  let draft = { busy: false, phase: "", error: "", file: null, name: "",
    collections: [], category: NONE, categoryName: "", appId: null, collectionDone: false, savedCategoryId: null,
    drives: [], drive: "" };
  const pickerPaths = new Map();
  let initialized = false;
  const publish = patch => {
    draft = { ...draft, ...patch };
    listeners.forEach(fn => fn(draft));
  };
  const sleep = ms => new Promise(resolve => env.setTimeout(resolve, ms));
  const cleanPath = value => {
    const text = typeof value === "string" ? value.trim() : "";
    return text.startsWith('"') && text.endsWith('"') ? text.slice(1, -1) : text;
  };
  const pathKey = value => cleanPath(value).replace(/\//g, "\\").toLocaleLowerCase();
  const pickerPathFor = value => {
    const path = value.replace(/\\/g, "/");
    // Decky's picker removes a final slash: D:/. must stay an absolute root.
    return /^[a-z]:\/$/i.test(path) ? path + "." : path;
  };
  const appIdOf = value => {
    if (!Number.isInteger(value) || value === 0 || value < -2147483648 || value > 4294967295) throw new Error("Steam 未返回有效游戏入口，请在库中查看后再试");
    return value >>> 0;
  };
  function collectionStore() {
    const store = env.collectionStore;
    if (!store?.collectionsFromStorage?.values || typeof store.GetCollection !== "function") throw new Error("Steam 游戏分类正在加载，请稍后重新打开添加游戏");
    return store;
  }
  function listCollections() {
    return [...collectionStore().collectionsFromStorage.values()]
      .filter(canAddToCollection)
      .map(item => ({ id: String(item.id), name: item.displayName, dynamic: item.bIsDynamic }))
      .sort((a, b) => a.name.localeCompare(b.name, "zh-CN"));
  }
  function canAddToCollection(item) {
    return Boolean(item?.bAllowsDragAndDrop && (item.bIsEditable || item.id === "favorite"));
  }
  function categoryTarget() {
    if (draft.category === NONE) return null;
    const store = collectionStore();
    if (draft.category === NEW) {
      const name = draft.categoryName.trim();
      if (!name || name.length > 128 || /[\x00-\x1f]/.test(name)) throw new Error("请填写有效的分类名称（最多 128 个字符）");
      if (store.BIsSystemCollectionName?.(name) || store.BIsPartnerCollectionName?.(name)) throw new Error("该名称已用于 Steam 内置分类，请换一个名称");
      const matches = store.GetUserCollectionsByName(name);
      if (matches.length > 1) throw new Error("有多个同名分类，请直接选择已有分类");
      if (matches[0]) {
        if (!canAddToCollection(matches[0])) throw new Error("该分类不能手动加入游戏，请换一个分类");
        return { collection: matches[0], name };
      }
      if (typeof store.NewUnsavedCollection !== "function") throw new Error("当前 Steam 不能创建分类，请选择已有分类");
      return { collection: null, name };
    }
    const collection = store.GetCollection(draft.category);
    if (!canAddToCollection(collection)) throw new Error("所选分类已改变，请返回后重新选择");
    return { collection, name: collection.displayName };
  }
  async function overviewFor(appId, name) {
    for (let attempt = 0; attempt < 50; attempt++) {
      const overview = env.appStore?.GetAppOverviewByAppID?.(appId);
      if (overview && (name === undefined || overview.display_name === name)) return overview;
      await sleep(100);
    }
    throw new Error(name === undefined ? "游戏已添加，Steam 游戏库尚未载入完成，请点击继续完成" :
      "游戏名称尚未保存完成，请点击继续完成重试");
  }
  function detailsFor(appId) {
    const store = env.appDetailsStore;
    const cached = store?.GetAppDetails?.(appId);
    if (cached?.strShortcutExe) return Promise.resolve(cached);
    if (typeof store?.RegisterForAppData !== "function") return Promise.reject(new Error("无法读取现有游戏入口，请稍后再试"));
    return new Promise((resolve, reject) => {
      let registration, timer, done = false;
      const finish = (error, details) => {
        if (done) return;
        done = true;
        env.clearTimeout(timer);
        registration?.unregister?.();
        error ? reject(error) : resolve(details);
      };
      timer = env.setTimeout(() => finish(new Error("Steam 游戏信息尚未读完，请稍后再试")), 3000);
      try {
        registration = store.RegisterForAppData(appId, details => {
          if (details?.strShortcutExe) finish(null, details);
        });
        if (done) registration?.unregister?.();
      } catch { finish(new Error("无法读取现有游戏入口，请稍后再试")); }
    });
  }
  async function findExisting(file) {
    const apps = env.appStore?.allApps;
    if (!Array.isArray(apps)) throw new Error("Steam 游戏库正在加载，请稍后再试");
    const shortcuts = apps.filter(app => app.BIsShortcut?.());
    const deadline = Date.now() + 15000;
    for (let offset = 0; offset < shortcuts.length; offset += 6) {
      if (Date.now() >= deadline) throw new Error("Steam 游戏信息尚未读完，请稍后再试");
      const batch = await Promise.all(shortcuts.slice(offset, offset + 6).map(async app => ({ app, details: await detailsFor(app.appid) })));
      const matches = batch.filter(item => pathKey(item.details.strShortcutExe) === pathKey(file.path) &&
        (!file.arguments || (item.details.strLaunchOptions ?? "").trim() === file.arguments.trim()));
      if (matches.length) return matches.find(item => item.app.display_name === draft.name.trim())?.app.appid ?? matches[0].app.appid;
    }
    return null;
  }
  async function saveCategory(overview) {
    const target = categoryTarget();
    if (!target) return null;
    const store = collectionStore();
    let collection = target.collection;
    if (!collection) {
      // Recheck just before creation: Steam's SaveCollection can replace a same-name collection.
      const matches = store.GetUserCollectionsByName(target.name);
      if (matches.length > 1) throw new Error("有多个同名分类，请选择已有分类");
      collection = matches[0] ?? store.NewUnsavedCollection(target.name, undefined, [overview]);
    }
    if (!canAddToCollection(collection) || typeof collection.Save !== "function" ||
        !collection.internalAddedList?.add || !collection.internalRemovedList?.delete) throw new Error("所选分类不支持手动加入游戏");
    const added = collection.internalAddedList.has(overview.appid);
    const removed = collection.internalRemovedList.has(overview.appid);
    // These are the same sets changed by Steam's AddApps, but await Save instead of its
    // fire-and-forget call so the UI cannot report a failed collection write as success.
    collection.internalAddedList.add(overview.appid);
    collection.internalRemovedList.delete(overview.appid);
    try {
      await collection.Save();
    } catch {
      if (!added) collection.internalAddedList.delete(overview.appid);
      if (removed) collection.internalRemovedList.add(overview.appid);
      collection.UpdateAllApps?.();
      throw new Error("游戏已在 Steam 库中，但分类未保存完成；可以点击继续完成重试");
    }
    return String(collection.id);
  }
  async function openNonSteamLibrary() {
    const router = ui.Router;
    const mainWindow = router?.WindowStore?.GamepadUIMainWindowInstance;
    const navigator = mainWindow?.Navigator;
    const target = "/library/tab/DesktopApps";
    if (typeof navigator?.LibraryTab !== "function" && typeof mainWindow?.Navigate !== "function" && typeof router?.Navigate !== "function")
      throw new Error("游戏已添加，请从 Steam 库打开它；当前界面不能自动跳转");
    // Let the side menu finish restoring focus before choosing the library tab.
    router.CloseSideMenus?.();
    await sleep(0);
    router.SetLastLibraryTab?.("DesktopApps", undefined);
    if (typeof navigator?.LibraryTab === "function") {
      // Steam's window navigator also moves focus away from the old collection.
      navigator.LibraryTab("DesktopApps", { bReplace: true });
    } else if (typeof mainWindow?.Navigate === "function") {
      mainWindow.Navigate(target, true);
    } else {
      router.Navigate(target, true);
    }
    if (mainWindow?.m_history?.location) {
      // Verify after Steam processes focus/tab effects, not just after history.push.
      await sleep(200);
      const tab = router.GetLastLibraryTab?.();
      if (mainWindow.m_history.location.pathname !== target || (tab && (tab.strActiveTab !== "DesktopApps" || tab.collectionid)))
        throw new Error("游戏已添加，非 Steam 游戏页面尚未打开，请点击继续完成重试");
    }
  }
  async function loadDrives() {
    const locations = await api.call("get_game_picker_locations");
    if (!Array.isArray(locations?.drives) || !locations.drives.length) throw new Error("没有可浏览的磁盘，请插入 SD 卡后刷新");
    const drive = locations.drives.some(item => item.id === draft.drive) ? draft.drive : locations.defaultDrive;
    if (!pickerPaths.has(locations.defaultDrive)) pickerPaths.set(locations.defaultDrive, pickerPathFor(locations.defaultPath));
    publish({ drives: locations.drives, drive });
  }
  async function refreshDrives() {
    if (draft.busy) return;
    publish({ busy: true, error: "", phase: "正在读取磁盘…" });
    try { await loadDrives(); }
    catch (error) { publish({ error: String(error.message ?? error) }); }
    finally { publish({ busy: false, phase: "" }); }
  }
  async function initialize() {
    if (initialized || draft.busy) return;
    publish({ error: "", busy: true, phase: "正在读取磁盘和游戏分类…" });
    try {
      await loadDrives();
      publish({ collections: listCollections() });
      initialized = true;
    } catch (error) { publish({ error: String(error.message ?? error) }); }
    finally { publish({ busy: false, phase: "" }); }
  }
  async function chooseFile() {
    if (draft.busy) return;
    publish({ busy: true, error: "", phase: "请选择游戏启动文件…" });
    try {
      if (!draft.drives.length) await loadDrives();
      const drive = draft.drives.find(item => item.id === draft.drive);
      if (!drive) throw new Error("请先选择浏览磁盘；SD 卡刚插入时可以刷新磁盘列表");
      const pickerPath = pickerPathFor(await api.call("get_game_picker_path", drive.id, pickerPaths.get(drive.id) || drive.path));
      const picked = await api.openFilePicker(0, pickerPath, true, true, undefined, ["exe", "lnk"], false, false, 500);
      const source = await api.call("inspect_game_file", picked.realpath || picked.path);
      let metadata;
      if (typeof env.SteamClient?.Apps?.GetShortcutDataForPath === "function") {
        try { metadata = await env.SteamClient.Apps.GetShortcutDataForPath(source.path); }
        catch { /* A regular executable can still be added using its file name. */ }
      }
      if (source.extension === ".lnk" && (!metadata || typeof metadata === "number" || !metadata.strExePath)) throw new Error("无法解析这个快捷方式，请直接选择游戏的 .exe 文件");
      const resolved = source.extension === ".lnk" ? await api.call("inspect_game_file", cleanPath(metadata.strExePath)) : source;
      if (resolved.extension !== ".exe") throw new Error("请选择指向游戏 .exe 程序的启动文件");
      const name = typeof metadata?.strAppName === "string" && metadata.strAppName.trim() ? metadata.strAppName.trim() : resolved.name;
      const sourceDrive = /^([a-z]:)[\\/]/i.exec(source.directory)?.[1].toUpperCase();
      if (sourceDrive) pickerPaths.set(sourceDrive, pickerPathFor(source.directory));
      publish({ file: { ...resolved, arguments: typeof metadata?.strArguments === "string" ? metadata.strArguments : "" },
        name: name.slice(0, 128), appId: null, created: false, shortcutConfigured: false, collectionDone: false, savedCategoryId: null,
        ...(draft.drives.some(item => item.id === sourceDrive) ? { drive: sourceDrive } : {}) });
    } catch (error) {
      // Canceling the picker keeps the draft and performs no library write.
      if (!/user cancel(?:ed|led)/i.test(String(error))) publish({ error: String(error.message ?? error) });
    } finally { publish({ busy: false, phase: "" }); }
  }
  async function addGame() {
    if (draft.busy || !draft.file) return;
    const name = draft.name.trim();
    if (!name || name.length > 128 || /[\x00-\x1f]/.test(name)) { publish({ error: "请填写有效的游戏名称（最多 128 个字符）" }); return; }
    publish({ busy: true, error: "", phase: "正在添加游戏…" });
    try {
      // All preflight checks are read-only. The first write occurs only after this button.
      await api.call("inspect_game_file", draft.file.path);
      if (!draft.collectionDone) categoryTarget();
      const apps = env.SteamClient?.Apps;
      if (typeof apps?.AddShortcut !== "function" || typeof apps.SetShortcutName !== "function" || typeof apps.SetShortcutStartDir !== "function" ||
          typeof apps.SetShortcutLaunchOptions !== "function") throw new Error("当前 Steam 添加游戏接口不可用，请稍后重试");
      if (draft.appId === null) {
        publish({ phase: "正在读取已有游戏入口…" });
        const existing = await findExisting(draft.file);
        if (existing !== null) {
          publish({ appId: appIdOf(existing), phase: "游戏已在库中，正在保存名称…" });
        } else {
          publish({ phase: "正在添加到 Steam 游戏库…" });
          const appId = appIdOf(await apps.AddShortcut(name, `"${draft.file.path}"`, "", ""));
          // Retain the real ID immediately: retrying a later step must never add again.
          publish({ appId, created: true });
        }
      }
      let overview = await overviewFor(draft.appId);
      if (overview.display_name !== name) {
        // AddShortcut can reuse an existing target. Save the confirmed name for either path.
        publish({ phase: "正在保存游戏名称…" });
        await apps.SetShortcutName(draft.appId, name);
        overview = await overviewFor(draft.appId, name);
      }
      if (draft.created && !draft.shortcutConfigured) {
        await apps.SetShortcutStartDir(draft.appId, `"${draft.file.directory}"`);
        await apps.SetShortcutLaunchOptions(draft.appId, draft.file.arguments);
        publish({ shortcutConfigured: true });
      }
      if (!draft.collectionDone) {
        publish({ phase: "正在保存游戏分类…" });
        const savedCategoryId = await saveCategory(overview);
        publish({ collectionDone: true, savedCategoryId });
      }
      publish({ phase: "正在打开非 Steam 游戏…" });
      await openNonSteamLibrary();
      api.toaster?.toast?.({ title: "游戏已加入 Steam 库", body: overview.display_name || name });
      publish({ file: null, name: "", appId: null, created: false, shortcutConfigured: false, collectionDone: false, savedCategoryId: null });
    } catch (error) {
      const detail = String(error.message ?? error);
      publish({ error: draft.appId !== null ? "游戏入口已保留。" + detail : detail });
    } finally { publish({ busy: false, phase: "" }); }
  }
  function GameAdder({ embedded = false } = {}) {
    const [value, setValue] = React.useState(() => draft);
    React.useEffect(() => {
      listeners.add(setValue); setValue(draft);
      void initialize();
      return () => listeners.delete(setValue);
    }, []);
    const row = (key, child) => h(ui.PanelSectionRow, { key }, child);
    const field = (key, label, content) => row(key, h("div", { className: "claw-field", style: { padding: "8px 0", lineHeight: 1.5, overflowWrap: "anywhere" } }, h("strong", { className: "claw-field-label" }, label), h("div", { className: "claw-field-value" }, content)));
    const step = (key, label) => row(key, h("div", { className: "claw-section-label" }, label));
    const options = React.useMemo(() => [{ label: "不加入分类", data: NONE }, ...value.collections.map(item => ({ label: item.name, data: item.id })), { label: "新建分类…", data: NEW }], [value.collections]);
    const driveOptions = React.useMemo(() => value.drives.map(item => ({ label: item.label, data: item.id })), [value.drives]);
    const locked = value.busy || value.appId !== null;
    return h(ui.PanelSection, { title: embedded ? undefined : "添加游戏" },
      step("select-file", "1 · 选择启动文件"),
      driveOptions.length > 0 && row("drive", h(ui.DropdownItem, { label: "浏览磁盘", rgOptions: driveOptions, selectedOption: value.drive, disabled: locked, onChange: option => {
        if (option.data !== draft.drive) publish({ drive: option.data, error: "" });
      } })),
      row("refresh-drives", h(ui.ButtonItem, { layout: "below", disabled: locked, onClick: refreshDrives }, "刷新磁盘列表")),
      row("pick", h(ui.ButtonItem, { layout: "below", disabled: locked, onClick: chooseFile }, value.file ? "重新选择启动文件" : "选择游戏启动文件")),
      step("confirm-game", "2 · 确认游戏信息"),
      row("name", h(ui.TextField, { label: "游戏名称", placeholder: "选择启动文件后可修改名称", value: value.name, disabled: locked || !value.file, onChange: event => publish({ name: event.target?.value ?? "" }) })),
      field("file", "启动文件", value.file?.path || "尚未选择"),
      row("category", h(ui.DropdownItem, { label: "游戏分类", rgOptions: options, selectedOption: value.category, disabled: value.busy || value.collectionDone, onChange: option => {
        if (option.data !== draft.category) publish({ category: option.data, collectionDone: false });
      } })),
      value.category === NEW && row("new-category", h(ui.TextField, { label: "新分类名称", placeholder: "例如：非 Steam 游戏", value: value.categoryName, disabled: value.busy || value.collectionDone, onChange: event => publish({ categoryName: event.target?.value ?? "" }) })),
      value.phase && field("progress", "进度", value.phase),
      value.error && field("error", "提示", value.error),
      row("add", h("div", { className: "claw-primary-action" }, h(ui.ButtonItem, { layout: "below", disabled: value.busy || !value.file || !value.name.trim() || (value.category === NEW && !value.categoryName.trim()), onClick: addGame }, value.appId !== null ? "继续完成并打开游戏库" : "添加并打开游戏库"))));
  }
  return GameAdder;
}
