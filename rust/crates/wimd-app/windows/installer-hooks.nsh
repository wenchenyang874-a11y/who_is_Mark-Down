!macro WimdWriteRestartRequest PHASE
  Push $0
  Push $1
  CreateDirectory "$LOCALAPPDATA\WIMD"
  System::Call 'kernel32::GetTickCount()i .r1'
  IntFmt $1 "%08X" $r1
  FileOpen $0 "$LOCALAPPDATA\WIMD\update-restart.request" w
  FileWrite $0 "${PHASE}:wimd-$1"
  FileClose $0
  Pop $1
  Pop $0
!macroend

!macro WimdPromoteRestartRequest
  ; Capture and restore must use the same token. Reuse the token already written
  ; by PREINSTALL instead of generating a second value.
  !define UniqueID ${__LINE__}
  Push $0
  Push $1
  Push $2
  ClearErrors
  FileOpen $0 "$LOCALAPPDATA\WIMD\update-restart.request" r
  IfErrors wimd_restart_missing_${UniqueID}
  FileRead $0 $1
  FileClose $0
  StrCpy $2 $1 8
  StrCmp $2 "capture:" 0 wimd_restart_missing_${UniqueID}
  StrCpy $1 $1 -1 8
  StrCpy $1 "restore:$1"
  FileOpen $0 "$LOCALAPPDATA\WIMD\update-restart.request" w
  FileWrite $0 $1
  FileClose $0
  Goto wimd_restart_done_${UniqueID}

wimd_restart_missing_${UniqueID}:
  !insertmacro WimdWriteRestartRequest "restore"

wimd_restart_done_${UniqueID}:
  Pop $2
  Pop $1
  Pop $0
  !undef UniqueID
!macroend

!macro NSIS_HOOK_PREINSTALL
  !insertmacro WimdWriteRestartRequest "capture"
  ; Restart Manager force-shutdown bypasses Tauri's close-requested handler.
  ; Ask every matching WIMD window to close normally first so the frontend can
  ; finish its update snapshot. Restart Manager remains the timeout fallback.
  Push $0
  InitPluginsDir
  File /oname=$PLUGINSDIR\wimd-updater-helper.exe "${__FILEDIR__}\..\..\wimd-updater-helper.exe"
  ExecWait '"$PLUGINSDIR\wimd-updater-helper.exe" "$INSTDIR\${MAINBINARYNAME}.exe"' $0
  Pop $0
!macroend

!macro NSIS_HOOK_POSTINSTALL
  !insertmacro WimdPromoteRestartRequest
  ; Older Rust preview installers used the same root Start Menu shortcut name
  ; as the legacy .NET application. Tauri creates the new Rust shortcut in a
  ; separate folder, but an already overwritten root shortcut must be repaired
  ; from the legacy installer's registered install location.
  Push $0
  Push $1
  ReadRegStr $0 HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\{A278F55A-4D41-46B4-A1D9-DA41D8C7D655}_is1" "InstallLocation"
  StrCmp $0 "" wimd_repair_legacy_shortcut_without_install
  IfFileExists "$0\WIMD.exe" 0 wimd_repair_legacy_shortcut_without_install
  Delete "$SMPROGRAMS\WIMD.lnk"
  SetOutPath "$0"
  CreateShortcut "$SMPROGRAMS\WIMD.lnk" "$0\WIMD.exe" "" "$0\WIMD.exe" 0
  SetOutPath "$INSTDIR"
  Goto wimd_repair_legacy_shortcut_done

wimd_repair_legacy_shortcut_without_install:
  ; Never remove an unrelated legacy shortcut. If the old installer is absent,
  ; delete only a root shortcut that still targets this Rust installation.
  !insertmacro IsShortcutTarget "$SMPROGRAMS\WIMD.lnk" "$INSTDIR\${MAINBINARYNAME}.exe"
  Pop $1
  StrCmp $1 1 0 wimd_repair_legacy_shortcut_done
  Delete "$SMPROGRAMS\WIMD.lnk"
