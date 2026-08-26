# 生成《屏幕实时翻译软件》架构图 PNG(2x 高清)
# 用法: pwsh -File render-diagram.ps1
Add-Type -AssemblyName System.Drawing

$scale = 2.0
$W = 1290; $Hgt = 780
$bmp = New-Object System.Drawing.Bitmap([int]($W*$scale), [int]($Hgt*$scale), [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$g.Clear([System.Drawing.Color]::White)
$g.ScaleTransform($scale, $scale)

function C($hex) { return [System.Drawing.ColorTranslator]::FromHtml($hex) }

function New-Font([single]$size, [bool]$bold) {
    $style = [System.Drawing.FontStyle]::Regular
    if ($bold) { $style = [System.Drawing.FontStyle]::Bold }
    return New-Object System.Drawing.Font('Microsoft YaHei', $size, $style, [System.Drawing.GraphicsUnit]::Pixel)
}

function Draw-Text($g, [string]$text, [single]$x, [single]$y, [single]$size, $colorHex, [bool]$bold = $false, [string]$align = 'left') {
    $font = New-Font $size $bold
    $brush = New-Object System.Drawing.SolidBrush((C $colorHex))
    $sf = New-Object System.Drawing.StringFormat
    if ($align -eq 'center') { $sf.Alignment = [System.Drawing.StringAlignment]::Center }
    $g.DrawString($text, $font, $brush, [single]$x, [single]$y, $sf)
    $font.Dispose(); $brush.Dispose()
}

function Draw-RoundRect($g, [single]$x, [single]$y, [single]$w, [single]$h, [single]$r, $fillHex, $strokeHex, [single]$strokeW = 2) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush((C $fillHex))), $path)
    if ($strokeHex) {
        $pen = New-Object System.Drawing.Pen((C $strokeHex), [single]$strokeW)
        $g.DrawPath($pen, $path)
        $pen.Dispose()
    }
    $path.Dispose()
}

function Draw-Arrow($g, [single]$x1, [single]$y1, [single]$x2, [single]$y2, $colorHex, [single]$width = 2, [bool]$dashed = $false) {
    $pen = New-Object System.Drawing.Pen((C $colorHex), $width)
    if ($dashed) { $pen.DashStyle = [System.Drawing.Drawing2D.DashStyle]::Dash }
    $g.DrawLine($pen, [single]$x1, [single]$y1, [single]$x2, [single]$y2)
    $pen.Dispose()
    $angle = [math]::Atan2([double]($y2 - $y1), [double]($x2 - $x1))
    $len = [single]9.0
    $brush = New-Object System.Drawing.SolidBrush((C $colorHex))
    $pen2 = New-Object System.Drawing.Pen($brush, [single]2.2)
    for ($i = 0; $i -lt 2; $i++) {
        $a = if ($i -eq 0) { $angle + 2.7 } else { $angle - 2.7 }
        $px = [single]($x2 + $len * [math]::Cos($a))
        $py = [single]($y2 + $len * [math]::Sin($a))
        $g.DrawLine($pen2, [single]$x2, [single]$y2, $px, $py)
    }
    $pen2.Dispose(); $brush.Dispose()
}

function Draw-Badge($g, [single]$cx, [single]$cy, [single]$r, [string]$num, $fillHex, $textHex) {
    $g.FillEllipse((New-Object System.Drawing.SolidBrush((C $fillHex))), [single]($cx - $r), [single]($cy - $r), [single]($r * 2), [single]($r * 2))
    $font = New-Font 12 $true
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = [System.Drawing.StringAlignment]::Center
    $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
    $rect = New-Object System.Drawing.RectangleF([single]($cx - $r), [single]($cy - $r), [single]($r * 2), [single]($r * 2))
    $g.DrawString($num, $font, (New-Object System.Drawing.SolidBrush((C $textHex))), $rect, $sf)
    $font.Dispose()
}

# ============ 标题 ============
Draw-Text $g '屏幕实时翻译软件 · 架构图(一看就懂版)' 40 26 24 '#1F3B66' $true
Draw-Text $g '作用:屏幕上出现的外文(游戏 / 视频 / 网页 / 软件界面)→ 自动识别并翻译成中文,译文像字幕一样浮在原文位置上。端到端约 0.2~0.8 秒。' 40 60 13 '#667788'

