# 手动把 nupkg 安装进 NuGet 全局包缓存(离线/受限网络环境的包安装器)
# 用法: .\install-nupkg.ps1 -Id microsoft.windows.sdk.net.ref -Version 10.0.19041.56
param(
    [Parameter(Mandatory = $true)][string]$Id,
    [Parameter(Mandatory = $true)][string]$Version
)

$ErrorActionPreference = 'Stop'
$cacheRoot = 'D:\ScreenTranslator\.nuget\packages'
$idLower = $Id.ToLowerInvariant()
$destDir = Join-Path $cacheRoot (Join-Path $idLower $Version)
$nupkgName = "$Id.$Version.nupkg"
$nupkgPath = Join-Path $destDir $nupkgName

if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }

if (-not (Test-Path $nupkgPath)) {
    $url = "http://repo.huaweicloud.com/artifactory/api/nuget/v3/nuget-remote/$idLower/$Version/$nupkgName"
    Write-Output "downloading $url"
    Invoke-WebRequest -Uri $url -OutFile $nupkgPath -TimeoutSec 300 -UseBasicParsing
}

# 解压内容(若尚未解压)
if (-not (Test-Path (Join-Path $destDir 'lib'))) {
    Write-Output "extracting..."
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($nupkgPath, $destDir)
}

# .nuspec 复制到根
$nuspec = Get-ChildItem $destDir -Filter '*.nuspec' -Recurse | Select-Object -First 1
if ($nuspec -and -not (Test-Path (Join-Path $destDir $nuspec.Name))) {
    Copy-Item $nuspec.FullName (Join-Path $destDir $nuspec.Name)
}

# .nupkg.sha512(base64 of SHA512)
$shaPath = "$nupkgPath.sha512"
if (-not (Test-Path $shaPath)) {
    $hash = Get-FileHash -Path $nupkgPath -Algorithm SHA512
    $bytes = [byte[]]::new($hash.Hash.Length / 2)
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        $bytes[$i] = [Convert]::ToByte($hash.Hash.Substring($i * 2, 2), 16)
    }
    [IO.File]::WriteAllText($shaPath, [Convert]::ToBase64String($bytes))
}

Write-Output "installed: $Id $Version -> $destDir"
Get-ChildItem $destDir | Select-Object -First 6 -ExpandProperty Name
