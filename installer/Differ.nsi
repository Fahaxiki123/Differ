; Offline, current-user Windows x64 installer. Build through scripts/build-installer.ps1.
Unicode true
ManifestSupportedOS all
ManifestDPIAware true
RequestExecutionLevel user
SetCompressor /SOLID lzma
SetCompressorDictSize 32
SetDatablockOptimize on
CRCCheck on
ShowInstDetails show
ShowUninstDetails show

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
!include "WinVer.nsh"
!include "x64.nsh"
!include "nsDialogs.nsh"
!include "${PAYLOAD_INCLUDE}"

!define APP_ID "Differ.Desktop.x64"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}"

Name "Differ ${APP_VERSION}"
OutFile "${OUTPUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\Differ"
InstallDirRegKey HKCU "${UNINSTALL_KEY}" "InstallLocation"
BrandingText "Differ"
VIProductVersion "${APP_FILE_VERSION}"
VIAddVersionKey /LANG=2052 "ProductName" "Differ"
VIAddVersionKey /LANG=2052 "CompanyName" "Differ"
VIAddVersionKey /LANG=2052 "FileDescription" "Differ 离线安装程序"
VIAddVersionKey /LANG=2052 "FileVersion" "${APP_FILE_VERSION}"
VIAddVersionKey /LANG=2052 "ProductVersion" "${APP_VERSION}"
VIAddVersionKey /LANG=2052 "LegalCopyright" "Differ"

Var ExistingInstall
Var BindIntegration
Var DesktopShortcut
Var IntegrationCheckbox
Var DesktopCheckbox
Var InstallResult
Var FinishSummary
Var OwnsRegistration

!define MUI_ICON "${ICON_FILE}"
!define MUI_UNICON "${ICON_FILE}"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE "安装 Differ"
!define MUI_WELCOMEPAGE_TEXT "将 Differ 安装到当前 Windows 用户。安装包包含运行所需组件，无需联网下载 .NET 或安装 Office。$\r$\n$\r$\n支持 64 位 Windows 10 / 11。TortoiseSVN 需由你或公司 IT 单独安装。$\r$\n$\r$\n升级前请关闭 Differ 比较窗口。安装程序不会强行关闭窗口或更改你的文档。"
!insertmacro MUI_PAGE_WELCOME
!define MUI_PAGE_CUSTOMFUNCTION_PRE DirectoryPagePre
!define MUI_PAGE_CUSTOMFUNCTION_LEAVE DirectoryPageLeave
!insertmacro MUI_PAGE_DIRECTORY
Page custom OptionsPageCreate OptionsPageLeave
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_TITLE "Differ 已安装"
!define MUI_FINISHPAGE_TEXT "$FinishSummary"
!define MUI_FINISHPAGE_RUN "$INSTDIR\Differ.App.exe"
!define MUI_FINISHPAGE_RUN_TEXT "打开 Differ 集成设置"
!define MUI_FINISHPAGE_RUN_NOTCHECKED
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"

!macro VisibleError Text
  DetailPrint "${Text}"
  ${IfNot} ${Silent}
    MessageBox MB_OK|MB_ICONSTOP "${Text}"
  ${EndIf}
!macroend

Function .onInit
  SetShellVarContext current
  SetRegView 64
  StrCpy $BindIntegration 0
  StrCpy $DesktopShortcut 0
  StrCpy $InstallResult 0
  StrCpy $FinishSummary "Differ 已安装，可以从开始菜单打开。$\r$\n$\r$\n如需用于 SVN 提交前比较，可在集成设置中绑定 TortoiseSVN。"
  ${IfNot} ${IsNativeAMD64}
    !insertmacro VisibleError "此安装包需要 64 位 Windows 10 或 Windows 11。"
    SetErrorLevel 1
    Quit
  ${EndIf}
  ${IfNot} ${AtLeastWin10}
  ${OrIfNot} ${AtLeastBuild} 14393
    !insertmacro VisibleError "此安装包需要 Windows 10 1607（14393）或更新的 64 位系统。"
    SetErrorLevel 1
    Quit
  ${EndIf}
  ReadRegStr $ExistingInstall HKCU "${UNINSTALL_KEY}" "InstallLocation"
  ${If} $ExistingInstall != ""
    StrCpy $INSTDIR $ExistingInstall
  ${EndIf}
  ${GetParameters} $0
  StrCpy $1 ""
  ${GetOptions} $0 "/INTEGRATE=" $1
  ${If} $1 == "1"
    StrCpy $BindIntegration 1
  ${EndIf}
  StrCpy $1 ""
  ${GetOptions} $0 "/DESKTOP=" $1
  ${If} $1 == "1"
    StrCpy $DesktopShortcut 1
  ${EndIf}
