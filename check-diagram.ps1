# 验证架构图排版:测量关键文本宽度,是否超出所在框
Add-Type -AssemblyName System.Drawing

function Measure-W($s, [single]$size, [bool]$bold) {
    $style = [System.Drawing.FontStyle]::Regular
    if ($bold) { $style = [System.Drawing.FontStyle]::Bold }
    $f = New-Object System.Drawing.Font('Microsoft YaHei', $size, $style, [System.Drawing.GraphicsUnit]::Pixel)
    $bmp = New-Object System.Drawing.Bitmap(20, 20)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $sz = $g.MeasureString($s, $f)
    $g.Dispose(); $bmp.Dispose(); $f.Dispose()
    return $sz.Width
}

$checks = @(
    # 标签框(9.5px,框宽 140)
    @{ s = 'InputHookService.cs';      size = 9.5;  max = 140; note = 'tag1' },
    @{ s = 'ScreenCaptureService.cs';  size = 9.5;  max = 140; note = 'tag3' },
    @{ s = 'WindowsOcrEngine.cs';      size = 9.5;  max = 140; note = 'tag5' },
    @{ s = 'PaddleOcrEngine.cs';       size = 9.5;  max = 140; note = 'tag5' },
    @{ s = 'TranslationPipeline.cs';   size = 9.5;  max = 140; note = 'tag7' },
    @{ s = 'OcrOverlayRenderer.cs';    size = 9.5;  max = 140; note = 'tag8' },
    @{ s = 'OverlayWindow.cs';         size = 9.5;  max = 140; note = 'tag8' },
    # 流水线副标题(10.5px,可用约 100)
    @{ s = '点击 / 滚轮 / 按键';        size = 10.5; max = 100; note = 'pipe1' },
    @{ s = '只留值得翻的外文';          size = 10.5; max = 100; note = 'pipe6' },
    # 引擎行(10.5px,可用 180)
    @{ s = '本地 127.0.0.1:1188';       size = 10.5; max = 180; note = 'eng1' },
    @{ s = '需自己部署一个 exe';        size = 10.5; max = 180; note = 'eng1' },
    @{ s = '微软浏览器同款翻译';        size = 10.5; max = 180; note = 'eng2' },
    # 底部说明行(10.5px,可用 ~526)
    @{ s = '• ScreenTranslator.csproj — 工程配方(.NET 8 + WPF,含 PaddleOCR 识别库)'; size = 10.5; max = 526; note = 'boxB1' },
    @{ s = '• architecture.md / PROGRESS.md / REVIEW.md — 设计与进度文档';            size = 10.5; max = 526; note = 'boxB3' },
    @{ s = '• 控制台:测试 / 演示按钮、目标语言(中/英/日)、渲染方式、状态栏';           size = 10.5; max = 554; note = 'boxA1' },
    # 面板标题(14px 粗,可用 1178)
    @{ s = '⑦ 翻译环节 · 内部细节:先查缓存,再排队找 4 家翻译服务,前面的失败自动换下一家'; size = 14; max = 1178; note = 'panel-title' },
    # 脚注(10.5px)
    @{ s = '行级降级:某一行翻译失败,只把这一行交给下一家,翻成功的行不重翻;全部失败则原文显示,并在状态栏提示网络问题。'; size = 10.5; max = 1178; note = 'footnote' }
)

$fail = 0
foreach ($c in $checks) {
    $w = Measure-W $c.s $c.size $false
    $ok = $w -le $c.max
    if (-not $ok) { $fail++ }
    Write-Host ("{0,-12} {1,6:F1}/{2}px  {3}" -f $c.note, $w, $c.max, $(if ($ok) { 'OK' } else { '!!! 溢出' }))
}
Write-Host ("---- 溢出项: {0} ----" -f $fail)
