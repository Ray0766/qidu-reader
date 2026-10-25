# 0.2.0 跨平台与 EPUB 预览

本页描述开发分支。已发布的 0.1.9 下载链接仍保持原样，不能用它测试新增 EPUB 或 Mac 功能。

| 构建 | 目标 | 当前验证边界 |
| --- | --- | --- |
| Windows 现代版 | .NET 10 / WPF，Windows 11 x64 | 本机完整回归 1,190 项通过 |
| Windows 兼容版 | Framework 4.8 / WPF，Windows 7 SP1、8.1、10、11，x64/x86 | 本机 Windows 11 + Framework 4.8.1：x64 1,231 项、x86 1,190 项通过；尚无 Win7/8.1 设备验收 |
| Mac 预览 | .NET 10 / Avalonia，macOS 15+、苹果 M 系列 | Windows 共用界面与 macOS 15.7.9 arm64 原生环境均通过 52 项阅读和 3 项新进程检查；用户 Mac 的 Finder、文件选择器、触控板仍待试用 |

没有开发 Android 版，也不提供 Linux 或 Intel Mac 包。

## EPUB 范围

支持无 DRM、可重排的 EPUB 2/3：OPF 阅读顺序、NCX / nav 目录、章节锚点、封面、JPEG / PNG / GIF / BMP 插图、简单 ruby 注音。采用栖读自己的排版，不运行书内脚本、不访问远程资源、不解压到任意路径。

搜索、书签、模式切换和字体重排均使用正文字符位置；插图也有独立位置。EPUB 标识含文本和图片内容，TXT 的原有内容标识保持不变。Windows 本地另用用户提供的 EPUB 2 完成了目录、17 幅图和模式切换验证；该文件及截图不提交、不分发。

复杂 CSS、内嵌字体、音视频、交互内容及 SVG 矢量图不保证还原。固定版式和受加密保护的正文明确拒绝。压缩包大小、条目数量、XML、图片和解压后的总内容都有上限。

## Windows 安装和数据

兼容安装器使用独立的“栖读 · 兼容预览”目录及卸载项，按系统选择 x64/x86。系统缺少 Framework 4.8 时显示微软原始运行库安装界面，由用户接受其许可及管理员授权；不会自动重启。

Framework、.NET 10 共用用户的 `QiduLibrary`。测试从 0.1.9 的自有样例库迁移，在 Framework x64、x86、现代版、再次打开四个阶段核对 2 本书、2 个书签、字符位置 3500/2599、设置和书籍字节，全部保留。独立安装验证包也通过安装、覆盖安装、卸载和重装，书库保留。

当前主机已有 Framework 4.8.1，缺少运行库时的真实安装路径未在旧系统执行。新构建的原生失焦自动检查也受到 Windows 前台激活限制，尚未形成完整的新一轮窗口中断验收；合成拖页取消和进程重开检查已通过。保留此限制，不把失败的测试前置条件报告为通过。

0.1.9 不能读取 EPUB。新增书籍后回到旧版，TXT 书架、书签和进度仍在，但 EPUB 应使用预览版打开。测试预览请先正常退出阅读器并备份整个书库。

## Mac 安装和数据

使用自带运行环境的 `.app`，解压 ZIP 后拖入“应用程序”。ZIP 保留执行权限，用户无需 SDK、命令行或单独的 .NET 安装。Mac 书库在 `~/Library/Application Support/Qidu`，退出程序后替换 app 可升级，移走 app 不删除书库。Mac 与 Windows 没有自动同步，跨系统书库迁移界面尚未实现。

预览尚无 Apple Developer ID 签名和公证。Mac 上打包时可验证本地开发签名，但它不能消除互联网下载的系统安全提示。正式面向普通用户分发前需要完成 Apple 签名、公证以及 Finder / 触控板的设备验收；不会关闭系统保护来掩盖发布问题。

Mac 当前覆盖书架、文本和图片排版、单/双页、滚动、目录跟随、200 条一批的搜索、书签、字号/字体/主题/宽度、点击/按键/拖动翻页和位置保留。曲面动效使用新的跨平台实现，和 WPF 视觉表现有差异。Windows 的手动编码重解释、搜索返回原位等细节尚未全部移植。

## 依据

- [.NET Framework 系统要求](https://learn.microsoft.com/en-us/dotnet/framework/get-started/system-requirements)
- [.NET 10 支持的操作系统](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)
- [Avalonia Mac 打包说明](https://docs.avaloniaui.net/docs/deployment/macos)
- [Apple Developer ID 与公证](https://developer.apple.com/developer-id/)

这些资料说明技术支持范围，不替代本项目在对应设备上的验证。
