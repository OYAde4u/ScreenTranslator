# ScreenTranslator 开发进度记录

更新:2026-08-26(第 16 轮:移除字幕底块渲染,只保留背景采样覆盖)

## 第 16 轮改动(渲染方式收敛为背景采样)
1. **删除字幕底块模式**(用户决策:背景采样效果已足够好,做减法):`RenderStyle` 枚举、`BuildOne`/`Build` 的 style 参数、段落级 `BuildParagraph`(字幕专用)、主窗口"渲染方式"下拉框、悬浮框"字幕/背景"切换按钮全部移除;
2. 主窗口渲染方式处改为静态说明文字;悬浮框宽度 410→360,剩 ▶/自动/→中/× 四钮;
3. 文档同步:README 功能/用法、CHANGELOG 1.3.0;
4. 验证:build 0 错误;e2e/filter/ocr-hybrid 自检 exit=0。

## 第 15 轮改动(OCR 速度优化)
1. **先测量再动手**(整屏 155 行基准:det 1182ms + rec/cls 2331ms = 3513ms):
   - **DoAngle=false**:屏幕文字恒横向,角度分类器是每行一次额外推理,纯浪费(省 ~700ms,精度零损失);
   - **InitModels(models, 8)**:onnxruntime 8 线程,det 1192→971ms;
   - MaxSideLen 1280 虽更快(det 430ms)但丢 13% 小字行——质量优先,不采用;
   - 优化后实测:整屏 91 行 total=1728ms;
2. **区域指纹复用**(`HashRegion` FNV-1a 采样 16K 点):横带像素未变 → 直接复用上轮识别结果,跳过 OCR;
3. **自身 UI 脏区不再触发全屏识别**:`BuildOcrRegions` 的"全被过滤退全屏"兜底改为"直接跳过"(该兜底在悬浮框/主窗口独占脏区时白白整屏识别一轮);
4. 验证:ocr-rapid-screen/ocr-hybrid/e2e/filter 自检 exit=0。

## 第 14 轮改动(悬浮框实用选项)
1. **悬浮框按钮扩展**(340→410 宽):▶ 立即翻译(转发 TranslateOnce,等同热键)、自动✓/自动(开关自动触发,复用 ChkAuto 逻辑,开启时蓝底高亮)、× 隐藏悬浮框(转发 ChkWidget);原有字幕/语言按钮保留;
2. **双向同步补齐**:主窗口改目标语言/渲染方式/自动触发时,悬浮框按钮文字与状态同步更新(此前仅创建时同步一次);
3. 验证:build 0 错误;e2e 自检 exit=0。

## 第 13 轮改动(实机反馈:文字叠压)
1. **字体叠加修复(根因:逐行块膨胀互压)**:字幕模式改为**段落级整块**渲染——`OcrOverlayRenderer.BuildParagraph` 覆盖段落全部行的联合区域(含行间隙),译文按行 \n 连接在块内整体换行、左对齐;行间残句(如被拆出的"に。")也一并盖住。背景采样模式保持逐行(原位替换需要)。`LineGrouping.Group` 在渲染侧复用(翻译侧本就按段聚合);
2. **字号多行自适应**(`OverlayWindow`):初始字号同时受"行数×行高"与"最长行宽"约束,再按 7% 迭代收缩到换行后实际高度放得下;单行行为不变;
3. 验证:build 0 错误;e2e/filter/translate 自检 exit=0。

