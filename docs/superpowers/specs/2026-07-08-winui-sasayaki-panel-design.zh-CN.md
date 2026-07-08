# WinUI Sasayaki 阅读器面板设计

## 背景

阅读器顶栏已有 Sasayaki 入口和基础播放菜单，但当前菜单只能承载少量命令。需要对齐 Niratan 的 Sasayaki sheet，在阅读器内提供完整 WinUI 面板，按 Audio、Playback、Settings 分区管理播放、同步和样式。Lyrics Mode 不进入本次范围。

## 交互形态

点击阅读器顶栏 Sasayaki 按钮打开 WinUI `ContentDialog` 风格的 sheet。该按钮继续仅在 Sasayaki 启用且当前书籍有可用状态时显示。Dialog 关闭后不影响当前播放状态。

## 面板内容

Audio 分区提供横向播放控制：后退 15 秒、上一 cue、播放/暂停、下一 cue、前进 15 秒，并显示当前时间、总时长、当前 cue 文本和加载音频入口。无音频或无匹配数据时，播放控制禁用，但加载音频保持可用。

Playback 分区提供 Delay 和 Speed 两个滑条。Delay 范围为 -2.00s 到 +2.00s，步进 0.05s；Speed 范围为 0.50x 到 1.50x，步进 0.05x。修改后立即应用到当前播放器，并持久化到书籍 Sasayaki playback sidecar。

Settings 分区提供 Show Sasayaki、Auto-Scroll、Auto-Pause 三个开关，直接更新全局 Sasayaki 设置。Show Sasayaki 控制阅读器内入口显示策略；Auto-Scroll 和 Auto-Pause 沿用现有播放同步和查词暂停行为。

Theme 分区提供 Light Theme 与 Dark Theme 的 Text Color、Background Color 四个 ColorPicker。颜色写入现有 `SasayakiSettings` 字段，并在当前 cue 高亮时实时刷新 WebView2 中的 Sasayaki 高亮样式。

## 数据流

面板复用 `SasayakiViewModel` 的加载状态、时间、cue 文本和播放速率。播放命令继续调用 `NovelReaderPage` 中已有的 Sasayaki 播放方法。设置和颜色通过现有设置服务保存，播放延迟和速度通过现有 sidecar 保存。WebView2 只接收窄消息更新高亮样式，不承载设置逻辑。

## 验证

新增或更新 XAML 资产测试，覆盖面板存在、Audio/Playback/Settings/Theme 分区、关键 AutomationId、Delay/Speed 范围、四个颜色选择器，以及 Lyrics Mode 不出现。实现后运行 `dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelReaderWebAssetTests"` 和 `dotnet build -p:Platform=x64`，最后用 `.\build-and-run.ps1` 启动验证窗口可打开。
