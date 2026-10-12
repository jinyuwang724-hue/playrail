// Observe Steam's own window store. Focus, minimization and a running game do
// not change ownership; exiting Big Picture does. Never replace native callbacks.
export function steamBigPictureOpen(env) {
  try {
    const windows = env.SteamUIStore?.WindowStore;
    return windows?.MainWindowInstance?.IsMainGamepadUIWindow?.() === true &&
      !!windows.GamepadUIMainWindowInstance;
  } catch { return false; }
}

export function installSteamContext(env, ui, call, options = {}) {
  let disposed = false, revision = 0, queued = Promise.resolve(), reactionDispose;
  const publish = () => {
    if (disposed) return queued;
    const currentRevision = ++revision, active = steamBigPictureOpen(env);
    queued = queued.catch(() => {}).then(async () => {
      await options.ready;
      if (disposed || currentRevision !== revision) return;
      return call('set_steam_context', options.frontendId, currentRevision, active);
    }).catch(error => options.onError?.(error));
    return queued;
  };
  try {
    let reaction = options.reaction;
    if (!reaction) {
      const symbol = Object.getOwnPropertySymbols(env.SteamUIStore ?? {}).find(s => s.description === 'mobx administration');
      const module = symbol && ui.findModule?.(m => Object.values(m).includes(symbol));
      const candidates = Object.values(module ?? {}).filter(fn => typeof fn === 'function' && fn.length === 3 && /["']Reaction["']/.test(Function.prototype.toString.call(fn)));
      if (candidates.length === 1) reaction = candidates[0];
    }
    if (typeof reaction === 'function') reactionDispose = reaction(() => steamBigPictureOpen(env), publish, { name: 'ClawSteamBigPicture' });
  } catch (error) { options.onError?.(error); }
  // A small lease also detects renderer loss or a missed store notification.
  // Native cleanup restores the old mapping when reports stop arriving.
  const timer = (options.setInterval ?? setInterval)(publish, 2000);
  void publish();
  return { sync: publish, dispose() {
    disposed = true;
    revision++;
    reactionDispose?.();
    (options.clearInterval ?? clearInterval)(timer);
  } };
}
