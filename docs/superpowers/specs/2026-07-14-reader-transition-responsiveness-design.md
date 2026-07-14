# Reader 进出响应性设计

## 背景与根因

当前设置启用了 TTU/Google Drive 自动同步。进入小说时，`NovelReaderPageViewModel.InitializeAsync` 在本地书籍读取之后等待 `ImportOnOpenAsync` 完成，Reader 页面已经切换但尚未开始 EPUB 和 WebView2 初始化，因此只显示空白外壳。退出小说时，`BackToLibrary` 在发送页面切换消息前等待 `PrepareForReaderLifecycleCloseAsync`；该关闭边界会同步刷新书签、统计和远端导出，因此网络等待期间仍停留在 Reader。

运行日志显示 EPUB 解析、WebView2 导航和章节恢复在开始后约一秒内完成；可见空档位于 Reader 初始化日志之前。用户设置同时满足 `EnableSync=true`、`EnableAutoSync=true` 且已有 Google Drive 凭据，和上述阻塞路径一致。

Niratan macOS 的对应行为是先从本地 sidecar 构造并显示 Reader，再用视图任务执行打开时导入；退出时立即结束视图，进度刷新和远端导出在 `onDisappear` 的后台任务中完成。

## 目标

- 点击书籍后先读取本地书籍、书签和 sidecar，尽快渲染 Reader，不等待网络导入。
- 打开时自动导入继续执行；若远端确实导入了更新数据，当前 Reader 安全刷新书签、统计和 Sasayaki 播放位置。
- 点击返回后立即切换到书架，不等待 Google Drive 导出。
- 本地书签和统计关闭检查点、最新 Sasayaki 位置以及自动导出仍可靠完成。
- 页面退出或切换书籍时，旧 Reader 的打开同步不得修改已经离开的页面。
- 同步失败保持被包含并记录，不把网络异常变成 Reader 导航失败。

## 非目标

- 不改变 TTU 冲突解决规则、远端文件格式或 Google Drive API。
- 不取消自动同步，也不以固定超时静默丢弃同步。
- 不重写 EPUB 渲染、WebView bridge、统计模型或 Sasayaki 匹配算法。
- 不修改 `native/hoshidicts/`。

## 进入 Reader

`NovelReaderPageViewModel.InitializeAsync` 只执行打开 Reader 所需的本地步骤：加载书籍、激活书籍 profile、更新最后打开时间和通知标题。网络导入从该方法中拆出为 `SyncOnOpenAsync`。

`NovelReaderPage.OnNavigatedTo` 的顺序为：

1. 完成本地 ViewModel 初始化；
2. 解析已导入 EPUB、加载本地 sidecar 并启动首章 WebView2 渲染；
3. 为本次页面实例创建打开同步 cancellation token；
4. fire-and-observe `SyncOnOpenAsync`，但不阻塞 `OnNavigatedTo` 完成。

打开同步返回 `Imported` 时，ViewModel 从书籍服务重新加载合并后的书籍状态。页面确认自己仍处于当前导航实例后：

- 将章节与章节内进度切换到导入书签；
- 重载统计投影；
- 读取并应用最新 `sasayaki_playback.json` 到现有播放器；
- 导航 WebView2 到导入后的章节位置。

这与 macOS `syncOnOpenIfNeeded` 在本地 Reader 已建立后执行并 `reloadBookmark` 的语义一致。没有导入、同步关闭、凭据缺失、取消或被包含的同步异常都不触发 Reader 重载。

## 退出 Reader

`BackToLibrary` 不再等待关闭检查点，而是立即发送 `SwitchAppModeMessage(AppMode.Navigation)`。`NovelReaderPage.OnNavigatedFrom` 已拥有脱离页面后的 `CompleteReaderLifecycleCloseAfterDetachAsync`，它继续负责：

- 结算尚未完成的章节导航；
- 关闭新的位置写入；
- 保存最新书签和统计；
- 安排并刷新自动导出；
- 在所有关闭工作完成后取消本 Reader 专属的自动同步协调器。

页面离开时取消打开同步 token，避免远端导入完成后再操作旧 WebView2。关闭检查点使用自己的不可取消持久化边界，因此取消打开同步不会丢失本地进度或退出导出。

字典 popup WebView2 的销毁不参与网络关闭边界；若实际验证显示同步退出后仍有明显切换延迟，则把 popup 的同步 `Close()` 调度到导航完成后的低优先级 UI 清理，不改变 popup 数据和视觉行为。

## 并发与生命周期

- 每个 `NovelReaderPage` 只允许一个打开同步任务。
- `OnNavigatedFrom` 先使页面实例失效并取消打开同步，再启动脱离页面关闭。
- 打开同步完成后必须同时检查 token 和页面实例状态，才能刷新 Reader。
- ViewModel 的关闭写入屏障保持现有行为，退出后产生的迟到位置写入不能越过关闭边界。
- `IReaderAutoSyncCoordinator` 继续按 Reader ViewModel transient 生命周期创建；`Cancel()` 的永久取消语义保持不变。

## 错误处理与可观测性

- 打开同步异常继续由 `ReaderAutoSyncCoordinator` 包含并记录，不阻止本地 Reader。
- 本地初始化失败仍使用现有错误通知，并且不启动打开同步。
- 导入后刷新失败记录 Reader 警告，保留已经可用的本地页面，不清空内容。
- 关闭检查点失败继续记录 `[Reader] Failed detached lifecycle close checkpoint`。
- 增加进入本地就绪、打开同步完成和脱离关闭完成的耗时日志，验证网络等待已从可见导航路径移除。

## 测试

### ViewModel

- `InitializeAsync` 在打开导入任务未完成时仍能完成本地初始化，且不调用自动导入。
- `SyncOnOpenAsync` 导入成功后重新加载 `CurrentBook` 并返回已导入状态。
- 未导入、取消和同步失败不替换当前书籍。
- `BackToLibrary` 立即发送模式切换，不等待关闭任务。
- 脱离页面关闭仍保存、刷新自动导出并调用协调器取消。

### 页面生命周期资产契约

- `OnNavigatedTo` 在 `InitializeReaderAsync` 之后启动非阻塞打开同步。
- `OnNavigatedFrom` 取消打开同步并继续调用脱离页面关闭。
- 导入后刷新包含书签章节、进度、统计和 Sasayaki 播放状态。
- 返回命令不直接等待 `PrepareForReaderLifecycleCloseAsync`。

### 运行验证

1. 自动同步开启且凭据有效时，从书架打开测试 EPUB；确认本地 Reader 先出现，Google Drive 导入不再留下空白外壳。
2. 远端进度较新时，确认后台导入完成后 Reader 定位到远端章节和进度。
3. 点击返回，确认书架立即出现；等待后台关闭完成后确认书架进度、统计和远端数据正确。
4. 快速打开、返回、再打开另一册，确认旧页面同步不会覆盖新 Reader。
5. 运行完整 x64 测试和构建，并从当前 worktree 的精确输出路径启动验证。

## 验收标准

- 可见的进入路径不等待 `ImportOnOpenAsync`。
- 可见的退出路径不等待 `FlushAsync`。
- 自动同步导入后仍应用远端书签、统计和 Sasayaki 播放位置。
- 退出后本地持久化和自动导出仍完成，且失败可诊断。
- 快速进出不会产生旧页面回写、未观察异常或崩溃。
- 定向测试、完整测试和 x64 构建通过，实机进出小说验证通过。
