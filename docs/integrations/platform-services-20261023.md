# Steam / Xbox 账户与服务、游戏库后台运行

2026-10-23。原生游戏库版本 0.1.82.0。

## 已实现

- 设置 → 账户与服务：Steam 本机账户记录、客户端运行状态、游戏来源开关、Steam 托盘预加载开关、立即托盘启动、打开 Steam / Xbox 管理账户、重新扫描平台游戏库。
- 默认打开游戏库时用 `steam.exe -silent` 启动 Steam；已运行时不重复启动、不改变窗口状态。启动请求在后台线程执行。Steam 不存在、需要首次登录等情况由客户端处理，启动失败记日志。
- 设置 → 通用 → 后台运行：关闭窗口后留在托盘（默认开）、立即收到后台、退出游戏库。双击托盘恢复原来的页面，右键可以打开或退出。关闭后台选项后，关闭窗口直接退出；托盘不可用时不取消关闭，手动后台动作退回最小化。
- 从托盘重新激活时显式显示窗口，兼容已有 Claw 键和控制中心路由；隐藏的窗口收到 minimize 路由时会恢复，避免一直藏在托盘。
- 收到托盘时暂停页面轮询、竖栏时钟和动态背景；音频沿用失焦淡出暂停。恢复后重启此前运行的页面计时器。
- “启动游戏后”选择收到后台时，后台常驻开启则隐藏到托盘，关闭则沿用任务栏最小化。
- Xbox 来源：当前用户注册的包中，只导入带 `MicrosoftGame.config`、`xboxservices.config` 或 `windows.gameExplorer` 声明的游戏，使用 `Package.GetAppListEntriesAsync` 取得名称与 AUMID，使用 `AppListEntry.LaunchAsync` 启动。Xbox 有独立页签，不混进本地页签。无管理员权限要求，也不直接运行受保护安装目录里的 EXE。
- Steam 当前用户优先读 `HKCU\Software\Valve\Steam\ActiveProcess\ActiveUser`，再回退最近修改的 userdata。账户名称是本机记录，不表示当前在线授权状态。

## 接口选择与边界

### Steam

当前继续使用本机 VDF、安装清单与客户端原生 `steam://rungameid/`、`steam://install/`。适合本机启动器，无需用户提供密码或 API Key，但封面缓存推断的库不是完整所有权清单，也可能含历史条目。

若后续需要跨机器、包含未安装游戏的完整账户库，采用官方 `IPlayerService/GetOwnedGames`，需要用户 Web API Key、SteamID64，并受用户游戏详情可见性影响。桌面客户端应使用公开主机 `api.steampowered.com`，不使用发行商专用 `partner.steam-api.com`。OpenID 用于验证身份，不等于授予完整库读取权限。本次没有加入云端登录或密钥存储。

- [Valve：IPlayerService](https://partner.steamgames.com/doc/webapi/IPlayerService?l=english)
- [Valve：Web API Overview](https://partner.steamgames.com/doc/webapi_overview?l=english)

### Xbox

选择 Windows 包枚举和系统应用启动接口。账户登录、商店授权、Game Pass 检查交给 Xbox / Gaming Services。覆盖已注册且带游戏标记的 GDK / Xbox Live 游戏；部分未带这些标记的 Store 游戏不会导入，不宣称得到用户的完整已购库或 Game Pass 云端目录。

微软商店 Collections 的 publisherQuery 主要针对发行商自己的产品目录，需要相应授权，客户端不能直接调用，不能据此承诺通用个人完整库。Xbox title history 也不是已购库。`Windows.Gaming.Preview.GamesEnumeration.GameList` 需要受限 `gameList` 能力，因此没有作为默认依赖。

- [Microsoft：PackageManager.FindPackagesForUser](https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.packagemanager.findpackagesforuser)
- [Microsoft：商店服务功能与授权范围](https://learn.microsoft.com/en-us/gaming/gdk/docs/store/commerce/service-to-service/service-to-service-nav)
- [Microsoft：publisherQuery](https://learn.microsoft.com/en-us/xbox/gdk/docs/store/commerce/service-to-service/microsoft-store-apis/xstore-v9-query-for-products?view=gdk-2604)
- [Microsoft：受限 gameList 能力](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/app-capability-declarations)

## 验证

- `dotnet run --project tests/LibraryServices/LibraryServices.csproj`：偏好迁移、保存、无效值、普通应用排除、Xbox 启动标识保留与页签隔离、来源关闭、原生托盘创建/更新/恢复/销毁，以及真实用户包枚举通过。
- 本机识别到 Xbox 客户端，0 款符合条件的 Xbox 游戏，0 个包读取失败；扫描约 0.8–1.1 秒，当前在载入游戏库时执行。没有真实 Xbox 游戏可测启动。
- 本机 Steam 已在运行，预加载路径会保留现有客户端；没有为了验证冷启动而退出用户 Steam。
- Debug 与 Release NativeAOT/MSIX 构建通过。实机确认服务页和后台选项正常显示；Alt+F4 后进程留在托盘；再次打开恢复原页面且 PID 不变；通用页“退出游戏库”完全退出。退出时先停止定时器并取消帧回调，再释放窗口资源。

## 代码

`Data/PlatformClients.cs` 管理客户端启动；`Data/XboxGames.cs` 扫描和启动 Xbox 游戏；`LibraryTray.cs` 管理 Win32 通知图标；`Stage/WallStage.Services.cs` 构建服务页与处理托盘计时器；`UiPrefs` 将三个新开关写入 ui.ini。


统一发布由小黄鸭/Magpie设置聊天完成：基于37bea64合并上述功能，0.1.82实际核对托盘隐藏、同PID恢复与完全退出。退出后的背景帧竞态在0.1.83中增加停止门控和绘制/释放同步；最终安装核对见docs/scaling-settings-20261023.md及artifacts/scaling-settings。
