# Changelog

## Reader 缺少 Niratan 歌词模式

**原因**：Windows 端已有 Sasayaki 音频、SRT 匹配和歌词模式快捷键，但 `L` 只会跳回当前正文，Reader 没有 Niratan 的沉浸歌词层、逐句进度、横竖排、遮罩与歌词查词入口。

**解决**：新增 Win2D 原生歌词层，在音频与有效匹配就绪后提供顶部按钮、Sasayaki 菜单和 `L` 入口；复用现有播放器、字典弹窗、相邻 cue 制卡上下文与统计会话，支持当前句进度、前后句、播放控制、横竖排和播放态柔化遮罩。自然跨句才累计阅读字符，手动 seek 重置基线，退出后正文回到当前 cue。

---

## 词典导入文件选择器无法多选

**原因**：Windows 的两个词典导入口都调用了单文件选择 API，导入流程也只处理一个 ZIP，与 Niratan 支持一次选择多个词典的行为不一致。

**解决**：词典文件选择器改为支持多选，并按选择顺序串行导入，避免并发调用 native 导入；单个 ZIP 失败时继续处理其余文件，最后统一刷新词典列表并汇总成功和失败结果。

---

## Popup 内链跳转后缺少历史操作栏且顶部留白过大

**原因**：Windows 弹窗只按“显示操作栏”设置决定后退/前进控件是否可见，内部链接虽然已写入 WebView 历史栈，却不会在关闭常驻操作栏时显出导航入口。操作栏、Sasayaki 控件和关闭状态的制卡提示还各占一行，导致内容顶部被多次留白。

**解决**：对齐 Niratan，在内部链接产生后退或前进历史时自动显示后退、前进和关闭控件；有 Sasayaki 音频时将它们与播放控件合并到同一条 36px 紧凑栏。关闭状态的制卡提示改为覆盖内容，不再参与纵向布局。

---

## 设置页备份入口禁用且无法迁移收藏

**原因**：Windows 设置导航只有禁用的 Backup 占位，没有 Niratan 的书籍/词典 `.hoshi` 备份恢复，也缺少 ッツ Backup 兼容导入导出；直接替换词典目录还会与 hoshidicts 当前 session 的文件句柄和 Profile 独立配置冲突。

**解决**：新增原生 Backup 设置页与 `BackupService`，对齐书籍、词典、ッツ Backup 三个分区、文件命名、覆盖语义和进度提示。书籍与词典 `.hoshi` 使用安全解包、同卷 replacement 和失败回滚；词典归档携带 `.hoshi-profiles`，恢复时覆盖收藏、合并 Profile 并立即重建 native query。补齐 EPUB → TTU `bookdata` 转换和 TTU ZIP 导入，已有原始书名只覆盖统计与阅读进度，新书正常加入收藏。

---

## 视频管理生成缩略图时弹出 mpv 窗口

**原因**：后台缩略图提取器初始化独立 libmpv 实例时没有指定无窗口视频输出，libmpv 会为解码后的画面自动创建原生播放窗口。

**解决**：缩略图实例固定使用 `vo=null`，并启用 `screenshot-sw=yes` 通过软件转换保存解码帧；视频管理可继续生成和缓存缩略图，同时不再创建可见 mpv 窗口。

---

## Sasayaki 配准文件无法直接在 Niratan 与 Hoshi 客户端间复用

**原因**：Windows 端曾把书籍 ID、本机音频/SRT 绝对路径、完整 cue 表和 `cueIndex` 写进自有 schema v3；Niratan 与 Hoshi 使用的则是每条 match 自包含音频时间、文本和 EPUB 字符范围的 `matches + unmatched` 结构，服务端无法把同一配准文件原样下发给各客户端。

**解决**：Windows 的 `sasayaki_match.json` 改为 Niratan/Hoshi portable 结构，本机路径拆到 `sasayaki_source.json`，播放状态继续独立保存在 `sasayaki_playback.json`。匹配器、Reader 导航、高亮和音频截取统一直接消费 portable match；读取旧 Windows v3 时自动合并 cue 数据、迁移来源路径并保留播放进度。

---

## 查词制卡后无法在 Anki 中定位新笔记

**原因**：Windows 端将 AnkiConnect `addNote` 的返回值压缩为布尔成功状态，弹窗拿不到新增 note ID，因此无法提供 Niratan 词条内的 Anki 跳转操作。

**解决**：保留 `addNote` 返回的 note ID；制卡成功后在对应词条显示放大镜按钮，点击通过受校验的 WebView2 消息调用 AnkiConnect `guiBrowse` 并以 `nid:<ID>` 精确打开新笔记。普通制卡与选择上下文制卡共用该行为，Reader、词典页、视频和全局查词弹窗同步生效。

---

## 视频窗口打开慢、控制栏偏移且窗口比例不跟随片源

**原因**：视频窗口初始化时先等待字幕 WebView2，再创建 libmpv 宿主；libmpv 事件线程还在持有播放器全局锁时最多等待 50ms，使连续属性命令累积延迟。打开路径残留测试用强制最大化，窗口也没有读取 mpv 的显示宽高和旋转信息。原生视频子窗口与顶层控制栏 Popup 分别按不同坐标和 DPI 规则定位，侧栏或缩放变化后边界可能错开。

**解决**：先创建并启动 libmpv，源提交后立即解除暂停，字幕 WebView2 延后到 `file-loaded` 后初始化；事件循环改为 mpv `wakeup` 驱动且不再持锁等待。窗口读取 `dwidth` / `dheight` 与视频旋转，按 Niratan 的视频区加侧栏模型自动适配工作区，并在 Win32 `WM_SIZING` 中保持片源显示比例。控制栏 Popup 和原生视频宿主统一使用按当前 XamlRoot DPI 对齐的 VideoSurface 边界，打开、侧栏切换和窗口缩放时保持同宽同高、底边一致。

---

## Anki 视频媒体与 EPUB 封面缺失

**原因**：视频和 YouTube 制卡直写 Anki 媒体目录时使用后台 fire-and-forget；卡片字段先得到标签，截图和音频文件尚未生成，失败异常也被吞掉。截图失败未参与制卡前校验。已保存的小说字段默认值还会覆盖视频预设的截图和音频占位符。EPUB 封面虽然上传到 Anki，但未记录上传后的文件名，`{book-cover}` 最终渲染成应用私有目录的本地路径。词典弹窗还把空格分隔的 Yomitan 词性规则字符串当数组调用 `.some()`，部分词条会在进入原生制卡链路前失败。AnkiConnect 关闭空闲 keep-alive 连接时，复用连接上的首次预检也可能被误报为不可用。

