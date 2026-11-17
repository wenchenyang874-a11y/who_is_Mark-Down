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

!macro NSIS_HOOK_PREINSTALL
  !insertmacro WimdWriteRestartRequest "capture"
!macroend

!macro NSIS_HOOK_POSTINSTALL
  !insertmacro WimdWriteRestartRequest "restore"
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
