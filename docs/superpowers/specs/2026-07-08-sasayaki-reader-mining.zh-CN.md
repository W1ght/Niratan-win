# Sasayaki 阅读器交互与制卡规格

## 目标

- 移除 NovelReader 底部 Sasayaki 传输控制条，不再占用阅读页底部空间。
- 在顶部工具栏提供紧凑 Sasayaki 入口：加载/匹配音频字幕、播放暂停、上一句、下一句、重播当前句、跳转到当前句。
- Reader 查词弹窗制卡时，携带小说句子、书名、封面和命中的 Sasayaki cue 音频。
- Sasayaki 音频只在 Anki 字段映射需要 `{sasayaki-audio}` 时按需导出，行为对齐现有 Niratan/视频制卡媒体流水线。

## 非目标

- 不重做 EPUB 渲染链路。
- 不把字典查询或制卡逻辑写进 WebView JavaScript。
- 不新增第二套播放器或数据库技术。

## 设计

- XAML 删除 `NovelReaderSasayakiBar`、进度滑块、速度下拉和底部跳转按钮。
- 顶部工具栏新增 `NovelReaderSasayakiButton` 及菜单项，复用当前 code-behind 的 Sasayaki 播放/导航方法。
- Reader lookup payload 已包含 `sentence`、`normalizedOffset`、`sentenceOffset`；native 侧据此创建 `AnkiMiningContext`。
- 当前章节内用 `normalizedOffset` 匹配 `SasayakiMatch`，命中后为 context 设置 `SasayakiAudioProvider`。
- 弹窗 mining preflight 增加 `NeedsSasayakiAudio`；仅字段映射使用 `{sasayaki-audio}` 时才请求 provider。
- Sasayaki provider 复用现有媒体导出服务生成 m4a 片段，并优先支持 Anki media 目录直接写入；失败时回落为普通上传路径。
- `AnkiHandlebarRenderer` 将 `{sasayaki-audio}` 渲染为 `[sound:...]` 标签，若尚未导出则回退到路径。

## 验证

- 新增/更新 asset tests 覆盖底部条移除、顶部入口存在、Reader lookup 注入 Anki mining context。
- 新增/更新 Anki tests 覆盖 `{sasayaki-audio}` media need 和 sound tag 渲染。
- 运行相关测试和 `dotnet build -p:Platform=x64`。
