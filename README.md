# Unity 客户端

在 Unity Hub 中选择 **Add → Add project from disk**，打开客户端仓库根目录。项目使用本机已安装的 Unity 6.3 LTS（`6000.3.27f1`），版本记录在 `ProjectSettings/ProjectVersion.txt`。

| 路径 | 职责 |
| --- | --- |
| `Assets/Scenes/Frontier.unity` | 3D 地形预览和登录演示 |
| `Assets/Scripts/Networking` | HTTP 登录、TCP 认证和健康检查 |
| `Assets/Scripts/Protocol` | 从 SLG-Server 的 `proto/session.proto` 生成的 C# 类型 |
| `Assets/Plugins` | Protobuf 托管运行库与许可证 |
| `Assets/Editor` | Unity 编辑器菜单与批处理冒烟入口 |
| `Packages/manifest.json` | Unity 包依赖 |
| `ProjectSettings` | Unity 工程设置 |

## 登录和 TCP 连接

打开 `Assets/Scenes/Frontier.unity`，点击 **Play**，在 **Game** 视图查看地形和登录面板。按 SLG-Server 的 README 在 Podman 中启动网关，再输入服务端配置的账号和密码并点击 **Sign in**。网关默认地址是 `http://127.0.0.1:8080`，账号默认是 `demo`。认证成功后界面显示服务端返回的会话 ID。此场景使用静态地形预览，支持主动断开连接，并会显示网关关闭连接的结果。若打包后通过本机明文 HTTP 联调，请构建 **Development Build**。

作品集交付包含实际玩法演示视频与两个代码仓库的链接。[SLG-Client](https://github.com/NumberMan1/SLG-Client) · [SLG-Server](https://github.com/NumberMan1/SLG-Server)

### 编辑器冒烟检查

在服务端设置 `SLG_DEMO_PASSWORD` 并启动网关。在启动 Unity 的终端设置同一个 `SLG_DEMO_PASSWORD`，然后选择 **SLG → Verify Login and TCP Connection**。账号默认是 `demo`，可在两端用 `SLG_DEMO_ACCOUNT` 指定同一个账号。网关默认是 `http://127.0.0.1:8080`，可用 `SLG_CLIENT_GATEWAY_URL` 覆盖。

成功时 Console 显示 `SLG login and TCP smoke passed (session …)`。脚本完成 HTTP 登录、TCP 建连及认证回执校验，然后关闭测试连接。批处理入口是 `Slg.EditorTools.LoginSmoke.RunBatch`，可替换下面命令中的 `-executeMethod` 参数。

Unity 插件包含 NuGet 的 `Google.Protobuf 3.33.0` 与 `System.Runtime.CompilerServices.Unsafe 4.5.2`，均使用 `netstandard2.0` 版本。生成代码通过服务端的 `scripts/generate-proto.sh` 更新。

## HTTP 健康检查

按 SLG-Server 的 README 启动 Go 游戏服和网关。然后在 Unity 编辑器中选择 **SLG → Verify Gateway Connection**。成功时，Console 会显示 `SLG gateway smoke passed (game-server: ok)`。检查会确认网关返回的响应来自游戏服，并验证其状态字段。默认访问 `http://127.0.0.1:8080/v1/game/health`。如需指定另一个网关，可在启动 Unity 前设置 `SLG_CLIENT_GATEWAY_URL` 为网关基址。[SLG-Server 仓库](https://github.com/NumberMan1/SLG-Server)

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
