# dotnet wrapper for sandboxed environment: redirects all dotnet/NuGet state into D:\ScreenTranslator
# 注意:重定向目录用 ASCII 名(沙箱对中文路径的子进程文件操作有限制)
$env:APPDATA = 'D:\ScreenTranslator\dotappdata\Roaming'
$env:LOCALAPPDATA = 'D:\ScreenTranslator\dotlocalappdata'
$env:DOTNET_CLI_HOME = 'D:\ScreenTranslator\dotnethome'
$env:NUGET_PACKAGES = 'D:\ScreenTranslator\.nuget\packages'
$env:NUGET_HTTP_CACHE_PATH = 'D:\ScreenTranslator\dotnugetcache'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
& dotnet @args
exit $LASTEXITCODE
