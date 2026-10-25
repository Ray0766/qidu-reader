# Windows 安装与 GitHub 发布方式

2026-10-25。正式名称为“栖读”；本地 EXE、便携目录和商店候选均为 0.1.9。项目采用 MIT 开源许可，GitHub 仓库为 [Ray0766/qidu-reader](https://github.com/Ray0766/qidu-reader)，商店申请已提交认证。

## 产品是什么

独立运行的 Windows 原生桌面程序，面向本地中文 TXT 小说。C# / .NET 10 / WPF 提供阅读和书架界面，SQLite 保存本机数据。支持单页、双页和滚动阅读。应用不依赖网页服务，不联网获取小说，也没有账号或云同步。

现在的安装包目标为 Windows 11 x64。安装器阻止 32 位 Windows 和 ARM64；其他平台没有验证，不在本次发布范围。

## 普通用户的使用步骤

正式分发已选择免费 Store MSIX：GitHub 首页提供商店下载入口，用户点击获取并安装，然后直接打开阅读器。安装／更新需要联网，读本地小说可以离线。商店负责审核后的签名与更新。[Store Web Installer](https://learn.microsoft.com/en-us/windows/apps/distribute-through-store/how-to-use-store-web-installer-for-distribution)

以下保留当前本地 EXE 试用渠道的步骤，尚未签名：

1. 从 [0.1.9 Release](https://github.com/Ray0766/qidu-reader/releases/tag/v0.1.9) 下载 `TXT-Reader-0.1.9-Setup-x64.exe`。
2. 双击安装包，按中文向导安装；可选择桌面快捷方式。
3. 打开栖读，点击“导入 TXT”或把小说文件拖入窗口，开始阅读。

安装包内已包含 .NET 运行环境，无需安装 SDK、Git、Visual Studio，无需下载源码或执行命令。首次安装完全使用本地载荷，不需要安装器联网下载依赖。[Microsoft 自包含发布说明](https://learn.microsoft.com/en-us/dotnet/core/deploying/)

GitHub 仓库保存源码；Releases 可以另附二进制文件。正式发布时，首页以商店入口为主；若另提供直装 EXE／便携 ZIP，应明确独立的签名和支持状态。GitHub 自动生成的 Source code ZIP 是源码归档。[GitHub Releases 文档](https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases)

## 0.1.9 EXE 安装行为

- 使用 Inno Setup 7；默认安装到 `%LOCALAPPDATA%\Programs\TXT Reader`，仅当前用户，无需 UAC 管理员授权。[PrivilegesRequired 文档](https://jrsoftware.org/ishelp/topic_setup_privilegesrequired.htm)
- 创建开始菜单快捷方式和 Windows 已安装应用中的卸载入口；桌面图标为可选项。
- 安装包固定 AppId，后续同产品安装器覆盖更新安装目录；当前更新方式是下载新版安装包，尚无自动更新。
- 安装器检查正在运行的阅读器，提示先关闭再继续，不强制终止保存中的进程。[AppMutex 文档](https://jrsoftware.org/ishelp/topic_setup_appmutex.htm)
- 数据目录为 `%USERPROFILE%\QiduLibrary`；首次启动复制并校验旧 `%LOCALAPPDATA%\TxtReaderPreview` 书库，保留原目录。已有新库时以新库为准。
- 升级、卸载均不删除该数据目录。重新安装后可继续阅读。卸载时保留数据属于当前产品约定。
- 发布内容只包含应用、自带运行时、第三方许可和自有示例；私人小说、数据库、测试回执均排除。

## 发布渠道与签名

正式产品名称为“栖读”，Microsoft Store 产品 ID 为 9PJM5L7JG8D1。GitHub 仓库由 Ray0766 维护，采用 MIT 许可。

当前自制 EXE 尚未做 Authenticode 代码签名。通过浏览器从互联网下载后，Windows 可能提示未知发布者或 SmartScreen 信誉不足。面向公众追求顺畅的安装体验，应配置可信代码签名，或走 Microsoft Store 的 MSIX 发布审核；新签名也不能保证立刻免除所有信誉提示。未购买证书；开发者账号已由用户注册，0.1.9 已提交商店认证。[Microsoft Windows 发布指南](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/publish-first-app)

GitHub Release 提供经过验证的 Setup EXE、便携 ZIP、SHA-256 校验值和版本说明。首页下载按钮指向 Release 文件；仓库源码和安装文件分开维护，私人小说和书库不进入公开提交。

## 代码签名选择：2026-10-25 核实

用户已选择 **GitHub 开源 + 免费 Microsoft Store MSIX 安装与更新**。这符合“下载、安装、直接读”的目标，个人开发者无需先购买签名证书。开发者账号已注册并完成验证，名称“栖读”已预留，当前产品已提交认证。SignPath Foundation 保留为将来 GitHub 直装包签名的候选，不在本轮申请。

“未知发布者”与 SmartScreen 的“应用信誉不足”是两件事。可信 Authenticode 签名用于确认发布者及文件未被修改；新软件即使付费签名，仍可能因信誉不足被提示。EV 也不保证立刻免提示，不能为了这个目标直接买最贵证书。微软的对比说明是分发选择依据，不能将其粗略价格表当作供应商实时报价。[微软签名选项](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options)

| 路线 | 费用与条件 | 对本项目的意义 |
| --- | --- | --- |
| Microsoft Store / MSIX | 个人开发者注册已免费；提交需身份验证和商店审核，商店负责包签名 | 首选；正常商店安装路径免去直装 EXE 的 SmartScreen 阻断，并提供更新 |
| SignPath Foundation | 合格开源项目免费；需申请审核和可验证构建 | 适合 GitHub Release 直装包，但不是创建仓库后自动获批 |
| Azure Artifact Signing | Basic 公开价约 US$9.99/月，含 5,000 次签名，超出 US$0.005/次；另计适用税费／汇率 | 可用地区的个人或组织可考虑，仍需积累信誉 |
| 传统 CA 的 OV / EV | 收费，通常按年购买，证书、硬件／托管与税费需具体报价 | 暂不建议为了消除提示而购买 EV；OV、EV 都不保证立即获得信誉 |

个人注册免费和 MSIX 免费托管、签名、更新见 [Windows 开发者公告](https://blogs.windows.com/windowsdeveloper/2025/09/10/free-developer-registration-for-individual-developers-on-microsoft-store/)。把现有 EXE 直接放进 Store 属于另一条提交路线，仍需自行签名；本建议指将 WPF 应用打包为 **MSIX**，不需要重写阅读界面。[MSIX 与 EXE 的签名差别](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options)

商店上架后可以在 GitHub 首页放下载按钮：用户点击商店网页或 Direct 徽章，下载由 Store 生成的小型 EXE 安装入口，再安装商店版本。它需要联网获取应用，不能把它称为当前这个离线完整安装包。这样仍保留“点下载 → 安装”的体验。[Store Web Installer](https://learn.microsoft.com/en-us/windows/apps/distribute-through-store/how-to-use-store-web-installer-for-distribution)

Azure 的资格以服务自己的最新文档为准：个人目前仅美国／加拿大；组织已包括澳大利亚、新西兰等地区。若以澳洲或中国个人身份申请，目前不满足 Public Trust 的个人地区条件；不能把数据中心所在地区当作身份所在地。签名概览页中的组织地区列表较旧，因此不照抄它。[Azure 资格要求](https://learn.microsoft.com/en-us/azure/artifact-signing/quickstart)、[Azure 公开价格](https://azure.microsoft.com/en-us/products/artifact-signing)

SignPath 提供免费开源签名，并以其基金会身份为可验证的源码构建背书；这不等于颁发以任意产品品牌为主体的个人证书。申请前要落实开源许可证、公开源码与 CI、团队 MFA、签名责任人、首页签名／隐私政策、每次签名人工批准等要求；是否接受本项目由其审核决定。不能把基金会的宣传语理解为保证所有 Windows 电脑均无信誉提示。[SignPath 服务](https://signpath.org/)、[申请条件](https://signpath.org/terms)

实施顺序：先确定正式名称、发布账号和开源许可证；再准备 MSIX、商店图标／截图／隐私说明，验证从当前安装版迁移书架和进度，然后提交审核。商店版本的数据路径和卸载行为不能直接沿用当前 EXE 的验证结论，须单独实测。GitHub 直装版后续接入签名时，先签自有 EXE／DLL，再制作并签安装器，附可信时间戳，校验签名后重新生成 ZIP 与 SHA-256。

用户本人已完成开发者注册和身份验证；正式候选已提交商店认证，本地副本仍未签名，没有购买证书。当前处于微软预处理阶段，尚未通过商店审核。

## MSIX 文件准备：2026-10-25

已新增 [打包脚本](../scripts/build-msix.ps1)、[包清单模板](../installer/msix/AppxManifest.xml)、[身份配置模板](../installer/msix/store-identity.example.json) 和 31 个商店图标资源。原有 0.1.5 程序二进制不变；草稿包使用明确的本地验证身份，只输出到 `artifacts/msix`，不是对外安装包。正式身份必须从 Partner Center 复制，不能把安装器 AppId 当成商店身份。[应用身份](https://learn.microsoft.com/en-us/windows/apps/publish/view-app-identity-details)

`scripts/build-msix.ps1 -Draft` 已通过 SDK 10.0.26100.0 MakePri 与 MakeAppx 校验。449 个载荷／清单／图标／资源索引文件的 SHA-256 与源文件一致；无私人小说或数据库。最终报告位于 `artifacts/msix/0.1.5-c657d4cd/report.json`。资源索引已核对三组图标的目录路径和缩放候选。MSIX 是 OPC 格式，中文文件名在 ZIP 中使用 URI 转义，校验按解码路径核对，不能用普通 ZIP 文件名查找直接断言文件缺失。

商店文案、隐私说明、截图说明和提交步骤放在 `installer/msix/store-materials`。四张新版截图来自独立数据目录和自有示例，不使用真实用户藏书。当前后台支持直接保存隐私政策正文，已填写维护者 Ray0766、本地处理及删除方式。桌面应用的公开支持网址／邮箱为可选项，本次留空；用户指定邮箱只在非公开通知受众中。无需先建立网站才能提交。[微软支持信息规则](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/support-info)

0.1.9 源码回归 1,199 项通过，四张新版截图完成；独立 MSIX 测试包已完成包内首次使用、重开、1.0.9.0 → 1.0.10.0 覆盖升级及当前用户卸载重装。空书库和旧库副本迁移两条路径均保持进度、书签、设置及小说文件一致，跨阶段汇总 51 项检查通过，测试包已清理。完整范围以 [发布验证说明](validation.md) 为准；两测试版本使用同一程序载荷，正式商店下载尚未验证。WACK 总结 PASS、13 必检通过，另有一项可选进程启动扫描提示和元数据加载告警；现已提交认证。新包采用标准桌面隔离行为，没有声明 `unvirtualizedResources`。[数据隔离说明](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-behind-the-scenes)、[受限能力](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/app-capability-declarations)

账号注册、名称预留和真实包身份已完成，产品 ID 为 `9PJM5L7JG8D1`。当前候选使用应用 0.1.9 / 包 1.0.9.0。Submission 1 已完成包、免费定价、属性／隐私正文、IARC、中文介绍／四张截图、权限理由和审核说明。用户重启后，断网干净 Windows 沙盒通过 34 项环境／生命周期及 35 项应用检查，正常启动和示例阅读也通过；随后于 2026-10-25 提交认证，后台进入预处理。审核通过后自动发布，真实 Store 下载验收仍待发布。GitHub 采用 MIT 许可，通过公开仓库与 Release 分发源码和直装包。[包要求](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-package-requirements)

0.1.9 将书库放在 `%USERPROFILE%\QiduLibrary`。MSIX 可能无法直接读取旧试用版 AppData；用户可重新导入 TXT，或先由新版未打包程序迁移到共享书库后再切到商店版。旧试用数据迁移不是新用户首次安装的前置条件。

## 发布后的更新

保持产品 ID、Identity Name 和 Publisher 不变，递增 MSIX 包版本，在现有产品中创建更新提交。审核通过后由商店分发，用户通常无需卸载重装；更新仍需测试数据库兼容和阅读锚点。日常开发／试用可本机直接构建运行，不需要每次提交商店。[更新分发与版本规则](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-package-requirements)、[商店自动更新](https://support.microsoft.com/en-us/windows/apps/turn-on-automatic-app-updates)

## 可重复构建

```powershell
.\scripts\build.ps1 -Publish
.\scripts\build-installer.ps1
# 将实际通过的发布二进制回归报告传给打包脚本：
.\scripts\package.ps1 -ValidationReports artifacts/release-check/report.json
```

编译器默认路径为 `.tools\inno-setup-7\ISCC.exe`，可通过 `-Compiler` 修改。官方下载后校验 Authenticode 签名；本机使用的 7.1.0 x64 安装器 SHA-256 为 `0362A383ED217D4C4239B5933866DD96D3EB2102737DA92F80F6057A4B40DF2F`，签名状态 Valid，发布者 Pyrsys B.V.。[官方下载与验证](https://jrsoftware.org/isdl-verify.php)
