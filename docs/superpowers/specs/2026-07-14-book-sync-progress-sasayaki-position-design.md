# 书籍同步过程提示与 Sasayaki 播放位置恢复设计

## 背景

Hoshi Windows 的书架手动同步命令已经可以执行，但同步期间没有持续可见的 UI，用户只能在操作结束后看到结果通知。Niratan 在书架同步期间使用全书架 `LoadingOverlay("Syncing...")`，使操作状态和输入阻塞都清晰可见。

同步有声书位置还存在独立的数据覆盖问题。Google Drive 导入会把远端 `playbackPosition` 写入 `sasayaki_playback.json`，但 Windows 后续导入音频和字幕时会把播放状态重置为零：

- Reader 的 `LoadSasayakiAsync` 在匹配并加载媒体后调用 `SaveSasayakiPlaybackAsync(0)`；
- 书架的 `SasayakiMatchService.MatchAsync` 保存匹配数据后写入新的空 `SasayakiPlaybackData`。

两条路径都会覆盖已经同步到本地的 `lastPosition`。Niratan 的行为是先加载现有播放 sidecar，导入音频时仅更新音频引用，并用现有 `lastPosition` 初始化播放器。

## 目标

- 导入、导出和自动书籍同步期间显示 Niratan 风格的全书架忙碌遮罩。
- 遮罩持续到所有已经开始的书籍同步都结束，不因并发完成顺序而提前消失。
- 同步成功、失败、无需变化和不可用状态继续使用现有结果通知语义。
- 导入或重新匹配音频与字幕时保留 `sasayaki_playback.json` 中已经同步的播放位置和其他播放偏好。
- 媒体加载完成后，播放器、时间文本和当前字幕 cue 立即投影保存的播放位置。
- 同时覆盖 Reader 内导入与书架“匹配 Sasayaki”两条入口。

## 非目标

- 不为 TTU 书籍 sidecar 同步伪造百分比进度；当前同步服务没有稳定的分阶段进度契约。
- 不改变 Google Drive 文件格式、文件名或远端数据模型。
- 不修改 `native/hoshidicts/`。
- 不重做 Sasayaki 播放器或字幕匹配算法。
- 不改变远端整本书下载卡片现有的独立下载进度 UI。

## 同步过程 UI

### ViewModel 状态

`NovelLibraryPageViewModel` 维护活动书籍同步数量，并暴露只读的 `IsBookSyncing` 投影。每次通过 `SyncNovelCoreAsync` 真正进入同步服务前增加计数，在 `finally` 中减少计数。

状态必须满足：

- 未开始同步时为 `false`；
- 至少一个同步进行时为 `true`；
- 同一本书的重复请求仍由现有 `_activeNovelSyncs` 去重，重复请求不增加计数；
- 不同书籍的同步即使并发完成，也只在最后一个操作退出后恢复为 `false`；
- 不可用检查发生在计数前，因此凭据缺失不会闪烁遮罩；
- 取消和异常都通过 `finally` 清理计数。

计数和属性通知由 ViewModel 管理，XAML 不直接读取服务或并发集合。

### View

`NovelLibraryPage` 在书架内容之上增加一个最高层级的原生 WinUI 遮罩：

- 半透明主题背景；
- 居中的不定进度 `ProgressRing`；
- 本地化文本“正在同步…” / “Syncing...”；
- `AutomationId` 供 UI Automation 和资产测试定位；
- 可见性单向绑定 `ViewModel.IsBookSyncing`；
- 遮罩命中测试，阻止同步期间继续操作书架。

遮罩只覆盖书架页面，不阻塞整个应用窗口之外的系统 UI。成功、失败和“已同步”通知仍由现有 `INotificationService` 处理，不用短生命周期 InfoBar 代替过程状态。

## Sasayaki 播放位置保留

### 匹配服务

`SasayakiMatchService.MatchAsync` 只负责解析、匹配并保存 `sasayaki_match.json`。它不得创建或重置 `sasayaki_playback.json`。