# ============ 8 步流水线 ============
$pipe = @(
    @{ x = 40;  t = '用户操作';    s = '点击 / 滚轮 / 按键'; fill = '#E8F1FB'; stroke = '#4A6FA5'; ink = '#1F3B66' },
    @{ x = 194; t = '自动触发';    s = '300ms 防抖';          fill = '#E8F1FB'; stroke = '#4A6FA5'; ink = '#1F3B66' },
    @{ x = 348; t = '截屏';        s = '拍下整个屏幕';        fill = '#E8F1FB'; stroke = '#4A6FA5'; ink = '#1F3B66' },
    @{ x = 502; t = '变化检测';    s = '只挑变了的部分';      fill = '#E8F1FB'; stroke = '#4A6FA5'; ink = '#1F3B66' },
    @{ x = 656; t = '文字识别';    s = '认出:字 + 位置';      fill = '#F0E9FB'; stroke = '#7A5FC0'; ink = '#4A2E8A' },
    @{ x = 810; t = '过滤';        s = '只留值得翻的外文';    fill = '#F0E9FB'; stroke = '#7A5FC0'; ink = '#4A2E8A' },
    @{ x = 964; t = '翻译';        s = '并发 + 缓存';         fill = '#FDF0E0'; stroke = '#D98E32'; ink = '#8A5419' },
    @{ x = 1118;t = '覆盖渲染';    s = '译文盖住原文';        fill = '#E7F6EC'; stroke = '#3FA56B'; ink = '#1F6B42' }
)
$pipeY = 100; $pipeH = 88; $boxW = 140
$n = 1
foreach ($b in $pipe) {
    $x = [single]$b.x
    Draw-RoundRect $g $x $pipeY $boxW $pipeH 10 $b.fill $b.stroke 2
    Draw-Badge $g ($x + 20) ($pipeY + 20) 13 ([string]$n) $b.stroke '#FFFFFF'
    Draw-Text $g $b.t ($x + 40) ($pipeY + 12) 14 $b.ink $true
    Draw-Text $g $b.s ($x + 40) ($pipeY + 34) 10.5 '#556677'
    $n++
}
for ($i = 0; $i -lt 7; $i++) {
    $x1 = [single]($pipe[$i].x + $boxW); $x2 = [single]($pipe[$i + 1].x)
    Draw-Arrow $g $x1 ($pipeY + 44) ($x2 - 3) ($pipeY + 44) '#8FA6BF' 2.5
}

# ============ 代码文件对照(虚线连接) ============
$tags = @(
    @('InputHookService.cs', 'HotKeyService.cs'),
    @('AutoTriggerService.cs'),
    @('ScreenCaptureService.cs', 'PixelFrame.cs'),
    @('ScreenDiff.cs', 'FrameOps.cs'),
    @('WindowsOcrEngine.cs', 'PaddleOcrEngine.cs', 'IOcrEngine.cs'),
    @('OcrLineFilter.cs', 'OcrLine.cs'),
    @('TranslationPipeline.cs', 'LineGrouping.cs', '翻译引擎 ×4'),
    @('OverlayManager.cs', 'OverlayWindow.cs', 'OcrOverlayRenderer.cs')
)
$tagY = 226; $tagH = 72
$i = 0
foreach ($b in $pipe) {
    $x = [single]$b.x
    $pen = New-Object System.Drawing.Pen((C '#B8C2CC'), [single]1.4)
    $pen.DashStyle = [System.Drawing.Drawing2D.DashStyle]::Dash
    $g.DrawLine($pen, [single]($x + 70), [single]($pipeY + $pipeH), [single]($x + 70), [single]($tagY - 4))
    $pen.Dispose()
    Draw-RoundRect $g $x $tagY $boxW $tagH 8 '#F4F6F8' '#B8C2CC' 1.4
    Draw-Text $g '代码文件' ($x + 70) ($tagY + 6) 9 '#93A3B4' $false 'center'
    $lines = $tags[$i]
    $ly = $tagY + 24
    foreach ($ln in $lines) {
        Draw-Text $g $ln ($x + 70) $ly 9.5 '#4A5568' $false 'center'
        $ly += 15
    }
    $i++
}

# ============ 翻译环节细节面板 ============
Draw-RoundRect $g 40 320 1218 192 12 '#FFFDF5' '#E0D5A8' 1.6
Draw-Text $g '⑦ 翻译环节 · 内部细节:先查缓存,再排队找 4 家翻译服务,前面的失败自动换下一家' 60 344 14 '#7A5C1E' $true

# 缓存盒
Draw-RoundRect $g 60 372 300 108 10 '#E7F6EC' '#3FA56B' 1.8
Draw-Text $g 'LRU 缓存(4096 条)' 72 388 12.5 '#1F6B42' $true
Draw-Text $g '翻过的句子再次出现时' 72 412 10.5 '#446655'
Draw-Text $g '直接复用 → 0 延迟' 72 428 10.5 '#446655'
Draw-Text $g '重复台词零翻译成本' 72 444 10.5 '#446655'

