这是 **mutsuki14 fork 的在线组件预览版**，从 `codex/health-check-safe-restore` 开发分支构建。没有合并 main。原应用作者为 NODIX TECH / Numidia Studios。

### 下载与使用

- `*-win-x64-setup.exe`：Windows x64 安装程序，按当前用户安装。
- `*-win-x64-portable.zip`：完整解压后运行 `DLSS 5 MANAGER.exe`。
- 两种包均包含 .NET 8；`SHA256SUMS.txt` 与包内 `build-info.json` 提供校验值和源码提交。

在游戏管理页选择在线组件，使用“下载时获取最新 Release”，或刷新列表后选择固定版本。下载加入方案 → 补全依赖并生成预览 → 确认安装。

### 本版变更

- 接入指定的 **OptiScaler Aurora**，支持 ZIP / 7z；可独立生成 x64 DX12 原生 DLSS 游戏的安装方案。
- 可选择 **REFramework 正式版 / Nightly、DLSS5 Feeder、RenoDX Krish / ShortFuse，以及 DLSS NR / SR 模型**版本。RenoDX 和模型的 RHI 分发来源在界面中明确显示。
- 自动补全适用的 Feeder/RenoDX 与 REFramework 依赖，保留用户指定的版本。旧 REFramework 的游戏 / TDB 资源可手选。
- 下载大小、SHA-256 与缓存完整性检查；支持取消和损坏缓存重试。预览与已安装信息记录实际仓库、tag、asset 和哈希。
- Feeder x86 add-on 与同包 x64 worker 成对安装。Aurora 保留运行库目录并启用新配置中的 NR 开关。
- 多组件共用文件预览、确认时重验、备份和还原。冲突消费端或不适用组合会阻止安装。
- 延续删除社区功能、RE 专用组件检查、033 包导入和游戏体检。

### 验证与边界

53 项回归测试覆盖安装/还原、Release 与缓存、压缩包和新组件组合。发布流程要求 Windows Release 编译、窗口启动及安装程序的安装/卸载检查通过。

**发布包仅包含管理器。** Aurora 在使用时下载；其他在线组合需要用户导入的 033 基底包。换已安装版本需要先还原。RE 无原生 DLSS 路线暂保留 033 内核，可更新 REFramework / 模型，不支持替换 RenoDX。

实际游戏与 GPU 效果仍需实机验证。上游脚本、全局驱动设置、Vulkan 注册与游戏 HDR add-on 自动匹配不在接入范围。详见包内 `docs/online-components.md`。

卸载管理器保留游戏模组、已有备份与设置。安装程序未做商业代码签名。