**解决**：直写视频媒体改为等待截图和音频全部完成，验证目标文件非空后才返回 Anki 标签；取消继续传播，任一必需媒体失败都会显示错误并阻止提交。独立 libmpv 截图改为等待 `playback-restart` 首帧可用事件，不再在 `file-loaded` 时过早执行。字段自动填充会把“另一内容类型的原始默认值”切换为当前预设，同时保留真正的用户自定义映射。新增 `CoverTag`，封面上传成功后转换为 `<img src="...">`，模板不再使用或暴露本地路径。`popup.js` 同时兼容规则数组和空格分隔字符串，避免音调分类阻断制卡。AnkiConnect 仅在本地传输异常时用新请求体重试一次，不重试应用级错误或重复提交。

---

## 字幕出现时侧边栏按钮失效且视频首帧过慢

**原因**：透明字幕选择画布的层级高于底部控制栏，字幕移动到底部后会拦截侧边栏按钮的点击。打开视频时又在解除暂停前等待外部字幕解析、章节读取和最多 1.8 秒的字幕轨道轮询，导致有字幕的视频尤其明显地延迟出画面。

**解决**：将底部控制栏提升到字幕选择层之上，保留字幕区域内的点选查词，同时保证重叠区域的播放器按钮优先接收输入。外部字幕解析移到线程池；视频源和必要播放属性就绪后立即开始渲染，章节、轨道、交互字幕和侧边栏数据在首帧之后异步补齐。

---

## 播放器控制栏入口冗余且操作卡顿

**原因**：播放器底部额外放置“打开 YouTube 链接”按钮，与视频库的添加入口重复且链环图标语义不清。播放计时器还会每 200ms 清空并重建全部章节行、执行六次 viewport 属性读取；鼠标在视频表面移动时也会为每个事件重启自动隐藏计时器，持续制造 UI 线程布局和集合通知。

**解决**：删除播放器控制栏的 YouTube 入口，只保留视频库添加入口。章节行只在章节实际切换时重建，viewport 指标降频到每秒刷新一次，控制栏自动隐藏计时器按 100ms 合并鼠标移动事件，并避免重复写入相同 `Visibility`。

---

## 对齐 Niratan 的实验性 YouTube 播放

**原因**：Windows 视频模块只能导入本地文件，缺少 Niratan 的 YouTube 添加/直接打开、画质、发布者字幕、进度恢复和制卡链路；官方 IFrame/Data API 又无法提供 libmpv 所需的可选择流和现有交互式字幕能力。初版发布者字幕还在未打包 WinUI 进程中调用了仅打包应用可用的 `ApplicationData.Current.TemporaryFolder`，导致所有语言在选中后、实际下载前就失败。

**解决**：固定引入纯 .NET `YoutubeExplode 6.6.0`，通过严格 URL 白名单解析匿名公开视频，在内存中缓存最高 1080p 的分离/合并流与发布者字幕。扩展 libmpv typed request、远程播放器会话、失败刷新和 muxed 降级，复用现有字幕查词、截图、transcript 与音频制卡；新增稳定远程身份和 Migration 013，使资料库、进度及字幕语言可恢复。YouTube `t` / `start` 时间参数会转换为初始播放位置，同时修复解析对话框、播放器切换来源和发布者字幕快速切换时的取消竞态；字幕下拉框只标记已下载且成功解析的活动轨道，失败或过期操作不会再留下虚假的选中状态。字幕文件改存到 `%LOCALAPPDATA%\Niratan\Temp` 的应用管理目录，不依赖包身份；语言选择由成功加载后的状态显式回写，避免单向绑定覆盖用户刚选中的项目。功能明确标注非官方接口的实验性与易失性，全链路不使用 yt-dlp、youtube-dl、Deno、Node、下载型 helper 或子进程。

---

## Windows 视频新增可选 Anime4K 在线超分

**原因**：Windows 视频播放器已有 libmpv GPU 渲染链路，但没有可选的实时动画超分；直接把 `glsl-shaders-append` 当 property 写入还会被 libmpv 拒绝，形成界面已开启但实际未加载的静默失败。

**解决**：在播放器侧边栏“视频增强”中新增 Anime4K Fast / High Quality 会话预设，从固定 `v4.0.1` revision 在线下载，使用 GitHub Raw 与 jsDelivr 回退源并逐文件校验固定 SHA-256，成功后原子落盘到应用私有目录。每次打开视频均默认关闭；选择已缓存预设时立即应用，只有文件尚未下载时才显示“下载”按钮，完成后自动应用。播放侧通过 `change-list glsl-shaders clr/append` 按官方顺序应用，下载失败或文件缺失时保持关闭。该功能是明确记录的 Windows 平台可选偏差，不改变 Niratan 默认画质行为。

---

## 全局查词偏离高亮选区、宿主边缘外露且子 popup 被裁切

**原因**：Windows 端只把热键触发时的鼠标位置传给 popup，丢弃了 UI Automation 能提供的选区屏幕矩形；离屏测量窗口又固定为 720×560 物理像素，并在 WebView `contentReady` 提交前同步读取 popup bounds，导致键盘触发、多显示器和高 DPI 场景定位漂移，首次弹窗还可能留在离屏 staging。后来虽将宿主缩成根 popup 的精确尺寸，但嵌套 popup 仍由同一个 Canvas 创建，因此所有子层都会被根 HWND 裁切；窗口 backdrop/非客户区也会在根 popup 顶边漏出细横条。窗口同时使用 `WS_EX_NOACTIVATE` 和失活即关闭策略，内容即使完成渲染也可能在最终显示前被 Deactivated 事件关闭；无词条时则静默退出。

**解决**：对齐 Niratan `SelectionSnapshot`，全局查词优先传递 UI Automation 选区矩形，Win32 文本框回退传递 caret 屏幕矩形，最后才使用鼠标点；按目标显示器 DPI 和工作区计算横排上下定位。全局查词的根/子 popup 改为每层一个独立原生 tool-window HWND；child 的本地选区矩形先补入 WebView 在父 popup 内的实际原点，再换算为屏幕坐标。每层水平以选区中心对齐并做工作区夹取，垂直只允许保留固定间距放在选区正上或正下，随后按真实内容尺寸做精确圆角裁切，因此可真正越出父窗口边界，顶边横条、透明画布、锚点覆盖和共享宿主裁切均不会出现。热键注册时预热两个空窗口，关闭或替换的 popup 会返回池中复用其 HWND 和 WebView2，快速连续查词不再反复冷启动。中央栈协调器对齐 Niratan：点击父层只关闭其后的子层，点击全部 popup 之外清空整栈。外部子窗口模式仅在全局查词启用，小说和视频仍保留原有窗口内嵌套查词。窗口继续使用 no-activate/topmost、`DWMWA_COLOR_NONE`；无词条时显示精确裁切的 3 秒状态浮层。

