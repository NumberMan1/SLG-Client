# Unity 客户端

在 Unity Hub 中选择 **Add → Add project from disk**，打开客户端仓库根目录。项目使用本机已安装的 Unity 6.3 LTS（`6000.3.27f1`），版本记录在 `ProjectSettings/ProjectVersion.txt`。

| 路径 | 职责 |
| --- | --- |
| `Assets/Scripts/Networking` | 运行时 HTTP 联通探针 |
| `Assets/Editor` | Unity 编辑器菜单与批处理冒烟入口 |
| `Packages/manifest.json` | Unity 包依赖 |
| `ProjectSettings` | Unity 工程设置 |

## 冒烟检查

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
