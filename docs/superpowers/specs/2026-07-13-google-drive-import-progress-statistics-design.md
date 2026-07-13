# Google Drive 书籍导入进度与统计对齐设计

## 背景

Hoshi 从 Google Drive 下载 TTU 书籍后，存在两个用户可见问题：

- 阅读进度没有恢复到远端保存的位置。
- 开启统计同步时，远端统计没有进入本地统计面板。

当前导入流程先把 TTU bookdata 转换为 EPUB，再通过普通 EPUB 导入链路创建本地书籍，随后按转换后的 EPUB 标题重新查询 Drive 文件夹并导入 sidecar。该流程与 Niratan 不一致，并产生两个缺陷：

1. Drive 文件夹标题与 EPUB 元数据标题不完全一致时，重新查询可能命中错误目录或新建空目录，从而丢失原先选中书籍的 progress/statistics 文件。
2. 普通 EPUB 导入返回时尚未生成 `bookinfo.json`，同步服务无法把远端总字数位置换算为正确 spine 章节，只能退化为第 1 章中的全书比例。

## 目标

- 使用用户在 Drive 书架中选中书籍的精确远端文件快照完成导入，不再按本地标题重新发现文件。
- 在同步远端进度之前生成完整 `bookinfo.json`。
- 首次打开下载书籍时直接定位到正确章节和章内进度，不进行打开后的二次修正，不产生进度闪烁。
- 开启统计同步时导入远端统计并遵守 Merge / Replace；关闭时不请求、不创建远端统计数据，严格对齐 Niratan。
- 保持 bookmark、statistics 和 Sasayaki sidecar 的事务式写入与失败回滚。

## 非目标

- 不改变 Google Drive 授权、文件夹结构或远端命名格式。
- 不取消或绕过“统计同步”开关。
- 不修改 Reader 排版、翻页或统计会话计算逻辑。
- 不访问真实 Google Drive 进行自动化测试。
- 不引入新的 EPUB 排版或字数统计引擎。

## 参考行为

Niratan 的 `SyncManager.importGoogleDriveBook` 直接使用书架发现阶段已经获得的 `DriveSyncFiles`，并行获取 bookdata、progress、statistics 和 audiobook。转换完成后生成书籍目录及 `bookinfo`，随后把进度与可选 sidecar 写入本地。

Hoshi 保留现有服务分层，但对齐以下行为：

- 远端文件身份来自被选中的 Drive 书籍快照。
- `bookinfo` 必须先于 progress 导入存在。
- statistics 是否导入由全局同步和统计同步开关共同决定。

## 架构与职责

### `TtuBookImportService`

继续负责 Drive 下载、TTU 转 EPUB、本地书籍导入和 sidecar 同步编排。

完成本地 EPUB 导入后，把 `TtuRemoteBook.Files` 作为已知远端文件快照传给 `TtuSyncService`。导入链路不得调用按本地书名重新发现远端文件的路径。

### `NovelEpubImportService`

在 EPUB 解析完成后，为每个 spine 章节读取已经安全解包到私有目录的 XHTML，并通过现有 `ReaderTextFilter.CountReadableCharacters` 计算可读字符数。该过滤器与 Reader 当前计算 `bookinfo` 的规则相同，不构成第二套排版或统计引擎。

服务通过 `INovelBookSidecarService.CreateBookInfo` 建立章节起始字数、章节字数与总字数映射，并在导入成功返回之前保存 `bookinfo.json`。

Reader 仍可在打开时按现有流程重新计算和覆盖同一份映射，以兼容未来过滤规则变化；首次定位不再依赖这次打开时的补算。

### `TtuSyncService`

正常手动/自动同步继续按本地书名发现远端文件。仅 Drive 新书导入传入已知远端文件快照，服务优先使用该快照。

导入 progress 时读取已经存在的 `bookinfo.json`，按 `exploredCharCount` 解析目标章节：

1. 将目标字数限制在 `0...bookInfo.characterCount`。
2. 按章节 `currentTotal` 排序。
3. 选择起始字数不大于目标字数的最后一个章节。
4. 用章节内字数除以章节字数得到章内进度。