## 全局查词快捷键无法在统一编辑器中修改

**原因**：Windows 快捷键注册表漏掉了 Niratan 的 `global.lookupSelectedText` action，全局查词设置又单独保存字符串，并且 Win32 registrar 只接受写死的 `Ctrl+Alt+D`。

**解决**：将“查询选中文本”加入统一“键盘快捷键”的全局类别，以 `ShortcutConfiguration` 作为唯一 binding 来源；默认仍为 `Ctrl+Alt+D`。Win32 registrar 现在支持编辑器可表达的 Ctrl/Shift/Alt/Win 与字母、数字、功能键等组合，binding 修改或重置后立即注销并重新注册，无需重启应用；全局查词开关仍独立保存在功能设置中。

---

## Profile 设置页与 Niratan 结构不一致

**原因**：Windows Profile 页同时提供 Active Profile 下拉框和重复的 “Installed” profile 列表，新建 profile 只创建空目录，不会像 Niratan 一样继承当前 profile 的词典顺序/开关、阅读外观和 Anki 配置。字典设置页又不突出当前激活 profile，导致用户在视频 profile 中全开词典后，全局查词仍按另一个 global profile 查询。

**解决**：移除自创的 “Installed” profile 分区，在 Active Profile 卡片中直接列出并切换全部 profile，并补齐中英文界面、内置 profile 名称、语言名称和状态提示；说明文字对齐 Niratan 的 profile 所有权边界。新建 profile 复制当前 profile 的 `dictionary-settings.json`、`reader-settings.json`、`anki-settings.json` 和 `dictionaries/dictionary-config.json`，随后再激活新 profile。主导航、Reader 与视频窗口在重新成为活动窗口时按各自上下文重新激活 profile，确保设置页编辑 global profile，Reader/视频查词恢复其内容 profile。

---

## 小说制卡状态错误且小说、视频无法选择上下文

**原因**：Windows 弹窗将 Anki 预检、重复和写入结果压缩为单一 `bool`，首次渲染也不检查词条是否已存在；小说只传当前句，视频媒体采集固定使用当前字幕 cue，因此无法像 Niratan 一样在制卡前扩展前后句。

**解决**：按 Niratan 区分成功、重复、失败结果并显示对应弹窗提示，词条渲染时异步刷新已制卡/未制卡按钮状态；新增小说正文句子和视频字幕 cue 的上下文选择入口，确认后重算句子偏移、相邻字幕、视频起止时间以及音频采集范围，并复用原有 AnkiConnect 与媒体管线。按钮改为与 Niratan 一致的原生内联点击语义和 `plus.square` / `rectangle.stack.badge.plus` 图标，并按 render generation 从已提交或正在提交的上下文取值，避免 popup 首帧可点击时上下文仍为空、重复检查回调被误判为过期而造成面板不显示或按钮永久禁用。视频端把选择器挂入 `BottomChromePopup` 内的模态宿主并使用 in-place placement，绕开 libmpv 子窗口的 HWND airspace，保证上下文面板始终覆盖在视频上方。

---

## 进入和退出小说会停在 Reader 页面

**原因**：进入 Reader 时在本地 EPUB 初始化前等待 Google Drive 自动导入，退出时又在发送书架导航消息前等待书签、统计和远端导出全部刷新；网络请求因此直接位于两个可见导航路径上。Sasayaki 本地加载与后台导入并行时还可能让旧播放位置晚于远端位置应用。

**解决**：先从本地书籍和 sidecar 初始化并显示 Reader，再以页面专属、可取消任务执行打开同步；导入成功后安全刷新书签、统计和 Sasayaki 播放位置。返回命令立即切换书架，现有脱离页面关闭边界继续在后台保存并导出；远端播放位置应用会等待本地播放器初始化完成。

---

## 书架同步菜单图标冗余且长书名撑高卡片

**原因**：书籍右键同步菜单为同步、导入和导出动作设置了图标；本地与远端书籍标题未限制行数，长标题会继续换行并改变卡片高度。

**解决**：移除同步菜单及其导入、导出子项的图标；本地与远端书名统一最多显示两行，超出内容直接裁切。

---

## 统计设置页多出 Niratan 不提供的阅读目标配置

**原因**：Windows 将 Dashboard 使用的每日/每周目标数据同时暴露到了统计设置页，与 Niratan 的设置结构不一致。

**解决**：从统计设置页移除目标类型、字数、时长和每周天数控件及其编辑绑定；保留 Dashboard 的目标模型和已有数据，保存启用、自动开始或同步设置时不再覆盖目标值。

---

## Sasayaki 高亮错位且前后句跳转跨越大量音频

**原因**：Windows 匹配器遗漏了 Niratan 对短 `＊` cue 的过滤，并只用第一条短标题确定初始位置；一两个常见字会把单调匹配游标推进到后文，导致后续本可精确匹配的 SRT 句子大量丢失。播放时间本身正确，但高亮和 `[` / `]` 只能落到相隔很远的残余匹配。

**解决**：按 Niratan 使用前 15 条中至少 6 字的正文锚点确定起点，跳过过滤后不足 5 字的 `＊` cue；检测到旧 sidecar 曾匹配这类 cue 时自动用原音频/SRT 重建匹配，同时保留现有播放 sidecar。

---

## 书籍同步缺少过程提示且 Sasayaki 位置被导入流程重置

**原因**：
- 书籍同步只在结束后显示通知，执行期间没有绑定到活动同步状态的持续 UI。
- Reader 导入音频/字幕后显式保存零位置，书架匹配服务也创建空播放 sidecar，覆盖了 Google Drive 导入的 `lastPosition`；即使位置成功传给 `MediaPlayer`，旧实现也会立即清除 pending seek，使异步跳转落稳前的旧位置采样再次回写 sidecar。

**解决**：
- 按 Niratan 在任意书籍同步期间显示阻塞式“正在同步…”遮罩，并以活动同步集合保证并发完成时不会提前隐藏。
- Sasayaki 匹配只保存匹配数据；Reader 加载媒体后读取并应用现有播放 sidecar，保留同步进来的位置、延迟、速率和 cue；对齐 Niratan 保留 pending seek，并在播放器实际落到目标 ±0.75 秒前屏蔽旧位置回调和持久化。

