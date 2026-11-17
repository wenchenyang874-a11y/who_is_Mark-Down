import {
  defaultShortcutMap,
  formatError,
  getCurrentWindow,
  invoke,
  isTauri,
  listen,
  pathDirectory,
  pathFileName,
} from "./api.js";
import { MarkdownEditor } from "./editor.js";
import { PreviewController } from "./preview.js";
import { THEME_OPTIONS, themeOption } from "./theme-catalog.js";

const dom = {
  root: document.documentElement,
  appShell: document.getElementById("app-shell"),
  mainLayout: document.getElementById("main-layout"),
  tabs: document.getElementById("tabs"),
  tabStrip: document.querySelector(".tab-strip"),
  newTab: document.getElementById("new-tab"),
  documentPath: document.getElementById("document-path"),
  sidebar: document.getElementById("sidebar"),
  sidebarResizer: document.getElementById("sidebar-resizer"),
  sidebarTitle: document.getElementById("sidebar-title"),
  sidebarSubtitle: document.getElementById("sidebar-subtitle"),
  sidebarContent: document.getElementById("sidebar-content"),
  sidebarCollapse: document.getElementById("sidebar-collapse"),
  sidebarExpand: document.getElementById("sidebar-expand"),
  editor: document.getElementById("editor"),
  highlight: document.getElementById("highlight-layer"),
  lineNumbers: document.getElementById("line-numbers"),
  editorTitle: document.getElementById("editor-title"),
  splitter: document.getElementById("splitter"),
  editorPane: document.getElementById("editor-pane"),
  previewPane: document.getElementById("preview-pane"),
  previewFrame: document.getElementById("preview-frame"),
  previewSummary: document.getElementById("preview-heading-summary"),
  readOnlyBadge: document.getElementById("read-only-badge"),
  documentActionStatus: document.getElementById("document-action-status"),
  dirtyStatus: document.getElementById("dirty-status"),
  processStatus: document.getElementById("process-status"),
  cursorStatus: document.getElementById("cursor-status"),
  selectionStatus: document.getElementById("selection-status"),
  renderStatus: document.getElementById("render-status"),
  wordStatus: document.getElementById("word-status"),
  themeStatus: document.getElementById("theme-status"),
  findBar: document.getElementById("find-bar"),
  findInput: document.getElementById("find-input"),
  replaceField: document.getElementById("replace-field"),
  replaceInput: document.getElementById("replace-input"),
  findCaseSensitive: document.getElementById("find-case-sensitive"),
  findPrevious: document.getElementById("find-previous"),
  findNext: document.getElementById("find-next"),
  replaceCurrent: document.getElementById("replace-current"),
  replaceAll: document.getElementById("replace-all"),
  findClose: document.getElementById("find-close"),
  contextMenu: document.getElementById("context-menu"),
  toastStack: document.getElementById("toast-stack"),
  tableDialog: document.getElementById("table-dialog"),
  tableRows: document.getElementById("table-rows"),
  tableColumns: document.getElementById("table-columns"),
  insertTableConfirm: document.getElementById("insert-table-confirm"),
  settingsDialog: document.getElementById("settings-dialog"),
  settingsTabs: document.getElementById("settings-tabs"),
  themeGrid: document.getElementById("theme-grid"),
  editorFontFamily: document.getElementById("editor-font-family"),
  editorFontSize: document.getElementById("editor-font-size"),
  previewFontFamily: document.getElementById("preview-font-family"),
  previewFontSize: document.getElementById("preview-font-size"),
  backgroundPreview: document.getElementById("background-preview"),
  backgroundChoose: document.getElementById("background-choose"),
  backgroundRemove: document.getElementById("background-remove"),
  backgroundOpacity: document.getElementById("background-opacity"),
  backgroundOpacityValue: document.getElementById("background-opacity-value"),
  fileOpenViewMode: document.getElementById("file-open-view-mode"),
  orderedListStyle: document.getElementById("ordered-list-style"),
  orderedListFileStyle: document.getElementById("ordered-list-file-style"),
  sidebarVisible: document.getElementById("sidebar-visible"),
  externalFileDefault: document.getElementById("external-file-default"),
  externalFileCurrent: document.getElementById("external-file-current"),
  checkUpdatesStartup: document.getElementById("check-updates-startup"),
  imageStorageMode: document.getElementById("image-storage-mode"),
  localImageSettings: document.getElementById("local-image-settings"),
  imgbbImageSettings: document.getElementById("imgbb-image-settings"),
  localImageDirectory: document.getElementById("local-image-directory"),
  imgbbApiKey: document.getElementById("imgbb-api-key"),
  imgbbKeyStatus: document.getElementById("imgbb-key-status"),
  imgbbKeyClear: document.getElementById("imgbb-key-clear"),
  shortcutList: document.getElementById("shortcut-list"),
  resetShortcuts: document.getElementById("reset-shortcuts"),
  settingsSave: document.getElementById("settings-save"),
  settingsStatus: document.getElementById("settings-status"),
  unsavedDialog: document.getElementById("unsaved-dialog"),
  unsavedMessage: document.getElementById("unsaved-message"),
  aboutDialog: document.getElementById("about-dialog"),
  aboutVersion: document.getElementById("about-version"),
  aboutSettingsPath: document.getElementById("about-settings-path"),
  cancelUpdateDownload: document.getElementById("cancel-update-download"),
};

const bundledFonts = [
  { id: "wimd:lxgw-wenkai", label: "霞鹜文楷", family: "LXGW WenKai" },
  { id: "wimd:lxgw-wenkai-mono", label: "霞鹜文楷等宽", family: "LXGW WenKai Mono" },
  { id: "wimd:lxgw-marker-gothic", label: "霞鹜漫黑", family: "LXGW Marker Gothic" },
  { id: "wimd:jetbrains-mono", label: "JetBrains Mono", family: "JetBrains Mono" },
  { id: "wimd:fira-code", label: "Fira Code", family: "Fira Code" },
];

const state = {
  bootstrap: null,
  settings: null,
  shortcuts: [],
  shortcutMap: {},
  documents: [],
  activeId: null,
  untitledCounter: 0,
  renderTimer: null,
  renderSequence: 0,
  previewReady: false,
  previewInitializing: null,
  workspaceRoot: null,
  workspaceEntries: [],
  workspaceExpanded: new Set(),
  workspaceRefreshTimer: null,
  externalTimers: new Map(),
  suppressExternalUntil: 0,
  pendingTable: null,
  scrollEchoUntil: 0,
  editorScrollTimer: null,
  closingWindow: false,
  dirtyPromise: null,
  settingsDraft: null,
  backgroundDataUri: "",
  processTimer: null,
  previousProcessSample: null,
  previousProcessSampleAt: 0,
  recoveryTimer: null,
  recoveryWritePromise: null,
  heartbeatTimer: null,
  updateDownloading: false,
  updateDownloadStartedAt: 0,
  startupPreviewMarked: false,
};

const editor = new MarkdownEditor({
  textarea: dom.editor,
  highlight: dom.highlight,
  lineNumbers: dom.lineNumbers,
  onChange: handleEditorChange,
  onSelection: handleEditorSelection,
  onScroll: handleEditorScroll,
});

const preview = new PreviewController({
  frame: dom.previewFrame,
  onTaskToggle: handleTaskToggle,
  onCopyCode: handleCopyCode,
  onOpenLink: handleOpenLink,
  onOpenImage: ({ source, title }) => void openImageViewer(source, title),
  onImageContextMenu: handleImageContextMenu,
  onLoadLocalImage: (path, source) => invoke("load_image", { documentPath: path, source }),
  onScroll: handlePreviewScroll,
  onNavigateSourceLine: handlePreviewSourceNavigation,
});

async function initialize() {
  bindUi();
  startUiHeartbeat();
  window.addEventListener("error", () => {
    void invoke("report_frontend_error", { kind: "UnhandledError" }).catch(() => {});
  });
  window.addEventListener("unhandledrejection", () => {
    void invoke("report_frontend_error", { kind: "UnhandledRejection" }).catch(() => {});
  });
  try {
    state.bootstrap = await invoke("bootstrap");
    state.settings = state.bootstrap.settings;
    state.shortcuts = state.bootstrap.shortcuts;
    state.shortcutMap = buildShortcutMap();
    applySettings();
    await restoreSavedBackground();
    dom.aboutVersion.textContent = state.bootstrap.version;
    dom.aboutSettingsPath.textContent = state.bootstrap.settingsPath;
    if (state.bootstrap.settingsWarning) {
      toast(state.bootstrap.settingsWarning, "error", 6000);
    }
  } catch (error) {
    state.settings = fallbackSettings();
    state.shortcuts = [
      { id: "save", label: "保存", defaultGesture: "Ctrl+S" },
      { id: "open", label: "打开文档", defaultGesture: "Ctrl+O" },
      { id: "reload", label: "重新加载", defaultGesture: "F5" },
      { id: "cycle-view", label: "切换视图", defaultGesture: "F9" },
    ];
    state.shortcutMap = buildShortcutMap();
    applySettings();
    toast(`初始化失败：${formatError(error)}`, "error", 7000);
  }

  createDocument({ activate: true });
  renderEverything();
  bindExternalFileEvents();
  bindSettingsChangedEvents();
  bindUpdateDownloadEvents();
  bindWindowDragDrop();
  bindWindowClose();
  startProcessStatusSampling();
  await restoreRecoverySnapshots();
  await invoke("consume_installer_restore").catch(() => false);
  await openStartupPaths();
  await markInitialStartupPreview();
  if (state.settings.checkForUpdatesOnStartup) {
    void checkForUpdates({ silent: true });
  }
  if (!isTauri) {
    dom.previewSummary.textContent = "浏览器开发预览";
  }
}

function startUiHeartbeat() {
  if (!isTauri || state.heartbeatTimer) return;
  const beat = () => {
    void invoke("ui_heartbeat").catch(() => {});
  };
  beat();
  state.heartbeatTimer = window.setInterval(beat, 1000);
}

async function markInitialStartupPreview() {
  if (state.startupPreviewMarked) return;
  const documentModel = activeDocument();
  if (!documentModel || documentModel.viewMode === "editorOnly") {
    state.startupPreviewMarked = true;
    await invoke("mark_startup_preview", { ready: false }).catch(() => {});
    return;
  }
  const ready = await schedulePreview({ preservePosition: true, immediate: true });
  state.startupPreviewMarked = true;
  await invoke("mark_startup_preview", { ready: Boolean(ready) }).catch(() => {});
}

function startProcessStatusSampling() {
  const sample = async () => {
    try {
      const current = await invoke("current_process_usage");
      const now = performance.now();
      let cpu = null;
      if (state.previousProcessSample) {
        const elapsed = now - state.previousProcessSampleAt;
        const delta =
          current.processorTime100ns - state.previousProcessSample.processorTime100ns;
        if (elapsed > 0 && delta > 0) {
          cpu =
            (delta / 10_000 / elapsed / Math.max(1, current.logicalProcessors)) * 100;
          cpu = Math.max(0, Math.min(100, cpu));
        } else {
          cpu = 0;
        }
      }
      state.previousProcessSample = current;
      state.previousProcessSampleAt = now;
      const memory =
        current.workingSetBytes >= 1024 * 1024 * 1024
          ? `${(current.workingSetBytes / 1024 / 1024 / 1024).toFixed(2)} GB`
          : `${(current.workingSetBytes / 1024 / 1024).toFixed(1)} MB`;
      dom.processStatus.textContent = `CPU ${cpu == null ? "--" : `${cpu.toFixed(1)}%`} · 内存 ${memory}`;
      dom.processStatus.classList.remove("status-error");
    } catch {
      dom.processStatus.textContent = "CPU -- · 内存 --";
      dom.processStatus.classList.add("status-error");
    }
  };
  void sample();
  state.processTimer = window.setInterval(sample, 1000);
}

