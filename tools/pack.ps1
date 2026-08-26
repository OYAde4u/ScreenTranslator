# 一键打包:Release 发布 → 修复 VC 运行库 → 编译安装包
# 用法: pwsh -File tools\pack.ps1   (workdir = 项目根目录)
# 产物: publish\ScreenTranslator.exe(浅层免安装版)、installer\ScreenTranslator-Setup-1.0.0.exe(安装包)
$ErrorActionPreference = 'Stop'

Write-Host '== 1/3 dotnet publish (self-contained win-x64) =='
dotnet publish .\ScreenTranslator\ScreenTranslator.csproj -c Release -r win-x64 --self-contained true -o .\publish -v minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Paddle 运行时包带的 VC++ 运行库太旧(14.28),会让 onnxruntime 1.29 初始化失败(DLL_INIT_FAILED);
# 用系统的 14.51 覆盖(VC v14 系列向后兼容,Paddle/onnxruntime 都能用;redist 允许再分发)
Write-Host '== 2/3 覆盖新版 VC++ 运行库 =='
foreach ($n in @('msvcp140.dll','msvcp140_1.dll','msvcp140_2.dll','vcruntime140.dll','vcruntime140_1.dll','concrt140.dll')) {
    Copy-Item "C:\Windows\System32\$n" ".\publish\$n" -Force
}

Write-Host '== 3/3 Inno Setup 编译安装包 =='
& '.\.tools\InnoSetup\ISCC.exe' 'tools\installer.iss'
exit $LASTEXITCODE