---

## 同步设置与书架右键未对齐 Niratan

**原因**：
- ッツ Sync 设置页只暴露全局、方向和上传书籍选项；连接成功后还会主动清空 Client Secret，重新进入页面也不会从 Windows 凭据管理器回读。
- 清缓存只清除了 Drive 文件夹 ID，未清理封面文件；本地书籍右键菜单没有 Niratan 的自动 Sync 或手动 Import/Export 入口。

**解决**：
- 按 Niratan 重组 Syncing、客户端凭据、Connection、Behaviour、Data 分组，统一投影书籍、统计与 Sasayaki 同步偏好；Client Secret 仅保存在 Windows 凭据管理器，并在页面进入时安全回读。
- 清缓存同时删除文件夹 ID 与封面缓存；书架右键按 Auto/Manual 模式显示 Sync 或 Import/Export，并由 ViewModel 调用现有 `ITtuSyncService`。

---

## Dashboard 今日圆环偏小且本周卡片过高

**原因**：
- 今日目标仅放大了宿主 Grid；`ProgressRing` 仍被 WinUI 默认样式固定为 32×32，因此视觉尺寸没有变化。
- 宽屏三列布局中，本周卡片与更高的排行卡共用同一 Grid 行；Border 默认纵向拉伸，使本周卡片内部出现大块空白。

**解决**：
- 将宿主与 `ProgressRing` 控件本身都固定为 118×118 effective pixels，对齐 Niratan 的视觉层级。
- 让本周卡片顶部对齐并按自身内容高度呈现，保留指标、七日状态和自适应列布局。

---

## Dashboard 阅读日历热力图间距过大

**原因**：
- 阅读日历没有约束 `ListViewItem` 的默认最小尺寸，也没有固定 `ItemsWrapGrid` 的单元槽位；WinUI 的触控项占位把小方块扩展到了稀疏的行列间距。

**解决**：
- 对齐 Niratan，将日期方块固定为 12×12 effective pixels、可见间距固定为 4 pixels，并使用 16×16 的七行网格槽位。
- 清除列表项的默认最小宽高、边距和内边距；保留最近一年横向滚动、选中范围、可访问文本和日期详情联动。

---

## Dashboard 趋势图缺少坐标数值且历史跨度不可拖动

**原因**：
- 趋势图只绘制归一化网格和柱/线，没有可见的横纵坐标数值，切换指标后难以判断绝对量级。
- 图表只有最小高度，日期范围依赖锚点日期选择器，不能直接拖动浏览历史窗口。

**解决**：
- 趋势图固定为 260 effective pixels，纵轴显示五级指标刻度，横轴显示当前窗口首、中、末标签；字符、时长和速度分别使用对应单位。
- 删除锚点日期控件，保留年/月/周/日跨度，并用常驻可见、按整数吸附的横向范围拖动条在最近一年内移动日历对齐的窗口；范围汇总、趋势、日历、速度、排行和书架对比同步更新。
- 滚动值吸附到整数窗口，切换跨度默认落到最新窗口，点击 Calendar 日期会移动到包含该日期的窗口。

---

## Google Drive 统计已下载但 Dashboard 无数据并崩溃

**原因**：
- Dashboard 派生缓存中的 `NovelStatisticsBookContribution` 同时有主构造函数和便利构造函数，却没有明确 JSON 构造函数；包含真实书籍贡献的缓存重载会抛出 `NotSupportedException`。
- 缓存读取没有把模型不兼容视为可重建的派生缓存失效，异常直接到达 WinUI 未处理异常边界。

**解决**：
- 明确统计贡献模型的 JSON 主构造函数，并用非空 `bookContributions` 覆盖磁盘往返。
- Dashboard 缓存遇到不支持的模型时只删除 `statistics_dashboard_cache_v1.json`，随后从各书 `statistics.json` 重建；原始统计、书签和 EPUB 不受影响。

---

## Google Drive 下载书籍未恢复进度且统计未导入

**原因**：
- 新书导入完成后按 EPUB 元数据标题重新查询 Drive 文件夹；当远端目录标题与 EPUB 标题不一致时，会命中错误或空目录，丢失用户所选书籍的 progress/statistics 文件快照。
- 普通 EPUB 导入返回时尚未生成 `bookinfo.json`，远端全书字符位置无法在首次打开前换算为正确 spine 章节。

**解决**：
- Drive 新书导入把已选择的远端文件快照直接传给同步服务；普通手动/自动同步仍保持按书名发现目录。
- EPUB 导入阶段复用 Reader 字符过滤规则生成 `bookinfo.json`，再导入 bookmark；统计仍严格受统计同步开关及 Merge/Replace 模式控制。
- 新增标题不一致、首次跨章定位、章节边界、统计开关、sidecar 导入失败清理等回归测试。

---

## Reader 翻页保存偶发提示路径访问被拒绝

**原因**：
- Reader 在同章翻页后原子覆盖 `bookmark.json`，自动同步可能同时读取同一 sidecar；JSON 读句柄只共享读取，而 Windows 的 `File.Move(..., overwrite: true)` 不能替换仍被读取的目标，因此快速翻页时偶发 `UnauthorizedAccessException`。

**解决**：
- sidecar 读取允许删除共享，使读取者继续看到打开时的旧文件；已有目标改用同卷 `File.Replace` 原子替换，并以独立临时备份保证替换成功后可清理。
- 新增“同步读取未结束时保存 bookmark”回归测试，验证旧读取正常完成、新读取取得新内容且不遗留临时文件。

---

## Reader typed host command 被错误信任检查拦截，所有翻页失效

**原因**：
- bridge 安全隔离把 native 命令改为 `CoreWebView2.PostWebMessageAsJson` 后，错误地用 DOM `MessageEvent.isTrusted == true` 判断消息来源；真实 WebView2 host message 不以该标志作为来源契约，因此 `navigatePage`、滚轮开关和 Sasayaki 命令都在进入私有 bridge handler 时被丢弃。
- Node runtime harness 人为给 host message 设置了 `isTrusted: true`，与 WebView2 事件形态不一致，导致回归未被测试捕获。

**解决**：
- 按 WebView2 契约校验 `event.source === window.chrome.webview`，继续保持 bridge IIFE 私有化和 typed payload 强校验；章节脚本无法取得 handler，也无法用错误 source 触发位置命令。
- runtime harness 改为使用真实的 host source 身份且不依赖 `isTrusted`，同时保留 renderer 侧错误 source 被拒绝的回归断言。

