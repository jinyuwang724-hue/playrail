// Screenshots uploaded to Steam (Big Picture → 媒体). They are not on disk, so the agent cannot list
// them; this asks Steam the same way its own 媒体 page does (PublishedFile.GetUserFiles over the
// client's signed-in connection, no web API key) and hands the list to the agent, which keeps a
// local copy for ClawLibrary's 截图 page. Runs once after load, every 30 minutes, and whenever the
// agent asks (the 截图 page opened). Read-only: nothing here changes anything on Steam.
const CLOUD_SHOT_FILETYPE = 4;   // k_EWorkshopFileTypeScreenshot
const CLOUD_SHOT_PRIVACY = 30;          // public | friends | private | unlisted, as the 媒体 page asks
const CLOUD_SHOT_PAGE = 100, CLOUD_SHOT_MAX_PAGES = 100, CLOUD_SHOT_EVERY_MS = 30 * 60 * 1000;

function cloudShotModules(win) {
  let req;
  const chunks = win.webpackChunksteamui;
  if (!chunks) return null;
  chunks.push([[Symbol("claw-screenshots")], {}, r => { req = r; }]);
  let service, request, init, cm;
  for (const id of Object.keys(req?.m ?? {})) {
    let mod;
    try { mod = req(id); } catch { continue; }
    if (!mod || typeof mod !== "object") continue;
    for (const v of Object.values(mod)) {
      if (!v) continue;
      if (!service && typeof v.GetUserFiles === "function" && typeof v.GetItemInfo === "function") service = v;
      if (!request && typeof v === "function" && v.prototype?.getClassName) {
        try { if (new v().getClassName() === "CPublishedFile_GetUserFiles_Request") request = v; } catch {}
      }
      if (!init && typeof v.Init === "function" && typeof v.InitFromMsg === "function") init = v;
      if (!cm && v.CMInterface?.GetServiceTransport) cm = v.CMInterface;
    }
    if (service && request && init && cm) return { service, request, init, cm };
  }
  return null;
}

function cloudShotSteamId(win) {
  const id = win.App?.m_CurrentUser?.strSteamID;
  return typeof id === "string" && /^\d{17}$/.test(id) ? id : null;
}

export async function listCloudScreenshots(win, modules) {
  const steamid = cloudShotSteamId(win);
  if (!modules || !steamid) throw new Error("Steam 截图接口不可用");
  const items = [];
  for (let page = 1; page <= CLOUD_SHOT_MAX_PAGES; page++) {
    const msg = modules.init.Init(modules.request), body = msg.Body();
    body.set_steamid(steamid); body.set_page(page); body.set_numperpage(CLOUD_SHOT_PAGE);
    body.set_sortmethod("newestfirst"); body.set_privacy(CLOUD_SHOT_PRIVACY); body.set_filetype(CLOUD_SHOT_FILETYPE);
    const response = await modules.service.GetUserFiles(modules.cm.GetServiceTransport(), msg);
    if (response.GetEResult() !== 1) throw new Error(`Steam 截图列表读取失败（${response.GetEResult()}）`);
    const { publishedfiledetails = [], total = 0 } = response.Body().toObject();
    for (const d of publishedfiledetails)
      items.push({ id: String(d.publishedfileid), url: d.image_url || d.file_url, appid: d.consumer_appid || 0,
        shortcutid: d.consumer_shortcutid || 0, shortcutname: d.shortcutname || "", time: d.time_created || 0,
        width: d.image_width || 0, height: d.image_height || 0 });
    if (publishedfiledetails.length < CLOUD_SHOT_PAGE || items.length >= total) break;
  }
  return items;
}

export function installCloudScreenshots(win, call, options = {}) {
  let disposed = false, modules, running = null;
  const sync = requestId => {
    if (disposed) return Promise.resolve();
    if (running && !requestId) return running;
    const work = (async () => {
      await options.ready;
      modules ??= cloudShotModules(win);
      const items = await listCloudScreenshots(win, modules);
      if (!disposed) await call("steam_cloud_screenshots", requestId ?? null, items);
    })().catch(error => options.onError?.(error));
    if (!requestId) running = work.finally(() => { running = null; });
    return work;
  };
  const timer = setInterval(() => void sync(), CLOUD_SHOT_EVERY_MS);
  void sync();
  return { sync, dispose() { disposed = true; clearInterval(timer); } };
}
