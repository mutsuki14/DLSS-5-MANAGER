DLSS 5 MANAGER — mutsuki14 fork preview
Original application: NODIX TECH / Numidia Studios
Fork: https://github.com/mutsuki14/DLSS-5-MANAGER

Windows x64 安装版与便携版均包含 .NET 8 运行环境。
安装版：运行 *-setup.exe，按当前用户安装。便携版：完整解压 ZIP，再运行 DLSS 5 MANAGER.exe。
请勿只复制 EXE：同目录的 DLL、原生组件和 languages 文件夹必须保留。

首次使用：添加游戏 → 管理 → 导入 ZIP，选择你的 033 运行包 → 分析适配 → 预览 → 确认安装。
本发布包只包含管理器，不包含 033 / DLSS 模型或原项目的 mod files。运行包需要另行提供并在程序中导入。

不同运行包版本独立保存。换版本时先在管理器中还原当前游戏安装，再选择新版本、预览并确认。
卸载管理器只移除程序文件，不会自动还原游戏，也不会删除 %LOCALAPPDATA%\DLSS5Manager 中的备份和设置。

这是 fork 预览版，保留原作者名称与版权信息。请阅读 Copyright.txt 及 docs 中的功能边界。
构建、文件回归与启动检查不代表真实游戏或 GPU 效果已经验收。
预览版更新可从上面的 fork Releases 页面获取；应用内自动检查仍只提示稳定版。

Both packages include the .NET 8 runtime. Extract the portable ZIP completely before launching.
Import your own 033 ZIP in the game's Manage window, inspect compatibility and file changes, then confirm.
Runtime payloads are not bundled. Uninstalling this manager does not remove game mods, backups or settings.
See build-info.json and payload-sha256.json for build provenance and file checksums.
