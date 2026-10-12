// Read Steam's existing stores; never replace its native UI-mode callbacks.
export function navigationUIActive(env) {
  try {
    const store = env.SteamUIStore;
    return store?.WindowStore?.MainWindowInstance?.IsMainGamepadUIWindow?.() === true &&
      store.IsGamepadUIWindowActive?.() === true && store.MainWindowVisible?.Value === true &&
      !store.MainRunningAppID;
  } catch { return false; }
}

export function installNavigationHaptics(env, call, options = {}) {
  env.__clawToolboxHaptics?.dispose?.();
  const originalFetch = env.fetch;
  const frontendId = options.frontendId;
  let disposed = false, transitioning = false, active, revision = 0, contextFailed = false;
  let context = Promise.resolve(false);
  const delegate = (receiver, args) => originalFetch.apply(receiver, args);
  const response = value => new (env.Response ?? Response)(JSON.stringify(value), {
    status: 200, headers: { 'Content-Type': 'application/json' },
  });
  const bounded = async operation => {
    let timer;
    try {
      return await Promise.race([operation,
        new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('Haptic request timed out')), 200); }),
      ]);
    } finally { clearTimeout(timer); }
  };
  const sync = (force = false) => {
    const next = !disposed && !transitioning && options.enabled?.() === true && navigationUIActive(env);
    if (!force && next === active && !contextFailed) return context;
    active = next;
    contextFailed = false;
    const currentRevision = ++revision;
    context = (async () => {
      const result = await bounded((async () => {
        await options.ready?.();
        return call('set_navigation_context', frontendId, currentRevision, next);
      })());
      if (currentRevision === revision) contextFailed = result?.handled !== true;
      return result?.handled === true;
    })().catch(error => {
      if (currentRevision === revision) contextFailed = true;
      options.onError?.(error); return false;
    });
    return context;
  };
  const stopOriginal = async () => {
    try {
      await bounded(delegate(env, [`http://127.0.0.1:${options.port?.() ?? 47991}/dash/haptic`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ stop: true, requestId: Date.now() * 1000 }),
      }]));
    } catch { /* A missing Playhub server must not prevent returning to Desktop. */ }
  };
  const wrapper = async function (...args) {
    const [input, init] = args;
    let url;
    try { url = new URL(input?.url ?? String(input)); } catch { return delegate(this, args); }
    if (url.protocol !== 'http:' || url.hostname !== '127.0.0.1' ||
        Number(url.port) !== (options.port?.() ?? 47991) ||
        (init?.method ?? input?.method ?? 'GET').toUpperCase() !== 'POST') return delegate(this, args);
    if (url.pathname === '/mode/desktop/switch') {
      // Playhub kills Decky's entire process tree. Drain motors before that request.
      transitioning = true;
      const stopped = await sync(true);
      await stopOriginal();
      // If the bridge is unavailable, let every previously queued pulse expire first.
      if (!stopped) await new Promise(resolve => setTimeout(resolve, 250));
      try { return await delegate(this, args); }
      finally { transitioning = false; void sync(); }
    }
    if (url.pathname !== '/dash/haptic') return delegate(this, args);
    let request;
    try { request = JSON.parse(init?.body ?? await input.clone().text()); }
    catch { return delegate(this, args); }
    if (request?.stop !== true && (request?.vendorId !== 0x0db0 || request?.productId !== 0x1901 ||
        typeof request?.pattern !== 'string')) return delegate(this, args);
    const signal = init?.signal ?? input?.signal;
    if (signal?.aborted) throw new DOMException('Aborted', 'AbortError');
    if (request.stop === true) {
      try { await bounded(call('play_navigation_haptic', { ...request, frontendId })); }
      catch (error) { options.onError?.(error); }
      return delegate(this, args);
    }
    const pending = sync(), currentRevision = revision;
    const connected = await pending;
    // Recheck after asynchronous parsing / RPC so an exit cannot replay a queued pulse.
    if (disposed || transitioning || !navigationUIActive(env) || currentRevision !== revision)
      return response({ handled: true, path: 'navigation-inactive' });
    if (options.enabled?.() !== true) return delegate(this, args);
    if (!connected) return response({ handled: true, path: 'bridge-unavailable' });
    if (signal?.aborted) throw new DOMException('Aborted', 'AbortError');
    try {
      const result = await bounded(call('play_navigation_haptic', { ...request, frontendId,
        revision, expiresAtMs: Date.now() + 180 }));
      if (result?.handled !== true) options.onError?.(new Error(result?.path ?? 'Haptic output unavailable'));
      // Playhub also has a browser fallback when handled=false. Keep it suppressed.
      return response({ ...result, handled: true });
    } catch (error) {
      options.onError?.(error);
      // A delayed failure must not start a second, unmanaged output during an exit.
      return response({ handled: true, path: 'bridge-unavailable' });
    }
  };
  const visibility = env.SteamUIStore?.MainWindowVisible?.Subscribe?.(() => { void sync(); });
  env.fetch = wrapper;
  const owner = {
    sync,
    dispose() {
      if (disposed) return;
      disposed = true;
      visibility?.Unsubscribe?.();
      void sync(true);
      void stopOriginal();
      if (env.fetch === wrapper) env.fetch = originalFetch;
      if (env.__clawToolboxHaptics === owner) delete env.__clawToolboxHaptics;
    },
  };
  env.__clawToolboxHaptics = owner;
  return owner;
}