wimd_repair_legacy_shortcut_done:
  Pop $1
  Pop $0

  ; Keep the Rust rewrite in a distinct ProgID. The legacy .NET installer used
  ; WIMD.Markdown; reusing it made Explorer and legacy integrations launch the
  ; Rust test build instead of the installed legacy application.
  WriteRegStr HKCU "Software\Classes\WIMD.Rust.Markdown" "" "Markdown 文档（Rust 预览版）"
  WriteRegStr HKCU "Software\Classes\WIMD.Rust.Markdown\DefaultIcon" "" "$INSTDIR\${MAINBINARYNAME}.exe,0"
  WriteRegStr HKCU "Software\Classes\WIMD.Rust.Markdown\shell\open\command" "" '"$INSTDIR\${MAINBINARYNAME}.exe" "%1"'
  WriteRegStr HKCU "Software\Classes\.md\OpenWithProgids" "WIMD.Rust.Markdown" ""
  WriteRegStr HKCU "Software\Classes\.markdown\OpenWithProgids" "WIMD.Rust.Markdown" ""

  ; Earlier Rust test packages wrote the shared ProgID. Remove it only when its
  ; command still points at this exact installation; never delete a legacy key.
  Push $0
  ReadRegStr $0 HKCU "Software\Classes\WIMD.Markdown\shell\open\command" ""
  StrCmp $0 '"$INSTDIR\${MAINBINARYNAME}.exe" "%1"' 0 wimd_keep_legacy_progid
  DeleteRegKey HKCU "Software\Classes\WIMD.Markdown"
  DeleteRegValue HKCU "Software\Classes\.md\OpenWithProgids" "WIMD.Markdown"
  DeleteRegValue HKCU "Software\Classes\.markdown\OpenWithProgids" "WIMD.Markdown"
wimd_keep_legacy_progid:
  Pop $0

  ; Older Rust test builds also reused the legacy Explorer verbs. Remove those
  ; entries only when their command belongs to this installation.
  Push $0
  ReadRegStr $0 HKCU "Software\Classes\SystemFileAssociations\.md\shell\WIMD\command" ""
  StrCmp $0 '"$INSTDIR\${MAINBINARYNAME}.exe" "%1"' 0 wimd_keep_legacy_md_verb
  DeleteRegKey HKCU "Software\Classes\SystemFileAssociations\.md\shell\WIMD"
wimd_keep_legacy_md_verb:
  Pop $0
  Push $0
  ReadRegStr $0 HKCU "Software\Classes\SystemFileAssociations\.markdown\shell\WIMD\command" ""
  StrCmp $0 '"$INSTDIR\${MAINBINARYNAME}.exe" "%1"' 0 wimd_keep_legacy_markdown_verb
  DeleteRegKey HKCU "Software\Classes\SystemFileAssociations\.markdown\shell\WIMD"
wimd_keep_legacy_markdown_verb:
  Pop $0
  Push $0
  ReadRegStr $0 HKCU "Software\Classes\Directory\shell\WIMD\command" ""
  StrCmp $0 '"$INSTDIR\${MAINBINARYNAME}.exe" "%1"' 0 wimd_keep_legacy_directory_verb
  DeleteRegKey HKCU "Software\Classes\Directory\shell\WIMD"