## 第 12 轮改动(实机反馈:慢/衔接/进度可视)
1. **并行翻译提速**:`TranslateParagraphsAsync` 两阶段化——阶段 1 各源语言组**并行**批量请求(原来串行);阶段 2 拆回行数失配的段落不再逐段串行重试,改为跨段收集后按语言**一次批量逐行重试**(此前长文档失配段落多时延迟线性堆叠);
2. **行衔接(振假名碎片)**:`OcrLineFilter.SuppressRuby`——日文排版汉字上方的小字注音被 OCR 成独立小行,单独翻译后成漂浮碎片(截图中的"桃子/阅读/会议"块)。启发式:行高<0.55×中位行高 + 与正常行水平重叠≥40% + 紧贴其上方 → 判为注音丢弃(行数≥4 才启用;已知风险:极小合法 UI 文本,误报可调阈值);
3. **悬浮状态框**(`StatusWidgetWindow`,主界面复选框开关,默认开):340×48 深灰半透明(Opacity=0.92;**不能用 AllowsTransparency**——本机分层窗口不合成上屏,已实测 Opacity 机制上屏成功,采样 std=0.00)置顶小窗,拖动移动;显示进度(截图中/识别中/翻译中 N 行/完成 N 块·耗时·引擎/失败原因);两个快捷按钮:渲染方式(字幕↔背景)、目标语言(中→英→日 循环),转发到主窗口下拉框复用同一逻辑;
4. **自身排除**:悬浮框区域加入排除列表——脏区不算变化(`BuildOcrRegions` 改收排除数组)、OCR 不识别(ignoreRects)、覆盖层不绘制(`OverlayManager.ExtraExcludes`),杜绝"识别自身"循环;
5. 验证:悬浮框上屏实测(截屏采样均匀深灰 mean=41.3 std=0.00);filter/e2e/translate 自检 exit=0。

## 第 11 轮改动(翻译"誊写"根因修复 + 渲染切换刷新)
1. **Edge 翻译接口重写(根因)**:旧流程的 JWT 鉴权端点 `edge.microsoft.com/translate/auth` 已被上游移除(2026-07,实测 404)——Edge 整体熔断,长段落落到 MyMemory 超长度限制后回退原文,表现为"大部分行直接誊写英文"。改用免鉴权后继端点 `edge.microsoft.com/translate/translatetext`(参考 read-frog 迁移):body 为纯 JSON 字符串数组、必须带浏览器 UA(否则 400 Client Browser Version not supported)、入参 HTML 转义防 `<` 被粘成伪标签、出参解码一次。实测:5 行段落译文保留 4 个换行、质量上乘("Privet Drive"→"女贞路四号");
2. **段落拆回失配兜底**:译文行数 ≠ 段落行数时不再整段回退原文,改为**逐行重试**(走管道行级缓存),仍失败的行保留原文且不渲染覆盖块;`MaxLinesPerParagraph` 8→6(单请求更短更稳);
3. **誊写行不画块**:渲染前逐行跳过"译文==原文"的行(失败行不再用黑块盖原文),部分失败时状态栏提示未翻出行数;
4. **切换渲染方式底色不刷新 bug**:`OverlayWindow` 内容签名原来只含坐标+文本,换"背景采样"后文本/位置不变 → 签名不变 → 跳过重建 → 永远显示旧色块。签名加入 `Bg`/`Fg` 颜色;
5. 验证:Edge 实测(2 段落 + 转义往返)通过;translate/filter/e2e/ocr-hybrid/diff 自检 exit=0。

## 第 10 轮改动(实机反馈修复)
1. **覆盖层被后弹出的 Topmost 窗口压住**(演示页英文上看不到译文块):`OverlayWindow.BringToFront()`(`SetWindowPos(HWND_TOPMOST, NOMOVE|NOSIZE|NOACTIVATE)`),`OverlayManager.SetItems`/`ShowAll` 每次渲染后重新断言置顶;
2. **长行被横向切断**(满屏英文只翻右半段,如 `and up the road, he wa` 碎片):`BuildOcrRegions` 从"脏区 bounding box 裁剪"改为**全宽横带**(x=0、宽=整屏,只裁垂直方向,margin 32px)——文本行是水平的,横带永不横向切行;带高>半屏仍退回全屏;RapidOCR 有 MaxSideLen=2000 硬上限 + 宽高比 8 信箱化,横带耗时按高度比例缩放,不会炸性能;
3. **背景取色更柔和**:`SampleAverage`(取 bbox 内部,混入字形像素偏灰)→ `SampleSurrounding`(取 bbox 外围 8px 环形带,纯背景像素,步长 2;环形全落屏幕外时回退内部平均),仅影响"背景采样"渲染方式;
4. 回归:e2e / filter / ocr-hybrid / diff 自检 exit=0。

