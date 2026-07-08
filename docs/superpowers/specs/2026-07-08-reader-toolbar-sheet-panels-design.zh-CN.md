# 阅读器工具面板 Sheet 化设计

## 背景

阅读器顶栏中 Sasayaki 已改为 WinUI `ContentDialog` sheet。其余工具按钮仍使用左侧自定义 `Popup`：章节、搜索、高亮、统计、外观。需要统一交互形态，使阅读器工具面板都以 WinUI sheet 打开。

## 范围

本次只转换章节、搜索、高亮、统计、外观五个按钮。返回按钮继续执行导航返回，不改成面板。Sasayaki 保持现有 `ContentDialog` 面板，不重复实现。

## 交互

点击对应顶栏按钮打开对应 `ContentDialog`。同一时间只允许一个阅读器工具 sheet 打开；打开新 sheet 前关闭当前 sheet。Dialog 关闭后不改变阅读器状态。搜索、高亮、章节、统计、外观内部内容复用现有控件、列表、ViewModel 绑定和事件。

## 数据流

现有业务路径不变：章节列表仍使用 `ReaderChapterListContent`，搜索仍更新 `ReaderSearchQueryBox` 与结果列表，高亮仍使用当前高亮集合和跳转/删除事件，统计仍使用 `ViewModel` 的统计文本和开始/停止按钮，外观仍复用 `ReaderAppearanceSettingsContent`。code-behind 仅负责打开/关闭 WinUI sheet 和调用现有 UI 刷新方法。

## 验证

新增资产测试确认五个 `Reader*PanelDialog` 存在、旧 `Reader*PanelPopup` 不再存在、按钮仍绑定原点击事件、关键 AutomationId 保留。实现后运行 `NovelReaderWebAssetTests`、`dotnet build -p:Platform=x64`，并用 `.\build-and-run.ps1` 验证应用可启动。