章节边界归属后一章开头；全书结尾归属最后一章末尾。最终 bookmark 保留远端 `lastBookmarkModified` 和 `exploredCharCount`。

## 数据流

1. 用户从 Drive 书架选择远端书籍。
2. `TtuBookImportService` 使用选中快照中的 bookdata 文件 ID 下载 TTU 数据。
3. `TtuBookDataConverter` 生成 EPUB。
4. `NovelEpubImportService` 把 EPUB 导入应用私有目录、解析 spine、计算章节字数并保存 `bookinfo.json`。
5. `TtuBookImportService` 把同一个 `TtuRemoteBook.Files` 快照交给 `TtuSyncService`。
6. `TtuSyncService` 使用快照中的 progress 文件导入 `bookmark.json`。
7. 统计同步开启且快照包含 statistics 时，按 Merge / Replace 导入 `statistics.json`。
8. Sasayaki 同步开启且快照包含 audiobook 时，导入播放 sidecar。
9. 所有必需步骤成功后，书籍导入结果返回书架。

## 统计同步语义

- 全局 TTU 同步关闭时，不提供 Drive 下载入口或同步行为，沿用现状。
- 全局同步开启、统计同步关闭时，不请求远端 statistics，不创建或覆盖 `statistics.json`。
- 统计同步开启但远端不存在 statistics 文件时，进度导入正常完成，不生成空统计文件。
- Merge：按现有日期键和最后修改时间合并本地与远端统计。
- Replace：以有效远端统计替换本地统计；有效空数组可清空本地统计，沿用现有同步服务语义。

## 错误处理与一致性

- 缺少 bookdata 时，导入在下载前失败。
- EPUB 转换、解析、章节读取或 `bookinfo.json` 保存失败时，普通 EPUB 导入负责清理不完整私有书籍目录。
- progress 文件存在但无法读取时，Drive 导入失败，不把书籍呈现为已完整导入。
- sidecar 获取发生异常时，不开始本地 sidecar 提交。
- sidecar 提交发生异常时，恢复导入前的 bookmark、statistics 和 Sasayaki 文件快照。
- 统计同步关闭或远端统计缺失不是错误。
- 目标字数小于零、超过总字数或章节字数为零时使用钳制和现有安全回退，不产生越界章节。

## 测试设计

### 单元与服务测试

- Drive 文件夹标题与导入后的 EPUB 标题不同，仍使用所选快照中的 progress/statistics 文件，且不调用按标题重新发现文件。
- EPUB 导入返回前已经生成 `bookinfo.json`，章节路径、spine index、起始字数、章节字数和总字数正确。
- 远端总字数位置可映射到第 2 章或更后章节的准确章内进度。
- 章节边界映射到后一章开头，全书末尾映射到最后一章末尾。
- 统计同步开启时测试 Merge 和 Replace。
- 统计同步关闭时不读取统计文件、不创建 `statistics.json`。
- 远端无统计文件时仍成功导入进度。
- 获取或提交失败时验证 sidecar 回滚。
- 普通 EPUB 导入继续成功，并生成与 Reader 相同过滤规则的字数映射。

### 回归验证

- 运行 `dotnet build -p:Platform=x64`。
- 运行 `dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64`。
- 构建并启动 Hoshi，使用本地假 Drive 数据或受控测试 fixture 验证下载流程，不访问真实 Google Drive。
- 打开下载书籍时确认首次渲染直接位于正确章节，没有先显示错误位置再跳转的闪烁。
- 验证统计同步开启时统计面板包含导入记录；关闭时不导入。

## 完成标准

- 标题不一致不再影响 Drive 书籍的进度和统计导入。
- 下载书籍首次打开直接恢复到远端保存位置。
- 统计行为与 Niratan 的同步开关、Merge / Replace 语义一致。
- 所有新增测试、现有测试和 x64 构建通过。
- 不修改第三方子模块或 `native/hoshidicts/`。