---

## Reader 反向跨章曾闪过错误进度并重复结算

**原因**：
- 相邻跨章、普通程序化跳转、Page 可见状态和 lifecycle writer 曾由多个 tracker/coordinator 与可变字段分别拥有；从 B 第一页返回 A 时，native 会先发布近似端点或旧候选位置，再等待 WebView 算出 A 的最后一页，因此出现临时 `1.0`/100%、二次进度更新和 baseline/bookmark 竞争。
- bridge error、关闭/后台与 Sasayaki 异步回调没有共享 point-of-no-return；目的地写入开始后仍可能被源位置恢复或另一条位置写入穿插，迟到的同章 render callback 也可能误认成当前完成。
- Reader 退出曾先关闭 writer admission 而没有原子 settlement；Pause/Stop 还会在 destination commit 前捕获 source 位置。history 在 reservation 时提前 push/pop，重复 `restoreCompleted` 则把“已由第一条接纳”误判成 terminal failure。
- EPUB 章节通过 virtual host 直接提供，原始 script、inline handler 和 script-scheme URL 没有清理；native-only 翻页授权函数还暴露在 `window`，同源伪造 bridge 消息缺少当前 render attempt source 绑定。

**解决**：
- 使用单一 `ReaderNavigationTransactionCoordinator` 持有不可变源/目的地、generation 和独立 `renderAttemptId`；目的章节隐藏分页，WebView 返回最终 page-aligned progress 后才按“保存 bookmark → 重置 baseline → 原子发布 → reveal”完成一次提交，旧 tracker/coordinator 与候选字段全部移除。
- `Rendering` 失败或 lifecycle 取消恢复源位置；`Committing` 进入不可取消的持久化边界，lifecycle 等待并按 durable 结果恢复目的地或源位置。bridge error、重复/过期 completion 和 recovery 都按事务身份收敛到一个终态。
- 事务存续期间统一阻止翻页、目录/搜索/链接/history 与 Sasayaki auto-scroll/load/progress/save 等位置突变；播放 UI 和非位置高亮仍可继续，异步回调在 await 后再次校验 gate。
- lifecycle 在同一 writer gate 内先发起 settlement 再关闭 admission；Resolve 原子拒绝 barrier 后的 destination writer。Pause/Stop 等待 settlement snapshot 后再串入 writer，history 只在 destination settlement 提交；重复/过期完成返回显式 `Ignored`，Page 不再进入 recovery。
- 章节内容按 EPUB manifest media type 识别为 HTML，在 Service 层按 local name 和 namespace URI 移除可执行元素、inline handler、`xml:base` 与危险 URL（包括别名前缀的 XLink/SVG/MathML）；清洗失败时 fail-closed，host 响应注入严格 no-script CSP，并拒绝外部、frame、new-window 与权限请求。所有 WebMessage 绑定当前 render attempt source；完整 bridge、分页引擎与 Sasayaki auto-scroll 收进 IIFE，native 翻页、滚轮开关和 Sasayaki 位置命令只通过严格校验的 typed host message 进入 closure，synthetic message event 不能调用位置 API。

---

## Reader 同章翻页未结算统计且最终同步可能混用位置

**原因**：
- Web bridge 的 `scrolled` 只表示同章滚动成功，native 曾把它当成命令已处理而不是位置已 `moved`；只有章节边界进入统计 checkpoint，导致同章翻页未及时更新字符、Session/Today 和 sidecar。
- bookmark、statistics 与延迟同步曾读取可变的当前进度；writer 排队或 Close/Background final flush 期间位置从 X 变为 Y 时，可能把不同时间点的数据写入同一次提交，或在最后 export 完成前取消 coordinator。

**解决**：
- 用 `ReaderPageNavigationEvent` 明确传递 `Scrolled`/`Limit`、方向与最终进度，再由 `ReaderPageNavigationOutcome` 统一表达 `DidMove`、同章移动或相邻章节；真实同章移动立即保存 bookmark 并写一次 typed reading checkpoint，程序化跳转仍保持独立事务。
- Reader writer 按 admission 顺序串行，并为 bookmark、statistics 和 sync 捕获同一份进度 snapshot；自动同步 coordinator 负责 open import、30 秒 debounce、single-flight follow-up，以及 Close/Background 的可等待 final flush，Close 只在最终 export 后取消。

---

## 小说书架分区、云端导入与统计入口回归

**原因**：
- 本地书卡依赖 code-behind 点击事件，分区模板重构后未归档书卡没有稳定绑定打开命令；Reading 仍被当作可选 rail，且各分区使用单行横向布局。
- Google Drive 列表只展示占位图，没有复用带鉴权的缩略图请求与本地缓存；页面级取消源在任一本书导入后刷新目录时会取消其他导入，因此无法并行。
- Dashboard 的 XAML AdaptiveTrigger 与 code-behind 同时修改同一组 Grid 属性，SizeChanged 又通过 DispatcherQueue 重入；统计缓存键还会在 UI 线程同步扫描所有 sidecar 文件时间。
- Dashboard 首次实例化还引用了 WinUI 3 中不存在的 `AccentStrokeColorDefaultBrush`；磁盘缓存中的 snapshot 有多个构造函数但没有指定 JSON 构造函数，重启后读取缓存会抛出未处理异常。两者都会表现为点击统计后卡住或进程退出。

**解决**：
- 所有本地书卡显式绑定 `OpenNovelCommand`，Reading 从未读完且有进度的书籍派生；Reading、自定义书架、Google Drive、Unshelved 统一为可换行的自适应多行分区。
- Google Drive 封面使用鉴权缩略图、格式校验、原子写入和磁盘缓存；导入改为每书独立状态与页面生命周期取消，最多 3 本并行，排队、下载、失败重试互不影响。
- 统计入口先让出 UI 帧，缓存键扫描移到后台线程；Dashboard 只保留 code-behind 单一布局所有者，并仅在跨越 840/1260 effective-pixel 断点时重排。
- 日历选区改用有效的 accent brush，并显式标注 snapshot 的 JSON 构造函数；新增“新缓存实例从磁盘重载”测试，覆盖应用重启后的真实缓存读取。

---

## Google Drive OAuth 回调显示成功但连接失败

**原因**：
- loopback 已收到授权码后就向浏览器显示 `Google Drive connected`，但此时 token 交换尚未完成。
- 桌面 OAuth 客户端要求 token 请求携带 `client_secret`；Windows 实现只接收和发送 `client_id`，首次交换返回 `client_secret is missing`，刷新路径也缺少同一参数。

