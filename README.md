# Amifolio

Windows 本地作品集 · 0.1.3 开发预览版。基于C# + WinUI 3。

源码仓库：[zhilerw/Amifolio](https://github.com/zhilerw/Amifolio)（当前为私有仓库）。

本次轻量动效见 0.1.3 更新说明；导航、编辑与弹窗调整见 0.1.2 更新说明。



2026-11-06 发布前复核见 测试报告，源码和运行包上传方式见 GitHub 上传说明。

## 立即运行

从 [v0.1.3 Release](https://github.com/zhilerw/Amifolio/releases/tag/v0.1.3) 下载 `Amifolio-0.1.3-win-x64.zip`，解压整个目录并双击 `desktop/Amifolio.exe`。源码 ZIP 用于开发，不能直接运行。从 GitHub 克隆或下载源码时，需要先按照下文完成构建，生成 `artifacts/Amifolio/desktop/Amifolio.exe`，再运行：

```powershell
.\tools\Start-Amifolio.ps1
```

默认数据目录是 `%LOCALAPPDATA%\Amifolio`。这是独立于源文件的本机作品库，关闭窗口即退出，无后台常驻服务。也可以使用 `tools/Start-Amifolio.ps1 -DataRoot 'E:\AmifolioData'` 指定另一个绝对路径；连接 Agent 时必须使用同一目录。

这是框架依赖的运行目录，不是签名安装包；其他电脑需要 x64 .NET Desktop Runtime 10 和相应 Windows App Runtime。当前只在本机 Windows x64 验证，不宣称所有 Windows/ARM64 环境都可用。

本机根目录的启动快捷方式仅为便利入口，不纳入源码仓库。

## Amifilo可以做什么

- **作品集**：选择文件、保存独立副本、提取受支持格式的正文片段，按名称/正文/标签检索，按类型/来源/项目筛选；修改名称、标签和项目。重复提交不会重复制造相同作品，内容变更保留版本。
- **代码库**：保存源码 ZIP 快照，排除常见依赖、构建目录和密钥；不执行代码，不保存 Git 历史。导入会显示排除提示。
- **项目**：创建项目并可绑定本机目录，归档记录与项目上下文分开组织。
- **风格与记忆**：全局/单项目分别查看、编辑、删除、关闭概要/记忆/风格/总结，发布 Markdown 入口与来源定位。全局只使用没有归属项目的作品，应用自产物不参与再次学习。
- **AGENTS.md**：快速访问全局/项目文件、编辑托管入口、预览差异、备份保存和撤销。检测到外部修改时停止覆盖。不会在首次启动时自动改动宿主文件。
- **模型候选**：在“AI 配置”填写兼容 Chat Completions 的服务、模型和当前会话密钥，明确允许外发后按需生成候选内容；正式人工文档不被覆盖。未配置模型时没有虚构的用户记忆。
- **本机 Agent 接入**：提供实际 CLI、stdio MCP 和配套 skill。连接材料在运行目录的 `codex` 文件夹；没有自动安装到用户 Codex。

删除作品记录与删除上下文是不同操作。人工上下文和归档副本需要分别管理；从一个位置删除不会自动追回其他程序已经读取、导出或复制的内容。

## 建议第一次操作

1. 导入一份无敏感内容的文档，关闭重开，确认作品与副本仍存在。
2. 新建项目并绑定目录，再导入属于它的文件。
3. 在“风格与记忆”选择全局或项目，编辑内容、开启该作用域并保存发布。
4. 在“AGENTS.md”读取对应文件，加入入口，预览实际差异后决定保存。后续在新的 Codex 会话验证真实加载；AGENTS 指导阅读顺序，不能强制模型或提供文件隔离。
5. 需要模型分析时才配置服务并开启外发。密钥只保留在当前应用会话，退出即丢弃。

## Agent 桥接

在本机已生成的运行目录中：

```powershell
.\artifacts\Amifolio\bridge\Amifolio.Bridge.exe projects
.\artifacts\Amifolio\bridge\Amifolio.Bridge.exe submit 'E:\Outputs\report.docx' --source Codex
.\artifacts\Amifolio\bridge\Amifolio.Bridge.exe list --search report
.\artifacts\Amifolio\bridge\Amifolio.Bridge.exe status '<返回的作品 ID>'
```

为项目提交时添加 `--project '<真实项目 ID>'`。桥接程序默认与桌面程序使用相同的数据目录；自定义目录需显式传 `--data-root`。`mcp` 命令启动 MCP stdio 服务；实际连接配置与技能安装方式见 [Codex 接入说明](plugins/codex/README.md)。已发布运行目录中对应说明位于 `codex/README.md`。

一个本机 MCP 连接具有本机整个作品库的元数据权限，不提供每个 Agent 的独立访问隔离。上下文读取仍严格按全局/项目及启停设置返回；不提供任意文件原文打开工具。skill 是协作约定，不能保证捕获全部 Agent 产物。

## 构建与验证

开发需要 Windows x64、PowerShell 7、Git、[global.json](global.json) 指定的 .NET SDK 10.0.403（或允许的稳定补丁），以及 Windows SDK 10.0.26100 工具链。运行原生界面还需 Windows App Runtime；本机验证版本见 环境报告。测试中的目录联接需要 NTFS。首次还原需能访问 NuGet。

在项目根目录用 PowerShell 7 运行：

```powershell
.\tools\Build.ps1
# 只有当前网络访问 NuGet 失败、且现有本机代理仍运行时才用：
.\tools\Build.ps1 -UseExistingLocalProxy
```

脚本对整个解决方案执行锁定依赖还原、Release 构建、四组可执行测试和发布，输出在 `artifacts/Amifolio`。这些测试是控制台程序，**不能用一次 `dotnet test` 的退出码代替运行它们**。测试数据在隔离临时目录，不修改实际用户 AGENTS.md、不调用真实收费模型。模型 HTTP 适配通过本地响应处理器及回环 HTTP 验证，实际服务兼容性须配置后检查。

在可交互的 Windows 桌面验证实际发布程序：

```powershell
.\tools\Test-Native.ps1
# 使用上一命令输出目录中的 summary.json；路径须替换为实际值：
.\tools\Package.ps1 -NativeSummary '.local/native-tests/<本次目录>/summary.json'
```

原生测试使用独立空库，检查空库、编辑、动效及七页布局，并输出实际 XAML 截图。打包会核对已测 DLL 的哈希、逐文件回读 ZIP 校验，输出源码包、Windows x64 运行包与 SHA-256 文件。源码包不含 `.git`、本机缓存、快捷方式或用户数据库；运行包需要完整保留，不能只拷贝 exe。重新构建之后须重新运行原生测试。

[GitHub Actions](.github/workflows/build.yml) 已配置 Windows 构建、四组测试和运行目录归档；远程首次执行结果以实际 Actions 运行为准，原生窗口回归仍在本机交互桌面执行。

## 未来与展望

0.1 是首个真实可用增量，不是完整产品验收：无人值守自动生成、完整 Office/PDF 格式与精确定位覆盖、解析 worker 进程隔离、跨重启任务队列、完整来源撤回/遗忘策略、真实 Codex 新会话全链路验证、正式 MSIX 签名/安装升级及万条数据性能预算仍需后续开发。OCR、离线模型、云同步、Claude 适配属于独立可选增强。

原始范围与优先级见 产品设计、首版执行计划。第一版实际验证结果见 0.1 交付说明。

## 参与与授权

贡献方式见 [CONTRIBUTING.md](CONTRIBUTING.md)，安全问题见 [SECURITY.md](SECURITY.md)，依赖和研究快照的许可见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。Amifolio 自身尚未选定开源 LICENSE，仓库公开范围仍待所有者确认。
