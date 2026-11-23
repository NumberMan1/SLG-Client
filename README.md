# Unity 客户端

在 Unity Hub 中选择 **Add → Add project from disk**，打开客户端仓库根目录。项目使用本机已安装的 Unity 6.3 LTS（`6000.3.27f1`），版本记录在 `ProjectSettings/ProjectVersion.txt`。

| 路径 | 职责 |
| --- | --- |
| `Assets/Scenes/Frontier.unity` | 地形预览、登录和共享地图 |
| `Assets/Scripts/Networking` | HTTP 登录、TCP 和 WebSocket 认证及玩法请求 |
| `Assets/Scripts/Protocol` | 从 SLG-Server 的 `proto` 生成的 C# 类型 |
| `Assets/Plugins` | Protobuf 托管运行库与许可证 |
| `Assets/Editor` | Unity 编辑器菜单与批处理冒烟入口 |
| `Packages/manifest.json` | Unity 包依赖 |
| `ProjectSettings` | Unity 工程设置 |

## 登录和 TCP 连接

打开 `Assets/Scenes/Frontier.unity`，点击 **Play**，在 **Game** 视图查看地形和登录面板。按 SLG-Server 的 README 启动服务，输入账号和密码并点击 **登录**。网关默认地址是 `http://127.0.0.1:8080`，账号默认是 `demo`。用两个账号分别登录，在六边形地图上选择相邻的中立或敌方地块，再在出征面板选 20、40 或 60 兵并确认。双方可以看到行军旗帜、倒计时、占领或交战提示，以及结算后的驻军与领地归属。战报面板可翻阅最近 8 份战报并定位对应地块。预备兵力低于 100 时会自动补充，撤回的士兵可使兵力超过 100。若打包后通过本机明文 HTTP 联调，请构建 **Development Build**。

双方也可以抢同一块与各自领地相邻的中立地。地图会同时显示两支部队的路线和倒计时。先抵达的部队占领并驻守，后抵达的部队对当时的守军发起进攻。

敌对行军抵达后，选择交战地块可查看持续变化的双方兵力。攻守双方均可从相邻领地派援军，援军抵达即加入交战。点击「撤出战斗」可让幸存兵力返回预备兵力，守方撤出时攻方接管地块。战报记录实际参战的援军及撤出方。