如果播放 sidecar 不存在，现有 `SasayakiSidecarService.LoadPlaybackAsync` 已能返回默认零位置，因此删除空写入不会破坏首次导入行为。如果 sidecar 已存在，其 `LastPosition`、`Delay`、`Rate` 和 `AudioBookmark` 必须原样保留。

### Reader 导入

Reader 的音频/字幕导入流程在替换播放器前读取当前书籍的播放 sidecar。完成字幕匹配和媒体加载后，使用现有 `ApplySasayakiPlayback` 将保存状态应用到：

- `SasayakiPlayer.PositionSeconds`；
- 播放速率；
- 字幕延迟；
- cue 导航当前位置；
- 面板的位置文本和当前 cue。

流程不得再调用 `SaveSasayakiPlaybackAsync(0)`。首次导入因读取到默认状态，仍从零开始；已有远端位置时立即恢复该位置。

应用保存位置仍必须对负数归零。若播放器不能接受超出媒体长度的位置，应使用播放器可用时长进行上限约束；时长尚未可用时保留保存值并在媒体就绪后完成 seek，不能静默写回零覆盖 sidecar。

### 数据所有权

- Google Drive 同步服务拥有远端 `playbackPosition` 到本地 sidecar 的导入。
- Sasayaki 匹配服务拥有匹配数据，不拥有播放状态初始化。
- Reader UI 负责把已持久化的播放状态应用到当前播放器，不重新定义同步冲突规则。

## 错误与取消

- 同步服务异常继续显示现有本地化错误通知，遮罩必须消失。
- 同步取消不显示错误，遮罩必须消失。
- 音频或字幕解析失败时保留同步进来的播放 sidecar，不写入部分重置状态。
- 媒体文件加载失败时保留 sidecar，允许用户重新选择文件后再次恢复。
- 匹配成功但播放位置应用失败时记录 Sasayaki 警告并显示现有失败状态；不得将零位置持久化为恢复手段。

## 测试

### ViewModel

- 同步开始时 `IsBookSyncing` 变为 `true`，完成、失败和取消后恢复为 `false`。
- 两本书并发同步时，第一个完成后仍为 `true`，最后一个完成后才为 `false`。
- 同一本书重复请求不增加活动计数。
- 凭据不可用时不进入忙碌状态。

### View / 资产契约

- XAML 包含本地化的同步遮罩、`ProgressRing`、AutomationId 和 `IsBookSyncing` 绑定。
- 遮罩位于书架内容之上并可命中输入。

### Sasayaki

- `SasayakiMatchService.MatchAsync` 在已有非零播放 sidecar 时只更新 match sidecar，播放状态保持完整不变。
- 首次匹配且没有播放 sidecar 时仍成功，后续读取返回默认零状态。
- Reader 导入路径加载现有播放状态、调用应用逻辑，并且不再保存显式零位置。
- 同步服务现有的远端 audiobook 位置导入测试继续通过。

### 完整验证

1. 运行相关 ViewModel、Sync、Sasayaki 和页面资产测试。
2. 运行完整 x64 构建和测试套件。
3. 启动隔离工作树的精确 x64 可执行文件。
4. 对本地书籍执行手动导入，确认遮罩从服务开始持续到结果通知出现。
5. 使用包含非零 audiobook 位置的同步数据，再导入对应音频和字幕；确认播放器、位置文本和 cue 都恢复到同步位置。
6. 关闭并重新打开 Reader，确认位置仍保持且 sidecar 没有被重置。

## 验收标准

- 用户点击书籍同步后立即看到持续的“正在同步…”过程提示。
- 同步期间书架不接受重复输入，最终结果仍明确可见。
- 远端非零 Sasayaki 播放位置在导入音频/字幕后不被覆盖。
- Reader 首次加载音频后和重新打开后都位于同步进来的位置。
- 所有新旧测试通过，隔离工作树构建和启动成功。
