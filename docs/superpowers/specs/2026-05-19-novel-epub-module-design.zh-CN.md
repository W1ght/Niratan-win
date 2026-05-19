# 小说 EPUB 模块设计

日期：2026-05-19
状态：第一阶段已确认并已实现

## 目标

在 Hoshi 现有漫画/Comic 模块之外，新增一个独立的小说/Novel 模块。第一阶段只支持本地 EPUB 文件，产品和业务域统一使用 `Novel` 命名，`Epub` 只出现在格式相关的导入、解析、渲染代码里。

这个模块复用 Hoshi 的 Windows 外壳骨架：WinUI 3、MVVM、依赖注入、Dapper、SQLite 和现有导航风格。小说功能不复用漫画服务，也不写入漫画数据库表。

## 第一阶段不做

第一阶段不实现：

- Yomitan 字典查询
- hoshidicts native interop
- AnkiConnect
- 高亮、笔记、书签
- OPDS、DRM、云同步
- 非 EPUB 格式
- 正式 EPUB 正文渲染

漫画 reader 和小说 reader 的状态也不会合并。

## 命名原则

面向用户和业务域的代码使用 `Novel`：

- `NovelBook`
- `NovelReadingProgress`
- `NovelReaderSettings`
- `NovelLibraryPage`
- `NovelReaderPage`
- `INovelLibraryService`

只有格式相关代码使用 `Epub`：

- `INovelEpubImportService`
- `EpubMetadataExtractor`
- `EpubCoverExtractor`
- `NovelEpubReaderHost`

这样短期明确 EPUB-only，长期也不把整个小说模块绑死在 EPUB 格式上。

## 架构

第一阶段是一条和漫画模块平行的垂直切片：

```text
WinUI shell
  -> NovelLibraryPage / NovelLibraryPageViewModel
  -> INovelLibraryService
  -> IDataService 小说存储方法
  -> SQLite 小说表

小说导入
  -> 文件选择器
  -> INovelEpubImportService
  -> EPUB 元数据提取
  -> NovelBook 写入 SQLite
```

现有漫画流程保持独立：

```text
Comic pages/viewmodels/services
  -> IComicService
  -> 漫画表和漫画源插件
```

共享外壳可以同时路由到漫画和小说页面，但两个业务域的 service 不互相调用。

## 数据模型

小说数据使用独立 SQLite 表。

`NovelBooks` 存本地导入的 EPUB：

```sql
CREATE TABLE NovelBooks (
  Id TEXT PRIMARY KEY,
  Title TEXT NOT NULL,
  Author TEXT,
  FilePath TEXT NOT NULL UNIQUE,
  CoverPath TEXT,
  ImportedAt TEXT NOT NULL,
  LastOpenedAt TEXT,
  Language TEXT,
  UniqueIdentifier TEXT
);
```

`NovelReadingProgress` 存未来 reader 的阅读位置：

```sql
CREATE TABLE NovelReadingProgress (
  BookId TEXT PRIMARY KEY,
  LocationJson TEXT NOT NULL,
  Progression REAL,
  ChapterHref TEXT,
  UpdatedAt TEXT NOT NULL
);
```

`NovelReaderSettings` 存全局或单书阅读设置：

```sql
CREATE TABLE NovelReaderSettings (
  Scope TEXT NOT NULL,
  ScopeId TEXT NOT NULL,
  SettingsJson TEXT NOT NULL,
  UpdatedAt TEXT NOT NULL,
  PRIMARY KEY (Scope, ScopeId)
);
```

重复导入按规范化后的 `FilePath` 去重。因为第一阶段只支持本地 EPUB，这个规则最稳定。

## 第一阶段 UI

新增 `Novels` 导航入口，打开 `NovelLibraryPage`。

小说书库页支持：

- 导入本地 `.epub`
- 显示标题、作者、文件路径
- 选择一本书后进入独立的 `NovelReaderPage`

第一阶段的 `NovelReaderPage` 是占位页，不渲染 EPUB 正文。正式渲染会在下一阶段接 WebView2 + foliate-js。

## EPUB 处理

短期只支持 EPUB。导入时会：

- 校验文件存在
- 校验扩展名为 `.epub`
- 尝试读取 EPUB 内 `.opf` 元数据
- 优先提取标题、作者、语言、唯一标识
- 元数据缺失时使用文件名作为标题兜底

导入阶段不读取整本书正文。正文加载和分页属于后续 reader host。

## Reader 方向

正式小说 reader 会使用 WebView2 + foliate-js。WinUI 原生文本控件只允许作为临时占位，不用于 EPUB 正文渲染。

后续 Reader bridge 要使用带 `version` 和 `type` 的强类型消息。JavaScript 只负责渲染、选择文本、坐标和事件；字典、日语文本逻辑、Anki 逻辑仍然留在 C# service 或 native backend。

## 错误处理

导入失败不能导致 App 崩溃。预期错误包括：

- 选择的不是 `.epub`
- 文件不存在或不可访问
- EPUB 元数据缺失或格式异常
- 封面提取失败
- 数据库写入失败

元数据缺失可以降级为文件名标题。文件访问和数据库失败通过现有通知/对话框模式提示用户。

## 测试

第一阶段已覆盖：

- EPUB 文件类型校验
- 元数据缺失时的标题兜底
- 小说数据库迁移建表
- `FilePath` 重复导入约束
- 小说书库 ViewModel 初始化、取消导入、打开小说消息

WinUI 页面渲染目前以构建和启动 smoke test 验证，后续如果引入 UI 自动化再补页面级测试。

