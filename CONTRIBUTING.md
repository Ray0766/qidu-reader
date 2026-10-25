# 参与开发

欢迎提交问题和改进。较大的交互或数据格式变动，请先开 Issue 说明场景和方案；提交的代码沿用项目 MIT 许可证。

## 环境

- Windows 11 x64；界面使用 WPF，不支持在 Linux 或 macOS 运行。
- PowerShell 7 和 .NET SDK 10.0.402（见 `global.json`）。普通用户使用发布包无需安装这些工具。
- 安装包需要 Inno Setup 7；MSIX 需要 Windows SDK 中的 MakeAppx 与 MakePri。日常开发不要求安装打包工具。

在仓库根目录运行：

```powershell
.\scripts\build.ps1
.\scripts\verify.ps1
.\scripts\build.ps1 -Publish
# 用最终发布程序验收；测试数据始终写入独立目录。
.\scripts\verify.ps1 -Executable .\dist\TxtReader-0.1.9-win-x64\TxtReader.exe -ReportDirectory .\artifacts\release-check
.\scripts\package.ps1 -ValidationReports artifacts/release-check/report.json
.\scripts\build-installer.ps1 -Compiler 'C:\Program Files\Inno Setup 7\ISCC.exe'
```

`build.ps1` 使用锁定的 NuGet 依赖；第一次构建需联网下载 SDK 之外的依赖。已有的发布目录、安装器和 ZIP 不会被脚本覆盖，重新验收请用新目录或先自行归档未发布候选。版本号以应用项目文件为准。

## 变更与验证

- 原文字符锚点是阅读进度依据，页码只用于当前布局。重排、拖动回弹、搜索往返、章节与书签跳转不得丢失原文位置。
- 修改书库或存储流程时，验证关闭重开、旧库迁移、已有新库优先、失败时保留源文件；不要对真实书库运行测试。
- 改动分页或动效时，补验单页／双页／滚动、100%／200% 缩放、拖动中取消与窗口失焦。自动回归不能替代必要的实际界面检查。
- 不提交私人小说、书库、测试日志、签名密钥或商店账号文件。提供问题样例时优先构造几行可公开的文本。

GitHub Actions 会在 Windows 上构建并运行独立回归，仅保存自有示例产生的报告。结果以仓库 [Actions 运行记录](https://github.com/Ray0766/qidu-reader/actions) 为准。

## 项目结构

- `src/TxtReader.Core`：文本解码、章节识别、分段和搜索。
- `src/TxtReader.App`：WPF 阅读界面、书架、纸页动效和 SQLite 存储。
- `samples`、`assets`：自有排版示例与原创图标。
- `scripts`、`installer`：构建、验证、安装包和商店包装。
- `third-party`：随程序分发的第三方许可。
