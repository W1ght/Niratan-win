<div align="center">

# Niratan Win

![Language](https://img.shields.io/github/languages/top/W1ght/Niratan-win)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2B-0078D4)
![Architecture](https://img.shields.io/badge/architecture-x64-lightgrey)
![License](https://img.shields.io/github/license/W1ght/Niratan-win)

Niratan Win 是 [Niratan](https://github.com/W1ght/Niratan) 面向 Windows 的原生实现，把 EPUB 阅读、Yomitan 风格查词、Sasayaki 有声书、本地字幕视频学习和 AnkiConnect 制卡放在同一个桌面工作流中。

Windows 版本使用 WinUI 3 构建原生界面，并以 Niratan 作为用户可见行为的对齐源；针对窗口、输入方式和系统控件的差异采用符合 Windows 习惯的交互。

</div>

## 功能

### 小说阅读

- EPUB 书架、拖放导入、导出、自定义书架、排序和阅读进度管理。
- 支持横排 / 竖排、分页 / 连续滚动、明暗主题、字体和排版调整。
- 支持目录、书内搜索、高亮、阅读历史、图片库和全屏专注阅读。
- 提供阅读计时、字符数、阅读速度、日历、趋势和书籍排行等统计。
- 支持键盘、鼠标、触控板和兼容游戏手柄操作。

### 查词与词典

- 通过 hoshidicts 导入和查询 Yomitan 格式的术语、频率与音高词典。
- 支持点击查词、文本选择查词、Shift 悬停查词和弹窗内嵌套查词。
- 阅读器、独立查词页、视频字幕和跨应用全局查词复用同一套词典渲染与 Profile 配置。
- 弹窗支持 Yomitan structured content，并可按词典类型独立启用、排序和删除。

### Sasayaki 有声书

- 将本地有声书与 SRT 字幕匹配到 EPUB 正文。
- 支持播放控制、句子跳转、自动滚动、当前句高亮和沉浸式歌词模式。
- 查词与 Anki 制卡可附带当前句对应的有声书音频片段。

### 视频学习

- 本地视频库支持文件夹导入、继续观看、搜索、排序、筛选、标签、收藏和智能合集。
- 独立播放器窗口支持字幕查词、Transcript、章节、Inspector、A-B 循环和播放历史。
- 支持内嵌字幕以及 SRT / VTT / ASS / SSA 等外挂字幕。
- 提供实验性的 YouTube 链接播放，并支持可用的发布者字幕。

### Anki 与同步

- 通过 AnkiConnect 创建卡片，支持字段映射、模板、重复检查和媒体字段。
- 小说卡片可附带本地单词音频、有声书音频和书籍封面；视频卡片可附带截图和字幕音频片段。
- 可选 ッツ Sync / Google Drive 同步书籍、阅读进度、统计、高亮和相关学习数据。
- 支持本地数据备份与恢复。

### Windows 桌面体验

- 基于 WinUI 3、Windows App SDK 和 WebView2 的 Windows 原生界面。
- 主窗口中的阅读器采用响应式布局；视频播放器与全局查词提供独立窗口体验。
- 提供中英文界面、Profile、统一快捷键、主题和更新检查。

## 为什么选择 Niratan Win

- 阅读、查词、听书、字幕视频学习和制卡可以在同一个 Windows 应用中完成。
- 小说与视频共用词典、弹窗、Profile 和 Anki 管线，减少重复配置。
- WebView2 负责可靠的日文竖排、ruby 和 EPUB 分页，外层交互保持 WinUI 原生体验。
- 书籍、词典、视频和大部分学习数据默认保存在本机；同步与 AnkiConnect 均为可选能力。

## 下载与安装

从 [GitHub Releases](https://github.com/W1ght/Niratan-win/releases) 下载最新的 Windows x64 版本。

- `Niratan.Setup.x64*.exe`：推荐使用的自包含安装包，无需另外安装 .NET Runtime。
- `Niratan.Minimal.x64.zip`：精简便携包，需要已安装 x64 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)；解压后运行 `Niratan.exe`。

最低支持 Windows 10 版本 2004（Build 19041）。当前正式发布仅提供 x64 架构。若 Windows SmartScreen 对尚未建立信誉的新版本显示提示，请确认文件来自本仓库的 GitHub Releases 后再决定是否运行。

## 快速开始

1. 首次启动后，在“设置 → 词典”中下载推荐词典，或导入 Yomitan 格式的词典压缩包。
2. 在“小说”页面拖入或选择一个 EPUB 文件，打开书籍开始阅读。
3. 点击文字、选择文本或按住 `Shift` 悬停进行查词。
4. 如需制卡，在“设置 → Anki”中配置 AnkiConnect 和字段映射。
5. Sasayaki、视频学习、全局查词和 Google Drive 同步均可按需启用。

## 开发状态

Niratan Win 正在持续对齐 Niratan 的阅读、词典、Sasayaki、视频、同步、统计和制卡体验。正式版本以 GitHub Releases 中的安装包和压缩包为准，用户可见变化记录在 Release Notes 与 [变更日志](docs/CHANGELOG.md) 中。

Niratan 是产品行为的唯一参考；Windows 版本不会机械复制 SwiftUI 外观，而是使用 WinUI 原生控件、交互和响应式规则实现相同行为。

## 从源码构建

### 环境要求

- Windows 10/11 x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Git、CMake
- Visual Studio 2022/2026 的“使用 C++ 的桌面开发”工作负载，或可用的 Clang/LLVM 工具链

### 构建与测试

```powershell
git clone --recursive https://github.com/W1ght/Niratan-win.git
Set-Location Niratan-win

# 构建原生词典 DLL
.\build-native.ps1

# 构建 Windows x64 应用
dotnet build -p:Platform=x64

# 运行测试
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64

# 构建并启动
.\build-and-run.ps1
```

更详细的实现与验证说明请参阅 [架构文档](docs/ARCHITECTURE.md) 和 [验证指南](docs/VERIFICATION.md)。

## 隐私与数据

- 本地书籍、词典、视频文件、设置和学习数据默认保存在用户电脑上。
- Google Drive 同步仅在用户显式启用并完成授权后访问云端数据。
- Anki 制卡只连接用户配置的 AnkiConnect 地址；默认场景为本机 Anki。
- 全局查词只在用户触发已配置的快捷键时读取当前选中的文本。
- YouTube 播放、在线词典目录和版本检查需要访问相应的网络服务。

## 反馈与功能请求

如果你在 Windows 阅读、查词、听书、同步、视频学习或 Anki 制卡中遇到问题，欢迎通过 [GitHub Issues](https://github.com/W1ght/Niratan-win/issues) 反馈。请尽量附上 Niratan Win 版本、Windows 版本、复现步骤和相关日志。

## 鸣谢
- [Manhhao/Hoshi-Reader](https://github.com/Manhhao/Hoshi-Reader)：原 Hoshi Reader 项目。
- [Hoshi Reader Android](https://github.com/HuangAntimony/Hoshi-Reader-Android)：Android 原生日语阅读器。
- [hoshidicts](https://github.com/Manhhao/hoshidicts)：词典查询引擎与 Yomitan 词典支持。
- [Yomitan](https://github.com/yomidevs/yomitan)：弹窗词典体验与 structured content 的重要参考。
- [ッツ Ebook Reader](https://github.com/ttu-ttu/ebook-reader)：阅读统计与同步体验参考。
- [mpv](https://mpv.io/)：视频播放能力。
- [TheMoeWay](https://learnjapanese.moe/)：日语沉浸学习资源。
- [hibiki](https://github.com/hajisensai/hibiki/)：A full-featured immersion language learning suite for mobile.

完整的第三方组件与许可信息请查看 [THIRD_PARTY_NOTICES](docs/THIRD_PARTY_NOTICES.md)。

## 许可证

本项目基于 GNU General Public License v3.0 发布。更多信息请查看 [LICENSE](LICENSE.txt)。