wimd_keep_legacy_directory_verb:
  Pop $0

  WriteRegStr HKCU "Software\Classes\SystemFileAssociations\.md\shell\WIMD.Rust" "" "用 WIMD Rust 预览版打开"
  WriteRegStr HKCU "Software\Classes\SystemFileAssociations\.md\shell\WIMD.Rust" "Icon" "$INSTDIR\${MAINBINARYNAME}.exe,0"
  WriteRegStr HKCU "Software\Classes\SystemFileAssociations\.md\shell\WIMD.Rust\command" "" '"$INSTDIR\${MAINBINARYNAME}.exe" "%1"'
  WriteRegStr HKCU "Software\Classes\SystemFileAssociations\.markdown\shell\WIMD.Rust" "" "用 WIMD Rust 预览版打开"
  WriteRegStr HKCU "Software\Classes\SystemFileAssociations\.markdown\shell\WIMD.Rust" "Icon" "$INSTDIR\${MAINBINARYNAME}.exe,0"
  WriteRegStr HKCU "Software\Classes\SystemFileAssociations\.markdown\shell\WIMD.Rust\command" "" '"$INSTDIR\${MAINBINARYNAME}.exe" "%1"'

  WriteRegStr HKCU "Software\Classes\Directory\shell\WIMD.Rust" "" "用 WIMD Rust 预览版打开"
  WriteRegStr HKCU "Software\Classes\Directory\shell\WIMD.Rust" "Icon" "$INSTDIR\${MAINBINARYNAME}.exe,0"
  WriteRegStr HKCU "Software\Classes\Directory\shell\WIMD.Rust\command" "" '"$INSTDIR\${MAINBINARYNAME}.exe" "%1"'
!macroend

!macro NSIS_HOOK_PREUNINSTALL
  Push $0
  ReadRegStr $0 HKCU "Software\Classes\WIMD.Markdown\shell\open\command" ""
  StrCmp $0 '"$INSTDIR\${MAINBINARYNAME}.exe" "%1"' 0 wimd_uninstall_keep_legacy_progid
  DeleteRegKey HKCU "Software\Classes\WIMD.Markdown"
  DeleteRegValue HKCU "Software\Classes\.md\OpenWithProgids" "WIMD.Markdown"
  DeleteRegValue HKCU "Software\Classes\.markdown\OpenWithProgids" "WIMD.Markdown"
wimd_uninstall_keep_legacy_progid:
  Pop $0
  Push $0
  ReadRegStr $0 HKCU "Software\Classes\SystemFileAssociations\.md\shell\WIMD\command" ""
  StrCmp $0 '"$INSTDIR\${MAINBINARYNAME}.exe" "%1"' 0 wimd_uninstall_keep_legacy_md_verb
  DeleteRegKey HKCU "Software\Classes\SystemFileAssociations\.md\shell\WIMD"
wimd_uninstall_keep_legacy_md_verb:
  Pop $0
  Push $0
  ReadRegStr $0 HKCU "Software\Classes\SystemFileAssociations\.markdown\shell\WIMD\command" ""
  StrCmp $0 '"$INSTDIR\${MAINBINARYNAME}.exe" "%1"' 0 wimd_uninstall_keep_legacy_markdown_verb
  DeleteRegKey HKCU "Software\Classes\SystemFileAssociations\.markdown\shell\WIMD"
wimd_uninstall_keep_legacy_markdown_verb:
  Pop $0
  Push $0
  ReadRegStr $0 HKCU "Software\Classes\Directory\shell\WIMD\command" ""
  StrCmp $0 '"$INSTDIR\${MAINBINARYNAME}.exe" "%1"' 0 wimd_uninstall_keep_legacy_directory_verb
  DeleteRegKey HKCU "Software\Classes\Directory\shell\WIMD"
wimd_uninstall_keep_legacy_directory_verb:
  Pop $0
  DeleteRegKey HKCU "Software\Classes\SystemFileAssociations\.md\shell\WIMD.Rust"
  DeleteRegKey HKCU "Software\Classes\SystemFileAssociations\.markdown\shell\WIMD.Rust"
  DeleteRegKey HKCU "Software\Classes\Directory\shell\WIMD.Rust"
  DeleteRegValue HKCU "Software\Classes\.md\OpenWithProgids" "WIMD.Rust.Markdown"
  DeleteRegValue HKCU "Software\Classes\.markdown\OpenWithProgids" "WIMD.Rust.Markdown"
  DeleteRegKey HKCU "Software\Classes\WIMD.Rust.Markdown"
!macroend
