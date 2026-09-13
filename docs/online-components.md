# 在线组件与版本选择

管理器可从 GitHub Release 下载所选组件，并把它们组成可预览、可还原的安装方案。下载或生成预览不会修改游戏；点击“确认按预览安装”后才部署文件。

## 使用步骤

1. 添加游戏并进入管理页，确认选择的是真实游戏 EXE。使用 Feeder、RenoDX 或单独更新 REFramework / 模型时，先导入自己的 033 ZIP，选择适用路线。
2. 在“在线组件与版本”选择来源。默认勾选“下载时获取最新 Release”，下载时会重新查询该来源。需要固定版本时，刷新列表、取消该选项，再选 Release 和资源包。
3. 点击“下载所选版本并加入方案”。可依次加入多个组件；同类组件只保留一个版本。RenoDX 的 Krish 和 ShortFuse 属于同一类。
4. 点击“补全依赖并生成安装预览”。未手选的必需组件会自动下载。返回的列表和文件预览显示实际版本；需要更换自动补全的版本时，下载另一版本后重新生成预览。
5. 回到运行包区域查看文件改动、组件来源和阻止原因，确认后安装。更换已安装版本前，先还原当前安装。

Aurora 可直接生成完整方案，无需导入 033 包。当前接入的是 **x64、DX12、已有原生 DLSS 文件证据**的本地代理路线。RE 游戏还会补全 REFramework。兼容条件来自静态检查，仍需在实际游戏中验证。

## 发布来源

| 选项 | GitHub 来源 | 资源选择 |
| --- | --- | --- |
| OptiScaler Aurora | [abc354402600/OptiScaler-Aurora](https://github.com/abc354402600/OptiScaler-Aurora/releases) | Aurora ZIP / 7z；默认排除 nightly |
| REFramework 通用 Nightly | [praydog/REFramework-nightly](https://github.com/praydog/REFramework-nightly/releases) | `REFramework.zip`；选择此来源会开启预发布 |
| REFramework 正式版 | [praydog/REFramework](https://github.com/praydog/REFramework/releases) | 按 EXE 匹配游戏 ZIP；旧 TDB 变体需手选 |
| DLSS5 Feeder | [jlrouzies-fr/DLSS5-Feeder](https://github.com/jlrouzies-fr/DLSS5-Feeder/releases) | 同一 Release 的 add-on、着色器及 x64 辅助进程 |
| RenoDX DLSS5 Krish | [RankFTW/rhi-repo](https://github.com/RankFTW/rhi-repo/releases) | `renodx-dlss5-*` 标签下的 DLSS5 add-on |
| RenoDX DLSS ShortFuse | [RankFTW/rhi-repo](https://github.com/RankFTW/rhi-repo/releases) | `renodx-dlss-SF-*` 标签下的 DLSS add-on |
| DLSS NR / SR 模型 | [RankFTW/rhi-repo](https://github.com/RankFTW/rhi-repo/releases) | 分别匹配 `dlssnr-数字版本` / `dlss-数字版本` |

RHI 是这些构件的**分发仓库**，界面会标明来源。它不等同于 NVIDIA 官方下载渠道，也不是 [clshortfuse/renodx](https://github.com/clshortfuse/renodx) 主仓库。本实现不会把任意游戏 HDR add-on 当成 DLSS 神经渲染组件。

“最新”按发布时间排序，限定在所选来源、组件标签、资源类型及预发布选项内。即使 GitHub 没有勾选 prerelease，含 beta、alpha、nightly 等标记的标签也默认排除。每次查询最多读取最近 200 个 Release；更早版本暂不在列表中。REFramework 正式版必须与游戏匹配，无法识别 EXE 时可选择通用 Nightly。

## 组合与文件位置

- **Aurora**：将 `OptiScaler.dll` 作为 `dxgi.dll`，保留 `OptiScaler/` 运行库层级和 NR 转接 DLL。新配置仅将 `[DlssNr]` 的 `Enabled` 设为 `true`；已有配置按 seed 策略保留。NR 模型同时放在包内运行库位置与游戏 EXE 同目录，选择其他 NR 版本时同步替换两处。
- **Feeder + RenoDX**：使用 033 包提供的 ReShade 等基础文件，移除本次方案中被替代的 033 内核与转接件，放入所选消费端。x64 add-on 放在游戏目录；x86 游戏的 Feeder add-on 放在游戏目录，配套 x64 worker、RenoDX 和模型放在 `host64/`。缺少 worker 或位数不符会阻止生成方案。
- **自动依赖**：Feeder 缺少消费端时补全 Krish RenoDX；非 RE 的无原生 DLSS 路线选择 RenoDX 时补全 Feeder。在线 RE 专用路线缺少手选 REFramework 时补全通用 Nightly。已手选的组件版本保持不变。
- **RE 路线**：使用 RE 引擎数据包标记和专用路线检查。只提取所选 REFramework 的 `dinput8.dll`。原生 DLSS 的 RE 路线可替换 RenoDX；无原生 DLSS 的 RE 路线目前保留 033 内核，可更新 REFramework 和模型。通用 Feeder 不用于 RE 专用路线。
- **已有在线方案**：作为基底重新选择 Feeder/RenoDX 时，会替换原消费端文件和对应说明，避免保留两个消费端。Aurora 更新需要同时选择 Aurora 版本。

Aurora 与 Feeder/RenoDX 是不同路线，不能放进同一次安装。已有代理 DLL 冲突、未清理的其他神经渲染消费端、缺少组件或与 EXE 不匹配时，会显示具体阻止原因。用户配置、相同的既有加载器和用户脚本仍遵循原有保留与还原规则。

## 下载与版本记录

下载来自选定仓库的 HTTPS Release 资源；只跟随 GitHub 资源域名的重定向。下载检查声明大小和 GitHub 提供的 SHA-256。旧 Release 没有服务端校验值时，会计算本地 SHA-256 并在预览中注明“源未提供校验值”；本地哈希不能证明发布者身份。

缓存键包含来源、Release 和资源身份。每次复用都检查文件大小与哈希；损坏缓存可重新下载。中断、取消或校验失败不会激活未完成下载。支持 ZIP 与 7z，拒绝路径越界、符号链接、重复文件名和超过限制的压缩包。

组成的不可变清单记录基底包、仓库、tag、asset ID、资源名称和完整 SHA-256。安装前重新核对文件与预览，安装后可查看锁定版本。上游更新 Release 不会静默改变已安装游戏；“最新”在用户再次下载时解析。

本接入仅复制经过适配的组件文件；不运行上游安装脚本，也不执行全局驱动修改、Aurora runtime sync、Vulkan Layer 注册或其他系统安装操作。组件许可证和说明随派生运行包保留。各组件的许可仍适用。

## 验证范围

使用 .NET 8 SDK：

```sh
dotnet build "DLSS 5 MANAGER.fsproj" --configuration Debug
dotnet fsi --exec tests/ComponentTests.fsx
```

53 项回归测试覆盖原有安装/还原保护，以及发布目录过滤、下载和缓存校验、取消与截断、压缩包路径、RE 资源选择、Feeder x86/x64 配对、Aurora 配置和模型覆盖、消费端冲突与版本替换。Windows 发布流程还验证完整安装包的启动、安装和卸载。

构建与文件测试不代表游戏/GPU 效果已验收。Vulkan、Aurora 的非原生 DLSS 路线、游戏专用 HDR add-on 自动匹配，以及系统级组件安装不在此版本范围内。
