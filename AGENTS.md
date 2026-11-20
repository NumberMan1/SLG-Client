# SLG 客户端

- 日常开发在 `develop`，`master` 用于稳定版本。
- Unity 资产与对应的 `.meta` 一起维护。运行时脚本放在 `Assets/Scripts`，编辑器工具放在 `Assets/Editor`。
- 网关联通检查见 [README.md](README.md)。协议变更同步核对 SLG-Server 与 Unity 调用方。
- 推送后按提交 SHA 跟踪 GitHub Actions 的 **Client CI**，等待项目校验和已启用的 Unity 构建结束。失败时查看失败步骤的日志，修复后核对新提交的运行。交付时附上运行链接和结果，说明条件跳过的构建作业。
