# TXT 阅读器功能调研

历史范围说明：本篇形成于平台定案前。现已确定 Windows 中文 TXT；针对夸克小说、QQ 阅读等移动应用的后续调研见 [移动阅读应用调研](mobile-reader-app-research.md)，实际已实现范围见 [发布验证说明](validation.md)。下文保留早期功能候选，不能作为当前开发状态。

调研日期：2026-10-25。用途：为拟发布到 GitHub 的 TXT 阅读器建立功能候选清单，供后续选择目标平台和首版范围。

这批产品的功能可以归为文件解析、阅读体验、内容管理和扩展服务。对以中文长篇 TXT 为核心的产品，优先值得研究的是编码容错、章节识别、文本整理和稳定续读；主题、听书、同步、AI 则可按用户场景选择。

以下以开发者官网、官方文档和项目仓库公开说明为依据，覆盖 10 款代表性产品，不代表完整市场名录或市场份额排名。功能存在不等于所有平台、版本和免费方案都支持；本轮没有进行安装实测或性能排名。目标平台和最终功能范围尚未定案。

下面这些产品分别提供了不同的参考方向。表中“可借鉴之处”是对本项目的判断。

| 产品 | 产品形态 | 已公开的相关功能 | 对本项目可借鉴之处 |
| --- | --- | --- | --- |
| [易笺 SimpleTextReader](https://github.com/henryxrl/SimpleTextReader) | 网页、浏览器扩展、可自部署 | 编码识别、中英文书名作者识别、自动章节、手动标题标记、约定格式的脚注、逐行保存进度 | 专门面向 TXT 的解析和呈现。其大文件速度描述属于项目自述 |
| [tReader](https://github.com/tiansh/reader) | 可安装的网页应用 | TXT 和 TXT.GZ、翻页与滚动、自动滚屏、繁简转换、浏览器朗读、离线使用；可配置目录模板和正则 | 纯本地网页阅读器的功能边界和较简洁的交互 |
| [Reader，binbyu](https://github.com/binbyu/Reader) | Windows 桌面 | 多种编码、自动目录、搜索、逐书进度、自动翻页、透明背景、置顶、隐藏快捷键、托盘 | Windows 专属操作和轻量阅读窗口 |
| [静读天下 Moon+ Reader](https://moondownload.com/chinese.html) | Android | 自动编码识别、断行整理、缩进与空行处理、章节导航、字体背景、多种自动滚屏、手势与音量键、标注 | 长时间阅读时的排版和操作细节 |
| [ReadEra](https://readera.org/) | Android | 本地文件发现、作者和系列分组、收藏与阅读状态、多收藏夹、书签摘录笔记、搜索、多种主题 | 本地书库整理和从书架继续阅读的流程 |
| [Readest](https://www.readest.com/docs) | 桌面、手机、网页 | TXT 等多格式、分页和滚动、批注、双书并排、查词翻译、朗读、同步、OPDS 书库接入 | 跨平台阅读、并排阅读和可迁移的阅读数据 |
| [Koodo Reader](https://www.koodoreader.com/en) | 桌面、手机、网页 | 批量导入、单栏双栏和滚动、书签高亮笔记、TTS、翻译词典、多个存储服务同步与备份 | 书库和笔记管理、用户自选存储服务 |
| [Anx Reader](https://github.com/Anxcye/anx-reader) | Android、iOS、macOS、Windows | TXT 等多格式、WebDAV 同步、阅读统计与热力图、笔记导出与卡片、TTS、AI 总结与分析 | 阅读统计和辅助理解。仓库将 OPDS 标为开发中，未计作已完成功能 |
| [calibre](https://calibre-ebook.com/about) | 桌面管理器及浏览器内容服务 | 元数据编辑、标签与检索、格式转换、设备传书；[阅读器](https://manual.calibre-ebook.com/viewer.html)有批注、朗读、位置引用和高级搜索 | 资料管理、导出和检索能力；TXT 涉及导入或转换流程，与专用 TXT 阅读器分别评估 |
| [KOReader](https://koreader.rocks/user_guide/) | 墨水屏设备、Android、Linux | TXT 等格式、排版设置、离线词典、OPDS、插件、阅读统计、进度同步、笔记导出 | 墨水屏适配及开放的书库、词典和数据接口 |

下面的清单是这些产品功能的合集；每行来源只用于证明相应功能存在于市场中，不表示所有产品都具备。

| 功能类别 | 已有功能细项 | 代表依据 |
| --- | --- | --- |
| 文件打开与导入 | 选择文件、拖拽打开、批量导入、扫描本地书籍、从原位置读取、接入远程书库；压缩包支持需按格式区分，tReader 仅支持 TXT.GZ | [Reader](https://github.com/binbyu/Reader)、[Koodo](https://www.koodoreader.com/en)、[Readest 书库](https://www.readest.com/docs/library)、[tReader 帮助](https://tiansh.github.io/reader/help/zh_cn.html) |
| 编码与文本识别 | 自动编码识别、配置编码尝试顺序；不同产品涉及 UTF-8、UTF-16、GBK、Big5 等；识别书名作者 | [tReader 配置](https://github.com/tiansh/reader)、[Reader](https://github.com/binbyu/Reader)、[易笺](https://github.com/henryxrl/SimpleTextReader) |
| 章节与目录 | 从正文推断章节、目录侧栏、目录跳转、标题模板、正则规则、手动标题标记、章节从新页开始 | [tReader 帮助](https://tiansh.github.io/reader/help/zh_cn.html)、[易笺](https://github.com/henryxrl/SimpleTextReader)、[Reader](https://github.com/binbyu/Reader) |
| 文本整理 | 合并被硬换行切断的句子、压缩空行与空格、首行缩进、繁简转换、规则替换和内容净化 | [静读天下](https://moondownload.com/chinese.html)、[tReader](https://github.com/tiansh/reader)、[Readest 校对规则](https://www.readest.com/docs/advanced) |
| 字体与版式 | 字体导入、字号与字重、字距、行距、段距、边距、对齐、单栏双栏、阅读区域宽度、每本书独立设置 | [Readest 自定义](https://www.readest.com/docs/customization)、[Koodo](https://www.koodoreader.com/en)、[Anx](https://github.com/Anxcye/anx-reader) |
| 外观与沉浸 | 浅色、深色、跟随系统、背景色与图片、全屏、工具栏按需显示、亮度手势 | [tReader 帮助](https://tiansh.github.io/reader/help/zh_cn.html)、[Readest 阅读](https://www.readest.com/docs/reading)、[ReadEra](https://readera.org/) |
| 阅读方式与操作 | 左右翻页、连续滚动、自动滚屏与速度设置、翻页动画、键盘和鼠标操作、触屏手势、自定义点击区域、音量键 | [静读天下](https://moondownload.com/chinese.html)、[tReader 帮助](https://tiansh.github.io/reader/help/zh_cn.html)、[Readest 操作](https://www.readest.com/docs/advanced) |
| 续读与导航 | 自动保存每本书位置、恢复上次阅读、书签、章节跳转、百分比跳转、跳转后前进与后退、全书和本章进度 | [Reader](https://github.com/binbyu/Reader)、[calibre 阅读器](https://manual.calibre-ebook.com/viewer.html)、[Readest 阅读](https://www.readest.com/docs/reading) |
| 搜索 | 书内全文搜索、匹配高亮、结果定位、大小写选项、整词与正则搜索、跨书正文搜索 | [ReadEra](https://readera.org/)、[calibre 阅读器](https://manual.calibre-ebook.com/viewer.html)、[Readest 书库](https://www.readest.com/docs/library) |
| 书库管理 | 封面网格和列表、最近阅读、未读在读已读、作者系列、标签分组、收藏、排序筛选、元数据编辑、自动生成封面或扉页 | [ReadEra](https://readera.org/)、[Readest 书库](https://www.readest.com/docs/library)、[calibre](https://calibre-ebook.com/about)、[易笺](https://github.com/henryxrl/SimpleTextReader) |
| 摘录与笔记 | 复制文本、彩色高亮、批注、按章节整理、导出 TXT／Markdown／CSV、摘录卡片 | [Anx](https://github.com/Anxcye/anx-reader)、[Readest 阅读](https://www.readest.com/docs/reading) |
| 听书 | 从当前位置或选区朗读、选择语音和语速、睡眠定时、逐句高亮、后台播放、锁屏控制、系统或云端语音 | [Readest 听书](https://www.readest.com/docs/listen)、[calibre 阅读器](https://manual.calibre-ebook.com/viewer.html) |
| 辅助理解 | 查词、离线词典、百科、选段翻译、双书或双语对照、AI 总结与问答分析、思维导图 | [Readest](https://www.readest.com/docs)、[KOReader 指南](https://koreader.rocks/user_guide/)、[Anx](https://github.com/Anxcye/anx-reader) |
| 同步与迁移 | 同步书籍、进度和笔记；WebDAV、网盘或自建服务；备份恢复；云端书籍按需下载；与其他阅读器互通 | [Koodo](https://www.koodoreader.com/en)、[Readest 同步](https://www.readest.com/docs/sync)、[Anx](https://github.com/Anxcye/anx-reader) |
| 阅读统计 | 阅读时长、按日周月年统计、热力图、阅读速度、章节或全书剩余时间估计 | [Anx](https://github.com/Anxcye/anx-reader)、[KOReader 指南](https://koreader.rocks/user_guide/)、[Readest 阅读](https://www.readest.com/docs/reading) |
| 平台特色 | Windows 置顶、透明窗口、快捷隐藏和托盘；网页安装与离线运行；墨水屏降低动画、对比度与刷新适配 | [Reader](https://github.com/binbyu/Reader)、[tReader 帮助](https://tiansh.github.io/reader/help/zh_cn.html)、[KOReader 指南](https://koreader.rocks/user_guide/) |
| 内容接入与扩展 | 在线目录 OPDS、calibre 书库、无线传书、自定义书源、插件 | [Readest 书库](https://www.readest.com/docs/library)、[KOReader 项目](https://github.com/koreader/koreader)、[Reader](https://github.com/binbyu/Reader) |

TXT 项目的核心问题是从缺少结构的原文中恢复合适的阅读体验。下面是基于调研提出的设计建议，不是已确定的需求。

| 问题 | 建议的产品行为 | 验证重点 |
| --- | --- | --- |
| 编码猜错后无法阅读 | 自动检测，加手动重选和解码预览；候选编码覆盖 UTF-8、UTF-16、GB18030／GBK、Big5 | 有无 BOM、简繁中文、英文、短文本；识别失败时仍能更改编码 |
| 章节漏识别或误识别 | 内置规则，加用户模板和结果预览；保留无目录也可阅读的路径 | 中文数字、阿拉伯数字、卷与章、序言番外、英文 Chapter、正文内类似标题的句子 |
| 原文换行不适合阅读 | 提供原样显示和可选整理，允许撤销整理设置 | 小说、诗歌、列表和中英混排分别处理，避免将所有换行直接合并 |
| 调整字号或窗口后进度漂移 | 用内容位置保存进度，并在重新排版后恢复 | 切换字号、窗口、模式和主题后仍能接着原文阅读 |
| 长篇文本卡顿 | 先显示可读内容，后台建立目录和搜索信息；操作能够取消 | 用明确的文件大小、设备和首次／再次打开条件测试，再定义性能指标 |
| 浏览器或设备数据丢失 | 提供书籍与阅读数据的导出、备份和恢复 | 进度、书签、笔记能迁移；浏览器缓存不作为唯一书籍备份 |

建议先以“本地中文长篇 TXT 阅读”为假设讨论首版，再根据平台和使用习惯调整。建议分期如下。

| 阶段 | 功能候选 | 取舍依据 |
| --- | --- | --- |
| 首版核心 | 本地打开／拖入、编码检测与重选、自动目录、全文搜索、可靠续读、书签、字体间距与明暗主题、适合平台的翻页操作、最近阅读、数据导出 | 覆盖从打开文件到下次继续读的完整流程；分页和滚动模式的先后顺序由阅读习惯决定 |
| 体验增强 | 自定义章节规则、文本整理、自动滚屏、书架分组、进阶排版、笔记导出、平台专属操作 | 扩展已经稳定的核心流程，针对实际反馈逐项加入 |
| 按场景扩展 | 听书、WebDAV 同步、EPUB、词典翻译、统计、并排阅读、AI、OPDS 或书源插件 | 各自会增加持续维护范围，应先确认使用频率和具体需求 |

用于后续选型的几个边界：Readest 文档区分了移动端系统语音与桌面／网页语音来源，不能把支持 TTS 等同于所有平台都能离线朗读；tReader 的浏览器存储可能被清理，离线运行也不等于可靠备份。[Readest 听书说明](https://www.readest.com/docs/listen)、[tReader 存储说明](https://tiansh.github.io/reader/help/zh_cn.html)。

功能评估还需要区分格式：TXT 没有固定版式，某个阅读器针对 EPUB 或 PDF 的功能不能直接算作 TXT 能力。

下一步需要确定首发平台、主要内容类型，以及默认采用分页还是滚动阅读。这三项将决定首版交互和后续技术选型。
