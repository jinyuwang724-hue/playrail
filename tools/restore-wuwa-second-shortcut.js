(() => {
  const appID = 3941414661;
  const overview = appStore.GetAppOverviewByAppID(appID);
  if (overview?.display_name !== 'Wuthering Waves') throw new Error('鸣潮第二入口身份不符，未恢复');
  if ((window.SteamUIStore.m_runningAppIDs?.size ?? 0) > 0) throw new Error('有游戏正在运行，未恢复');
  SteamClient.Apps.SetShortcutExe(appID, '"C:\\Software\\Playhub\\Playhub.GameSession.exe"');
  SteamClient.Apps.SetShortcutStartDir(appID, '"C:\\Game\\Wuthering Waves Game\\Client\\Binaries\\Win64"');
  SteamClient.Apps.SetShortcutLaunchOptions(appID, '--game "C:\\Game\\Wuthering Waves Game\\Client\\Binaries\\Win64\\Client-Win64-Shipping.exe"');
  return { appID, restoredFields: ['Exe', 'StartDir', 'LaunchOptions'] };
})()
