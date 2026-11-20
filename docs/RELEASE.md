# 安装、升级与排查

- 运行包适用 Windows 11 x64，需要 .NET Desktop Runtime 10 和匹配的 Windows App Runtime。解压整个 ZIP，保留 `desktop`、`bridge`、`worker` 和 `codex`，双击 `desktop/Amifolio.exe`。免安装包未签名。
- 用 `Get-FileHash -Algorithm SHA256 <ZIP 路径>` 核对旁置 `.sha256.txt`。`PACKAGE-MANIFEST.json` 记录包内文件摘要。源码包用于构建，第三方许可见 `THIRD_PARTY_NOTICES.md`。
- 升级前关闭旧窗口，保留旧运行目录；先按[数据恢复](STORAGE-RECOVERY.md)备份应用数据。项目原件需由用户另行备份。旧版未验证可直接读取新版数据库，回退时使用升级前的备份。
- 升级后在“环境配置”检查并更新 Codex 的 MCP、skills 和受管指令，再手动重启 Codex。结束钩子需在 Codex 内信任。移除接入见[扫描与接入](COLLECTION.md)；先撤销共享或远程发送权限，再移除受管配置。
- 若程序打不开，先确认系统架构和运行时，再用完整 ZIP 解压到新目录。若看不到项目，检查本机 Codex 环境并重新扫描；若解析失败，检查原件存在且可读，再到“设置 → 日志”查看状态。
- “风格与记忆”可按作用域查看、编辑、删除或停用画像。独立控制学习、共享与远程发送，默认不授权。移除程序文件不会删除 `%LOCALAPPDATA%\Amifolio` 中的应用数据；需要保留资料时先备份。

适用范围为 Windows 11 x64 和本机 Windows Codex。未验证 ARM64、其他 Agent 或无匹配运行时的环境；华文宋体未安装时使用系统字体回退。
