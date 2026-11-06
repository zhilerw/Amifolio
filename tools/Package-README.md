# Amifolio 0.1.3

双击 `desktop/Amifolio.exe` 启动。本机数据保存在 `%LOCALAPPDATA%\Amifolio`；关闭窗口即退出，不安装常驻服务。请保留整个运行目录，不要只移动 exe。

第一版支持文件入库、版本与分类检索、项目、现代 Office/文本片段、源码快照、可编辑上下文、AGENTS.md 差异预览/备份，以及本机 Agent 桥接。语义分析需要自行配置模型服务；外发默认关闭，密钥仅限当前会话。没有演示数据混入正式作品库。

Agent 接入材料和技能安装说明见 [codex/README.md](codex/README.md)。首次启动不会更改用户 Codex 配置或 AGENTS.md。

这是框架依赖的 Windows x64 开发预览版，未制作签名安装包。其他机器需安装匹配的 .NET Desktop Runtime 10 和 Windows App Runtime；尚未完成跨机器部署验收。PDF/OCR、无人值守学习、完整任务恢复与性能预算验收仍待后续开发。

本次轻量动效见 0.1.3 说明，导航与编辑见 0.1.2 说明，功能范围和限制见 首版交付说明。

第三方包及随附许可见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。运行 ZIP 不分发 PDB 调试符号；正式程序及 WinUI 资源保留完整。

2026-11-06 发布前复核见 测试报告。本文件会复制到运行包根目录，链接相对于解压后的根目录。ZIP 内的 `PACKAGE-MANIFEST.json` 记录逐文件 SHA-256，旁置 `.sha256.txt` 校验整个 ZIP；源码另行提供 `Amifolio-0.1.3-source.zip`。两种 ZIP 都不含用户作品库或本机启动快捷方式。