## 第 9 轮改动(RapidOCR 高质量主引擎)
1. **接入 RapidOcrNet 4.0.2**(ONNX,PP-OCRv5/v6 模型,无 PaddleOCRSharp 社区版 `box sizes <100` 限制):新增依赖 RapidOcrNet + Microsoft.ML.OnnxRuntime 1.29 + SkiaSharp 3.119(win-x64 native 自动拷贝);新增 `Services/Ocr/RapidOcrEngine.cs`(BGRA 零拷贝喂 SKBitmap、`RapidOcrOptions.PPOCRv6` 预设、逐字符置信度取均值、四角点→bbox、模型懒加载在后台线程、绝对路径解析防 cwd 不一致);
2. **内置 PP-OCRv6 small 多语模型**(`ScreenTranslator/models/v6/`,det 9.9MB + rec 21MB + dict,SHA256 校验通过;来源 RapidAI/modelscope):Latin + CJK(含假名),中英日一模型覆盖;参考 DangoTranslator 的本地多语 PP-OCR 模型组织方式;
3. **实测对比**(`--selftest-ocr-rapid*`):合成图 3/3 全对(0.987~1.000,含中文 `屏幕翻译测试` 无空格无错字);真实整屏 2560x1600 识别 116 行、中文无错字(对照 Windows 同屏 `HeIlo`/中文逐字插空格);整屏 ~2~4s(含首次模型加载 1~2s);
4. **主引擎切换**:`HybridOcrEngine` 改为 RapidOCR 全区域优先 + Windows 兜底(`RapidOcrEngine.LastCallFailed` 区分"识别失败"与"确实无文字");PaddleOCRSharp 仅保留 `--selftest-ocr-paddle*` 诊断;
5. **自检**:新增 `--selftest-ocr-rapid` / `--selftest-ocr-rapid-screen`;全套 7 项(ocr/hybrid/rapid/filter/e2e/translate/diff)exit=0。

