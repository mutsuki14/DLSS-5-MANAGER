DLSS 5 MANAGER — mutsuki14 fork preview
Original application: NODIX TECH / Numidia Studios
Fork: https://github.com/mutsuki14/DLSS-5-MANAGER

Windows x64 安装版与便携版均包含 .NET 8 运行环境。
安装版：运行 *-setup.exe，按当前用户安装。便携版：完整解压 ZIP，再运行 DLSS 5 MANAGER.exe。
请勿只复制 EXE：同目录的 DLL、原生组件和 languages 文件夹必须保留。

首次使用：添加游戏 → 管理 → 在线组件与版本 → 下载加入方案 → 补全依赖并预览 → 确认安装。
Aurora 可独立生成适用方案；Feeder/RenoDX、REFramework 或模型更新需先导入自己的 033 ZIP 并选择路线。
默认下载最新适用 Release；固定版本请刷新列表，取消“下载时获取最新 Release”后选择。
本发布包只包含管理器，不预装 033 / DLSS 模型或原项目的 mod files。在线组件在使用时下载，来源与版本见安装预览。

不同运行包版本独立保存。换版本时先在管理器中还原当前游戏安装，再选择新版本、预览并确认。
卸载管理器只移除程序文件，不会自动还原游戏，也不会删除 %LOCALAPPDATA%\DLSS5Manager 中的备份和设置。

这是 fork 预览版，保留原作者名称与版权信息。请阅读 Copyright.txt 及 docs 中的功能边界。
构建、文件回归与启动检查不代表真实游戏或 GPU 效果已经验收。
预览版更新可从上面的 fork Releases 页面获取；应用内自动检查仍只提示稳定版。

Both packages include the .NET 8 runtime. Extract the portable ZIP completely before launching.
Select component releases in Manage, download, resolve dependencies and review the file plan before confirming.
Aurora supports a standalone package; other combinations use your imported 033 base. See docs/online-components.md.
Runtime payloads are not bundled. Uninstalling this manager does not remove game mods, backups or settings.
See build-info.json and payload-sha256.json for build provenance and file checksums.
