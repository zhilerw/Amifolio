# Third-party notices

本文件记录 Amifolio 0.1.3 构建所使用的第三方包与仓库内保留的研究材料。它不为 Amifolio 自有源码、图标或设计自动授予许可证；本项目自身的授权方案尚待所有者决定。

## NuGet components

以实际还原包的 `.nuspec` 和原包内许可证为依据，版本、作者、版权、许可证类型及原文 SHA-256 见 [dependencies.json](docs/third-party/dependencies.json)。该清单包含 22 个显式/传递依赖包；构建工具被列出并不代表工具本体随应用发行。

| 组件 | 版本 | 包内声明 |
| --- | --- | --- |
| Microsoft.Data.Sqlite / Microsoft.Data.Sqlite.Core | 10.0.14 | MIT；Microsoft Corporation，另见 [.NET 许可证](docs/third-party/Dotnet-LICENSE.txt) |
| SQLitePCLRaw.bundle_e_sqlite3 / core / lib.e_sqlite3 / provider.e_sqlite3 | 2.1.13 | Apache-2.0；Copyright 2014–2024 SourceGear, LLC；[许可原文](docs/third-party/SQLitePCLRaw-LICENSE.txt) |
| System.Numerics.Tensors | 9.0.0 | MIT；原包 LICENSE / 第三方声明已复制至下述目录 |
| Microsoft.Web.WebView2 | 1.0.3719.77 | Microsoft 包内软件许可及 NOTICE |
| Microsoft.Windows.AI.MachineLearning | 2.4.89 | Microsoft 包内软件许可及 ThirdPartyNotices |
| Microsoft.WindowsAppSDK 及其拆分包 | 2.6.5 | 各包实际附带的软件许可；不能把全部内容视作 Amifolio 的许可证覆盖范围 |
| Microsoft.Windows.SDK.BuildTools | 10.0.26100.7705 | 元数据中的 [Windows SDK 许可链接](https://aka.ms/WinSDKLicenseURL)，该包未附根目录许可文本 |
| Microsoft.Windows.SDK.BuildTools.MSIX | 1.7.251221100 | sdk_license.txt 及 NOTICE |

各包实际附带的 LICENSE、NOTICE 和 ThirdPartyNotices 均原字节保存在 [docs/third-party/packages](docs/third-party/packages/)，并一并放入运行 ZIP。表达式许可证的上游原文另存于该目录旁。Windows SDK/.NET 隐式工具链及系统运行时仍适用其安装许可；本文件不宣称穷尽了供应链中的全部许可证或替代原许可中的分发条件。

## Research snapshots

`docs/research/2026-11-05-github/` 中保留的 Files 构建配置和 Repomix 包配置/文件搜索源码是研究快照，不属于 Amifolio 原创代码。

- Files：Copyright (c) 2018 - present Files Community，MIT；[保留的许可证](docs/third-party/Files-LICENSE-MIT.txt)。
- Repomix：Copyright 2024 Kazuki Yamada，MIT；[保留的许可证](docs/third-party/Repomix-LICENSE.txt)。

原材料来源和内容哈希见源码仓库的 `docs/research/2026-11-05-github/source-evidence.json`（运行包不携带研究源码）。本次补充许可文件的来源、Git blob 和 SHA-256 见 [upstream-licenses.json](docs/third-party/upstream-licenses.json)。后者是本次取得的上游许可快照，不冒充研究时已固定的完整仓库提交。
