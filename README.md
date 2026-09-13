<div align="center">

# DLSS 5 MANAGER

### The first intelligent manager and one-click mod installer for DLSS 5, ReShade, and Streamline.

<br/>

[![Download Fork Preview](https://img.shields.io/badge/DOWNLOAD-FORK%20PREVIEW-00e676?style=for-the-badge&logo=github&logoColor=white)](https://github.com/mutsuki14/DLSS-5-MANAGER/releases)

<br/>

![Platform](https://img.shields.io/badge/Platform-Windows%20x64-grey?style=flat-square)
![Framework](https://img.shields.io/badge/Framework-.NET%208%20|%20Avalonia%20UI-blueviolet?style=flat-square)
![Language](https://img.shields.io/badge/Language-F%23-378BBA?style=flat-square)
![License](https://img.shields.io/badge/License-NODIX%20TECH-lightgrey?style=flat-square)

</div>

---

### 📁 Storage & Cache Locations

All cached metadata, custom layouts, and configuration backups are safely stored locally at:

```cmd
%LOCALAPPDATA%\DLSS5Manager\
```

### Development: health checks and protected restore

The development branch adds a **Game health check** panel to Manage:

- Inspect PE architecture and normal/delay-loaded graphics imports before choosing a route.
- Import `managed/game_rules.json` from an extracted 033 package for advisory game-specific API, mount and anti-cheat notes.
- Inspect bounded runtime log tails and export a local text report.
- Verify SHA-256 ownership and original backups before restore. Keep changed settings/logs; stop on changed binaries or damaged backups.
- Restore files touched by a failed install attempt, including failed repairs and known files written by ReShade setup.

中文说明与融合范围：[游戏体检与安全恢复](docs/health-check-and-restore.md)。

Build and run the regression suite with the .NET 8 SDK:

```sh
dotnet build "DLSS 5 MANAGER.fsproj" --configuration Debug
dotnet fsi --exec tests/ComponentTests.fsx
```

The checkout does not include the `mod files` runtime payload. Building the manager does not download or manufacture those binaries. Existing upstream authorship and copyright remain unchanged.

### Imported runtime packages (fork development)

Import a schema 1 033 ZIP from the game management window, inspect compatibility evidence, review every planned file change, then confirm deployment. Package versions are stored separately; restore the current installation before selecting another version. See [运行包、适配建议与安装预览](docs/runtime-packages.md) for scope, limitations and validation.

### Game-specific components

The fork removes the community tab, report/comment composer and community network service. RE Engine games now show their REFramework requirement in Manage. The imported package preview checks the dedicated RE route, shows required component files and conflicts, and restores only files the manager actually deployed. Existing matching loaders and user scripts remain intact. See the [component guide](docs/runtime-packages.md#游戏专用组件reframework).

### Fork preview downloads

Windows x64 setup and self-contained portable ZIP: [mutsuki14 fork releases](https://github.com/mutsuki14/DLSS-5-MANAGER/releases). The original application's authorship remains NODIX TECH / Numidia Studios. These packages contain the manager; import your own 033 ZIP or select an online Aurora release after installation.

### Online component versions

Manage now offers a source, release and asset selector for **OptiScaler Aurora, REFramework, DLSS5 Feeder, RenoDX DLSS5 (Krish / ShortFuse), and DLSS NR / SR models**. Choose the latest appropriate release at download time, or pin a listed version. Downloaded bytes are verified and cached; the dependency preview records repository, tag, asset identity and SHA-256 before applying the plan with backup and restore.

Aurora uses [abc354402600/OptiScaler-Aurora](https://github.com/abc354402600/OptiScaler-Aurora) and can form a standalone package for native-DLSS x64 DX12 games. Other combinations build on an imported 033 package. Required Feeder/RenoDX and REFramework dependencies are downloaded into the same reviewable plan. These are experimental game integrations; application tests do not establish GPU or game compatibility. See [在线组件与版本选择](docs/online-components.md) for sources, route restrictions and usage.

The release workflow builds from the development branch when `release/release.json` changes, tests the app and installer on Windows, verifies uploaded assets, then publishes a prerelease in this fork. It never merges `main`. For a local Windows build with .NET 8 and Inno Setup 6, run `pwsh ./release/build-windows.ps1` from a fresh checkout.

If packaging passes but publishing fails, `release/verified-build.json` can identify the original release workflow run and commit. Updating that file runs the publication-only workflow: it checks the successful Windows build job, downloads its immutable artifact, verifies provenance and checksums, and publishes those exact bytes without rebuilding. Existing published releases are not overwritten.
