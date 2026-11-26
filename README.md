# Amifolio

基于 C# + WinUI 3 的 Windows 桌面应用，配有 Codex 插件，可查看上传件和交付物、编辑创作偏好、管理作品范例与本机指令。

当前版本为 **1.1.1 预发行版**。Windows 11 x64 运行包和源码包见 [GitHub Releases](https://github.com/zhilerw/Amifolio/releases)。安装、升级和回退见[运行说明](docs/RELEASE.md)。

## Amifilo可以做什么

- 按项目查看已登记的 Codex 上传件与交付物，浏览预览、打开原件和核对会话来源。
- 在风格样张上调整全局或项目偏好，把有参考价值的作品设为范例。
- 在「指令」中查看与编辑全局或项目 AGENTS.md，保存前核对差异，并可撤销受管变更。
- 手动浏览、扫描和解析已发现的项目目录。从「设置」可进入项目文件浏览、扫描和手动解析。文本 PDF、现代 Office、文本和代码支持有界提取与来源定位。

## 1.1.1 更新

- 重做风格样张的视觉和编辑体验。可以直接选中页面上的元素，调节字体、颜色及间距，预览与已保存的创作偏好相连。
- 作品卡优先展示缩略图或内容预览，并保留打开原件与核对来源的入口。

更新记录 [1.1.0](docs/releases/1.1.0.md) · [1.1.1](docs/releases/1.1.1.md)。

## 安装与构建

运行包需要 Windows 11 x64、.NET Desktop Runtime 10、Windows App Runtime 2.6.5、Visual C++ x64 运行库和 Edge WebView2 Runtime。完整解压后运行 `desktop/Amifolio.exe`。Codex 的接入、升级及移除参见[插件说明](plugins/codex/README.md)。

从源码构建需要 Windows x64、PowerShell 7.2、Git、Windows SDK 工具链，以及 [global.json](global.json) 指定的 .NET SDK。

```powershell
.	ools\Build.ps1
.	ools\Test-Native.ps1
```

构建脚本锁定还原依赖、构建解决方案、运行四组测试并发布到 `artifacts/Amifolio`。依赖和授权见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

## 未来与展望

继续验收真实 Codex 桌面中的文档收集、实时偏好注入与空闲计时，并验证独立环境上的升级与回退。
