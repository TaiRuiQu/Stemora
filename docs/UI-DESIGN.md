# Stemora 界面设计

以微软的 Windows Apps Design / WinUI 3 指南为依据，使用应用已经采用的原生控件。蓝色为用户指定的应用强调色。

## 参考

- Windows design principles: https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/design-principles
- Typography: https://learn.microsoft.com/en-us/windows/apps/design/style/typography
- Layout: https://learn.microsoft.com/en-us/windows/apps/design/layout/
- Color: https://learn.microsoft.com/en-us/windows/apps/design/style/color
- NavigationView: https://learn.microsoft.com/en-us/windows/apps/design/controls/navigationview
- Progressive disclosure: https://learn.microsoft.com/en-us/windows/apps/design/basics/design-and-ui-intro

## 实现

- 页面标题采用原生 `TitleTextBlockStyle`，分组标题采用 `SubtitleTextBlockStyle`；正文与辅助信息使用 14 的正文级别。根元素按当前语言标记为 `zh-CN` 或 `en-US`，由系统处理字体与文字渲染。
- 使用 8、16、24、32 的间距组织内容。浅色、深色的背景、文字、分隔线和控件表面来自 WinUI 语义资源，不自制另一套卡片皮肤。
- 蓝色用于主操作、链接、选中、进度与音频波形；内容背景保持中性。原生按钮、输入框、列表、复选框保留其交互状态与焦点反馈。
- 导航为音频分离、旋律转谱、模型库、设置，页面切换使用稳定 Tag。页面移除品牌口号、步骤编号、常驻状态徽标与重复边框。
- 旋律转谱使用钢琴卷帘为主编辑区：深色中性网格、可试听琴键、蓝色音块，只展示编辑后的音符。顶部为全曲波形及合并为一行的吸附 / 试听 / 编辑工具，底部固定钢琴＋人声 / 钢琴 / 人声播放器与独立音量。文件与保存状态底部对齐，移除浮在工具栏上方的重复文字。移除频谱显示与计算，修改在播放中实时生效。结果页文件与模型设置收进工具按钮，简谱在同一工作台切换，详见 `MELODY-TRANSCRIPTION.md`。
- 分离设置可折叠，多轨工作台使用统一时间坐标。每轨控制区宽 272，波形高 104，M / S、音量与采样峰值留出独立空间。底部播放控制、共用进度和任务操作固定可见，不随音轨列表滚动。
- 移除底部装饰分割线和重复的 M / S 帮助文字；按钮解释保留在悬停提示和辅助功能名称中。电平底槽占用固定空间，仅在播放时填充；按物理像素更新宽度，避免低电平缩放产生细线。
- 音轨与页面共用一层滚动，滚动视口右侧保留 24 像素空白，防止 WinUI 的覆盖式滚动条压住波形。
- 模型库按音频分离 / 旋律转谱用途统一管理，结合量化、音轨、平台、架构和导出格式筛选。每个模型显示适用的用途、权重、导出与文件格式标签，以及输出内容、运行平台和资源或授权说明；下载按钮靠右对齐，说明可以换行。已下载状态为普通文字。MuScriptor 下载授权与环境准备统一放在模型库。
- 模型库只显示简洁的平台与建议容量，不在页面或模型条目重复解释测试限制。实测与估算的区分、硬件、分块和方法保存在资源测试文档中。
- 语言切换立即更新静态控件、队列状态、音轨名称、辅助功能名称和模型标签，并独立保存语言偏好。
- 高级参数、自定义引擎、命令行通过原生 Expander 按需展开；日志放在操作栏的 Flyout 中。
- 空列表、单文件、浅色、深色、紧凑窗口、模型库、设置、推理中和推理完成状态均通过应用内 `RenderTargetBitmap` 检查。