**解决**：
- 设置页使用 `PasswordBox` 接收客户端密钥，成功授权后将其与 token 一起存入 Windows Credential Manager，不写入普通设置。
- 授权码交换和 refresh token 请求都发送客户端密钥，并兼容读取不含密钥的旧凭据。
- loopback 页面只提示已收到授权，最终连接成功状态由 token 交换和凭据保存完成后的 WinUI 页面显示。

---

## 视频 popup 显示后无法继续查字幕

**原因**：
- 视频 popup 的透明外层和 overlay Canvas 覆盖字幕并参与命中，popup 显示后空白区域也会截获单击和 Shift hover。
- 根 popup 替换曾在新首屏 ready 前隐藏或清空已显示内容；渲染器失联时 generation 所有权不明确，过期回调、无结果、取消或失败可能替换或关闭最后一次成功结果。
- 视频查询与 popup 提交没有共同的 request-version 显示所有权，快速连续查询时，旧请求的迟到提交可能取得新锚点和高亮。
- overlay 曾在新 generation 提交前覆盖嵌套查词设置和根锚点；旧 popup 仍可交互时会读取新请求上下文，abort 后也无法恢复。
- 字幕 Canvas 与 JS bridge 的空命中没有携带点击/Shift hover 来源，hover 到字符间隙或扫描边界会被误判为显式关闭并清除已提交 popup、高亮和所有权。

**解决**：
- 视频宿主启用 `DictionaryPopupCanvasInputMode.VisibleHostsOnly`：实际 popup host 保持可交互，透明空白把输入交给字幕 Canvas；默认 modal 行为不影响小说和其他宿主。
- 根 popup 使用 committed/pending 两阶段事务。JavaScript 按 document epoch 暂存 DOM 和完整交互数据并发送 prepared；native 只接受精确 epoch + generation，线性化 commit 后再原子替换。提交状态无法确认时导航到新 epoch shell，待其 ready 后才精确终止旧 generation 并恢复 latest queue，旧文档迟到消息不能完成新事务。
- 视频侧为每个 request version 分配唯一显示身份；只有当前或已被 renderer 接受的精确事务能在 committed 事件后提交锚点和高亮，queued drop、abort、显式关闭也按同一身份终止所有权。
- 新查询无结果、被取代、取消或失败时保留最后一次 committed popup、交互上下文和高亮；只有新的成功 generation 原子提交后才整体切换。
- overlay 的 context、anchor 与 layout 按 generation + trace 暂存；嵌套查词在每个异步边界校验 root/parent generation，root 高亮脚本也拒绝不同 generation 的 DOM；resize 同时刷新 committed 与 exact pending layout 但只显示 committed，精确 commit 才整体提升，abort/drop/stale terminal 不改变旧状态。
- Canvas → JS → native 显式传递 `dismissOnEmpty` / `isHover`：点击空白仍关闭，Shift hover 的空白、间隙和扫描失败只重置 hover 去重并保留 committed popup、高亮及 accepted transaction。
---

## 小说统计已有完整数据，但书架内联面板无法呈现 Niratan Dashboard

**原因**：
- typed sidecar repository 与纯计算器已经覆盖最近一年、目标、速度、趋势、日历、排行和书架对比，但 UI 仍是书架顶部的限高内联面板。
- 统计展示状态和格式化通过 `NovelLibraryPageViewModel` 转发，无法建立 Niratan 的全页切换、独立生命周期、三档自适应布局和完整键盘/UI Automation 契约。
- 该问题是展示投影和页面架构缺口，不是统计引擎或 `statistics.json` 数据缺失。

**解决**：
- 新增独立 `NovelStatisticsDashboardViewModel` 与全页 `NovelStatisticsDashboardView`；父 ViewModel 只切换 Bookshelf/Statistics 并提供当前书籍、书架状态。
- 补齐 Range & Trend、Today、Goal、This Week、Reading Calendar、Selected Range、Speed Summary、Book Ranking、Shelf Comparison；自研 UI-only Canvas 控件绘制 Bar/Line，不新增图表依赖。
- Dashboard 使用单一纵向滚动所有者，在 1260/840 effective pixels 切换三列、两列、单列；Calendar 仅横向滚动，所有 selector 有稳定 AutomationId 与中英文资源。
- 激活 generation、linked cancellation source 与激活期 refresh 订阅保证离开/重进时旧 load 和旧 refresh 不能覆盖新 snapshot；损坏 sidecar 仍保持原文件不变。
- 小说、书架和统计继续以 JSON sidecar 为真源；SQLite 只保留视频业务边界，视频功能未移除。

---

## Reader 跳转会污染阅读统计，关闭时可能丢失最后一段时间

**原因**：
- Reader ViewModel 曾同时负责时钟、字符差、日期聚合和 sidecar 写入，真实翻页、程序化跳转与生命周期事件没有统一 checkpoint 边界。
- 搜索、目录、高亮、字符和 Sasayaki 跳转直接改写章节/进度，分页对齐后的回调无法区分普通 restore 与跳转 restore，长距离跳转可能被计为阅读字符。
- Reader 没有 generation-scoped destination、内部链接/history 事务、一秒投影和可等待的窗口关闭边界。

**解决**：
- `ReaderStatisticsSession` 独占 TTU 公式、TimeProvider、本地日期 rollover、基线和 `statistics.json` 写入，ViewModel 只投影状态。
- 真实阅读移动使用 typed checkpoint；所有程序化入口统一执行“结算旧位置 → 等待 generation 目标 → 保存最终书签 → 重置基线”，过期 bridge callback 不能完成新跳转。
- WebView2 拦截 EPUB 链接，native 只允许同源 spine 目标；补齐 fragment 与 Back/Forward 历史导航并复用同一统计事务。
- tracking 且未 paused 时每秒更新内存统计；窗口最小化写 Background checkpoint，返回书架、页面消失和主窗口关闭共享幂等 Close checkpoint。
- Dashboard 改为最近一年 typed snapshot 与纯计算器，补齐可选范围/anchor、目标、速度窗口、趋势粒度与指标、日历详情、Book Ranking 指标和 Shelf Comparison，并移除旧 By Book distribution。
- 损坏统计 sidecar 会显示可见警告并保持原文件不变；派生 Dashboard cache 使用 schema/key 校验和书库事件失效，命中后后台重读 sidecar，缓存损坏只删除缓存自身。

---

## 小说存储与书架状态曾被 SQLite 绑定

