# Niratan Win Agent 指南

Niratan Win 是面向 Windows 10+ x64 的原生日语沉浸学习 App。单一 WinUI 3 / .NET 应用始终包含小说、视频和漫画模块，并共享词典、Popup、Profile 和 Anki 流程。

`docs/reference/Niratan` 是共有用户可见行为的唯一对齐源。Windows 实现使用原生控件、窗口和输入规则；Niratan 没有的 Windows 扩展必须在本仓库规格或架构文档中明确记录，且不得反向改变共有行为。

`docs/reference/ShokoServer` 是视频动画识别与刮削能力的长期上游参考，以 git submodule 固定版本。涉及 ED2K/AniDB 文件身份、分集映射、系列关系分组、跨源 metadata、图片、MyList、缓存、限流或失败恢复时，先读其根 `CLAUDE.md` 和最邻近实现；只移植与单机 Niratan 架构相容的行为，不修改子模块，也不引入 Shoko 的服务端 API、媒体移动/重命名或多用户假设。共有 UI/播放/学习行为仍以 `docs/reference/Niratan` 为唯一真源。

本文件只保存每个任务都必须知道的产品边界、高后果不变量、仓库陷阱和上下文入口。专项架构、验证矩阵、调查记录和操作步骤按需读取，不在这里重复。

## 常驻边界

- 禁止修改 `native/hoshidicts/` 下的任何代码。字典功能只能通过 C# P/Invoke 调用 `hoshidicts_c_api` 暴露的窄接口。
- 保护用户的书籍、漫画、视频、sidecar、catalog、阅读/播放进度、Profile、词典、Anki 配置、凭据和 token；不得通过清空、重建或迁移用户数据掩盖 bug。
- 本地漫画和视频库是非破坏性索引。导入、刷新、移除和验证不得移动、重命名、改写或删除用户源媒体；小说导入后的私有副本也不得被越界访问。
- Shoko 对齐保持文件身份为 `ED2K + file size`、AniDB AID/EID/FID 与本地层级分离，并把 provider 原始缓存、跨源映射和用户覆盖区分持久化；不得以文件夹季号或 TMDB 季号覆盖已确认的 AniDB 身份。
- EPUB、CBZ/ZIP、Mokuro、字幕、torrent 元数据、远端响应和 WebView2 消息均视为不可信输入。校验路径、来源、大小、格式、跳转和消息类型；native/JS bridge 保持窄、强类型、带版本。
- 保留当前工作树中与任务无关的改动。未经用户明确要求，不 commit、push、打 tag 或 release。
- 新增用户可见文案进入 `Niratan/Strings/en-US/Resources.resw` 和 `Niratan/Strings/zh-CN/Resources.resw`；XAML 优先使用 `x:Uid`。

## 架构不变量

- 保持 `View（XAML + UI-only code-behind）→ ViewModel（状态与命令）→ Service（IO、持久化、网络、字典、Anki、native 工作）`。code-behind 只承载必须依赖 WinUI/WebView2/mpv 的生命周期、输入、窗口和坐标适配；不要扩大现有遗留例外。
- Reader、Video、Manga 是同一 App 内的模块边界，不是独立产品。共享 Dictionary、Popup、Profile 和 Anki 管线不得反向依赖某个内容来源；快捷键按各窗口现有 scope 处理。
- 小说正文继续使用 WebView2 + CSS multi-column；不得恢复 foliate-js、自研第二套 EPUB 排版引擎，或用 WinUI 文本控件替代正文渲染。
- Reader JavaScript 只负责渲染、选择、坐标和事件；字典查询、章节边界、持久化和业务决策留在 native/ViewModel/Service。
- Video UI 通过现有 playback engine/service 契约操作播放状态；普通 View 或 ViewModel 不直接调用 mpv C API。
- Manga 通过现有 session、page provider 和 catalog/service 边界读取源内容；page/cover cache 可重建，`catalog.json` 是必须保护的用户状态，源媒体保持只读。
- 持久化格式及迁移规则以当前模型、服务、契约测试和 `docs/ARCHITECTURE.md` 为准，不在根指令中复制易变 schema。

## 仓库特有陷阱

- 默认只构建和测试 x64：`dotnet build -p:Platform=x64`；`dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64`。不默认构建 ARM64。
- 构建并启动使用 `.\build-and-run.ps1`；原生字典 DLL 由项目构建目标按需确保并复制。
- Windows PowerShell 5.1 读取中日文文件时显式使用 `Get-Content -Encoding UTF8`。
- 发布必须使用 `.\release.ps1 -Version x.y.z`；禁止手工创建、移动、删除或复用 `v*` 标签。预览使用 `-PlanOnly`。
- UI 验证只信任本次构建输出启动的进程。使用 disposable fixture；没有安全数据、账户、服务或硬件时，列出未验证场景，不操作用户现有数据。

## 按需上下文

实现、调试、验证、上游对齐、构建或发布任务使用 `.codex/skills/niratan-win-workflow/SKILL.md`，只加载与当前范围匹配的文档。

- `docs/ARCHITECTURE.md`：持久架构、模块所有权、数据与安全边界。
- `docs/VERIFICATION.md`：Reader、Dictionary、Audio、Video、Manga 和导入流程的验证矩阵。
- `docs/reference/Niratan/AGENTS.md` 与最邻近的 Niratan 源码：共有产品行为。
- `docs/reference/ShokoServer/CLAUDE.md` 与最邻近的 Shoko 源码：动画文件识别、AniDB/跨源刮削、关系分组、图片和 MyList 行为；仅作只读参考。
- `docs/SHOKO_SCRAPING_ALIGNMENT.md`：Shoko 固定版本对应的 Niratan 动画刮削链路、身份层级、存储位置、桌面等价边界与验收门槛。
- `docs/superpowers/specs/`、`docs/superpowers/plans/`：已批准扩展和历史设计。
- `docs/CHANGELOG.md`：已解决问题的根因与用户可见结果。
- `.claude/skills/`：Claude Code 的专项构建、测试和 UI 约定。

只有实现使现有真源失真时才更新对应文档。不要把一次性日志、代码可直接表达的事实或重复命令写回根文件。

## 完成契约

- 先检查工作树、最近实现、邻近测试和当前真源，再决定修改。
- 运行与改动最接近的 contract/test；修改可运行 App 代码后至少完成 x64 build，并打开受影响模块，除非专项验证文档规定更安全的例外。
- Contract test 不能证明 Reader、Video 或 Manga 的视觉正确性；不得声明未亲自验证的 UI、外部账户、Anki、同步或发布行为可用。
- 最终回复说明改了什么、验证了什么，以及哪些场景尚未验证。
