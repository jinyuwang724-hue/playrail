// Decky updates its plugin list on reload but can keep the old active panel object.
export function syncPluginPanel(env, name, Content) {
  const timer = env.setTimeout(() => {
    const store = env.DeckyPluginLoader?.deckyState;
    const state = store?.publicState?.();
    const loaded = state?.plugins?.find(plugin => plugin.name === name);
    if (state?.activePlugin?.name === name && loaded?.content?.type === Content && state.activePlugin !== loaded)
      store.setActivePlugin(name);
  }, 0);
  return () => {
    env.clearTimeout(timer);
    const store = env.DeckyPluginLoader?.deckyState;
    const active = store?.publicState?.().activePlugin;
    if (active?.name === name && active.content?.type === Content) store.closeActivePlugin();
  };
}