FunctionEnd

Function DirectoryPagePre
  ${If} $ExistingInstall != ""
    ; A registered installation is upgraded in place, even if /D was supplied.
    StrCpy $INSTDIR $ExistingInstall
    Abort
  ${EndIf}
FunctionEnd

Function DirectoryPageLeave
  Call ValidateInstallDirectory
FunctionEnd

Function ValidateInstallDirectory
  ${If} $ExistingInstall != ""
    StrCpy $INSTDIR $ExistingInstall
  ${EndIf}
  ; NSIS GetFullPathName requires an existing parent directory. The Win32 API
  ; normalizes a first-install path without creating it before safety checks.
  System::Call 'kernel32::GetFullPathNameW(w "$INSTDIR", i ${NSIS_MAX_STRLEN}, w .r0, p 0) i.r1'
  ${If} $1 == 0
  ${OrIf} $1 >= ${NSIS_MAX_STRLEN}
    !insertmacro VisibleError "无法解析安装位置，请选择长度合适的独立文件夹。"
    SetErrorLevel 1
    Abort
  ${EndIf}
  StrCpy $INSTDIR $0
  ${GetRoot} "$INSTDIR" $0
  ${If} $INSTDIR == "$0\"
  ${OrIf} $INSTDIR == "$0"
  ${OrIf} $INSTDIR == "$PROFILE"
  ${OrIf} $INSTDIR == "$LOCALAPPDATA"
  ${OrIf} $INSTDIR == "$WINDIR"
  ${OrIf} $INSTDIR == "$SYSDIR"
  ${OrIf} $INSTDIR == "$PROGRAMFILES64"
    !insertmacro VisibleError "请选择用于 Differ 的独立文件夹，不能直接使用磁盘根目录或系统目录。"
    SetErrorLevel 1
    Abort
  ${EndIf}
  CreateDirectory "$INSTDIR"
  ClearErrors
  GetTempFileName $0 "$INSTDIR"
  ${If} ${Errors}
    !insertmacro VisibleError "当前用户无法写入这个安装位置，请选择可写文件夹。"
    SetErrorLevel 1
    Abort
  ${EndIf}
  Delete "$0"
FunctionEnd

Function OptionsPageCreate
  !insertmacro MUI_HEADER_TEXT "安装选项" "为当前 Windows 用户选择快捷方式与 SVN 集成。"
  nsDialogs::Create 1018
  Pop $0
  ${If} $0 == error
    Abort
  ${EndIf}
  ${NSD_CreateLabel} 0 0 100% 24u "将创建开始菜单入口。应用与全部运行组件离线安装，不需要管理员权限。"
  Pop $0
  ${NSD_CreateCheckbox} 0 32u 100% 14u "创建桌面快捷方式"
  Pop $DesktopCheckbox
  ${NSD_SetState} $DesktopCheckbox $DesktopShortcut
  ${NSD_CreateCheckbox} 0 58u 100% 14u "设为 TortoiseSVN 差异查看器（当前用户）"
  Pop $IntegrationCheckbox
  ${NSD_SetState} $IntegrationCheckbox $BindIntegration
  ${NSD_CreateLabel} 12u 78u 94% 56u "勾选后会备份当前全局差异工具及受支持扩展名配置，再绑定 Differ。未勾选不会修改 SVN 设置。$\r$\n$\r$\n如尚未安装 TortoiseSVN，可稍后从 Differ 集成设置绑定。"
  Pop $0
  nsDialogs::Show
