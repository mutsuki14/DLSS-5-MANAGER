这是 **mutsuki14 fork 的首个 033 集成预览版**，从 `codex/health-check-safe-restore` 开发分支构建。没有合并 main，也没有向上游发布。原应用作者为 NODIX TECH / Numidia Studios。

### 下载与使用

- `*-win-x64-setup.exe`：Windows x64 安装程序，按当前用户安装。
- `*-win-x64-portable.zip`：完整解压后运行 `DLSS 5 MANAGER.exe`。
- 两种包都包含 .NET 8 运行环境，无需另装 .NET。
- `SHA256SUMS.txt`：下载文件的校验值。包内 `build-info.json` 记录准确源码提交。

打开游戏管理页，导入自己的 033 ZIP，分析适配并预览文件改动，确认后安装。**此发布只包含管理器；033 / DLSS 模型及原项目的 mod files 不随包分发。**

### 本版功能

- 运行包完整性与 PE 位数检查、多版本保存。
- 根据 PE、图形 API、原生 DLSS 和引擎标记给出适配理由。
- 导入包逐文件预览、冲突拦截、确认时重验。
- 游戏体检、规则导入、日志报告与备份校验恢复。

### 验证与边界

发布流程要求回归测试、Release 编译、发布文件检查、Windows 窗口启动，以及安装程序的安装和卸载检查全部通过。

真实游戏/GPU 效果仍待实机验证；换运行包版本需要先还原旧安装。可选多帧转接件暂不部署。完整范围见包内 `docs/runtime-packages.md`。

卸载管理器不自动删除游戏模组，也不删除已有备份与设置。安装程序未做商业代码签名。
