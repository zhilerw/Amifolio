# 安装、升级与排查

1. 使用 Windows 11 x64，安装 .NET Desktop Runtime 10、Windows App Runtime 2.6.5、Visual C++ x64 运行库和 Edge WebView2 Runtime。
2. 用 `Get-FileHash -Algorithm SHA256 <ZIP 路径>` 核对下载页的 `.sha256.txt`。包内 `PACKAGE-MANIFEST.json` 记录逐文件摘要。
3. 完整解压运行包到新目录，保留 `desktop`、`bridge`、`worker` 和 `codex`，然后运行 `desktop/Amifolio.exe`。运行包未签名。
4. 升级前关闭窗口并按[数据恢复](STORAGE-RECOVERY.md)备份应用数据。回退时用旧运行目录和升级前的备份。项目原件需单独备份。
5. 要启用 Codex 接入，添加随包提供的本地插件市场，安装 Amifolio 插件并按 Codex 提示审核、信任钩子。[插件接入与移除](https://github.com/zhilerw/Amifolio/blob/v1.1.0/plugins/codex/README.md)。

如果应用无法启动，先核对系统架构和运行时，并在新目录完整解压。若作品未出现，检查宿主与项目授权，并使用「更新作品」。默认应用数据位于 `%LOCALAPPDATA%\Amifolio`。