FunctionEnd

Function OptionsPageLeave
  ${NSD_GetState} $DesktopCheckbox $DesktopShortcut
  ${NSD_GetState} $IntegrationCheckbox $BindIntegration
FunctionEnd

!macro DefineLockCheck Prefix
Function ${Prefix}CheckFileUnlocked
  Exch $0
  Push $1
  IfFileExists "$0" 0 unlocked
  ; A read/write handle with no sharing detects running images and other locks
  ; without changing file contents or terminating any process.
  System::Call 'kernel32::CreateFileW(w r0, i 0xC0000000, i 0, p 0, i 3, i 0x80, p 0) p.r1'
  ${If} $1 == -1
    !insertmacro VisibleError "安装目录中的文件正在使用或无法写入。请关闭 Differ 以及使用该目录文件的程序，然后重试。"
    SetErrorLevel 1
    Abort
  ${EndIf}
  System::Call 'kernel32::CloseHandle(p r1)'
unlocked:
  Pop $1
  Pop $0
FunctionEnd
!macroend
!insertmacro DefineLockCheck ""
!insertmacro DefineLockCheck "un."

; Official Evergreen registry detection. No online bootstrapper is used.
Function ReadWebViewVersion
  ReadRegStr $0 HKCU "Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" "pv"
  ${If} $0 == ""
  ${OrIf} $0 == "0.0.0.0"
    SetRegView 32
    ReadRegStr $0 HKLM "Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" "pv"
    SetRegView 64
  ${EndIf}
FunctionEnd

Function EnsureWebViewRuntime
  Call ReadWebViewVersion
  ${If} $0 == ""
  ${OrIf} $0 == "0.0.0.0"
    InitPluginsDir
    SetOutPath "$PLUGINSDIR"
    File /oname=WebView2RuntimeX64.exe "${WEBVIEW_INSTALLER}"
    DetailPrint "正在离线安装文本编辑器所需的 Microsoft WebView2 Runtime..."
    ClearErrors
    ExecWait '"$PLUGINSDIR\WebView2RuntimeX64.exe" /silent /install' $1
    ${If} ${Errors}
      !insertmacro VisibleError "无法启动内含的 WebView2 离线安装程序。请检查系统策略后重试。"
      SetErrorLevel 1
      Abort
    ${EndIf}
    Call ReadWebViewVersion
    ${If} $0 == ""
    ${OrIf} $0 == "0.0.0.0"
      !insertmacro VisibleError "WebView2 离线安装未成功。请联系管理员检查系统策略，再重新运行安装包。"
      SetErrorLevel 1
      Abort
    ${EndIf}
  ${EndIf}
FunctionEnd

Section "Differ" MainSection
  Call ValidateInstallDirectory
  !insertmacro CheckPayloadLocks ""
  Call EnsureWebViewRuntime
  SetOverwrite on
  ClearErrors
  !insertmacro InstallPayload
  SetOutPath "$INSTDIR"
  WriteINIStr "$INSTDIR\Differ.installation.ini" "Product" "Id" "${APP_ID}"
  WriteINIStr "$INSTDIR\Differ.installation.ini" "Product" "Version" "${APP_VERSION}"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  IfErrors install_failed

  CreateDirectory "$SMPROGRAMS\Differ"
  CreateShortcut "$SMPROGRAMS\Differ\Differ.lnk" "$INSTDIR\Differ.App.exe" "" "$INSTDIR\Differ.App.exe" 0
  CreateShortcut "$SMPROGRAMS\Differ\卸载 Differ.lnk" "$INSTDIR\Uninstall.exe" "" "$INSTDIR\Uninstall.exe" 0
  ${If} $DesktopShortcut == ${BST_CHECKED}
    CreateShortcut "$DESKTOP\Differ.lnk" "$INSTDIR\Differ.App.exe" "" "$INSTDIR\Differ.App.exe" 0
  ${EndIf}
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "Differ"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "Differ"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\Differ.App.exe,0"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "EstimatedSize" ${INSTALLED_SIZE}
  IfErrors install_failed

  ${If} $BindIntegration == ${BST_CHECKED}
    ClearErrors
    ExecWait '"$INSTDIR\Differ.App.exe" --install-integration' $0
    ${If} ${Errors}
      StrCpy $0 1
    ${EndIf}
    ${If} $0 == 0
      StrCpy $FinishSummary "Differ 已安装并设为当前用户的 TortoiseSVN 差异查看器。原工具配置已备份。$\r$\n$\r$\n可在 TortoiseSVN 提交窗口双击文件进行比较。"
    ${ElseIf} $0 == 2
      StrCpy $InstallResult 2
      StrCpy $FinishSummary "Differ 已安装，但没有检测到 TortoiseSVN，因此尚未绑定。$\r$\n$\r$\n安装 TortoiseSVN 后，请打开 Differ 集成设置完成绑定。"
      DetailPrint "$FinishSummary"
      ${IfNot} ${Silent}
        MessageBox MB_OK|MB_ICONINFORMATION "$FinishSummary"
      ${EndIf}
    ${Else}
      StrCpy $InstallResult 1
      StrCpy $FinishSummary "Differ 程序已安装，但 TortoiseSVN 绑定失败。$\r$\n$\r$\n请打开 Differ 集成设置检查备份与注册表权限后重试。"
      !insertmacro VisibleError "$FinishSummary"
    ${EndIf}
  ${EndIf}
  SetErrorLevel $InstallResult
  Goto installed