function bindUi() {
  document.addEventListener("click", handleDocumentClick);
  document.addEventListener("contextmenu", (event) => {
    if (!event.target.closest("[data-recent-path], [data-workspace-path]")) hideContextMenu();
  });
  document.addEventListener("keydown", handleGlobalKeydown, { capture: true });
  dom.newTab.addEventListener("click", () => createDocument({ activate: true }));
  dom.sidebarCollapse.addEventListener("click", () => setSidebarVisible(false));
  dom.sidebarExpand.addEventListener("click", () => setSidebarVisible(true));
  dom.findClose.addEventListener("click", closeFind);
  dom.findNext.addEventListener("click", () => runFind(true));
  dom.findPrevious.addEventListener("click", () => runFind(false));
  dom.replaceCurrent.addEventListener("click", runReplaceCurrent);
  dom.replaceAll.addEventListener("click", runReplaceAll);
  dom.findInput.addEventListener("input", () => scheduleFind(true));
  dom.findInput.addEventListener("keydown", (event) => {
    if (event.key === "Enter") {
      event.preventDefault();
      runFind(!event.shiftKey);
    }
  });
  dom.replaceInput.addEventListener("keydown", (event) => {
    if (event.key === "Enter") {
      event.preventDefault();
      runReplaceCurrent();
    }
  });

  dom.tabs.addEventListener("click", handleTabClick);
  dom.tabs.addEventListener("auxclick", (event) => {
    if (event.button !== 1) return;
    const close = event.target.closest("[data-tab-close]");
    if (close) void closeDocument(close.dataset.tabClose);
  });

  dom.sidebarContent.addEventListener("click", handleSidebarClick);
  dom.sidebarContent.addEventListener("contextmenu", handleSidebarContextMenu);

  dom.editor.addEventListener("paste", handleEditorPaste);

  dom.insertTableConfirm.addEventListener("click", confirmInsertTable);
  dom.settingsTabs.addEventListener("click", handleSettingsTabClick);
  dom.themeGrid.addEventListener("click", handleThemeClick);
  dom.backgroundChoose.addEventListener("click", chooseBackgroundImage);
  dom.backgroundRemove.addEventListener("click", removeBackgroundImage);
  dom.backgroundOpacity.addEventListener("input", () => {
    dom.backgroundOpacityValue.textContent = `${dom.backgroundOpacity.value}%`;
  });
  dom.imageStorageMode.addEventListener("change", updateImageSettingsVisibility);
  dom.imgbbKeyClear.addEventListener("click", clearImgbbApiKey);
  dom.settingsSave.addEventListener("click", saveSettingsFromDialog);
  dom.resetShortcuts.addEventListener("click", resetShortcutDrafts);
  dom.shortcutList.addEventListener("keydown", captureShortcutGesture);
  dom.shortcutList.addEventListener("focusin", (event) => {
    if (event.target.matches("[data-shortcut-input]")) event.target.select();
  });

  document.querySelectorAll("[data-close-dialog]").forEach((button) => {
    button.addEventListener("click", () => closeDialog(button.dataset.closeDialog));
  });
  dom.unsavedDialog.addEventListener("click", handleUnsavedDialogClick);
  dom.contextMenu.addEventListener("click", handleContextMenuCommand);
  dom.splitter.addEventListener("pointerdown", beginSplitterDrag);
  dom.splitter.addEventListener("keydown", handleSplitterKeydown);
  dom.sidebarResizer.addEventListener("pointerdown", beginSidebarResize);
  dom.sidebarResizer.addEventListener("keydown", handleSidebarResizeKeydown);
  document.addEventListener("pointerdown", (event) => {
    if (!event.target.closest(".menu")) closeMenus();
    if (!event.target.closest("#context-menu")) hideContextMenu();
  });
  document.querySelectorAll(".menu-trigger").forEach((button) => {
    button.addEventListener("click", (event) => {
      event.stopPropagation();
      const menu = button.closest(".menu");
      const wasOpen = menu.classList.contains("open");
      closeMenus();
      menu.classList.toggle("open", !wasOpen);
    });
  });
}

function bindExternalFileEvents() {
  void listen("wimd://document-changed", (event) => {
    const path = event.payload?.path;
    if (!path) return;
    const previous = state.externalTimers.get(path);
    if (previous) window.clearTimeout(previous);
    const timer = window.setTimeout(() => inspectExternalChange(path), 320);
    state.externalTimers.set(path, timer);
  });
  void listen("wimd://workspace-changed", (event) => {
    const root = event.payload?.path;
    if (!root || !state.workspaceRoot || !samePath(root, state.workspaceRoot)) return;
    window.clearTimeout(state.workspaceRefreshTimer);
    state.workspaceRefreshTimer = window.setTimeout(() => {
      void refreshWorkspace().catch((error) =>
        toast(`刷新工作区失败：${formatError(error)}`, "error", 6000),
      );
    }, 260);
  });
}

function bindSettingsChangedEvents() {
  void listen("wimd://settings-changed", async (event) => {
    if (!event.payload || typeof event.payload !== "object") return;
    state.settings = event.payload;
    state.shortcutMap = buildShortcutMap();
    applySettings();
    await restoreSavedBackground();
    renderSidebar();
    updateDocumentChrome();
    updateStatus();
    schedulePreview({ preservePosition: true, immediate: true });
  });
}

function bindUpdateDownloadEvents() {
  void listen("wimd://update-download-progress", (event) => {
    if (!state.updateDownloading) return;
    const downloaded = Number(event.payload?.downloadedBytes) || 0;
    const total = Number(event.payload?.totalBytes) || 0;
    if (total <= 0) return;
    const percent = Math.min(100, Math.max(0, (downloaded / total) * 100));
    const elapsed = Math.max(0.001, (Date.now() - state.updateDownloadStartedAt) / 1000);
    const speed = downloaded / elapsed / 1024 / 1024;
    dom.renderStatus.textContent =
      `正在下载更新 ${percent.toFixed(0)}% · ${speed.toFixed(1)} MiB/s`;
  });
}

function bindWindowDragDrop() {
  const current = window.__TAURI__?.webviewWindow?.getCurrentWebviewWindow?.();
  if (!current?.onDragDropEvent) return;
  void current.onDragDropEvent(async (event) => {
    if (event.payload?.type !== "drop") return;
    for (const path of event.payload.paths || []) {
      try {
        if (/\.(?:md|markdown)$/i.test(path)) await openDocument(path);
        else await openWorkspacePath(path);
      } catch (error) {
        toast(`拖入文件失败：${path}：${formatError(error)}`, "error", 7000);
      }
    }
  });
}

function bindWindowClose() {
  const appWindow = getCurrentWindow();
  if (!appWindow?.onCloseRequested) return;
  void appWindow.onCloseRequested(async (event) => {
    if (state.closingWindow) return;
    event.preventDefault();
    const installerCapture = await withTimeout(
      invoke("installer_capture_requested").catch(() => false),
      2_000,
      false,
    );
    if (!installerCapture) {
      const canClose = await confirmCloseAllDocuments();
      if (!canClose) return;
    }
    state.closingWindow = true;
    window.clearTimeout(state.recoveryTimer);
    window.clearInterval(state.processTimer);
    window.clearInterval(state.heartbeatTimer);
    const cleanup = (async () => {
      if (state.recoveryWritePromise) {
        await state.recoveryWritePromise.catch(() => {});
      }
      if (installerCapture) {
        await persistDirtyRecoverySnapshots({ force: true, includeClean: true });
      }
      await persistCurrentSessionSettings().catch(() => {});
      if (!installerCapture) {
        for (const documentModel of state.documents) {
          await invoke("remove_recovery_snapshot", {
            id: documentModel.id,
          }).catch(() => {});
        }
      }
      await invoke("close_image_viewer").catch(() => {});
    })();
    await withTimeout(cleanup, installerCapture ? 10_000 : 4_000, null);
    // The host owns final process termination so WebView2 cannot deadlock an
    // onCloseRequested callback by waiting on its own window.
    queueMicrotask(() => {
      void invoke("exit_application").catch(() => {
        const closePromise =
          typeof appWindow.destroy === "function"
            ? appWindow.destroy()
            : appWindow.close();
        Promise.resolve(closePromise).catch(() => {});
      });
    });
  });
}

function withTimeout(promise, milliseconds, fallback) {
  return Promise.race([
    promise,
    new Promise((resolve) => {
      window.setTimeout(() => resolve(fallback), milliseconds);
    }),
  ]);
}

async function openStartupPaths() {
  const paths = [...(state.bootstrap?.startupPaths || [])];
  if (!paths.length) return;
  const errors = [];
  for (const path of paths) {
    try {
      if (/\.(?:md|markdown)$/i.test(path)) {
        await openDocument(path, { activate: false, recordRecent: true });
      } else {
        await openWorkspacePath(path, { activate: true });
      }
    } catch (error) {
      errors.push(`${path}：${formatError(error)}`);
    }
  }
  if (state.documents.length > 1) {
    const latest = state.documents.at(-1);
    if (latest) activateDocument(latest.id);
  }
  if (errors.length) toast(errors.join("\n"), "error", 8000);
}

async function restoreRecoverySnapshots() {
  let snapshots = [];
  try {
    snapshots = await invoke("list_recovery_snapshots");
  } catch (error) {
    toast(`读取恢复快照失败：${formatError(error)}`, "error", 6000);
    return;
  }
  for (const snapshot of snapshots) {
    const cleanFileSnapshot =
      snapshot.originalPath &&
      snapshot.text === snapshot.savedText;
    if (cleanFileSnapshot) {
      try {
        const opened = await openDocument(snapshot.originalPath, {
          activate: false,
          recordRecent: true,
        });
        opened.viewMode = snapshot.viewMode || opened.viewMode;
        await invoke("remove_recovery_snapshot", {
          id: snapshot.id,
        }).catch(() => {});
        continue;
      } catch {
        // Fall through so the user can still recover the copied text if the
        // original file disappeared during the installer-controlled restart.
      }
    }
    const restore = window.confirm(
      `发现上次未正常关闭时的未保存文档：\n${snapshot.title || snapshot.originalPath || snapshot.id}\n\n是否恢复？`,
    );
    if (!restore) {
      await invoke("remove_recovery_snapshot", { id: snapshot.id }).catch(() => {});
      continue;
    }
    const existing = state.documents.find((candidate) =>
      samePath(candidate.path, snapshot.originalPath),
    );
    if (existing) {
      existing.text = snapshot.text;
      existing.savedText = snapshot.savedText;
      existing.hadUtf8Bom = snapshot.hadUtf8Bom;
      if (state.activeId === existing.id) {
        editor.setValue(existing.text, { scrollTop: existing.scrollTop });
      }
      continue;
    }
    createDocument({
      id: snapshot.id,
      activate: false,
      path: snapshot.originalPath,
      title: snapshot.title,
      text: snapshot.text,
      savedText: snapshot.savedText,
      hadUtf8Bom: snapshot.hadUtf8Bom,
      viewMode: snapshot.viewMode,
      selectionStart: snapshot.selectionStart || 0,
      selectionEnd: snapshot.selectionEnd || 0,
      scrollTop: Number(snapshot.scrollTop) || 0,
      previewScrollRatio: Number(snapshot.previewScrollRatio) || 0,
    });
    if (snapshot.originalPath) {
      await invoke("watch_document", { path: snapshot.originalPath }).catch(() => {});
    }
  }
  if (snapshots.length) renderEverything();
}

