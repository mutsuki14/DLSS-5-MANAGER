<div align="center">

# DLSS 5 MANAGER

### The first intelligent manager and one-click mod installer for DLSS 5, ReShade, and Streamline.

<br/>

[![Download Latest Release](https://img.shields.io/badge/DOWNLOAD-LATEST%20RELEASE%20(V1.2.5)-00e676?style=for-the-badge&logo=github&logoColor=white)](https://github.com/NODIX-TECH/DLSS-5-MANAGER/releases)

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
dotnet fsi --exec tests/RegressionTests.fsx
```

The checkout does not include the `mod files` runtime payload. Building the manager does not download or manufacture those binaries. Existing upstream authorship and copyright remain unchanged.