# 4 家翻译引擎
$engines = @(
    @{ x = 390; t = 'DeepLX 本地代理'; l1 = '质量最好(五星)'; l2 = '需自己部署一个 exe'; l3 = '本地 127.0.0.1:1188' },
    @{ x = 596; t = 'Edge 免费接口';   l1 = '微软浏览器同款翻译'; l2 = '免费,国内可直连';  l3 = '无需注册' },
    @{ x = 802; t = 'MyMemory 免费';   l1 = '免费在线翻译';   l2 = '无需 API Key';        l3 = '质量一般' },
    @{ x = 1008;t = 'Echo 原样兜底';   l1 = '前几家全失败时'; l2 = '原文返回,不卡流程';  l3 = '状态栏会提示' }
)
$engY = 372; $engH = 108; $engW = 190
foreach ($e in $engines) {
    $x = [single]$e.x
    Draw-RoundRect $g $x $engY $engW $engH 10 '#FFFFFF' '#D98E32' 1.6
    Draw-Text $g $e.t ($x + 10) ($engY + 12) 12.5 '#8A5419' $true
    Draw-Text $g $e.l1 ($x + 10) ($engY + 38) 10.5 '#555555'
    Draw-Text $g $e.l2 ($x + 10) ($engY + 56) 10.5 '#555555'
    Draw-Text $g $e.l3 ($x + 10) ($engY + 74) 10.5 '#555555'
}
for ($i = 0; $i -lt 3; $i++) {
    $x1 = [single]($engines[$i].x + $engW); $x2 = [single]($engines[$i + 1].x)
    Draw-Arrow $g $x1 ($engY + 54) ($x2 - 4) ($engY + 54) '#D98E32' 2
}

# 缓存 <-> 引擎:查 / 写
Draw-Arrow $g 363 420 387 420 '#3FA56B' 1.8 $true
Draw-Arrow $g 387 446 363 446 '#3FA56B' 1.8 $true
Draw-Text $g '查' 364 406 9.5 '#3FA56B'
Draw-Text $g '写' 364 432 9.5 '#3FA56B'

Draw-Text $g '行级降级:某一行翻译失败,只把这一行交给下一家,翻成功的行不重翻;全部失败则原文显示,并在状态栏提示网络问题。' 60 496 10.5 '#777777'

# ============ 底部:主程序 + 工程文件 ============
Draw-RoundRect $g 40 544 610 132 10 '#EAF2FB' '#4A6FA5' 1.8
Draw-Text $g '主程序界面 · 总调度  MainWindow.xaml(.cs)' 56 566 13.5 '#1F3B66' $true
Draw-Text $g '• 控制台:测试 / 演示按钮、目标语言(中/英/日)、渲染方式、状态栏' 56 592 10.5 '#334455'
Draw-Text $g '• 把上面 8 步串成一条流水线;重活放后台线程,界面不卡' 56 612 10.5 '#334455'
Draw-Text $g '• 段落聚合:被 OCR 拆成多行的句子,拼回整段再翻译,更通顺' 56 632 10.5 '#334455'

Draw-RoundRect $g 670 544 586 132 10 '#F4F6F8' '#B8C2CC' 1.8
Draw-Text $g '工程文件:配方与文档' 686 566 13.5 '#333333' $true
Draw-Text $g '• ScreenTranslator.csproj — 工程配方(.NET 8 + WPF,含 PaddleOCR 识别库)' 686 592 10.5 '#555555'
Draw-Text $g '• App.xaml 启动入口 · SelfTests.cs 自检 · dot.ps1 一键构建 exe' 686 612 10.5 '#555555'
Draw-Text $g '• architecture.md / PROGRESS.md / REVIEW.md — 设计与进度文档' 686 632 10.5 '#555555'

# ============ 图例 ============
$legend = @(
    @{ x = 40;  c = '#4A6FA5'; t = '收集信号 / 截图' },
    @{ x = 220; c = '#7A5FC0'; t = '看懂文字(OCR)' },
    @{ x = 400; c = '#D98E32'; t = '翻译' },
    @{ x = 500; c = '#3FA56B'; t = '显示结果' },
    @{ x = 640; c = '#B8C2CC'; t = '灰框 = 代码文件(虚线连接)' },
    @{ x = 880; c = '#E0D5A8'; t = '黄框 = 翻译细节' }
)
foreach ($lg in $legend) {
    $x = [single]$lg.x
    $g.FillRectangle((New-Object System.Drawing.SolidBrush((C $lg.c))), $x, 704, 18, 18)
    Draw-Text $g $lg.t ($x + 26) 706 11 '#445566'
}

$out = Join-Path (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)) 'architecture-diagram.png'
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Host "saved: $out"