function createDocument({
  id = null,
  activate = true,
  path = null,
  title = null,
  text = "",
  savedText = text,
  hadUtf8Bom = false,
  lineEnding = "none",
  readOnly = false,
  stamp = null,
  viewMode = null,
  selectionStart = 0,
  selectionEnd = selectionStart,
  scrollTop = 0,
  previewScrollRatio = 0,
} = {}) {
  id = id || `doc-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
  if (!path && !title) state.untitledCounter += 1;
  const documentModel = {
    id,
    path,
    title: title || pathFileName(path) || `未命名-${state.untitledCounter}`,
    text,
    savedText,
    hadUtf8Bom,
    lineEnding,
    readOnly,
    stamp,
    viewMode: viewMode || state.settings.lastViewMode || "editorAndPreview",
    selectionStart,
    selectionEnd,
    scrollTop,
    previewScrollRatio,
  };
  state.documents.push(documentModel);
  if (activate || !state.activeId) activateDocument(id);
  renderTabs();
  return documentModel;
}

function activateDocument(id) {
  const current = activeDocument();
  if (current) {
    current.selectionStart = editor.selectionStart;
    current.selectionEnd = editor.selectionEnd;
    current.scrollTop = dom.editor.scrollTop;
  }
  const target = state.documents.find((documentModel) => documentModel.id === id);
  if (!target) return;
  state.activeId = id;
  editor.setValue(target.text, {
    selectionStart: target.selectionStart,
    selectionEnd: target.selectionEnd,
    scrollTop: target.scrollTop,
  });
  dom.editor.readOnly = target.readOnly;
  dom.readOnlyBadge.hidden = !target.readOnly;
  applyViewMode(target.viewMode, { persist: false });
  updateDocumentChrome();
  renderTabs();
  renderSidebar();
  updateStatus();
  schedulePreview({ preservePosition: true });
}

async function closeDocument(id) {
  const documentModel = state.documents.find((candidate) => candidate.id === id);
  if (!documentModel) return false;
  if (isDirty(documentModel) && !(await confirmUnsavedDocument(documentModel))) return false;
  await invoke("remove_recovery_snapshot", { id: documentModel.id }).catch(() => {});
  const index = state.documents.findIndex((candidate) => candidate.id === id);
  if (index === -1) return false;
  state.documents.splice(index, 1);
  if (state.activeId === id) {
    const next = state.documents[Math.max(0, index - 1)] || state.documents[index] || null;
    if (next) {
      state.activeId = next.id;
      activateDocument(next.id);
    } else {
      state.activeId = null;
      createDocument({ activate: true });
    }
  } else {
    renderTabs();
    renderSidebar();
  }
  return true;
}

async function confirmCloseAllDocuments() {
  for (const documentModel of [...state.documents]) {
    if (!isDirty(documentModel)) continue;
    const accepted = await confirmUnsavedDocument(documentModel);
    if (!accepted) return false;
  }
  return true;
}

function confirmUnsavedDocument(documentModel) {
  if (state.dirtyPromise) return state.dirtyPromise;
  dom.unsavedMessage.textContent = `“${documentModel.title}”有未保存的修改。`;
  dom.unsavedDialog.dataset.documentId = documentModel.id;
  state.dirtyPromise = new Promise((resolve) => {
    dom.unsavedDialog.dataset.resolve = "pending";
    dom.unsavedDialog._resolver = resolve;
    dom.unsavedDialog.showModal();
  });
  return state.dirtyPromise;
}

async function handleUnsavedDialogClick(event) {
  const action = event.target.closest("[data-unsaved-action]")?.dataset.unsavedAction;
  if (!action) return;
  const documentModel = state.documents.find(
    (candidate) => candidate.id === dom.unsavedDialog.dataset.documentId,
  );
  const resolver = dom.unsavedDialog._resolver;
  if (!resolver) return;
  if (action === "save" && documentModel) {
    try {
      const saved = await saveDocument(documentModel, { saveAs: !documentModel.path });
      if (!saved) return;
    } catch (error) {
      toast(`保存失败：${formatError(error)}`, "error", 7000);
      return;
    }
  }
  resolver(action !== "cancel");
  dom.unsavedDialog._resolver = null;
  state.dirtyPromise = null;
  delete dom.unsavedDialog.dataset.documentId;
  dom.unsavedDialog.close();
}

async function openDocument(path, { activate = true, recordRecent = true } = {}) {
  const existing = state.documents.find(
    (documentModel) => samePath(documentModel.path, path),
  );
  if (existing) {
    if (activate) activateDocument(existing.id);
    return existing;
  }
  const loaded = await invoke("load_document", { path });
  const documentModel = createDocument({
    activate,
    path: loaded.path,
    title: pathFileName(loaded.path),
    text: loaded.text,
    savedText: loaded.text,
    hadUtf8Bom: loaded.hadUtf8Bom,
    lineEnding: loaded.lineEnding,
    readOnly: loaded.readOnly,
    stamp: loaded.stamp,
    viewMode: resolveOpenViewMode(),
  });
  if (recordRecent) await recordRecent(loaded.path);
  await invoke("watch_document", { path: loaded.path }).catch(() => {});
  renderEverything();
  return documentModel;
}

async function openWorkspacePath(path, { activate = true } = {}) {
  const workspace = await invoke("open_workspace", { path });
  if (!samePath(state.workspaceRoot, workspace.root)) {
    state.workspaceExpanded.clear();
  }
  state.workspaceRoot = workspace.root;
  state.workspaceEntries = workspace.entries || [];
  state.settings.lastWorkspace = workspace.root;
  await invoke("watch_workspace", { root: workspace.root }).catch(() => {});
  await recordRecentWorkspace(workspace.root);
  renderSidebar();
  if (activate) {
    const firstFile = await firstWorkspaceFileAsync(state.workspaceEntries);
    if (firstFile) {
      await openDocument(firstFile.path, { activate: true, recordRecent: true });
    }
  }
  toast(`已打开工作区：${workspace.root}`, "success");
}

async function saveDocument(documentModel, { saveAs = false } = {}) {
  let path = documentModel.path;
  if (!path || saveAs || documentModel.readOnly) {
    path = await invoke("pick_save_path", {
      suggestedName: pathFileName(documentModel.title || "未命名.md"),
    });
    if (!path) return false;
  }
  const stamp = await invoke("save_document", {
    path,
    text: documentModel.text,
    hadUtf8Bom: documentModel.hadUtf8Bom,
  });
  state.suppressExternalUntil = Date.now() + 1200;
  documentModel.path = path;
  documentModel.title = pathFileName(path);
  documentModel.savedText = documentModel.text;
  documentModel.stamp = stamp;
  documentModel.readOnly = false;
  dom.editor.readOnly = false;
  await invoke("remove_recovery_snapshot", { id: documentModel.id }).catch(() => {});
  await recordRecent(path);
  await invoke("watch_document", { path }).catch(() => {});
  renderEverything();
  toast(`已保存：${path}`, "success");
  return true;
}

async function reloadActiveDocument() {
  const documentModel = activeDocument();
  if (!documentModel?.path) return;
  if (isDirty(documentModel) && !(await confirmUnsavedDocument(documentModel))) return;
  const loaded = await invoke("load_document", { path: documentModel.path });
  documentModel.text = loaded.text;
  documentModel.savedText = loaded.text;
  documentModel.hadUtf8Bom = loaded.hadUtf8Bom;
  documentModel.lineEnding = loaded.lineEnding;
  documentModel.readOnly = loaded.readOnly;
  dom.editor.readOnly = loaded.readOnly;
  documentModel.stamp = loaded.stamp;
  editor.setValue(loaded.text, { scrollTop: documentModel.scrollTop });
  preview.clearFolds();
  preview.clearImageCache();
  updateDocumentChrome();
  schedulePreview({ preservePosition: true });
  toast("已从磁盘重新加载文档", "success");
}

function handleEditorChange(text) {
  const documentModel = activeDocument();
  if (!documentModel) return;
  documentModel.text = text;
  updateDocumentChrome();
  updateStatus();
  renderTabs();
  schedulePreview();
  scheduleRecoverySnapshots();
}

function handleEditorSelection(info) {
  const documentModel = activeDocument();
  if (!documentModel) return;
  documentModel.selectionStart = info.offset;
  documentModel.selectionEnd = editor.selectionEnd;
  dom.cursorStatus.textContent = `第 ${info.line} 行，第 ${info.column} 列`;
  dom.selectionStatus.textContent =
    info.selectionLength > 0 ? `已选择 ${info.selectionLength} 字符` : "";
  dom.wordStatus.textContent = `${info.characterCount.toLocaleString()} 字符`;
  if (documentModel.viewMode !== "editorOnly" && state.previewReady) {
    preview.revealSourceLine(info.zeroBasedLine);
  }
}

function handleEditorScroll(snapshot) {
  const documentModel = activeDocument();
  if (!documentModel) return;
  documentModel.scrollTop = snapshot.top;
  if (Date.now() < state.scrollEchoUntil) return;
  if (!state.previewReady || documentModel.viewMode === "editorOnly") return;
  window.clearTimeout(state.editorScrollTimer);
  state.editorScrollTimer = window.setTimeout(() => {
    const ratio = snapshot.height <= snapshot.viewport ? 0 : snapshot.top / (snapshot.height - snapshot.viewport);
    preview.setScrollRatio(ratio);
  }, 35);
}

function handlePreviewScroll(snapshot) {
  const documentModel = activeDocument();
  if (!documentModel || snapshot.suppressed) return;
  documentModel.previewScrollRatio = snapshot.ratio;
  if (documentModel.viewMode === "previewOnly") return;
  state.scrollEchoUntil = Date.now() + 90;
  editor.setVerticalRatio(snapshot.ratio);
}

function handlePreviewSourceNavigation(target) {
  const section = target?.closest?.("[data-source-line]");
  const line = Number(section?.dataset.sourceLine);
  if (Number.isFinite(line)) editor.scrollToLine(line, { center: true });
}

async function handleTaskToggle(sourceLine, completed) {
  const documentModel = activeDocument();
  if (!documentModel) return;
  if (documentModel.readOnly) throw new Error("当前文档为只读。");
  const result = await invoke("toggle_task", {
    text: documentModel.text,
    sourceLine,
    completed,
  });
  if (!result.changed) return;
  const scrollRatio = preview.scrollRatio();
  documentModel.text = result.text;
  editor.setValue(result.text, {
    selectionStart: documentModel.selectionStart,
    selectionEnd: documentModel.selectionEnd,
    scrollTop: dom.editor.scrollTop,
  });
  updateDocumentChrome();
  renderTabs();
  await schedulePreview({ preservePosition: false, immediate: true });
  preview.setScrollRatio(scrollRatio);
}

async function handleCopyCode(code) {
  await invoke("copy_text", { text: code });
}

async function handleOpenLink(href) {
  const documentModel = activeDocument();
  try {
    if (/^(?:https?:|mailto:)/i.test(href)) {
      await invoke("open_external", { url: href });
      return;
    }
    if (documentModel?.path && /\.(?:md|markdown)(?:[?#].*)?$/i.test(href)) {
      const path = joinPath(
        pathDirectory(documentModel.path),
        decodeURIComponent(href.split(/[?#]/)[0]),
      );
      await openDocument(path);
      return;
    }
  } catch (error) {
    toast(`打开链接失败：${formatError(error)}`, "error");
    return;
  }
  toast("已阻止非 HTTP/HTTPS/mailto 链接。", "error");
}

function schedulePreview({ preservePosition = true, immediate = false } = {}) {
  const documentModel = activeDocument();
  if (!documentModel) return Promise.resolve(false);
  window.clearTimeout(state.renderTimer);
  if (documentModel.viewMode === "editorOnly") {
    dom.renderStatus.textContent = "仅编辑模式，预览未运行";
    return Promise.resolve(false);
  }
  const delay = immediate ? 0 : 120;
  return new Promise((resolve) => {
    state.renderTimer = window.setTimeout(async () => {
      const sequence = ++state.renderSequence;
      const started = performance.now();
      dom.renderStatus.textContent = "正在生成预览...";
      try {
        const rendered = await invoke("render_preview", { markdown: documentModel.text });
        if (sequence !== state.renderSequence || state.activeId !== documentModel.id) {
          resolve(false);
          return;
        }
        await preview.render(rendered, documentModel, resolvedPreviewSettings(documentModel.path), {
          preservePosition,
        });
        state.previewReady = true;
        dom.renderStatus.classList.remove("status-error");
        const elapsed = Math.round(performance.now() - started);
        dom.renderStatus.textContent = `预览 ${elapsed} ms · ${rendered.headings.length} 个标题`;
        dom.previewSummary.textContent = "离线安全";
        resolve(true);
      } catch (error) {
        await preview.renderError(formatError(error));
        dom.renderStatus.textContent = "预览失败";
        dom.renderStatus.classList.add("status-error");
        resolve(false);
      }
    }, delay);
  });
}

function applyViewMode(mode, { persist = true } = {}) {
  const normalized = ["editorOnly", "previewOnly", "editorAndPreview"].includes(mode)
    ? mode
    : "editorAndPreview";
  dom.mainLayout.classList.toggle("view-editor-only", normalized === "editorOnly");
  dom.mainLayout.classList.toggle("view-preview-only", normalized === "previewOnly");
  dom.mainLayout.classList.toggle("view-split", normalized === "editorAndPreview");
  const documentModel = activeDocument();
  if (documentModel) documentModel.viewMode = normalized;
  if (state.settings && persist) {
    state.settings.lastViewMode = normalized;
    scheduleSettingsPersistence();
  }
}

function cycleViewMode() {
  const current = activeDocument()?.viewMode || "editorAndPreview";
  const next =
    current === "previewOnly"
      ? "editorAndPreview"
      : current === "editorAndPreview"
        ? "editorOnly"
        : "previewOnly";
  applyViewMode(next);
  if (next !== "editorOnly") schedulePreview({ preservePosition: true });
}

function resolveOpenViewMode() {
  const setting = state.settings.fileOpenViewMode;
  if (setting === "editorOnly") return "editorOnly";
  if (setting === "previewOnly") return "previewOnly";
  if (setting === "editorAndPreview") return "editorAndPreview";
  return state.settings.lastViewMode || "editorAndPreview";
}

function updateDocumentChrome() {
  const documentModel = activeDocument();
  if (!documentModel) return;
  const dirty = isDirty(documentModel);
  const unsaved = !documentModel.path || dirty;
  dom.documentActionStatus.textContent = !documentModel.path
    ? "已新建文档"
    : "已打开文档";
  dom.dirtyStatus.textContent = unsaved
    ? "未保存"
    : documentModel.readOnly
      ? "只读"
      : "已保存";
  dom.dirtyStatus.classList.toggle("dirty", unsaved);
  dom.documentPath.textContent = documentModel.path || "未保存";
  dom.documentPath.title = documentModel.path || "";
  dom.documentPath.hidden = !documentModel.path;
  dom.readOnlyBadge.hidden = !documentModel.readOnly;
  dom.editorTitle.textContent = documentModel.title;
  dom.editorTitle.title = documentModel.path || documentModel.title;
  document.title = `${dirty ? "* " : ""}${documentModel.title} - WIMD`;
  const currentWindow = getCurrentWindow();
  if (currentWindow?.setTitle) {
    void currentWindow.setTitle(document.title).catch(() => {});
  }
}

function updateStatus() {
  const documentModel = activeDocument();
  if (!documentModel) return;
  const dirty = isDirty(documentModel);
  const unsaved = !documentModel.path || dirty;
  dom.dirtyStatus.textContent = unsaved
    ? "未保存"
    : documentModel.readOnly
      ? "只读"
      : "已保存";
  dom.dirtyStatus.classList.toggle("dirty", unsaved);
  dom.wordStatus.textContent = `${documentModel.text.length.toLocaleString()} 字符`;
  dom.themeStatus.textContent = themeOption(state.settings.theme).name;
}

function renderEverything() {
  renderTabs();
  renderSidebar();
  updateDocumentChrome();
  updateStatus();
}

function renderTabs() {
  dom.tabs.replaceChildren();
  for (const documentModel of state.documents) {
    const tab = document.createElement("div");
    tab.className = `tab${documentModel.id === state.activeId ? " active" : ""}`;
    tab.role = "tab";
    tab.tabIndex = 0;
    tab.dataset.tabId = documentModel.id;
    tab.setAttribute("aria-selected", String(documentModel.id === state.activeId));

    const title = document.createElement("span");
    title.className = "tab-title";
    title.textContent = documentModel.title;
    title.title = documentModel.path || documentModel.title;
    tab.append(title);

    if (isDirty(documentModel)) {
      const dirty = document.createElement("span");
      dirty.className = "tab-dirty";
      dirty.title = "未保存";
      tab.append(dirty);
    }

    const close = document.createElement("button");
    close.type = "button";
    close.className = "tab-close";
    close.dataset.tabClose = documentModel.id;
    close.title = "关闭文档";
    close.setAttribute("aria-label", `关闭 ${documentModel.title}`);
    close.textContent = "×";
    tab.append(close);
    dom.tabs.append(tab);
  }
  dom.tabStrip.hidden = state.documents.length <= 1;
}

function renderSidebar() {
  const visible = Boolean(state.settings?.sidebarVisible);
  dom.mainLayout.classList.toggle("sidebar-collapsed", !visible);
  dom.sidebarContent.replaceChildren();
  if (state.workspaceRoot) {
    dom.sidebarTitle.textContent = "工作区";
    dom.sidebarSubtitle.textContent = state.workspaceRoot;
    renderWorkspaceSidebar();
  } else {
    dom.sidebarTitle.textContent = "最近打开";
    dom.sidebarSubtitle.textContent = "打开文件夹后切换为资源管理器";
    renderRecentSidebar();
  }
}

function renderRecentSidebar() {
  const recentFiles = state.settings?.recentFiles || [];
  const recentWorkspaces = state.settings?.recentWorkspaces || [];
  if (!recentFiles.length && !recentWorkspaces.length) {
    dom.sidebarContent.innerHTML =
      '<div class="sidebar-empty"><span class="sidebar-empty-icon">◷</span><strong>还没有最近文件</strong><small>打开文档后会显示在这里</small></div>';
    return;
  }
  if (recentFiles.length) {
    for (const recent of recentFiles) {
      dom.sidebarContent.append(
        createRecentButton(recent.path, isActivePath(recent.path), "recentPath", recent.lastOpenedUtc),
      );
    }
  }
  if (recentWorkspaces.length) {
    dom.sidebarContent.append(createSidebarSubheading("文件夹"));
    for (const recent of recentWorkspaces) {
      dom.sidebarContent.append(
        createRecentButton(
          recent.path,
          isActiveWorkspacePath(recent.path),
          "recentWorkspace",
          recent.lastOpenedUtc,
        ),
      );
    }
  }
}

function createRecentButton(path, active, datasetName = "recentPath", openedAt = null) {
  const row = document.createElement("div");
  row.className = "recent-row";
  const button = document.createElement("button");
  button.type = "button";
  button.className = `recent-item${active ? " active" : ""}`;
  button.dataset[datasetName] = path;
  button.title = path;
  const copy = document.createElement("span");
  copy.className = "item-copy";
  const name = document.createElement("strong");
  name.textContent =
    datasetName === "recentWorkspace"
      ? path.split(/[\\/]/).filter(Boolean).at(-1) || path
      : pathFileName(path);
  const directory = document.createElement("small");
  directory.textContent =
    datasetName === "recentWorkspace" ? path : pathDirectory(path) || path;
  const date = document.createElement("small");
  date.className = "item-date";
  date.textContent = formatRecentDate(openedAt);
  copy.append(name, directory, date);
  button.append(copy);
  const remove = document.createElement("button");
  remove.type = "button";
  remove.className = "recent-remove";
  remove.title =
    datasetName === "recentWorkspace"
      ? "移出最近文件夹（不删除原文件夹）"
      : "移出最近文件（不删除原文件）";
  remove.setAttribute("aria-label", remove.title);
  remove.textContent = "×";
  if (datasetName === "recentWorkspace") {
    remove.dataset.recentWorkspaceRemove = path;
  } else {
    remove.dataset.recentRemove = path;
  }
  row.append(button, remove);
  return row;
}

function formatRecentDate(value) {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "";
  return new Intl.DateTimeFormat("zh-CN", {
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
  }).format(date);
}

function createSidebarSubheading(text) {
  const heading = document.createElement("div");
  heading.className = "sidebar-subheading";
  heading.textContent = text;
  return heading;
}

function renderWorkspaceSidebar() {
  const header = document.createElement("div");
  header.className = "workspace-header";
  const path = document.createElement("span");
  path.textContent = state.workspaceRoot;
  path.title = state.workspaceRoot;
  const actions = document.createElement("div");
  actions.className = "workspace-actions";
  actions.append(
    createMiniButton("+", "新建 Markdown 文件", "workspace-new-file"),
    createMiniButton("▱", "新建文件夹", "workspace-new-folder"),
    createMiniButton("↻", "刷新工作区", "workspace-refresh"),
    createMiniButton("×", "关闭工作区", "workspace-close"),
  );
  header.append(path, actions);
  dom.sidebarContent.append(header);

  const tree = document.createElement("div");
  tree.className = "workspace-tree";
  const children = document.createElement("div");
  children.className = "workspace-children-root";
  appendWorkspaceEntries(children, state.workspaceEntries);
  tree.append(children);
  dom.sidebarContent.append(tree);
}

function appendWorkspaceEntries(container, entries) {
  for (const entry of entries) {
    if (entry.isDirectory) {
      const details = document.createElement("details");
      details.open = state.workspaceExpanded.has(entry.path);
      const summary = document.createElement("summary");
      summary.dataset.workspacePath = entry.path;
      summary.dataset.workspaceDirectory = "true";
      summary.title = entry.path;
      const mark = document.createElement("span");
      mark.className = "workspace-folder-mark";
      mark.textContent = "▰";
      const name = document.createElement("span");
      name.textContent = entry.name;
      summary.append(mark, name);
      const children = document.createElement("div");
      children.className = "workspace-children";
      details.append(summary, children);
      details.addEventListener("toggle", () => {
        if (details.open) {
          state.workspaceExpanded.add(entry.path);
          void loadWorkspaceDirectory(entry, children);
        } else {
          state.workspaceExpanded.delete(entry.path);
        }
      });
      container.append(details);
      if (details.open) void loadWorkspaceDirectory(entry, children);
    } else {
      const button = document.createElement("button");
      button.type = "button";
      button.className = `workspace-file${isActivePath(entry.path) ? " active" : ""}`;
      button.dataset.workspacePath = entry.path;
      button.title = entry.path;
      const icon = document.createElement("span");
      icon.className = "item-icon";
      icon.textContent = "MD";
      const name = document.createElement("span");
      name.textContent = entry.name;
      button.append(icon, name);
      container.append(button);
    }
  }
}

async function loadWorkspaceDirectory(entry, container) {
  if (!container.isConnected) return;
  if (entry.childrenLoaded) {
    container.replaceChildren();
    appendWorkspaceEntries(container, entry.children || []);
    return;
  }
  const loading = document.createElement("div");
  loading.className = "workspace-loading";
  loading.textContent = "正在加载...";
  container.replaceChildren(loading);
  try {
    const children = await invoke("workspace_children", {
      root: state.workspaceRoot,
      directory: entry.path,
    });
    entry.children = children || [];
    entry.childrenLoaded = true;
    if (!container.isConnected) return;
    container.replaceChildren();
    appendWorkspaceEntries(container, entry.children);
  } catch (error) {
    if (!container.isConnected) return;
    loading.textContent = `加载失败：${formatError(error)}`;
  }
}

function createMiniButton(text, title, action) {
  const button = document.createElement("button");
  button.type = "button";
  button.className = "small-icon-button";
  button.textContent = text;
  button.title = title;
  button.dataset.workspaceAction = action;
  return button;
}

function applySettings() {
  const settings = state.settings || fallbackSettings();
  dom.root.dataset.theme = settings.theme;
  dom.root.style.setProperty("--editor-font", fontStack(settings.editorFontFamily, true));
  dom.root.style.setProperty("--preview-font", fontStack(settings.previewFontFamily, false));
  dom.root.style.setProperty("--editor-size", `${settings.editorFontSize}px`);
  dom.root.style.setProperty("--preview-size", `${settings.previewFontSize}px`);
  dom.root.style.setProperty("--sidebar-width", `${settings.sidebarWidth}px`);
  dom.root.style.setProperty("--editor-ratio", String(settings.splitRatio));
  dom.mainLayout.classList.toggle("sidebar-collapsed", !settings.sidebarVisible);
  dom.themeStatus.textContent = themeOption(settings.theme).name;
  applyBackground(settings.backgroundImagePath ? state.backgroundDataUri : "");
  preview.setTheme(settings.theme);
  preview.setAppearance(settings);
  preview.applyOrderedListStyle(resolvedOrderedListStyle(activeDocument()?.path));
  editor.refresh();
}

function resolvedOrderedListStyle(path) {
  const fallback = state.settings?.orderedListStyle || "plain";
  if (!path) return fallback;
  const override = Object.entries(
    state.settings?.previewStyleFiles?.fileOverrides || {},
  ).find(([candidate]) => samePath(candidate, path))?.[1];
  return ["plain", "bold", "circle", "square"].includes(override?.orderedList)
    ? override.orderedList
    : fallback;
}

function resolvedPreviewSettings(path) {
  return {
    ...(state.settings || fallbackSettings()),
    orderedListStyle: resolvedOrderedListStyle(path),
  };
}

function applyBackground(dataUri) {
  const settings = state.settings || fallbackSettings();
  document.body.classList.toggle("has-background", Boolean(dataUri));
  dom.root.style.setProperty(
    "--background-image",
    dataUri ? `url("${dataUri}")` : "none",
  );
  dom.root.style.setProperty(
    "--background-opacity",
    String(settings.backgroundOpacity ?? 0.18),
  );
}

async function restoreSavedBackground() {
  if (!state.settings?.backgroundImagePath) {
    state.backgroundDataUri = "";
    applyBackground("");
    return;
  }
  try {
    const loaded = await invoke("load_background_image", {
      path: state.settings.backgroundImagePath,
    });
    state.backgroundDataUri = loaded.dataUri;
    applyBackground(state.backgroundDataUri);
  } catch (error) {
    state.backgroundDataUri = "";
    applyBackground("");
    toast(`背景图片不可用，已使用默认背景：${formatError(error)}`, "error", 6000);
  }
}

async function chooseBackgroundImage() {
  dom.backgroundChoose.disabled = true;
  dom.backgroundChoose.textContent = "正在导入...";
  try {
    const selected = await invoke("choose_background_image");
    if (!selected) return;
    state.settingsDraft.backgroundImagePath = selected.path;
    state.settingsDraft.backgroundDataUri = selected.dataUri;
    renderBackgroundDraft(selected.dataUri);
  } catch (error) {
    toast(`导入背景图片失败：${formatError(error)}`, "error", 7000);
  } finally {
    dom.backgroundChoose.disabled = false;
    dom.backgroundChoose.textContent = "选择图片...";
  }
}

function removeBackgroundImage() {
  state.settingsDraft.backgroundImagePath = null;
  delete state.settingsDraft.backgroundDataUri;
  renderBackgroundDraft("");
}

function renderBackgroundDraft(dataUri) {
  dom.backgroundPreview.classList.toggle("has-image", Boolean(dataUri));
  dom.backgroundPreview.style.backgroundImage = dataUri ? `url("${dataUri}")` : "";
  dom.backgroundPreview.querySelector("span").textContent = dataUri
    ? "本地托管副本"
    : "未选择背景";
  dom.backgroundRemove.disabled = !dataUri;
}

async function recordRecent(path) {
  try {
    state.settings = await invoke("record_recent_path", { path });
    renderSidebar();
  } catch (error) {
    toast(`更新最近文件失败：${formatError(error)}`, "error");
  }
}

async function removeRecent(path) {
  try {
    state.settings = await invoke("remove_recent_path", { path });
    renderSidebar();
  } catch (error) {
    toast(`移出最近文件失败：${formatError(error)}`, "error");
  }
}

async function recordRecentWorkspace(path) {
  try {
    state.settings = await invoke("record_recent_workspace", { path });
    renderSidebar();
  } catch (error) {
    toast(`更新最近文件夹失败：${formatError(error)}`, "error");
  }
}

async function removeRecentWorkspace(path) {
  try {
    state.settings = await invoke("remove_recent_workspace", { path });
    renderSidebar();
  } catch (error) {
    toast(`移出最近文件夹失败：${formatError(error)}`, "error");
  }
}

function scheduleSettingsPersistence() {
  window.clearTimeout(scheduleSettingsPersistence.timer);
  scheduleSettingsPersistence.timer = window.setTimeout(() => {
    void persistCurrentSessionSettings();
  }, 300);
}

function scheduleRecoverySnapshots() {
  window.clearTimeout(state.recoveryTimer);
  state.recoveryTimer = window.setTimeout(() => {
    state.recoveryWritePromise = persistDirtyRecoverySnapshots().finally(() => {
      state.recoveryWritePromise = null;
    });
  }, 2000);
}

async function persistDirtyRecoverySnapshots({
  force = false,
  includeClean = false,
} = {}) {
  if (state.closingWindow && !force) return;
  const dirtyDocuments = includeClean ? state.documents : state.documents.filter(isDirty);
  for (const documentModel of dirtyDocuments) {
    try {
      const cleanFile =
        Boolean(documentModel.path) && !isDirty(documentModel);
      await invoke("save_recovery_snapshot", {
        snapshot: {
          id: documentModel.id,
          originalPath: documentModel.path,
          title: documentModel.title,
          text: cleanFile ? "" : documentModel.text,
          savedText: cleanFile ? "" : documentModel.savedText,
          hadUtf8Bom: documentModel.hadUtf8Bom,
          viewMode: documentModel.viewMode,
          selectionStart: documentModel.selectionStart,
          selectionEnd: documentModel.selectionEnd,
          scrollTop: documentModel.scrollTop,
          previewScrollRatio: documentModel.previewScrollRatio,
          savedAtUtc: new Date().toISOString(),
        },
      });
    } catch (error) {
      toast(`保存恢复快照失败：${formatError(error)}`, "error", 6000);
      break;
    }
  }
}

async function persistCurrentSessionSettings() {
  if (!state.settings) return;
  try {
    await captureWindowState();
    state.settings = await invoke("save_settings", { settings: state.settings });
  } catch (error) {
    toast(`保存设置失败：${formatError(error)}`, "error", 6000);
  }
}

async function captureWindowState() {
  const appWindow = getCurrentWindow();
  if (!appWindow?.outerPosition || !appWindow?.outerSize) return;
  try {
    const [position, size, maximized, minimized] = await Promise.all([
      appWindow.outerPosition(),
      appWindow.outerSize(),
      appWindow.isMaximized?.() ?? false,
      appWindow.isMinimized?.() ?? false,
    ]);
    state.settings.windowMaximized = Boolean(maximized);
    if (!maximized && !minimized && size.width >= 860 && size.height >= 560) {
      state.settings.windowX = position.x;
      state.settings.windowY = position.y;
      state.settings.windowWidth = size.width;
      state.settings.windowHeight = size.height;
    }
  } catch {
    // Window geometry is best-effort and must never block settings persistence.
  }
}

function setSidebarVisible(visible) {
  state.settings.sidebarVisible = visible;
  renderSidebar();
  applySettings();
  scheduleSettingsPersistence();
}

function handleTabClick(event) {
  const close = event.target.closest("[data-tab-close]");
  if (close) {
    event.stopPropagation();
    void closeDocument(close.dataset.tabClose);
    return;
  }
  const tab = event.target.closest("[data-tab-id]");
  if (tab) activateDocument(tab.dataset.tabId);
}

function handleSidebarClick(event) {
  const action = event.target.closest("[data-workspace-action]")?.dataset.workspaceAction;
  if (action) {
    void handleWorkspaceAction(action);
    return;
  }
  const removeRecentPath = event.target.closest("[data-recent-remove]");
  if (removeRecentPath) {
    void removeRecent(removeRecentPath.dataset.recentRemove);
    return;
  }
  const removeRecentWorkspacePath = event.target.closest(
    "[data-recent-workspace-remove]",
  );
  if (removeRecentWorkspacePath) {
    void removeRecentWorkspace(
      removeRecentWorkspacePath.dataset.recentWorkspaceRemove,
    );
    return;
  }
  const recent = event.target.closest("[data-recent-path]");
  if (recent) {
    void openDocument(recent.dataset.recentPath).catch((error) =>
      toast(`打开文档失败：${formatError(error)}`, "error", 7000),
    );
    return;
  }
  const recentWorkspace = event.target.closest("[data-recent-workspace]");
  if (recentWorkspace) {
    void openWorkspacePath(recentWorkspace.dataset.recentWorkspace).catch((error) =>
      toast(`打开最近文件夹失败：${formatError(error)}`, "error", 7000),
    );
    return;
  }
  const file = event.target.closest("[data-workspace-path][data-workspace-directory='false'], [data-workspace-path]:not([data-workspace-directory])");
  if (file) {
    void openDocument(file.dataset.workspacePath).catch((error) =>
      toast(`打开工作区文件失败：${formatError(error)}`, "error", 7000),
    );
  }
}

function handleSidebarContextMenu(event) {
  const recent = event.target.closest("[data-recent-path]");
  const recentWorkspace = event.target.closest("[data-recent-workspace]");
  const workspace = event.target.closest("[data-workspace-path]");
  if (!recent && !recentWorkspace && !workspace) return;
  event.preventDefault();
  if (recent) showRecentContextMenu(recent.dataset.recentPath, event.clientX, event.clientY);
  else if (recentWorkspace) {
    showRecentWorkspaceContextMenu(
      recentWorkspace.dataset.recentWorkspace,
      event.clientX,
      event.clientY,
    );
  }
  else {
    showWorkspaceContextMenu(
      workspace.dataset.workspacePath,
      workspace.dataset.workspaceDirectory === "true",
      event.clientX,
      event.clientY,
    );
  }
}

function showRecentWorkspaceContextMenu(path, x, y) {
  state.contextMenuItems = [
    ["打开文件夹", () => openWorkspacePath(path)],
    ["在文件资源管理器中显示", () => invoke("reveal_in_explorer", { path })],
    ["复制文件夹路径", () => copyText(path)],
    ["separator"],
    ["移出最近记录（不删除文件夹）", () => removeRecentWorkspace(path), "danger"],
  ];
  renderContextMenu(x, y);
}

function handleImageContextMenu({
  source,
  originalSource,
  documentPath,
  title,
  link,
  clientX,
  clientY,
}) {
  const frameRect = dom.previewFrame.getBoundingClientRect();
  const resolvedSource =
    source && !source.startsWith("data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP")
      ? source
      : "";
  const items = [
    [
      "打开图片",
      async () => {
        let imageSource = resolvedSource;
        if (!imageSource && documentPath && originalSource) {
          imageSource = (await invoke("load_image", {
            documentPath,
            source: originalSource,
          })).dataUri;
        }
        await openImageViewer(imageSource || source, title);
      },
    ],
    ["separator"],
    [
      "另存为原格式",
      () => savePreviewImageFromContext(resolvedSource, documentPath, originalSource, title, "original"),
    ],
    [
      "另存为 PNG",
      () => savePreviewImageFromContext(resolvedSource, documentPath, originalSource, title, "png"),
    ],
    [
      "另存为自包含 HTML",
      () => savePreviewImageFromContext(resolvedSource, documentPath, originalSource, title, "html"),
    ],
  ];
  if (link) {
    items.push(["separator"]);
    items.push(["打开链接", () => handleOpenLink(link)]);
  }
  state.contextMenuItems = items;
  renderContextMenu(frameRect.left + clientX, frameRect.top + clientY);
}

async function savePreviewImageFromContext(
  source,
  documentPath,
  originalSource,
  title,
  format,
) {
  try {
    let resolved = source;
    if (!resolved && documentPath && originalSource) {
      resolved = (await invoke("load_image", {
        documentPath,
        source: originalSource,
      })).dataUri;
    }
    if (!resolved) throw new Error("图片尚未加载完成。");
    await savePreviewImageData(resolved, title, format);
  } catch (error) {
    toast(`图片另存失败：${formatError(error)}`, "error", 7000);
  }
}

async function savePreviewImageData(source, title, format) {
  const prepared = await invoke("prepare_preview_image", { source });
  const baseName =
    String(title || "image")
      .replace(/\.(?:png|jpe?g|gif|bmp|webp|svg)$/i, "")
      .replace(/[<>:"/\\|?*\x00-\x1f]/g, "_")
      .slice(0, 80) || "image";
  const extension =
    format === "png" ? "png" : format === "html" ? "html" : prepared.extension;
  const destination = await invoke("pick_preview_image_save_path", {
    suggestedName: `${baseName}.${extension}`,
    format,
  });
  if (!destination) return;
  const dataUri =
    format === "png" ? await convertImageDataUriToPng(prepared.dataUri) : prepared.dataUri;
  const savedPath = await invoke("save_preview_image", {
    dataUri,
    format,
    target: destination,
  });
  toast(`图片已保存：${savedPath}`, "success", 6000);
}

async function convertImageDataUriToPng(dataUri) {
  const image = await new Promise((resolve, reject) => {
    const element = new Image();
    element.onload = () => resolve(element);
    element.onerror = () => reject(new Error("无法解码待转换的图片。"));
    element.src = dataUri;
  });
  const scale = Math.min(
    1,
    4096 / Math.max(1, image.naturalWidth),
    4096 / Math.max(1, image.naturalHeight),
  );
  const canvas = document.createElement("canvas");
  canvas.width = Math.max(1, Math.round(image.naturalWidth * scale));
  canvas.height = Math.max(1, Math.round(image.naturalHeight * scale));
  const context = canvas.getContext("2d", { alpha: true });
  if (!context) throw new Error("无法创建 PNG 转换画布。");
  context.drawImage(image, 0, 0, canvas.width, canvas.height);
  return canvas.toDataURL("image/png");
}

function showRecentContextMenu(path, x, y) {
  state.contextMenuItems = [
    ["打开", () => openDocument(path)],
    ["在新窗口中打开", () => invoke("open_in_new_window", { path })],
    ["在文件资源管理器中显示", () => invoke("reveal_in_explorer", { path })],
    ["separator"],
    ["复制文件路径", () => copyText(path)],
    ["复制所在文件夹路径", () => copyText(pathDirectory(path))],
    ["复制文件名", () => copyText(pathFileName(path))],
    ["separator"],
    ["移出最近记录（不删除文件）", () => removeRecent(path), "danger"],
  ];
  renderContextMenu(x, y);
}

function showWorkspaceContextMenu(path, isDirectory, x, y) {
  if (!path) return;
  const items = [];
  if (!isDirectory) {
    items.push(["打开", () => openDocument(path)]);
    items.push(["在新窗口中打开", () => invoke("open_in_new_window", { path })]);
    items.push(["重命名", () => renameWorkspaceEntry(path, false)]);
  } else {
    items.push(["新建 Markdown 文件", () => createWorkspaceEntry(path, "file")]);
    items.push(["新建文件夹", () => createWorkspaceEntry(path, "directory")]);
    items.push(["重命名", () => renameWorkspaceEntry(path, true)]);
  }
  items.push(["separator"]);
  items.push(["在文件资源管理器中显示", () => invoke("reveal_in_explorer", { path })]);
  items.push(["复制路径", () => copyText(path)]);
  if (!isDirectory) items.push(["复制文件名", () => copyText(pathFileName(path))]);
  items.push(["separator"]);
  items.push(["删除", () => deleteWorkspaceEntry(path), "danger"]);
  state.contextMenuItems = items;
  renderContextMenu(x, y);
}

function renderContextMenu(x, y) {
  dom.contextMenu.replaceChildren();
  for (const item of state.contextMenuItems || []) {
    if (item[0] === "separator") {
      const separator = document.createElement("div");
      separator.className = "context-separator";
      dom.contextMenu.append(separator);
      continue;
    }
    const [label, action, className] = item;
    const button = document.createElement("button");
    button.type = "button";
    button.textContent = label;
    if (className) button.classList.add(className);
    button._wimdAction = action;
    dom.contextMenu.append(button);
  }
  dom.contextMenu.hidden = false;
  const rect = dom.contextMenu.getBoundingClientRect();
  dom.contextMenu.style.left = `${Math.min(x, window.innerWidth - rect.width - 8)}px`;
  dom.contextMenu.style.top = `${Math.min(y, window.innerHeight - rect.height - 8)}px`;
}

function handleContextMenuCommand(event) {
  const button = event.target.closest("button");
  if (!button?._wimdAction) return;
  hideContextMenu();
  Promise.resolve(button._wimdAction()).catch((error) =>
    toast(`操作失败：${formatError(error)}`, "error", 7000),
  );
}

function hideContextMenu() {
  dom.contextMenu.hidden = true;
  dom.contextMenu.replaceChildren();
}

async function handleWorkspaceAction(action) {
  if (action === "workspace-refresh") {
    await openWorkspacePath(state.workspaceRoot, { activate: false });
  } else if (action === "workspace-close") {
    state.workspaceRoot = null;
    state.workspaceEntries = [];
    state.workspaceExpanded.clear();
    state.settings.lastWorkspace = null;
    renderSidebar();
    scheduleSettingsPersistence();
  } else if (action === "workspace-new-file") {
    await createWorkspaceEntry(state.workspaceRoot, "file");
  } else if (action === "workspace-new-folder") {
    await createWorkspaceEntry(state.workspaceRoot, "directory");
  }
}

async function createWorkspaceEntry(parent, kind) {
  if (!state.workspaceRoot) return;
  const promptText = kind === "file" ? "输入新 Markdown 文件名（含 .md）" : "输入新文件夹名称";
  const name = window.prompt(promptText, kind === "file" ? "新文档.md" : "新文件夹");
  if (!name) return;
  if (kind === "file") {
    const path = await invoke("create_workspace_markdown", {
      root: state.workspaceRoot,
      parent,
      name,
    });
    await refreshWorkspace();
    await openDocument(path);
  } else {
    await invoke("create_workspace_directory", {
      root: state.workspaceRoot,
      parent,
      name,
    });
    await refreshWorkspace();
  }
}

async function renameWorkspaceEntry(path, isDirectory) {
  if (!state.workspaceRoot) return;
  const name = window.prompt("输入新名称", pathFileName(path));
  if (!name || name === pathFileName(path)) return;
  const renamed = await invoke("rename_workspace_entry", {
    root: state.workspaceRoot,
    path,
    newName: name,
  });
  const target = renamed.target;
  state.settings = renamed.settings;
  state.workspaceExpanded = new Set(
    [...state.workspaceExpanded].map((expandedPath) =>
      isPathAtOrBelow(expandedPath, path)
        ? target + expandedPath.slice(path.length)
        : expandedPath,
    ),
  );
  for (const documentModel of state.documents) {
    if (samePath(documentModel.path, path)) {
      documentModel.path = target;
      documentModel.title = pathFileName(target);
      await invoke("watch_document", { path: target }).catch(() => {});
    }
  }
  if (isDirectory) {
    for (const documentModel of state.documents) {
      if (isPathAtOrBelow(documentModel.path, path)) {
        documentModel.path = target + documentModel.path.slice(path.length);
        await invoke("watch_document", { path: documentModel.path }).catch(() => {});
      }
    }
  }
  await refreshWorkspace();
  renderEverything();
}

async function deleteWorkspaceEntry(path) {
  if (!state.workspaceRoot) return;
  const first = window.confirm(`将删除磁盘上的实际内容：\n${path}\n\n此操作不能通过 WIMD 撤销。是否继续？`);
  if (!first) return;
  const second = window.confirm(`请再次确认删除：\n${path}`);
  if (!second) return;
  const deleted = await invoke("delete_workspace_entry", {
    root: state.workspaceRoot,
    path,
  });
  state.settings = deleted.settings;
  state.workspaceExpanded = new Set(
    [...state.workspaceExpanded].filter(
      (expandedPath) => !isPathAtOrBelow(expandedPath, path),
    ),
  );
  for (const documentModel of state.documents) {
    if (isPathAtOrBelow(documentModel.path, path)) {
      documentModel.path = null;
      documentModel.title = `${documentModel.title}（已删除）`;
      documentModel.savedText = "";
      documentModel.hadUtf8Bom = false;
    }
  }
  await refreshWorkspace();
  renderEverything();
}

async function refreshWorkspace() {
  if (!state.workspaceRoot) return;
  const workspace = await invoke("open_workspace", { path: state.workspaceRoot });
  state.workspaceRoot = workspace.root;
  state.workspaceEntries = workspace.entries || [];
  renderSidebar();
}

function openFind(replace) {
  dom.findBar.hidden = false;
  dom.replaceField.hidden = !replace;
  dom.findInput.focus();
  const selected = editor.value.slice(editor.selectionStart, editor.selectionEnd);
  if (selected && !selected.includes("\n")) dom.findInput.value = selected;
}

function closeFind() {
  dom.findBar.hidden = true;
  editor.focus();
}

function scheduleFind(focus) {
  if (focus) runFind(true);
}

function runFind(forward) {
  const found = editor.find(dom.findInput.value, {
    forward,
    caseSensitive: dom.findCaseSensitive.checked,
  });
  if (!found) toast("未找到匹配内容。", "error");
}

function runReplaceCurrent() {
  if (!ensureEditableDocument()) return;
  const query = dom.findInput.value;
  const replacement = dom.replaceInput.value;
  if (!editor.replaceCurrent(query, replacement, dom.findCaseSensitive.checked)) {
    runFind(true);
    return;
  }
  runFind(true);
}

function runReplaceAll() {
  if (!ensureEditableDocument()) return;
  const count = editor.replaceAll(
    dom.findInput.value,
    dom.replaceInput.value,
    dom.findCaseSensitive.checked,
  );
  toast(count ? `已替换 ${count} 处。` : "未找到匹配内容。", count ? "success" : "error");
}

async function handleEditorPaste(event) {
  const items = [...(event.clipboardData?.items || [])];
  const imageItem = items.find((item) => item.type.startsWith("image/"));
  if (!imageItem) return;
  event.preventDefault();
  const documentModel = activeDocument();
  if (!documentModel || documentModel.readOnly) {
    toast("当前文档为只读。", "error");
    return;
  }
  const usesImgBb = state.settings.imageStorageMode === "imgBb";
  if (!usesImgBb && !documentModel.path) {
    const saved = await saveDocument(documentModel, { saveAs: true });
    if (!saved) return;
  }
  const file = imageItem.getAsFile();
  if (!file) return;
  if (file.size > 32 * 1024 * 1024) {
    toast("剪贴板图片超过 32 MiB 上限。", "error");
    return;
  }
  try {
    const bytes = [...new Uint8Array(await file.arrayBuffer())];
    const extension = extensionForMime(file.type);
    if (usesImgBb) {
      const hosted = await invoke("upload_image_to_imgbb", {
        sourcePath: null,
        bytes,
        extension,
      });
      editor.insertText(markdownImage(file.name || "截图", hosted.url));
      return;
    }
    const stored = await invoke("insert_image_bytes", {
      documentPath: documentModel.path,
      bytes,
      extension,
      relativeDirectory: state.settings.localImageDirectory,
    });
    editor.insertText(markdownImage(file.name || "截图", stored.markdownPath));
  } catch (error) {
    toast(`保存剪贴板图片失败：${formatError(error)}`, "error", 7000);
  }
}

async function insertImageFromFile() {
  const documentModel = activeDocument();
  if (!documentModel) return;
  if (documentModel.readOnly) {
    toast("当前文档为只读。", "error");
    return;
  }
  const usesImgBb = state.settings.imageStorageMode === "imgBb";
  if (!usesImgBb && !documentModel.path) {
    const saved = await saveDocument(documentModel, { saveAs: true });
    if (!saved) return;
  }
  const sourcePath = await invoke("pick_image");
  if (!sourcePath) return;
  try {
    if (usesImgBb) {
      const hosted = await invoke("upload_image_to_imgbb", {
        sourcePath,
        bytes: null,
        extension: null,
      });
      editor.insertText(markdownImage(pathFileName(sourcePath), hosted.url));
    } else {
      const stored = await invoke("insert_image", {
        documentPath: documentModel.path,
        sourcePath,
        relativeDirectory: state.settings.localImageDirectory,
      });
      editor.insertText(markdownImage(pathFileName(sourcePath), stored.markdownPath));
    }
  } catch (error) {
    toast(`插入图片失败：${formatError(error)}`, "error", 7000);
  }
}

function markdownImage(alt, path) {
  const safeAlt = String(alt || "图片").replaceAll("]", "\\]");
  return `![${safeAlt}](${path})`;
}

function extensionForMime(type) {
  return (
    {
      "image/png": "png",
      "image/jpeg": "jpg",
      "image/gif": "gif",
      "image/bmp": "bmp",
      "image/webp": "webp",
      "image/svg+xml": "svg",
    }[type] || "png"
  );
}

async function openImageViewer(source, title) {
  try {
    await invoke("open_image_viewer", {
      source,
      title: title || "图片查看",
    });
  } catch (error) {
    toast(`打开图片查看器失败：${formatError(error)}`, "error", 7000);
  }
}

async function applyMarkdownFormat(format) {
  const documentModel = activeDocument();
  if (!documentModel) return;
  if (documentModel.readOnly) {
    toast("当前文档为只读。", "error");
    return;
  }
  if (format === "image") {
    await insertImageFromFile();
    return;
  }
  if (format === "table") {
    state.pendingTable = null;
    dom.tableRows.value = "3";
    dom.tableColumns.value = "3";
    dom.tableDialog.showModal();
    return;
  }
  try {
    const edit = await invoke("format_markdown", {
      text: documentModel.text,
      selectionStart: editor.selectionStart,
      selectionLength: editor.selectionEnd - editor.selectionStart,
      format,
      rowCount: null,
      columnCount: null,
    });
    editor.applyEdit(edit);
  } catch (error) {
    toast(`Markdown 格式操作失败：${formatError(error)}`, "error", 6000);
  }
}

async function copyEditorSelection() {
  const start = editor.selectionStart;
  const end = editor.selectionEnd;
  if (start === end) {
    toast("请先选择要复制的内容。", "error");
    return;
  }
  await invoke("copy_text", { text: editor.value.slice(start, end) });
}

async function cutEditorSelection() {
  if (!ensureEditableDocument()) return;
  const start = editor.selectionStart;
  const end = editor.selectionEnd;
  if (start === end) {
    toast("请先选择要剪切的内容。", "error");
    return;
  }
  await invoke("copy_text", { text: editor.value.slice(start, end) });
  editor.replaceRange(start, end, "", start, start);
}

async function pasteIntoEditor() {
  if (!ensureEditableDocument()) return;
  const text = await invoke("read_clipboard_text");
  if (!text) {
    toast("剪贴板中没有可粘贴的文本。", "error");
    return;
  }
  editor.insertText(text);
}

async function confirmInsertTable() {
  if (!ensureEditableDocument()) return;
  const rows = Number(dom.tableRows.value);
  const columns = Number(dom.tableColumns.value);
  closeDialog("table-dialog");
  try {
    const documentModel = activeDocument();
    const edit = await invoke("format_markdown", {
      text: documentModel.text,
      selectionStart: editor.selectionStart,
      selectionLength: editor.selectionEnd - editor.selectionStart,
      format: "table-custom",
      rowCount: rows,
      columnCount: columns,
    });
    editor.applyEdit(edit);
  } catch (error) {
    toast(`插入表格失败：${formatError(error)}`, "error", 6000);
  }
}

function handleDocumentClick(event) {
  const commandButton = event.target.closest("[data-command]");
  if (commandButton) {
    closeMenus();
    void executeCommand(commandButton.dataset.command);
    return;
  }
  const formatButton = event.target.closest("[data-format]");
  if (formatButton) {
    closeMenus();
    void applyMarkdownFormat(formatButton.dataset.format);
  }
}

async function executeCommand(command) {
  try {
    switch (command) {
      case "new":
        createDocument({ activate: true });
        break;
      case "open":
        await commandOpen();
        break;
      case "open-workspace":
        await commandOpenWorkspace();
        break;
      case "close-workspace":
        await handleWorkspaceAction("workspace-close");
        break;
      case "save":
        await saveActiveDocument();
        break;
      case "save-as":
        await saveActiveDocument({ saveAs: true });
        break;
      case "reload":
        await reloadActiveDocument();
        break;
      case "export-pdf":
        await exportCurrentDocumentToPdf();
        break;
      case "insert-image":
        await insertImageFromFile();
        break;
      case "close-tab":
        if (state.activeId) await closeDocument(state.activeId);
        break;
      case "exit":
        await getCurrentWindow()?.close();
        break;
      case "undo":
      case "redo":
        editor.focus();
        document.execCommand(command);
        break;
      case "cut":
        await cutEditorSelection();
        break;
      case "copy":
        await copyEditorSelection();
        break;
      case "paste":
        await pasteIntoEditor();
        break;
      case "select-all":
        editor.focus();
        dom.editor.select();
        break;
      case "find":
        openFind(false);
        break;
      case "replace":
        openFind(true);
        break;
      case "format-bold":
        await applyMarkdownFormat("bold");
        break;
      case "format-italic":
        await applyMarkdownFormat("italic");
        break;
      case "format-strike":
        await applyMarkdownFormat("strike");
        break;
      case "format-inline-code":
        await applyMarkdownFormat("inline-code");
        break;
      case "format-code-block":
        await applyMarkdownFormat("code-block");
        break;
      case "format-link":
        await applyMarkdownFormat("link");
        break;
      case "format-image":
        await applyMarkdownFormat("image");
        break;
      case "format-ordered-list":
        await applyMarkdownFormat("ordered-list");
        break;
      case "format-unordered-list":
        await applyMarkdownFormat("unordered-list");
        break;
      case "format-quote":
        await applyMarkdownFormat("quote");
        break;
      case "format-task-list":
        await applyMarkdownFormat("task-list");
        break;
      case "format-table":
        await applyMarkdownFormat("table");
        break;
      case "format-separator":
        await applyMarkdownFormat("separator");
        break;
      case "view-editor":
        applyViewMode("editorOnly");
        break;
      case "cycle-view":
        cycleViewMode();
        break;
      case "view-split":
        applyViewMode("editorAndPreview");
        schedulePreview();
        break;
      case "view-preview":
        applyViewMode("previewOnly");
        schedulePreview();
        break;
      case "toggle-view":
        cycleViewMode();
        break;
      case "toggle-sidebar":
        setSidebarVisible(!state.settings.sidebarVisible);
        break;
      case "settings":
        await invoke("open_settings_window", {
          kind: "appearance",
          documentPath: activeDocument()?.path || null,
        });
        break;
      case "settings-preview":
        await invoke("open_settings_window", {
          kind: "preview",
          documentPath: activeDocument()?.path || null,
        });
        break;
      case "settings-behavior":
        await invoke("open_settings_window", {
          kind: "behavior",
          documentPath: activeDocument()?.path || null,
        });
        break;
      case "settings-images":
        await invoke("open_settings_window", {
          kind: "images",
          documentPath: activeDocument()?.path || null,
        });
        break;
      case "settings-shortcuts":
        await invoke("open_settings_window", {
          kind: "shortcuts",
          documentPath: activeDocument()?.path || null,
        });
        break;
      case "about":
        openAboutDialog();
        break;
      case "check-update":
        await checkForUpdates({ silent: false });
        break;
      case "cancel-update-download":
        await invoke("cancel_update_download");
        dom.renderStatus.textContent = "正在取消更新下载...";
        break;
      case "toggle-update-startup":
        state.settings.checkForUpdatesOnStartup =
          !state.settings.checkForUpdatesOnStartup;
        await persistCurrentSessionSettings();
        toast(
          state.settings.checkForUpdatesOnStartup
            ? "已启用启动时检查更新。"
            : "已关闭启动时检查更新。",
          "success",
        );
        break;
      case "open-settings-folder":
        await invoke("reveal_in_explorer", { path: state.bootstrap.settingsPath });
        break;
      case "open-logs-folder":
        await invoke("open_logs_folder");
        break;
      case "capture-startup-diagnostic":
        await invoke("capture_startup_diagnostic", {
          documentPath: activeDocument()?.path || null,
        });
        toast("已启动独立启动诊断采集；请在 UAC 提示后确认。", "success", 7000);
        break;
      default:
        toast(`未实现命令：${command}`, "error");
    }
  } catch (error) {
    toast(formatError(error), "error", 7000);
  }
}

async function commandOpen() {
  const path = await invoke("pick_document");
  if (!path) return;
  await openDocument(path);
}

async function commandOpenWorkspace() {
  const path = await invoke("pick_folder");
  if (!path) return;
  await openWorkspacePath(path);
}

async function saveActiveDocument(options = {}) {
  const documentModel = activeDocument();
  if (documentModel) await saveDocument(documentModel, options);
}

async function exportCurrentDocumentToPdf() {
  const documentModel = activeDocument();
  if (!documentModel) return;
  const destination = await invoke("pick_pdf_save_path", {
    suggestedName: `${documentModel.title.replace(/\.(?:md|markdown)$/i, "")}.pdf`,
  });
  if (!destination) return;
  dom.renderStatus.textContent = "正在准备 PDF...";
  dom.renderStatus.classList.remove("status-error");
  try {
    const rendered = await invoke("render_preview", { markdown: documentModel.text });
    await preview.render(rendered, documentModel, resolvedPreviewSettings(documentModel.path), {
      preservePosition: false,
    });
    const pdfDocument = await preview.createPdfSnapshot();
    dom.renderStatus.textContent = "正在生成 PDF...";
    const exportedPath = await invoke("export_pdf", {
      path: destination,
      html: pdfDocument.html,
      css: pdfDocument.css,
      theme: pdfDocument.theme,
      inlineStyle: pdfDocument.inlineStyle,
    });
    toast(`PDF 已导出：${exportedPath}`, "success", 6000);
    dom.renderStatus.textContent = "PDF 导出完成";
  } catch (error) {
    dom.renderStatus.textContent = "PDF 导出失败";
    dom.renderStatus.classList.add("status-error");
    throw error;
  }
}

async function checkForUpdates({ silent = false } = {}) {
  dom.renderStatus.textContent = "正在检查更新...";
  try {
    const update = await invoke("check_update");
    if (!update) {
      if (!silent) toast("当前已经是最新正式版本。", "success");
      dom.renderStatus.textContent = "已是最新版本";
      return;
    }
    const notes = String(update.notes || "").slice(0, 1200);
    const shouldDownload = window.confirm(
      `发现新版本 ${update.version}。\n\n${notes}\n\n是否下载安装包？`,
    );
    if (!shouldDownload) return;
    dom.renderStatus.textContent = `正在下载 WIMD ${update.version}...`;
    state.updateDownloading = true;
    state.updateDownloadStartedAt = Date.now();
    dom.cancelUpdateDownload.disabled = false;
    let installerPath;
    try {
      installerPath = await invoke("download_update", { update });
    } finally {
      state.updateDownloading = false;
      dom.cancelUpdateDownload.disabled = true;
    }
    const shouldInstall = window.confirm(
      `WIMD ${update.version} 安装包已下载并完成 SHA-256 校验。\n\n${installerPath}\n\n是否现在启动安装程序？`,
    );
    if (!shouldInstall) {
      toast(`安装包已保存：${installerPath}`, "success", 7000);
      return;
    }
    await invoke("install_update", {
      path: installerPath,
      expectedSha256: update.sha256Hex,
    });
    toast("安装程序已启动。请按安装向导完成更新。", "success", 7000);
    dom.renderStatus.textContent = "更新安装程序已启动";
  } catch (error) {
    if (formatError(error).includes("取消")) {
      dom.renderStatus.textContent = "更新下载已取消";
      dom.renderStatus.classList.remove("status-error");
      toast("更新下载已取消。", "success");
      return;
    }
    dom.renderStatus.textContent = "检查更新失败";
    dom.renderStatus.classList.add("status-error");
    if (!silent) throw error;
    toast(`检查更新失败：${formatError(error)}`, "error", 7000);
  }
}

function openSettingsDialog(tab = "appearance") {
  state.settingsDraft = structuredClone(state.settings);
  populateSettingsDialog();
  activateSettingsTab(tab);
  dom.settingsStatus.textContent = "";
  dom.settingsDialog.showModal();
}

function populateSettingsDialog() {
  const settings = state.settingsDraft;
  renderThemeCards(settings.theme);
  dom.editorFontFamily.value = fontDisplayName(settings.editorFontFamily);
  dom.editorFontSize.value = String(settings.editorFontSize);
  dom.previewFontFamily.value = fontDisplayName(settings.previewFontFamily);
  dom.previewFontSize.value = String(settings.previewFontSize);
  dom.backgroundOpacity.value = String(
    Math.round((settings.backgroundOpacity ?? 0.18) * 100),
  );
  dom.backgroundOpacityValue.textContent = `${dom.backgroundOpacity.value}%`;
  void populateBackgroundDraft(settings);
  dom.fileOpenViewMode.value = settings.fileOpenViewMode;
  dom.orderedListStyle.value = settings.orderedListStyle;
  const currentDocument = activeDocument();
  const currentStyleOverride = currentDocument?.path
    ? Object.entries(settings.previewStyleFiles?.fileOverrides || {}).find(
        ([candidate]) => samePath(candidate, currentDocument.path),
      )?.[1]
    : null;
  dom.orderedListFileStyle.disabled = !currentDocument?.path;
  dom.orderedListFileStyle.value = currentStyleOverride?.orderedList || "follow";
  dom.sidebarVisible.checked = settings.sidebarVisible;
  dom.checkUpdatesStartup.checked = Boolean(settings.checkForUpdatesOnStartup);
  dom.externalFileDefault.value =
    settings.externalFileChangeBehavior || "prompt";
  dom.externalFileCurrent.disabled = !currentDocument?.path;
  dom.externalFileCurrent.value = currentDocument?.path
    ? settings.externalFileOverrides?.[currentDocument.path] || "follow"
    : "follow";
  dom.imageStorageMode.value = settings.imageStorageMode || "local";
  dom.localImageDirectory.value = settings.localImageDirectory;
  dom.imgbbApiKey.value = "";
  dom.imgbbKeyStatus.textContent = settings.protectedImgbbApiKey
    ? "已使用 DPAPI 保存"
    : "未保存";
  dom.imgbbKeyClear.disabled = !settings.protectedImgbbApiKey;
  updateImageSettingsVisibility();
  renderShortcutEditor();
}

function updateImageSettingsVisibility() {
  const usesImgBb = dom.imageStorageMode.value === "imgBb";
  dom.localImageSettings.hidden = usesImgBb;
  dom.imgbbImageSettings.hidden = !usesImgBb;
}

async function clearImgbbApiKey() {
  if (!window.confirm("清除已保存的 ImgBB API Key？此操作不可恢复。")) return;
  try {
    state.settings = await invoke("clear_imgbb_api_key");
    state.settingsDraft.protectedImgbbApiKey = null;
    state.settingsDraft.imageStorageMode = "local";
    dom.imageStorageMode.value = "local";
    dom.imgbbApiKey.value = "";
    dom.imgbbKeyStatus.textContent = "未保存";
    dom.imgbbKeyClear.disabled = true;
    updateImageSettingsVisibility();
    toast("ImgBB API Key 已清除。", "success");
  } catch (error) {
    toast(`清除 ImgBB API Key 失败：${formatError(error)}`, "error", 6000);
  }
}

async function populateBackgroundDraft(settings) {
  if (settings.backgroundDataUri) {
    renderBackgroundDraft(settings.backgroundDataUri);
    return;
  }
  if (state.backgroundDataUri) {
    renderBackgroundDraft(state.backgroundDataUri);
    return;
  }
  if (!settings.backgroundImagePath) {
    renderBackgroundDraft("");
    return;
  }
  try {
    const loaded = await invoke("load_background_image", {
      path: settings.backgroundImagePath,
    });
    settings.backgroundDataUri = loaded.dataUri;
    renderBackgroundDraft(loaded.dataUri);
  } catch {
    renderBackgroundDraft("");
  }
}

function renderThemeCards(selected) {
  dom.themeGrid.replaceChildren();
  for (const theme of THEME_OPTIONS) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = `theme-card${theme.id === selected ? " active" : ""}`;
    button.dataset.themeId = theme.id;
    button.style.setProperty("--swatch-bg", theme.bg);
    button.style.setProperty("--swatch-surface", theme.surface);
    button.style.setProperty("--swatch-accent", theme.accent);
    button.innerHTML = `<span class="theme-swatch"></span><span><strong></strong><small></small></span>`;
    button.querySelector("strong").textContent = theme.name;
    button.querySelector("small").textContent = theme.description;
    dom.themeGrid.append(button);
  }
}

function renderShortcutEditor() {
  dom.shortcutList.replaceChildren();
  for (const shortcut of state.shortcuts) {
    const row = document.createElement("label");
    row.className = "shortcut-row";
    const label = document.createElement("span");
    label.textContent = shortcut.label;
    const input = document.createElement("input");
    input.type = "text";
    input.readOnly = true;
    input.dataset.shortcutInput = shortcut.id;
    input.value =
      state.settingsDraft.shortcutOverrides?.[shortcut.id] || shortcut.defaultGesture;
    row.append(label, input);
    dom.shortcutList.append(row);
  }
}

function activateSettingsTab(tab) {
  dom.settingsTabs.querySelectorAll("[data-settings-tab]").forEach((button) => {
    button.classList.toggle("active", button.dataset.settingsTab === tab);
  });
  dom.settingsDialog.querySelectorAll("[data-settings-page]").forEach((page) => {
    page.classList.toggle("active", page.dataset.settingsPage === tab);
  });
}

function handleSettingsTabClick(event) {
  const tab = event.target.closest("[data-settings-tab]")?.dataset.settingsTab;
  if (tab) activateSettingsTab(tab);
}

function handleThemeClick(event) {
  const card = event.target.closest("[data-theme-id]");
  if (!card) return;
  state.settingsDraft.theme = card.dataset.themeId;
  renderThemeCards(card.dataset.themeId);
}

function captureShortcutGesture(event) {
  const input = event.target.closest("[data-shortcut-input]");
  if (!input) return;
  event.preventDefault();
  event.stopPropagation();
  if (event.key === "Tab" || event.key === "Escape") {
    input.blur();
    return;
  }
  const gesture = gestureFromKeyboardEvent(event);
  if (!gesture) {
    dom.settingsStatus.textContent = "请使用 Ctrl、Alt 或功能键组合。";
    return;
  }
  input.value = gesture;
  dom.settingsStatus.textContent = "";
}

function resetShortcutDrafts() {
  state.settingsDraft.shortcutOverrides = {};
  renderShortcutEditor();
}

async function saveSettingsFromDialog() {
  const draft = state.settingsDraft;
  draft.editorFontFamily = normalizeFontValue(dom.editorFontFamily.value);
  draft.editorFontSize = clampNumber(dom.editorFontSize.value, 10, 32, 15);
  draft.previewFontFamily = normalizeFontValue(dom.previewFontFamily.value);
  draft.previewFontSize = clampNumber(dom.previewFontSize.value, 10, 32, 16);
  draft.backgroundOpacity =
    clampNumber(dom.backgroundOpacity.value, 0, 100, 18) / 100;
  draft.fileOpenViewMode = dom.fileOpenViewMode.value;
  draft.orderedListStyle = dom.orderedListStyle.value;
  draft.sidebarVisible = dom.sidebarVisible.checked;
  draft.checkForUpdatesOnStartup = dom.checkUpdatesStartup.checked;
  draft.externalFileChangeBehavior = dom.externalFileDefault.value;
  draft.externalFileOverrides = {
    ...(draft.externalFileOverrides || {}),
  };
  const currentDocument = activeDocument();
  if (currentDocument?.path) {
    for (const candidate of Object.keys(draft.externalFileOverrides)) {
      if (samePath(candidate, currentDocument.path)) {
        delete draft.externalFileOverrides[candidate];
      }
    }
    if (dom.externalFileCurrent.value !== "follow") {
      draft.externalFileOverrides[currentDocument.path] =
        dom.externalFileCurrent.value;
    }
  }
  draft.imageStorageMode = dom.imageStorageMode.value;
  draft.localImageDirectory = dom.localImageDirectory.value.trim() || "img";
  draft.previewStyleFiles = {
    ...(draft.previewStyleFiles || {}),
    fileOverrides: {
      ...(draft.previewStyleFiles?.fileOverrides || {}),
    },
  };
  if (currentDocument?.path) {
    const existingEntry = Object.entries(
      draft.previewStyleFiles.fileOverrides,
    ).find(([candidate]) => samePath(candidate, currentDocument.path));
    for (const candidate of Object.keys(draft.previewStyleFiles.fileOverrides)) {
      if (samePath(candidate, currentDocument.path)) {
        delete draft.previewStyleFiles.fileOverrides[candidate];
      }
    }
    if (dom.orderedListFileStyle.value !== "follow") {
      draft.previewStyleFiles.fileOverrides[currentDocument.path] = {
        ...(existingEntry?.[1] || {}),
        orderedList: dom.orderedListFileStyle.value,
      };
    }
  }
  draft.shortcutOverrides = {};
  dom.shortcutList.querySelectorAll("[data-shortcut-input]").forEach((input) => {
    const shortcut = state.shortcuts.find((item) => item.id === input.dataset.shortcutInput);
    if (shortcut && input.value !== shortcut.defaultGesture) {
      draft.shortcutOverrides[input.dataset.shortcutInput] = input.value;
    }
  });

  dom.settingsSave.disabled = true;
  dom.settingsStatus.textContent = "正在保存...";
  try {
    const pendingApiKey = dom.imgbbApiKey.value.trim();
    if (pendingApiKey) {
      const keySettings = await invoke("save_imgbb_api_key", {
        secret: pendingApiKey,
      });
      draft.protectedImgbbApiKey = keySettings.protectedImgbbApiKey;
      dom.imgbbApiKey.value = "";
    }
    if (draft.imageStorageMode === "imgBb" && !draft.protectedImgbbApiKey) {
      throw new Error("请先填写并保存 ImgBB API Key。");
    }
    state.settings = await invoke("save_settings", { settings: draft });
    state.backgroundDataUri = draft.backgroundDataUri || "";
    state.shortcutMap = buildShortcutMap();
    applySettings();
    renderSidebar();
    updateStatus();
    schedulePreview({ preservePosition: true, immediate: true });
    dom.settingsDialog.close();
    toast("设置已保存。", "success");
  } catch (error) {
    dom.settingsStatus.textContent = formatError(error);
  } finally {
    dom.settingsSave.disabled = false;
  }
}

function openAboutDialog() {
  dom.aboutVersion.textContent = state.bootstrap?.version || "2.0.0";
  dom.aboutSettingsPath.textContent =
    state.bootstrap?.settingsPath || "浏览器开发模式（未连接 Rust 后端）";
  dom.aboutDialog.showModal();
}

function buildShortcutMap() {
  const defaults = defaultShortcutMap(state.shortcuts);
  return {
    ...defaults,
    ...(state.settings?.shortcutOverrides || {}),
  };
}

function gestureFromKeyboardEvent(event) {
  const key = normalizedKey(event.key, event.code);
  if (!key) return null;
  const modifiers = [];
  if (event.ctrlKey) modifiers.push("Ctrl");
  if (event.shiftKey) modifiers.push("Shift");
  if (event.altKey) modifiers.push("Alt");
  if (!modifiers.length && !/^F\d{1,2}$/.test(key)) return null;
  return [...modifiers, key].join("+");
}

function normalizedKey(key, code) {
  if (/^F\d{1,12}$/.test(key)) return key.toUpperCase();
  if (key.length === 1) return key.toUpperCase();
  if (code === "Escape") return "Escape";
  if (code === "Tab") return "Tab";
  if (code === "Space") return "Space";
  if (code.startsWith("Arrow")) return code;
  return null;
}

function handleGlobalKeydown(event) {
  if (event.key === "Escape") {
    hideContextMenu();
    closeMenus();
    if (!dom.findBar.hidden) closeFind();
    return;
  }
  if (dom.settingsDialog.open || dom.aboutDialog.open || dom.unsavedDialog.open) return;

  if (event.ctrlKey && event.key === "Tab") {
    event.preventDefault();
    switchRelativeDocument(event.shiftKey ? -1 : 1);
    return;
  }
  if (event.key === "F5") {
    event.preventDefault();
    void reloadActiveDocument();
    return;
  }
  if (event.ctrlKey && !event.altKey && /^[1-6]$/.test(event.key)) {
    event.preventDefault();
    void applyMarkdownFormat(`h${event.key}`);
    return;
  }
  if (event.ctrlKey && event.key.toLowerCase() === "f") {
    event.preventDefault();
    openFind(false);
    return;
  }
  if (event.ctrlKey && event.key.toLowerCase() === "h") {
    event.preventDefault();
    openFind(true);
    return;
  }

  const gesture = gestureFromKeyboardEvent(event);
  if (!gesture) return;
  const shortcutId = Object.entries(state.shortcutMap).find(
    ([, value]) => normalizeGesture(value) === normalizeGesture(gesture),
  )?.[0];
  if (!shortcutId) return;
  event.preventDefault();
  switch (shortcutId) {
    case "new":
      createDocument({ activate: true });
      break;
    case "open":
      void commandOpen();
      break;
    case "save":
      void saveActiveDocument();
      break;
    case "save-as":
      void saveActiveDocument({ saveAs: true });
      break;
    case "reload":
      void reloadActiveDocument();
      break;
    case "cycle-view":
      cycleViewMode();
      break;
    case "bold":
    case "italic":
    case "strike":
    case "inline-code":
    case "code-block":
    case "link":
    case "image":
    case "ordered-list":
    case "unordered-list":
    case "quote":
    case "task-list":
      void applyMarkdownFormat(shortcutId);
      break;
    case "heading-1":
    case "heading-2":
    case "heading-3":
    case "heading-4":
    case "heading-5":
    case "heading-6":
      void applyMarkdownFormat(`h${shortcutId.at(-1)}`);
      break;
  }
}

function switchRelativeDocument(delta) {
  if (state.documents.length < 2) return;
  const current = state.documents.findIndex((documentModel) => documentModel.id === state.activeId);
  const next = (current + delta + state.documents.length) % state.documents.length;
  activateDocument(state.documents[next].id);
}

async function inspectExternalChange(path) {
  const documentModel = state.documents.find((candidate) => samePath(candidate.path, path));
  if (!documentModel) return;
  if (Date.now() < state.suppressExternalUntil) return;
  try {
    const stamp = await invoke("inspect_document_stamp", { path });
    if (sameStamp(stamp, documentModel.stamp)) return;
    const dirty = isDirty(documentModel);
    const behavior = resolveExternalFileBehavior(path);
    const reload =
      !dirty && behavior === "autoReload"
        ? true
        : window.confirm(
            dirty
              ? `文档已在外部修改：\n${path}\n\n当前文档有未保存修改。是否继续重新加载？`
              : `文档已在外部修改：\n${path}\n\n是否重新加载？`,
          );
    if (!reload) return;
    if (dirty && !(await confirmUnsavedDocument(documentModel))) return;
    const loaded = await invoke("load_document", { path });
    documentModel.text = loaded.text;
    documentModel.savedText = loaded.text;
    documentModel.stamp = loaded.stamp;
    if (state.activeId === documentModel.id) {
      editor.setValue(loaded.text, { scrollTop: documentModel.scrollTop });
      preview.clearImageCache();
      schedulePreview({ preservePosition: true, immediate: true });
    }
    renderEverything();
    toast("已重新加载外部修改。", "success");
  } catch (error) {
    toast(`检查外部修改失败：${formatError(error)}`, "error", 7000);
  }
}

function resolveExternalFileBehavior(path) {
  const overrides = state.settings.externalFileOverrides || {};
  const normalized = String(path).toLocaleLowerCase();
  const override = Object.entries(overrides).find(
    ([candidate]) => candidate.toLocaleLowerCase() === normalized,
  )?.[1];
  return override || state.settings.externalFileChangeBehavior || "prompt";
}

function beginSplitterDrag(event) {
  event.preventDefault();
  dom.splitter.classList.add("dragging");
  dom.splitter.setPointerCapture(event.pointerId);
  const move = (moveEvent) => {
    const rect = dom.mainLayout.getBoundingClientRect();
    const left = state.settings.sidebarVisible ? state.settings.sidebarWidth : 0;
    const usable = rect.width - left - 5;
    const ratio = (moveEvent.clientX - rect.left - left) / usable;
    state.settings.splitRatio = Math.min(0.82, Math.max(0.18, ratio));
    dom.root.style.setProperty("--editor-ratio", String(state.settings.splitRatio));
  };
  const end = () => {
    dom.splitter.classList.remove("dragging");
    dom.splitter.removeEventListener("pointermove", move);
    dom.splitter.removeEventListener("pointerup", end);
    dom.splitter.removeEventListener("pointercancel", end);
    scheduleSettingsPersistence();
  };
  dom.splitter.addEventListener("pointermove", move);
  dom.splitter.addEventListener("pointerup", end);
  dom.splitter.addEventListener("pointercancel", end);
}

function handleSplitterKeydown(event) {
  if (event.key !== "ArrowLeft" && event.key !== "ArrowRight") return;
  event.preventDefault();
  const delta = event.key === "ArrowLeft" ? -0.02 : 0.02;
  state.settings.splitRatio = Math.min(0.82, Math.max(0.18, state.settings.splitRatio + delta));
  dom.root.style.setProperty("--editor-ratio", String(state.settings.splitRatio));
  scheduleSettingsPersistence();
}

function beginSidebarResize(event) {
  event.preventDefault();
  dom.sidebarResizer.classList.add("dragging");
  dom.sidebarResizer.setPointerCapture(event.pointerId);
  const move = (moveEvent) => {
    const rect = dom.mainLayout.getBoundingClientRect();
    state.settings.sidebarWidth = Math.max(
      180,
      Math.min(520, moveEvent.clientX - rect.left),
    );
    dom.root.style.setProperty("--sidebar-width", `${state.settings.sidebarWidth}px`);
  };
  const end = () => {
    dom.sidebarResizer.classList.remove("dragging");
    dom.sidebarResizer.removeEventListener("pointermove", move);
    dom.sidebarResizer.removeEventListener("pointerup", end);
    dom.sidebarResizer.removeEventListener("pointercancel", end);
    scheduleSettingsPersistence();
  };
  dom.sidebarResizer.addEventListener("pointermove", move);
  dom.sidebarResizer.addEventListener("pointerup", end);
  dom.sidebarResizer.addEventListener("pointercancel", end);
}

function handleSidebarResizeKeydown(event) {
  if (event.key !== "ArrowLeft" && event.key !== "ArrowRight") return;
  event.preventDefault();
  const delta = event.key === "ArrowLeft" ? -8 : 8;
  state.settings.sidebarWidth = Math.max(
    180,
    Math.min(520, state.settings.sidebarWidth + delta),
  );
  dom.root.style.setProperty("--sidebar-width", `${state.settings.sidebarWidth}px`);
  scheduleSettingsPersistence();
}

function toast(message, type = "", duration = 3500) {
  const item = document.createElement("div");
  item.className = `toast ${type}`.trim();
  item.textContent = message;
  dom.toastStack.append(item);
  window.setTimeout(() => item.remove(), duration);
}

function closeMenus() {
  document.querySelectorAll(".menu.open").forEach((menu) => menu.classList.remove("open"));
}

function closeDialog(id) {
  const dialog = document.getElementById(id);
  if (dialog?.open) dialog.close();
}

function activeDocument() {
  return state.documents.find((documentModel) => documentModel.id === state.activeId) || null;
}

function ensureEditableDocument() {
  const documentModel = activeDocument();
  if (documentModel?.readOnly) {
    toast("当前文档为只读；请使用“另存为”创建可编辑副本。", "error");
    return false;
  }
  return Boolean(documentModel);
}

function isDirty(documentModel) {
  return documentModel.text !== documentModel.savedText;
}

function isActivePath(path) {
  const documentModel = activeDocument();
  return Boolean(documentModel?.path && samePath(documentModel.path, path));
}

function isActiveWorkspacePath(path) {
  return Boolean(state.workspaceRoot && samePath(state.workspaceRoot, path));
}

function samePath(left, right) {
  if (!left || !right) return false;
  return String(left).replaceAll("\\", "/").toLocaleLowerCase() === String(right).replaceAll("\\", "/").toLocaleLowerCase();
}

function isPathAtOrBelow(candidate, root) {
  if (!candidate || !root) return false;
  const normalizedCandidate = String(candidate).replaceAll("\\", "/").replace(/\/+$/, "");
  const normalizedRoot = String(root).replaceAll("\\", "/").replace(/\/+$/, "");
  return (
    normalizedCandidate.toLocaleLowerCase() === normalizedRoot.toLocaleLowerCase() ||
    normalizedCandidate
      .toLocaleLowerCase()
      .startsWith(`${normalizedRoot.toLocaleLowerCase()}/`)
  );
}

function sameStamp(left, right) {
  return (
    left?.length === right?.length &&
    left?.modifiedUnixMs === right?.modifiedUnixMs
  );
}

function joinPath(directory, relative) {
  if (!directory) return relative;
  return `${directory.replace(/[\\/]+$/, "")}\\${relative.replaceAll("/", "\\")}`;
}

async function firstWorkspaceFileAsync(entries, budget = { remaining: 500 }) {
  for (const entry of entries) {
    if (budget.remaining-- <= 0) return null;
    if (!entry.isDirectory) return entry;
    if (!entry.childrenLoaded) {
      try {
        entry.children = await invoke("workspace_children", {
          root: state.workspaceRoot,
          directory: entry.path,
        });
        entry.childrenLoaded = true;
      } catch {
        entry.children = [];
        entry.childrenLoaded = true;
      }
    }
    if (entry.children?.length) {
      const nested = await firstWorkspaceFileAsync(entry.children, budget);
      if (nested) return nested;
    }
  }
  return null;
}

function fontStack(family, code) {
  const normalized = String(family || "").replaceAll('"', "");
  const bundled = bundledFonts.find(
    (font) =>
      font.id === normalized ||
      font.label === normalized ||
      font.family === normalized ||
      `WIMD ${font.family}` === normalized,
  );
  if (normalized) {
    const resolved = bundled ? `"WIMD ${bundled.family}"` : `"${normalized}"`;
    return code
      ? `${resolved}, "Cascadia Code", "Consolas", "Microsoft YaHei UI", monospace`
      : `${resolved}, "Microsoft YaHei UI", "Segoe UI", sans-serif`;
  }
  return code
    ? '"Cascadia Code", "Consolas", "Microsoft YaHei UI", monospace'
    : '"Microsoft YaHei UI", "Segoe UI", sans-serif';
}

function fontDisplayName(value) {
  const normalized = String(value || "");
  const bundled = bundledFonts.find(
    (font) => font.id === normalized || font.family === normalized || `WIMD ${font.family}` === normalized,
  );
  return bundled?.label || normalized;
}

function normalizeFontValue(value) {
  const normalized = String(value || "").trim();
  return (
    bundledFonts.find(
      (font) => font.id === normalized || font.label === normalized || font.family === normalized,
    )?.id || normalized
  );
}

function clampNumber(value, minimum, maximum, fallback) {
  const number = Number(value);
  return Number.isFinite(number) ? Math.min(maximum, Math.max(minimum, number)) : fallback;
}

function normalizeGesture(value) {
  const parts = String(value || "")
    .split("+")
    .map((part) => part.trim())
    .filter(Boolean)
    .map((part) => {
      const lower = part.toLowerCase();
      if (lower === "control") return "ctrl";
      return lower.length === 1 ? lower.toUpperCase() : lower;
    });
  const key = parts.at(-1) || "";
  const modifiers = ["ctrl", "shift", "alt"].filter((modifier) =>
    parts.includes(modifier),
  );
  return [...modifiers, key.toLowerCase()].filter(Boolean).join("+");
}

function fallbackSettings() {
  return {
    recentFiles: [],
    recentWorkspaces: [],
    theme: "system",
    editorFontFamily: "",
    editorFontSize: 15,
    previewFontFamily: "",
    previewFontSize: 16,
    sidebarVisible: true,
    sidebarWidth: 252,
    splitRatio: 0.5,
    windowX: null,
    windowY: null,
    windowWidth: 1320,
    windowHeight: 820,
    windowMaximized: false,
    backgroundImagePath: null,
    backgroundOpacity: 0.18,
    fileOpenViewMode: "rememberLast",
    lastViewMode: "editorAndPreview",
    orderedListStyle: "plain",
    previewStyleFiles: { fileOverrides: {} },
    localImageDirectory: "img",
    imageStorageMode: "local",
    protectedImgbbApiKey: null,
    externalFileChangeBehavior: "prompt",
    externalFileOverrides: {},
    checkForUpdatesOnStartup: false,
    lastWorkspace: null,
    shortcutOverrides: {},
  };
}

void initialize();