**原因**：
- 小说元数据、进度与排序曾以主 App SQLite 为真源，无法按 Niratan 的单书 sidecar 结构独立迁移、恢复和同步。
- 书架缺少持久化服务边界，书籍移动、书架排序和损坏 JSON 恢复无法保证原子性。

**解决**：
- 小说元数据、书签、书籍信息、统计和高亮改为每书目录 sidecar；全局顺序与书架分别写入 `book_order.json`、`shelves.json`。
- 启动时先备份并校验导出旧小说表，再退役小说 SQLite schema；失败时保留旧表和原文件并切换为只读恢复模式。
- 主 App SQLite 缩小为视频业务边界，视频功能和外部只读音频数据库保持不变。
- 新增 Reading、自定义书架、Unshelved 与独立 Google Drive rail，以及创建、重命名、删除、排序和书籍移动入口。

---

## 视频字幕软阴影出现双命中或黑色矩形

**原因**：
- 可见 Canvas 与可交互 WebView2 同时处理字幕点击时，一次操作会产生两套坐标和两次查询；后到的空结果可能立即关闭刚打开的 popup。
- 把 WebView2 改为唯一可见字幕层虽能恢复 DOM 选中，但透明 WebView2 无法合成到原生视频 HWND 上，会暴露整块黑色 backing surface。
- Canvas 自定义行距只设置了 `LineSpacing`、没有同步设置 `LineSpacingBaseline`，导致字形基线与 `GetCharacterRegions` 返回的选区行框纵向错位。

**解决**：
- `CanvasControl` 成为唯一可见、可命中的字幕表面，统一负责文字、Niratan 单层高斯阴影、字符命中和选中高亮。
- WebView2 仅保留为 `Opacity=0`、`IsHitTestVisible=False` 的无头选择桥，继续复用非日文扫描和边界提取逻辑，不参与输入或视频合成。
- 普通点击和 Shift hover 都先由同一个 `CanvasTextLayout` 命中，再把 UTF-16 字符偏移发送到窄 JS bridge；popup 关闭或字幕切换时同步清除 Canvas 选中范围。
- 自定义行距使用 1.25 倍字号，并把基线设置为字号本身，使选区行框、字形和查词锚点共享同一纵向布局。

---

## 视频查词首次打开和大词条结果卡顿

**原因**：
- 视频窗口在 native lookup 完成后才预热根/子 WebView2，首次查询承担完整冷启动成本。
- 全部 `maxResults` 结果曾被序列化进单个 `ExecuteScriptAsync`；大型 structured content 可产生 1 MB 以上 payload，使 WebView2 传输远慢于 native lookup。
- 字幕 Shift hover 只有 in-flight 布尔锁，没有 latest-request-wins，旧结果可能继续占用热路径。

**解决**：
- 字幕 WebView ready 后后台预热 popup，保留按需 warm 作为失败回退。
- 首条结果独立注入并显示，剩余结果以不超过三条的 generation-scoped 小批次追加，保留用户配置的最终结果数量和顺序。
- 视频查词请求使用版本和取消令牌；新请求使旧请求失效，旧结果不能再高亮、显示或替换当前 popup。
- `DictionaryPopupRequest.TraceId` 贯穿视频 overlay，并分别记录首批/延后批次的序列化字节数和传输耗时。

---

## popup 先显示释义、后闪出主词

**原因**：
- popup 同时存在两套 `contentReady`：shell observer 在第一块释义出现时提前通知，完整 renderer 又在所有词条完成后通知。
- renderer 隐藏了网页根节点，但双列布局给释义卡写入内联 `visibility: visible`，导致 native 提前显示 WebView2 时只有释义能穿透根隐藏，标题和标签仍不可见。
- 全部结果按词典逐帧渲染，放大了半成品暴露时间；native lookup、反序列化和 rebuild 还可能在 WinUI 线程同步执行。

**解决**：
- `popup.js` 成为唯一 ready 来源：先一次性构造并布局完整首词，再发送当前 generation 的唯一 `contentReady`；其余词条随后逐帧追加。
- 保留 native `Opacity=0` generation gate，过期 renderer 不能显示旧内容或继续追加。
- hoshidicts lookup、styles、media 和 rebuild 通过同一 worker executor 串行访问 native session；styles 缓存到下次 rebuild。

---

## popup 圆角出现黑色角块

**原因**：
- WinUI 3 WebView2 不能把透明网页像素合成到同窗口的兄弟 XAML 视频内容上；圆角外的透明像素会退回到视频窗口的黑色宿主背景。
- 对 WebView2 父级 Grid 添加 Composition 圆角裁剪不能改变 WebView2 的透明合成限制。

**解决**：
- 使用 WinUI 原生 Border 绘制 popup 外轮廓，并按圆角半径计算 12→4 DIP、8→3 DIP 的安全内缩，使矩形 WebView2 完全位于圆角轮廓内。
- 原生护边、WebView2 默认背景和网页根背景统一使用不透明主题色，避免初始化、导航和主题切换期间露出黑色 backing surface。

---

## popup 嵌套查词冻结/崩溃

**原因**：
- popup 内 Shift hover 会在 `mousemove` 高频路径中连续触发 `lookupRedirect`，同一个 query 也可能重复进入 native lookup。
- Windows 侧每次 nested 查词都会新建 child `WebView2`，并在关闭/替换 child 时立即 `Dispose()` 旧 WebView2。高频创建和销毁 WebView2 会触发 native heap corruption，WER 表现为 `ntdll.dll` `0xc0000374`，有时先表现为窗口冻结。
- 隐藏后的 child host 仍然 `Visibility=Visible`，命中测试只看 `Visibility` 时会把已隐藏 host 当成可点击区域，进一步放大关闭/重建异常。

**解决**：
- `DictionaryPopupOverlay` 新增 redirect version + async semaphore，过期 redirect 结果不再创建 popup，child redirect 串行更新。
- child popup 改为池化复用：关闭时只 `Hide()`，不在查词热路径销毁 WebView2；只在 overlay `Dispose()` 时统一释放。
- 隐藏 host 的命中测试同时检查 `Opacity` 与 `IsHitTestVisible`。
- `popup.js` 对 Shift hover nested lookup 按 query 去重，避免同一文本连续触发重复 redirect。

---

字典查词链路的已修复问题记录。只记根因和解决方案，不记流水账。

---

## 嵌套查词无结果或空白

