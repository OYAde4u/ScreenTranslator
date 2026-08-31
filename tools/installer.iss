; =============================================================
; 屏幕实时翻译 ScreenTranslator 安装包脚本(Inno Setup 6)
; 编译: .tools\InnoSetup\ISCC.exe tools\installer.iss
; 产物: installer\ScreenTranslator-Setup-1.1.0.exe
; 说明: 自包含发布(无需 .NET 运行时);排除 Paddle 诊断运行时
;       (paddle_inference/mklml/mkldnn/opencv/paddle_yt_phi ≈308MB
;        + inference\ ≈79MB,Paddle 仅为开发诊断,正式版用 RapidOCR)
; =============================================================
#define MyAppVersion "1.4.3"
#define MyAppPublisher "OYAde4u"
#define MyAppURL "https://github.com/OYAde4u/ScreenTranslator"
#define PublishDir "D:\ScreenTranslator\publish"

[Setup]
AppId={{5E8F2A1C-9D3B-4C7E-A6F1-2B8D4C0E7A55}
AppName=屏幕实时翻译 ScreenTranslator
AppVersion={#MyAppVersion}
AppVerName=屏幕实时翻译 ScreenTranslator {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
DefaultDirName={localappdata}\Programs\ScreenTranslator
DefaultGroupName=屏幕实时翻译
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=D:\ScreenTranslator\installer
OutputBaseFilename=ScreenTranslator-Setup-{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\ScreenTranslator.exe
DisableProgramGroupPage=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; 顶层文件(不递归,排除 Paddle 诊断运行时 native 库)
Source: "{#PublishDir}\*"; DestDir: "{app}"; \
  Flags: ignoreversion; \
  Excludes: "paddle_inference.dll,paddle_yt_phi.dll,mklml.dll,mkldnn.dll,opencv_world470.dll,*.pdb"
; OCR 模型(递归;不含 Paddle 的 inference\ 目录)
Source: "{#PublishDir}\models\*"; DestDir: "{app}\models"; Flags: ignoreversion recursesubdirs createallsubdirs
; .NET 语言资源(逐目录显式列出,避免递归把 inference\ 带进来)
Source: "{#PublishDir}\cs\*"; DestDir: "{app}\cs"; Flags: ignoreversion
Source: "{#PublishDir}\de\*"; DestDir: "{app}\de"; Flags: ignoreversion
Source: "{#PublishDir}\es\*"; DestDir: "{app}\es"; Flags: ignoreversion
Source: "{#PublishDir}\fr\*"; DestDir: "{app}\fr"; Flags: ignoreversion
Source: "{#PublishDir}\it\*"; DestDir: "{app}\it"; Flags: ignoreversion
Source: "{#PublishDir}\ja\*"; DestDir: "{app}\ja"; Flags: ignoreversion
Source: "{#PublishDir}\ko\*"; DestDir: "{app}\ko"; Flags: ignoreversion
Source: "{#PublishDir}\pl\*"; DestDir: "{app}\pl"; Flags: ignoreversion
Source: "{#PublishDir}\pt-BR\*"; DestDir: "{app}\pt-BR"; Flags: ignoreversion
Source: "{#PublishDir}\ru\*"; DestDir: "{app}\ru"; Flags: ignoreversion
Source: "{#PublishDir}\tr\*"; DestDir: "{app}\tr"; Flags: ignoreversion
Source: "{#PublishDir}\zh-Hans\*"; DestDir: "{app}\zh-Hans"; Flags: ignoreversion
Source: "{#PublishDir}\zh-Hant\*"; DestDir: "{app}\zh-Hant"; Flags: ignoreversion

[Icons]
Name: "{userdesktop}\屏幕实时翻译"; Filename: "{app}\ScreenTranslator.exe"
Name: "{group}\屏幕实时翻译"; Filename: "{app}\ScreenTranslator.exe"
Name: "{group}\卸载 屏幕实时翻译"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\ScreenTranslator.exe"; Description: "启动屏幕实时翻译"; Flags: nowait postinstall skipifsilent
