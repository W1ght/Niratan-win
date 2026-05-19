# 小说 EPUB 模块实现计划（中文导读）

日期：2026-05-19
状态：第一阶段已按英文详细计划执行完成

英文详细计划保留在：

```text
docs/superpowers/plans/2026-05-19-novel-epub-module.md
```

这份中文文档用于快速理解已经做了什么、为什么这么拆，以及后续怎么继续。

## 目标

在不影响现有漫画模块的前提下，新增独立小说模块：

- 支持导入本地 EPUB
- 写入独立 SQLite 表
- 在新的 `Novels` 页面展示
- 点击后进入独立小说 reader 占位页
- 保持漫画 reader 和漫画服务不被小说逻辑污染

## 技术路线

使用现有项目骨架：

- WinUI 3
- MVVM
- CommunityToolkit.Mvvm
- Microsoft.Extensions.DependencyInjection
- Dapper
- Microsoft.Data.Sqlite
- xUnit + FluentAssertions

命名上遵守：

- 业务域：`Novel`
- EPUB 格式能力：`Epub`
- 漫画模块：继续使用现有 `Comic`

## 已创建的主要文件

模型：

- `Hoshi/Models/NovelBook.cs`
- `Hoshi/Models/Data/NovelReadingProgress.cs`
- `Hoshi/Models/DTO/NovelImportResult.cs`
- `Hoshi/Models/DTO/NovelReaderNavigationArgs.cs`

服务：

- `Hoshi/Services/Novels/INovelEpubImportService.cs`
- `Hoshi/Services/Novels/NovelEpubImportService.cs`
- `Hoshi/Services/Novels/INovelLibraryService.cs`
- `Hoshi/Services/Novels/NovelLibraryService.cs`

存储：

- `Hoshi/Services/Storage/Migrations/Migration_003.cs`
- `IDataService` 中新增小说存储方法
- `DataService` 中实现小说查询、写入、最近打开时间更新

ViewModel：

- `Hoshi/ViewModels/Components/NovelBookItemViewModel.cs`
- `Hoshi/ViewModels/Pages/NovelLibraryPageViewModel.cs`
- `Hoshi/ViewModels/Pages/NovelReaderPageViewModel.cs`

页面：

- `Hoshi/Views/Pages/NovelLibraryPage.xaml`
- `Hoshi/Views/Pages/NovelLibraryPage.xaml.cs`
- `Hoshi/Views/Pages/NovelReaderPage.xaml`
- `Hoshi/Views/Pages/NovelReaderPage.xaml.cs`

测试：

- `Hoshi.Tests/Services/Novels/NovelEpubImportServiceTests.cs`
- `Hoshi.Tests/Services/Storage/NovelDataServiceTests.cs`
- `Hoshi.Tests/ViewModels/Pages/NovelLibraryPageViewModelTests.cs`

## 实现顺序

1. 新增小说域模型。
2. 新增 SQLite migration 和小说表。
3. 在 `IDataService` / `DataService` 中加入小说存储方法。
4. 新增 EPUB 导入服务。
5. 新增小说书库 service。
6. 新增小说书库 ViewModel 和测试。
7. 新增 `Novels` 导航入口。
8. 新增独立 `NovelReaderPage` 占位页。
9. 跑 restore、build、全量 test。
10. 启动 App 做进程级 smoke test。

## 已验证结果

已执行：

```powershell
$env:HTTP_PROXY='http://127.0.0.1:7890'
$env:HTTPS_PROXY='http://127.0.0.1:7890'
& 'C:\Program Files\dotnet\dotnet.exe' restore .\Hoshi.slnx -r win-x64
& 'C:\Program Files\dotnet\dotnet.exe' build .\Hoshi.slnx -c Debug -p:Platform=x64 --no-restore
& 'C:\Program Files\dotnet\dotnet.exe' test .\Hoshi.slnx -c Debug -p:Platform=x64 --no-build --logger "console;verbosity=minimal"
```

结果：

- restore 成功
- build 成功，0 error
- 保留 6 个项目既有 nullable warning
- test 成功，95 passed / 0 failed
- `Hoshi.exe` 可启动并响应

## 下一阶段建议

下一阶段做正式 EPUB reader host：

```text
NovelReaderPage
  -> WebView2
  -> 本地 reader-host.html
  -> reader-bridge.js
  -> foliate-js adapter
  -> C# typed bridge messages
```

第一步不要急着做字典、Anki、高亮。先让一本本地 EPUB 可以被 WebView2 加载并显示正文，再做翻页、进度、设置、查词。

## 注意事项

- 小说 reader 不能复用漫画 `ReaderPageViewModel`。
- EPUB 正文不要用 WinUI 原生 `TextBlock` / `RichTextBlock` 渲染。
- JavaScript 不写字典逻辑，只处理渲染和事件。
- C# bridge 消息后续要强类型，并带 `version` / `type`。
- 新增依赖或下载依赖时继续显式使用代理。