install_failed:
  !insertmacro VisibleError "安装未能完成。请确认安装目录可写、磁盘空间充足，并关闭正在使用 Differ 的程序后重新运行安装包。"
  SetErrorLevel 1
  Abort
installed:
SectionEnd

Function .onInstSuccess
  SetErrorLevel $InstallResult
FunctionEnd

Function .onInstFailed
  SetErrorLevel 1
FunctionEnd

Function un.onInit
  SetShellVarContext current
  SetRegView 64
  StrCpy $OwnsRegistration 0
  ReadRegStr $0 HKCU "${UNINSTALL_KEY}" "InstallLocation"
  ${If} $0 == $INSTDIR
    StrCpy $OwnsRegistration 1
  ${EndIf}
  ReadINIStr $0 "$INSTDIR\Differ.installation.ini" "Product" "Id"
  ${If} $0 != "${APP_ID}"
    !insertmacro VisibleError "无法验证此目录中的 Differ 安装身份，卸载已取消。"
    SetErrorLevel 1
    Quit
  ${EndIf}
FunctionEnd

Section "Uninstall"
  !insertmacro CheckPayloadLocks "un."
  ClearErrors
  ExecWait '"$INSTDIR\Differ.App.exe" --restore-integration' $0
  ${If} ${Errors}
    StrCpy $0 1
  ${EndIf}
  ${If} $0 != 0
    !insertmacro VisibleError "未能安全恢复 TortoiseSVN 设置，卸载已取消，程序文件已保留。请打开 Differ 集成页检查备份和权限后重试。"
    SetErrorLevel 1
    Abort
  ${EndIf}
  ClearErrors
  !insertmacro RemovePayload
  ${If} $OwnsRegistration == 1
    Delete "$SMPROGRAMS\Differ\Differ.lnk"
    Delete "$SMPROGRAMS\Differ\卸载 Differ.lnk"
    RMDir "$SMPROGRAMS\Differ"
    Delete "$DESKTOP\Differ.lnk"
    DeleteRegKey HKCU "${UNINSTALL_KEY}"
  ${EndIf}
  Delete "$INSTDIR\Differ.installation.ini"
  Delete "$INSTDIR\Uninstall.exe"
  ; Never recursively remove an installation directory or the user's data.
  RMDir "$INSTDIR"
  SetErrorLevel 0
  Goto uninstalled
uninstall_failed:
  !insertmacro VisibleError "部分安装文件无法删除。TortoiseSVN 恢复步骤已完成，请关闭占用文件的程序后重试。个人设置和备份未删除。"
  SetErrorLevel 1
  Abort
uninstalled:
SectionEnd

Function un.onUninstFailed
  SetErrorLevel 1
FunctionEnd