**原因**：
- 子弹窗创建后立即同步导航，`CoreWebView2` 还没有初始化完成，导致首次或嵌套查词出现空白、脚本未注入或 WebView2 生命周期竞态。
- 弹窗内选区脚本只处理 `caretPositionFromPoint`，在 WebView2/Chromium 下部分点击位置需要 `caretRangeFromPoint` 或 DOM rect fallback。

**解决**：
- 子弹窗改为 `ShowResultsNavigatedAsync`，创建后先 `await EnsureWebViewAsync()`。
- `PopupHtmlGenerator` 注入 Android 风格的选区 fallback：优先 caret API，失败后扫描文本节点 rect。

---

## 弹窗位置覆盖原窗口/父弹窗

**原因**：
- Windows 侧横排定位曾把 popup height 当成 screen height 传入 `SpaceBelow`。
- 嵌套弹窗曾固定按父弹窗偏移量摆放，没有使用弹窗内当前选区 rect。
- `lookupRedirect` 的选区 rect 使用正文 WebView 局部坐标；应用内子弹窗曾漏加正文相对父 popup 的顶部偏移，导致上弹和下弹都整体偏上。
- 子弹窗滚动时直接清理全部子弹窗，和 Android `closeChildPopupsForScrolledParent` 的栈行为不一致。

**解决**：
- `DictionaryPopupOverlay.ShowBelow` 改为使用真实 `screenHeight`，按 Android 规则 `spaceBelow >= popupHeight` 判断。
- `lookupRedirect` payload 携带弹窗内选区 rect，C# 侧换算后用同一套 Android-style `PositionHost` 定位。
- 应用内与全局独立子窗口统一通过 `父 popup Canvas 位置 + 正文 WebView 偏移 + 选区 rect` 解析锚点，不再使用两套坐标原点。
- 子弹窗滚动改为 `ClearChildrenAfter(parent)`。

---

## 英文单词无法查词

**原因**：
- Android 默认 `scanNonJapaneseText = true`，Windows 曾无条件把非日文字符当作扫描边界。
- 弹窗内的 selection shim 也有同样的无条件非日文边界判断。

**解决**：
- `selection.js` 改为读取 `window.scanNonJapaneseText`，默认允许非日文扫描。
- `PopupHtmlGenerator` 的 `isScanBoundary` 改成仅在 `window.scanNonJapaneseText === false` 时阻断非日文字符。

---

## 弹窗图片、词频、音调与栈关闭

**原因**：
- Android popup 通过 `https://hoshi.local/image` 加载媒体，Windows 曾设为空导致图片加载失败。
- 字典导入曾只按文件名前缀判断类型，漏掉 Yomitan metadata bank 内的 freq/pitch。
- lookup rebuild 曾只加载 term 字典，没把 frequency/pitch 一起加入查询。
- 子弹窗关闭时只移除当前 popup，可能留下子孙 WebView2。

**解决**：
- `DictionaryLookupPopup` 为 `https://niratan-dictionary-media.local/image` 增加 `WebResourceRequested` 拦截。
- `DictionaryImportService` 检测 metadata bank 内容，导入后分别写入 Frequency/Pitch 配置。
- `DictionaryLookupService` rebuild 时分别加载 Term、Frequency、Pitch 已启用字典。
- 弹窗内查词统一走 `DictionaryPopupOverlay.HandleRedirectAsync`。

---

## 弹窗振假名影响与父子层关闭

**原因**：
- Windows popup 内 selection shim 曾直接使用 caret range，可能把振假名当正文查词。
- `tapOutside` 语义与 Android 不一致：Windows 曾把 root 和 child 的 tapOutside 混淆。
- 阅读页 Shift hover 移到新句子时，旧 popup 曾等新结果回来才替换。

**解决**：
- `PopupHtmlGenerator` 的 popup selection shim 过滤 `rt/rp`，用 `getCharacterAtPoint` 校准真实正文字符。
- `popup.js` 按 Android 原版区分 `tapOutside` 行为：root 只关闭 child，child 只关闭其后代。
- `DictionaryPopupOverlay` 把 `tapOutside` 和 `dismiss/close` 分成两条事件。
- 阅读页收到新 lookup request 后先 `Dismiss()` 当前 overlay 再查新词。

---

## 词频显示与 popup 闪烁

**原因**：
- hoshidicts 支持三种 frequency 形态，Windows 曾只读平铺字段导致值掉成 0。
- 同一本词频词典的多条 frequency 曾不聚合，导致重复显示多个标签。
- Windows 曾没有把词频纳入排序，结果顺序与 Android 偏离。
- root/child popup 曾在渲染完成前就 `Visible`，导致空壳或闪烁。
- warm root WebView2 复用上一轮 DOM，旧 `contentReady` 可能在新位置短暂显示残影。

**解决**：
- `ParseFrequency` 递归解析嵌套 `frequency` 字段，保留真实 `value` 和 `displayValue`。
- 同一词典的频率聚合到现有 `FrequencyEntry`，去重。
- lookup 排序加入最小 frequency rank，对齐 hoshidicts 低 rank 优先原则。
- popup 对齐 Android alpha 模型：先 `Opacity=0` + `visibility:hidden`，收到当前 generation 的 `contentReady` 后再显示。
- 每次显示生成新 render generation，旧 generation 的 ready 消息失效。
- 待渲染和关闭状态下禁止把 WebView2 `Visibility` 设为 `Collapsed`。
- 预热后保持 `_overlayPopup.IsOpen = true`，关闭查词只 `Hide()` root host。

---

## MK3 字典导入失败

**原因**：
- `MK3Fix0213.zip` 的原始 `index.json` 标题是非 ASCII；Windows 上 hoshidicts 直导会在创建/读取导入目录时触发代码页或 SEH 异常。
- 参考 Niratan Reader Windows `codex/anki-sasayaki-sidecar` 分支后确认，该分支也不是直接导入成功，而是先失败再走 lookup-safe 兼容 zip retry。

**解决**：
- `DictionaryImportService` 保留正常直导路径；仅在 Windows code-page/SEH 类失败时创建临时兼容 zip。
- 兼容 zip 只保留查词核心文件：`index.json`、`styles.css`、`term_bank_*`、`term_meta_bank_*`、`tag_bank_*`，并把临时标题改为 ASCII `niratan-import-*`。
- native 导入成功后把 `index.json` 显示标题恢复为原始标题，字典是否进入 Term/Frequency/Pitch 目录只以 native import count 为准。
- `MK3Fix0213.zip` 验证结果为 `term=140821`、`freq=0`、`pitch=0`、`media=0`；这本 zip 是词条字典，不包含可导入词频/音调数据。