原生端和 WebGL 共用简体中文界面。`Assets/Fonts/SlgInterfaceSC.ttf` 是基于 Noto Sans SC 制作的项目字体，包含常用简体中文字形及界面用字，许可在同目录的 `NotoSansSC-OFL.txt`。 [Noto CJK](https://github.com/notofonts/noto-cjk)

作品集以在线试玩网页作为主要入口，并提供实际玩法演示视频与两个代码仓库的链接。[SLG-Client](https://github.com/NumberMan1/SLG-Client) · [SLG-Server](https://github.com/NumberMan1/SLG-Server)

## 浏览器试玩

在 Unity Hub 为 `6000.3.27f1` 添加 **Web Build Support**。在编辑器选择 **SLG → 构建网页客户端** 并选择输出目录，或用批处理入口 `Slg.EditorTools.BuildCiPlayer.BuildWeb`，传入 `-buildTarget WebGL -customBuildPath <输出目录>`。构建使用未压缩的 Web 文件，方便普通静态文件服务器托管。

网页启动后自动使用当前网页所在的域名和端口作为网关地址。HTTP 注册和登录分别使用 `/v1/register` 和 `/v1/login`。浏览器通过 `/v1/session` 建立二进制 WebSocket，HTTPS 网页自动选择 WSS。每个 WebSocket 消息包含一个完整 Orbit 帧。

本机可将网关的 `SLG_WEB_ROOT` 设置为 Web 构建目录，再通过网关 HTTP 地址打开试玩。部署时将 Web 静态文件、`/v1/register`、`/v1/login` 和 `/v1/session` 放在同一个 HTTPS 站点，反向代理需支持 WebSocket Upgrade 并保留原始 Host。浏览器连接遵循同源校验。

进入共享地图后，网页继续使用这条 WebSocket 发送行军指令和读取世界状态。

### 编辑器冒烟检查

玩家可在登录页选择 **注册账号**，填写账号、密码和确认密码。账号和密码均须为 1 至 8 个 Unicode 字符。注册成功后返回登录页，使用新账号登录。

执行编辑器冒烟检查时，在启动 Unity 的终端设置 `SLG_DEMO_PASSWORD` 为已有联调账号的密码，然后选择 **SLG → 验证登录和 TCP 连接**。账号默认是 `demo`，可用 `SLG_DEMO_ACCOUNT` 指定。网关默认是 `http://127.0.0.1:8080`，可用 `SLG_CLIENT_GATEWAY_URL` 覆盖。

成功时 Console 显示「登录和 TCP 验证通过」及会话编号。脚本完成 HTTP 登录、TCP 建连及认证回执校验，然后关闭测试连接。批处理入口是 `Slg.EditorTools.LoginSmoke.RunBatch`，可替换下面命令中的 `-executeMethod` 参数。

Unity 插件包含 NuGet 的 `Google.Protobuf 3.33.0` 与 `System.Runtime.CompilerServices.Unsafe 4.5.2`，均使用 `netstandard2.0` 版本。生成代码通过服务端的 `scripts/generate-proto.sh` 更新。

## HTTP 健康检查

按 SLG-Server 的 README 启动 C++ 游戏服和 Go 网关。然后在 Unity 编辑器中选择 **SLG → 验证网关连接**。成功时，Console 会显示「网关连接验证通过，游戏服运行正常」。默认访问 `http://127.0.0.1:8080/v1/game/health`。如需指定另一个网关，可用 `SLG_CLIENT_GATEWAY_URL` 覆盖。[SLG-Server 仓库](https://github.com/NumberMan1/SLG-Server)

也可以关闭该工程的编辑器窗口后，在客户端仓库根目录运行批处理检查。将命令中的编辑器路径替换为本机安装路径。检查完成时，成功退出码为 0，失败退出码为 1，细节记录在日志中。

```powershell
$editor = 'C:\Program Files\Unity\Hub\Editor\6000.3.27f1\Editor\Unity.exe'
$project = (Get-Location).Path
New-Item -ItemType Directory -Force -Path ./Logs | Out-Null
$log = Join-Path $project 'Logs/gateway-smoke.log'
$unityArgs = @('-batchmode', '-nographics', '-projectPath', ('"{0}"' -f $project),
    '-executeMethod', 'Slg.EditorTools.GatewaySmoke.RunBatch', '-logFile', ('"{0}"' -f $log))
$process = Start-Process -FilePath $editor -ArgumentList $unityArgs -WindowStyle Hidden -PassThru -Wait
$process.ExitCode
```

## GitHub Actions

Client CI 在 `develop`、`master` 的推送和以这两个分支为目标的 PR 上运行，也支持手动运行。默认检查 Unity 包锁文件、资产的 `.meta` 和 Frontier 场景的构建配置。这些检查无需 Unity 许可证。

如果 Unity Hub 在 Windows 上生成了 `%LOCALAPPDATA%\Unity\licenses\UnityEntitlementLicense.xml`，直接用同一 Windows 用户下已激活的 Unity 编辑器打包。打开 **File → Build Profiles**，选择 Windows，启用 **Development Build** 后点击 **Build**。也可以在上面的批处理命令里把 `-executeMethod` 换成 `Slg.EditorTools.BuildCiPlayer.Build`，追加 `-quit -buildTarget Win64 -customBuildPath <Frontier.exe 的完整路径>`。[Unity 许可证文件说明](https://docs.unity.com/en-us/engine/6000.6/manual/get-started/install-and-upgrade/licenses-and-activation/activation-faq)

拿到 GameCI 支持的 `.ulf` 后，可在 **Settings → Secrets and variables → Actions** 设置 `UNITY_LICENSE`、`UNITY_EMAIL` 和 `UNITY_PASSWORD`，再到 **Variables** 新建 `SLG_GAMECI_ENABLED` 并设为 `true`。启用后 GameCI 分别构建 Windows 和 Web 开发包，成功产物为 `Frontier-Windows-x64` 和 `Frontier-Web`。来自 fork 或 Dependabot 的 PR 仅运行项目检查。[GameCI 许可证配置](https://game.ci/docs/github/activation/)