## 第 8 轮改动(OCR 识图质量优化)
1. **新增 `HybridOcrEngine`**(`Services/Ocr/HybridOcrEngine.cs`):按输入规模分流——面积 ≤1.8MP 且最长边 ≤2000 的区域优先 PaddleOCR(PP-OCRv5,真实置信度、错字少);整屏/超限或 Paddle 触发社区版 "box sizes <100" 限制(返回 0 行)时自动回退 Windows.Media.Ocr;Paddle 首次加载放后台线程,不卡 UI;
2. **依据(实测)**:合成图 Paddle 3/3 行全对(`Hello ScreenTranslator 12345`、`屏幕翻译测试 ABCDEFG`,置信度 0.95~0.995),Windows 同图把 `Hello` 识成 `HeIlo`;真实屏幕 2000x1000 裁剪 Paddle 83 行可用,2560x1600 整屏触发社区版限制返回 0 行;
3. **过滤修复**:`IsPathLike` 不再按"含冒号"一刀切(误杀 `NPC: ...` 对话),只过滤协议 `://`、`C:\` 盘符与多斜杠路径;纯时间仍由纯数字规则过滤;
4. **自检**:新增 `--selftest-ocr-hybrid`(合成小图应路由 Paddle)、`--selftest-ocr-paddle-screen`(全屏+多尺寸裁剪摸清社区版限制);`--selftest-filter` 扩充对话/盘符用例;构建 0 错误,ocr/hybrid/filter/e2e 自检 exit=0;
5. **主引擎切换**:`MainWindow` 默认使用 `HybridOcrEngine` 并正确 Dispose。

## 第 7 轮改动(REVIEW.md 修复路线图 1/2/4/5/6/7/8 项)
1. **段落聚合 + 整段翻译**(P0-1):新增 `LineGrouping`——OCR 行按几何关系(垂直间距<1.5×行高且水平重叠>30%)聚合成段落,行间 `\n` 连接一次翻译请求,译文按行拆回(行数不匹配回退原文);一句话被拆成多行时上下文连贯;
2. **日/韩语言检测修复**(P0-2):`IsChinese` 改为"汉字占比>12% 且假名数<汉字一半";新增 `IsJapanese`(含假名)、`IsKorean`;`IsTargetLanguage` 支持 ZH/JA/EN——目标 ZH 只过滤中文行,日/韩/英文保留翻译;段落按源语言分组(JA/KO/ZH/EN 各自独立翻译),不再整批共用一个源语言;
3. **Echo 兜底不画块**(P0-3):译文全部==原文时不渲染覆盖块,状态栏提示"翻译服务不可用";管道记录实际使用引擎名;
4. **译文换行自适应**(P1-7):TextBlock 换行 + Measure 后块高自适应,取消省略号截断;
5. **半行碎片拼接**(P1-5):切块边界产生的同水平线碎片按"y 重叠>60% + x 相邻≤12px"拼接回整行;
6. **垃圾过滤**(P1-6):纯数字、含数字短码(<6 字符无空格)、URL/路径(含 :// 或 ≥2 斜杠或冒号)过滤;
7. **真 LRU 缓存**(P2-10):Dictionary+LinkedList 实现,热台词不被 FIFO 误驱逐;
8. 自检新增 `--selftest-filter`(语言检测/垃圾过滤/段落聚合,全部通过)。

## 第 6 轮改动
1. **OCR 精度增强**(Windows OCR 榨干):
   - 输入**切块(≤1100px)+ 放大 2x** 再识别(实测系统 MaxImageDimension=10000,整图放大 5120x3200 会漏检部分区域——演示窗口文本整屏识别不到,切块后识别成功);
   - **预处理**:灰度化 + 自动对比度拉伸 + 深色背景反色(黑底白字识别率大增),后台线程执行;
   - 每块识别带 15 秒超时保护(防 WinRT 挂起);
2. **翻译链重构**:DeepLX → **MyMemory(免费无需 key)** → Echo。DeepLX 限流(429)/未启动时自动用 MyMemory 出**真译文**(实测 "This is a live demo page" → "这是一个现场演示页面"),不再是原文;状态栏提示 DeepLX 状态(429 = 需配置 DeepL API Key);
3. **过滤增强**:CJK 判定阈值 30%→12%(中英混合行不再漏判为英文);新增纯符号乱码行过滤;代码路径/操作日志类混合长行(如 `写入ScreenTranslator\Services\...`)被正确过滤;
4. **演示窗口 Topmost**(不再被全屏窗口遮挡,确保 OCR 能识别到演示页);
5. 修复多个编译期问题(此前部分轮次 build 实际失败,exe 未更新,导致验证跑在旧版上)。

## 第 5 轮关键修复(为什么之前"点演示没覆盖块")
1. **覆盖层改为普通窗口 + SetWindowRgn 裁剪**(不再用 WPF 分层窗口 AllowsTransparency):实测该机器上分层窗口内容渲染正常但**不合成上屏**(PrintWindow 能抓到内容、屏幕上看不到)。普通窗口 + 区域裁剪(块并集)必定上屏,GDI 截图可验证(实测截图中出现 92.6% 深色测试块);
2. **OCR 主引擎切换为 Windows.Media.Ocr**:PaddleOCRSharp 6.2.0 社区版限制"检测框 <100px",全屏截图里正常文本行(>100px)直接抛异常导致**整个 OCR 失败返回 0 行**(实测报 `free community edition only support box sizes <100`)。Windows OCR 全屏 0.3 秒识别 54+ 行,无此限制;
3. **修复脏区误杀**:BuildOcrRegions 原来用"脏区中心是否在 app 窗口内"判断——首次全屏脏区合并后中心恰在居中的 app 窗口内,导致**OCR 区域为空、识别永不执行**。改为交集面积占比(<50% 保留)+ 空结果兜底全屏;
4. **修复翻译管线挂死**:DeepLXTranslator 的 SemaphoreSlim **只 Wait 不 Release**,DeepLX 不可用时前 4 个请求异常后信号量泄漏,后续请求永久死等 → 整批翻译永久挂起。加 finally Release + 熔断(失败 60 秒内快速降级)+ 禁用系统代理 + 2 秒短超时(离线降级从 16 秒+ 降到 2 秒);
5. **过滤规则统一为"交集面积 ≥70% 才排除"**:OcrLineFilter 与 OverlayManager 双重过滤原先不一致(后者"有交集即排除"),与 app 窗口轻微重叠的内容(如演示页)被误杀。

## 第 4 轮改动(投射 + 性能)
### 1. 正确投射到显示器(而非 app 上)
- **逐显示器截图**:`ScreenCaptureService` 改为按显示器 `CreateDC(设备名)` 分别 BitBlt 再拼接为虚拟屏幕帧。副屏/多 GPU(笔记本独显+核显)不再黑屏,你显示器上的内容能被完整截到并翻译;
- **逐显示器覆盖窗**:`OverlayManager` 为每块显示器建一个 `OverlayWindow`(精确贴合该屏边界),物理像素→DIP 按"目标显示器 DPI"换算,抵消 DWM 跨屏缩放——任何 DPI 组合下译文块都精确压在原文位置;
- **app 自身窗口排除**:应用窗口区域不绘制覆盖块、不参与 OCR/脏区判定(译文明明白白显示在你的显示器/桌面上,不再盖在 app 界面上,app 自身 UI 也不会被翻);
- 显示器插拔/分辨率变化自动重建覆盖窗(2s 轮询,`DisplayLayout`)。
### 2. 消除捕获后的卡顿
- **重活全部移出 UI 线程**:截图/指纹/脏区、Paddle OCR(PNG 编码+推理 `Task.Run`)、背景采样、预览降采样、调试截图存盘;
- **只翻变化区域**:脏区裁剪后局部 OCR(原先每次全屏 OCR + 全屏 PNG 编码,是最重的两步);无变化直接跳过;
- **截图前隐藏覆盖层**:覆盖层(分层窗口)会被 BitBlt 捕获,原先形成"翻译自身输出→再识别→再翻译"的反馈循环,现改为隐藏→等合成器生效(35ms)→截图→恢复;
- **忙锁+合并触发**:一轮运行中再触发只记一次待办,不再叠加并发;
- **零散优化**:池化像素缓冲(免每帧 3 份全屏数组)、覆盖层内容签名不变跳过重建、PNG 调试截图默认关(复选框)、预览降采样到 480px、避免每次全量重建覆盖层。
### 3. 悬浮窗渲染风格(1.3.0 起收敛为背景采样唯一模式)
- **背景采样覆盖(唯一保留)**:取文本外围 8px 环形带平均色填充 + 黑/白对比字,纯色/半透明对话框背景上像原文原位替换;
- ~~字幕底块~~:已于第 16 轮移除(用户决策,做减法);历史实现见 git 记录(段落级 BuildParagraph 曾用于防字体叠加)。

## 项目状态:✅ 可运行,主引擎 = RapidOCR(PP-OCRv6 small 多语,整屏高质量)+ Windows.Media.Ocr 兜底(HybridOcrEngine 调度)
- 源码:`D:\ScreenTranslator\ScreenTranslator\`(WPF,.NET 8 `net8.0-windows10.0.19041.0`)
- 构建:`pwsh -c "& 'D:\ScreenTranslator\tools\dot.ps1' build"`(workdir = 项目目录;沙箱内脚本调用受限时改用内联环境变量执行 dotnet build)
- 输出:调试 `bin\Debug\net8.0-windows10.0.19041.0\ScreenTranslator.exe`(注意 TFM 全名目录);发布 `publish\ScreenTranslator.exe`(自包含 win-x64,浅层)
- NuGet:华为云 **http** artifactory 源;`tools\install-nupkg.ps1` 手动装包(沙箱 https 被拦截,restore 解析依赖时仍会尝试 https 资源 URL,所以所有依赖包须预先手动装进 `D:\ScreenTranslator\.nuget\packages`)

## 已完成 ✅
- **M1 骨架**:BitBlt 截图(2560x1600 物理像素)、置顶透明穿透覆盖窗、Ctrl+Shift+T 热键、DPI 坐标系统一
- **M2 OCR**:`IOcrEngine` 抽象;主引擎 **PaddleOcrEngine**(PaddleOCRSharp 6.2.0 + PP-OCRv5 模型随包拷贝,真实置信度 0.95+,中文无空格,四角点→bbox),回退 **WindowsOcrEngine**(系统 OCR);FakeOcrEngine(演示)
- **M3 翻译**:DeepLX(127.0.0.1:1188)优先 + LRU 缓存 4096 + 批量去重 + 并发 4 + Echo 降级
- **M4 自动触发**:全局钩子(点击/滚轮/按键)+ 300ms 防抖 + 800ms 冷却 + 64px 分块指纹区域 diff(垂直合并)
- **M5 打磨**:OcrLineFilter(长度/置信度/目标语言 CJK/忽略区域)、翻译接入主流程、目标语言选择(中/英/日)、文字字号自适应 bbox、全局异常落盘 crash.txt

## 自检命令(全部 exit=0)
`--diag`、`--selftest`、`--selftest-ocr`(系统 OCR)、`--selftest-ocr-hybrid`(主路径=RapidOCR)、`--selftest-ocr-rapid` / `--selftest-ocr-rapid-screen`(PP-OCRv6 合成/整屏)、`--selftest-ocr-paddle` / `--selftest-ocr-paddle-screen`(Paddle 诊断)、`--selftest-translate`、`--selftest-diff`、`--selftest-e2e`、`--selftest-filter`

## 已装 NuGet 包(缓存,勿删)
- Microsoft.Windows.SDK.NET.Ref 10.0.19041.56(WinRT 投影)
- PaddleOCRSharp 6.2.0 + Paddle.Runtime.win_x64 3.3.0.1 + System.Drawing.Common 8.0.7 + Microsoft.Win32.SystemEvents 8.0.0 + Newtonsoft.Json 13.0.3
- RapidOcrNet 4.0.2 + Microsoft.ML.OnnxRuntime(.Managed) 1.29.0 + SkiaSharp 3.119.1 + Clipper2 2.0.0(win-x64 native 随包自动拷贝;PP-OCRv6 模型在 `ScreenTranslator/models/v6/`,勿删)
- 注意:Paddle.Runtime 的 build 资产解析为空,**native 拷贝已固化在 csproj**(绝对路径 None Include)

## 待办/已知限制
1. **DeepLX 未实测**:用户机器部署 DeepLX(1188 端口)后免费高质量翻译生效;当前离线降级 Echo(显示原文)
2. ~~多显示器不同 DPI 跨屏错位~~ ✅ 已修复:逐显示器截图 + 逐显示器覆盖窗按各屏 DPI 换算(非系统 DPI 的屏上文字仍有轻微 DWM 缩放,位置始终精确)
3. 复杂背景抹除:背景采样(取文本外围平均色,纯色/对话框好);图片/视频文字背景复杂时仍需 inpaint
4. ~~覆盖层每次全量重建~~ ✅ 已修复:内容签名不变跳过重建
5. 截图前隐藏覆盖层 35ms:触发时译文块有一瞬闪烁(换取"不翻译自身输出"的正确性)

## 关键文件
- `Services\`:DisplayLayout / ScreenCaptureService / OverlayManager / OverlayWindow / Ocr\(WindowsOcrEngine, PaddleOcrEngine, OcrLineFilter) / OcrOverlayRenderer / Translate\(DeepLX, TranslationPipeline) / InputHookService / AutoTriggerService / ScreenDiff
- `App.xaml.cs`(自检+DPI+异常落盘)、`MainWindow.xaml(.cs)`(控制面板,引擎择优)
- 工具:`tools\dot.ps1`(构建环境重定向)、`tools\install-nupkg.ps1`(离线装包)、`NuGet.Config`(http 源)

## 环境备忘
- dotnet 8.0.413;沙箱只通 HTTP(HTTPS 拦截);读文件用 read,写用 write/edit
