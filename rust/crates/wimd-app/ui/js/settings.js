import {
  displayPath,
  formatError,
  getCurrentWindow,
  invoke,
  pathFileName,
} from "./api.js";
import { THEME_OPTIONS, themeOption } from "./theme-catalog.js";

const root = document.documentElement;
const mount = document.getElementById("settings-root");
const currentWindow = getCurrentWindow();
const validModes = new Set(["appearance", "preview", "images", "shortcuts", "behavior"]);
const queryMode = new URLSearchParams(window.location.search).get("kind");
const label = currentWindow?.label || "";
const initialMode = validModes.has(queryMode)
  ? queryMode
  : validModes.has(label.replace(/^settings-/, ""))
    ? label.replace(/^settings-/, "")
    : "appearance";

const bundledFonts = [
  { id: "wimd:lxgw-wenkai", label: "霞鹜文楷", family: "LXGW WenKai" },
  { id: "wimd:lxgw-wenkai-mono", label: "霞鹜文楷等宽", family: "LXGW WenKai Mono" },
  { id: "wimd:lxgw-marker-gothic", label: "霞鹜漫黑", family: "LXGW Marker Gothic" },
  { id: "wimd:jetbrains-mono", label: "JetBrains Mono", family: "JetBrains Mono" },
  { id: "wimd:fira-code", label: "Fira Code", family: "Fira Code" },
];

const orderedStyleOptions = [
  { value: "plain", label: "原生简洁" },
  { value: "bold", label: "加粗强调" },
  { value: "circle", label: "圆形徽标" },
  { value: "square", label: "圆角方标" },
];

const state = {
  mode: initialMode,
  bootstrap: null,
  settings: null,
  draft: null,
  context: null,
  activeTab: "theme",
  backgroundDataUri: "",
  apiKeyDraft: "",
  clearStoredKey: false,
  busy: false,
  status: "",
  statusTone: "normal",
  capturingShortcut: null,
  shortcutMessage: "",
  shortcutMessageTone: "normal",
  installedFonts: [],
};

void initialize();

async function initialize() {
  try {
    const bootstrap = await invoke("bootstrap");
    let context = null;
    try {
      context = await invoke("settings_context");
    } catch {
      context = null;
    }
    state.bootstrap = bootstrap;
    state.settings = bootstrap.settings;
    state.draft = structuredClone(bootstrap.settings);
    state.context = context || {};
    state.installedFonts = await invoke("installed_fonts").catch(() => []);
    populateFontFamilyOptions();
    if (validModes.has(context?.kind)) state.mode = context.kind;
    state.backgroundDataUri = "";
    if (state.mode === "appearance" && state.draft.backgroundImagePath) {
      await loadBackgroundDraft().catch(() => {});
    }
    applyPageTheme(state.settings.theme);
    render();
    installKeyboardHandling();
    window.matchMedia("(prefers-color-scheme: dark)").addEventListener("change", () => {
      if (state.settings?.theme === "system") applyPageTheme("system");
    });
  } catch (error) {
    mount.textContent = `设置初始化失败：${formatError(error)}`;
  }
}

function applyPageTheme(themeId) {
  const effective =
    themeId === "system"
      ? window.matchMedia("(prefers-color-scheme: dark)").matches
        ? "dark"
        : "light"
      : themeId;
  const theme = themeOption(effective);
  root.dataset.theme = theme.id;
  const variables = {
    "--app-bg": theme.bg,
    "--surface": theme.surface,
    "--surface-soft": theme.muted,
    "--surface-muted": theme.muted,
    "--surface-accent": `${theme.accent}20`,
    "--border": theme.border,
    "--border-strong": theme.secondary,
    "--text": theme.text,
    "--text-secondary": theme.secondary,
    "--text-muted": theme.secondary,
    "--accent": theme.accent,
    "--accent-hover": theme.accentHover,
    "--accent-soft": `${theme.accent}18`,
    "--success": theme.dark ? "#7bd2a5" : "#257a58",
    "--danger": theme.dark ? "#ff9098" : "#b43f4c",
  };
  for (const [name, value] of Object.entries(variables)) {
    root.style.setProperty(name, value);
  }
  root.style.colorScheme = theme.dark ? "dark" : "light";
}

function render() {
  mount.replaceChildren();
  switch (state.mode) {
    case "preview":
      mount.append(renderPreviewSettings());
      break;
    case "images":
      mount.append(renderImageSettings());
      break;
    case "shortcuts":
      mount.append(renderShortcutSettings());
      break;
    case "behavior":
      mount.append(renderBehaviorSettings());
      break;
    default:
      mount.append(renderAppearanceSettings());
      break;
  }
}

function renderAppearanceSettings() {
  const main = element("main", "settings-main");
  main.append(
    header(
      "让写作更合心意",
      "主题、字体与背景，都在这里。点击应用即可试用，不关闭窗口。",
    ),
  );

  const layout = element("div", "appearance-layout");
  const nav = element("nav", "settings-nav");
  nav.setAttribute("aria-label", "外观设置分类");
  const tabs = [
    ["theme", "主题"],
    ["font", "字体"],
    ["background", "背景"],
  ];
  for (const [id, labelText] of tabs) {
    const button = actionButton(labelText, "", () => {
      state.activeTab = id;
      render();
    });
    button.classList.toggle("active", state.activeTab === id);
    nav.append(button);
  }
  const page = element("section", "settings-page");
  page.id = "appearance-page";
  layout.append(nav, page);
  main.append(layout);
  main.append(
    footer({
      restore: resetAppearanceDraft,
      cancel: closeSettingsWindow,
      actions: [
        { label: "应用", action: () => void persistSettings(false) },
        { label: "保存", action: () => void persistSettings(true), primary: true },
      ],
    }),
  );

  queueMicrotask(() => renderAppearancePage(page));
  return main;
}

function renderAppearancePage(page) {
  page.replaceChildren();
  if (state.activeTab === "font") {
    page.append(renderFontPage());
  } else if (state.activeTab === "background") {
    page.append(renderBackgroundPage());
  } else {
    page.append(renderThemePage());
  }
}

function renderThemePage() {
  const fragment = document.createDocumentFragment();
  fragment.append(
    pageHeading("界面主题", "低饱和配色，照顾长时间阅读。"),
  );
  const grid = element("div", "theme-grid");
  for (const theme of THEME_OPTIONS) {
    const card = element("button", "theme-card");
    card.type = "button";
    card.dataset.themeId = theme.id;
    card.style.setProperty("--card-bg", theme.bg);
    card.style.setProperty("--card-surface", theme.surface);
    card.style.setProperty("--card-text", theme.text);
    card.style.setProperty("--card-secondary", theme.secondary);
    card.style.setProperty("--card-accent", theme.accent);
    card.classList.toggle("active", state.draft.theme === theme.id);
    card.setAttribute("aria-pressed", String(state.draft.theme === theme.id));
    const swatch = element("span", "theme-swatch", "Aa · 文");
    const name = element("strong", "", theme.name);
    const description = element("small", "", theme.description);
    card.append(swatch, name, description);
    card.addEventListener("click", () => {
      state.draft.theme = theme.id;
      const host = document.getElementById("appearance-page");
      if (host) renderAppearancePage(host);
    });
    grid.append(card);
  }
  fragment.append(grid);
  return fragment;
}

function renderFontPage() {
  const fragment = document.createDocumentFragment();
  fragment.append(
    pageHeading(
      "字体与字号",
      "内置开源字体无需安装；也可选择本机字体。直接输入中文或英文名筛选，再点击候选。",
    ),
  );

  const editorGrid = element("div", "field-grid");
  const editorField = field("编辑区字体");
  const editorInput = element("input");
  editorInput.id = "editor-font-family";
  editorInput.setAttribute("list", "font-families");
  editorInput.placeholder = "跟随 WIMD 默认";
  editorInput.value = fontDisplayName(state.draft.editorFontFamily);
  const editorSizeField = field("字号");
  const editorSize = numberInput(state.draft.editorFontSize, 10, 32);
  editorSizeField.append(editorSize);
  editorField.append(editorInput);
  editorGrid.append(editorField, editorSizeField);

  const previewGrid = element("div", "field-grid");
  const previewField = field("预览区字体");
  const previewInput = element("input");
  previewInput.id = "preview-font-family";
  previewInput.setAttribute("list", "font-families");
  previewInput.placeholder = "跟随 WIMD 默认";
  previewInput.value = fontDisplayName(state.draft.previewFontFamily);
  const previewSizeField = field("字号");
  const previewSize = numberInput(state.draft.previewFontSize, 10, 32);
  previewSizeField.append(previewSize);
  previewField.append(previewInput);
  previewGrid.append(previewField, previewSizeField);

  const previewCard = element("div", "font-preview");
  previewCard.append(element("span", "", "字体试读"));
  const editorPreview = element(
    "pre",
    "font-preview-editor",
    "# WIMD · 清晰专注 0123456789\nconst answer = 42;  // Hello, 世界",
  );
  const previewPreview = element(
    "p",
    "font-preview-preview",
    "风过竹梢，月落书页。\n把灵感留在这里，让文字自然流淌。",
  );
  previewCard.append(editorPreview, previewPreview);

  function updateFontPreview({ commitFonts = false } = {}) {
    const editorFontFamily = normalizeFontValue(editorInput.value);
    const previewFontFamily = normalizeFontValue(previewInput.value);
    if (commitFonts) {
      state.draft.editorFontFamily = editorFontFamily;
      state.draft.previewFontFamily = previewFontFamily;
    }
    state.draft.editorFontSize = clampNumber(editorSize.value, 10, 32, 15);
    state.draft.previewFontSize = clampNumber(previewSize.value, 10, 32, 16);
    editorPreview.style.fontFamily = fontStack(editorFontFamily, true);
    editorPreview.style.fontSize = `${state.draft.editorFontSize}px`;
    previewPreview.style.fontFamily = fontStack(previewFontFamily, false);
    previewPreview.style.fontSize = `${state.draft.previewFontSize}px`;
  }

  editorInput.addEventListener("input", updateFontPreview);
  editorInput.addEventListener("change", () => updateFontPreview({ commitFonts: true }));
  previewInput.addEventListener("input", updateFontPreview);
  previewInput.addEventListener("change", () => updateFontPreview({ commitFonts: true }));
  editorSize.addEventListener("input", updateFontPreview);
  previewSize.addEventListener("input", updateFontPreview);

  fragment.append(editorGrid, previewGrid, previewCard);
  const note = element(
    "p",
    "page-note license-note",
    "内置字体均按 SIL OFL 1.1 原样分发，许可证与版权声明随软件附带。本机字体仅引用，不复制或分发。",
  );
  fragment.append(note);
  const licenseButton = actionButton("查看字体许可证与来源", "subtle", () => {
    void invoke("open_external", {
      url: "https://github.com/lxgw/LxgwWenKai",
    }).catch((error) => setStatus(`无法打开字体来源：${formatError(error)}`, "error"));
  });
  licenseButton.style.marginTop = "10px";
  fragment.append(licenseButton);
  queueMicrotask(updateFontPreview);
  return fragment;
}

function renderBackgroundPage() {
  const fragment = document.createDocumentFragment();
  fragment.append(
    pageHeading(
      "自定义背景",
      "图片会先复制到 WIMD 本地目录，不上传。调整后点击“应用”或“保存”生效。",
    ),
  );
  const preview = element("div", "background-preview");
  const empty = element("span", "", "还没有选择背景图片");
  preview.append(empty);
  const fileName = element("div", "background-file-name");
  const buttons = element("div", "button-row");
  const choose = actionButton("选择图片", "", () => void chooseBackground());
  const remove = actionButton("移除", "", () => {
    state.draft.backgroundImagePath = null;
    state.backgroundDataUri = "";
    render();
  });
  buttons.append(choose, remove);
  const opacity = element("label", "field background-opacity");
  const opacityLabel = element("span");
  const output = element(
    "output",
    "",
    `${Math.round((state.draft.backgroundOpacity ?? 0.18) * 100)}%`,
  );
  opacityLabel.append("背景可见度 ", output);
  const slider = element("input");
  slider.type = "range";
  slider.min = "0";
  slider.max = "100";
  slider.step = "1";
  slider.value = String(Math.round((state.draft.backgroundOpacity ?? 0.18) * 100));
  slider.disabled = !state.draft.backgroundImagePath;
  slider.addEventListener("input", () => {
    output.textContent = `${slider.value}%`;
    state.draft.backgroundOpacity = Number(slider.value) / 100;
    preview.style.opacity = String(0.35 + Number(slider.value) * 0.0065);
  });
  opacity.append(opacityLabel, slider);
  const note = element(
    "p",
    "page-note",
    "移动或删除原图不会影响 WIMD 已保存的本地副本；移除背景也不会删除源文件。",
  );
  fragment.append(preview, fileName, buttons, opacity, note);

  if (state.backgroundDataUri) {
    preview.classList.remove("is-empty");
    preview.style.backgroundImage = `url("${state.backgroundDataUri}")`;
    empty.hidden = true;
    preview.style.opacity = String(
      0.35 + Math.round((state.draft.backgroundOpacity ?? 0.18) * 100) * 0.0065,
    );
    fileName.textContent = pathFileName(state.draft.backgroundImagePath || "背景图片");
  } else {
    preview.classList.add("is-empty");
    fileName.textContent = "尚未选择图片";
  }
  remove.disabled = !state.draft.backgroundImagePath;
  return fragment;
}

async function chooseBackground() {
  const button = document.querySelector("#appearance-page .button-row button");
  if (button) {
    button.disabled = true;
    button.textContent = "正在导入...";
  }
  try {
    const selected = await invoke("choose_background_image");
    if (!selected) return;
    state.draft.backgroundImagePath = selected.path;
    state.backgroundDataUri = selected.dataUri;
    setStatus("背景副本已准备好；点击“应用”或“保存”后生效。", "success");
    render();
  } catch (error) {
    setStatus(`背景导入失败，已保留原背景：${formatError(error)}`, "error");
  } finally {
    if (button?.isConnected) {
      button.disabled = false;
      button.textContent = "选择图片";
    }
  }
}

async function loadBackgroundDraft() {
  if (!state.draft?.backgroundImagePath) return;
  const loaded = await invoke("load_background_image", {
    path: state.draft.backgroundImagePath,
  });
  state.backgroundDataUri = loaded.dataUri;
}

function renderPreviewSettings() {
  const main = element("main", "settings-main");
  main.append(
    header(
      "让数字编号更清晰",
      "只改变预览和 PDF 外观，不改 Markdown 源文。",
    ),
  );
  const layout = element("div", "appearance-layout");
  const nav = element("nav", "settings-nav");
  const active = actionButton("有序列表", "active", () => {});
  nav.append(active);
  const page = element("section", "settings-page");
  page.append(renderPreviewPage());
  layout.append(nav, page);
  main.append(layout);
  main.append(
    footer({
      cancel: closeSettingsWindow,
      actions: [
        { label: "应用", action: () => void persistSettings(false) },
        { label: "确定", action: () => void persistSettings(true), primary: true },
      ],
    }),
  );
  return main;
}

function renderPreviewPage() {
  const fragment = document.createDocumentFragment();
  fragment.append(pageHeading("有序列表", "选择全局默认和当前文件覆盖。"));

  const globalField = field("全局默认");
  const globalSelect = selectInput(
    orderedStyleOptions.map((item) => [item.value, item.label]),
    state.draft.orderedListStyle || "plain",
  );
  globalField.append(globalSelect);
  fragment.append(globalField);
  fragment.append(
    element(
      "p",
      "page-note",
      "保存到本机，供以后新开的窗口使用；其他已开窗口不实时同步。",
    ),
  );

  const current = currentOrderedOverride();
  const localField = field("当前文件");
  localField.style.marginTop = "18px";
  const localSelect = selectInput(
    [["follow", "跟随全局默认"], ...orderedStyleOptions.map((item) => [item.value, item.label])],
    current || "follow",
  );
  localSelect.disabled = !state.context?.documentPath;
  localField.append(localSelect);
  fragment.append(localField);
  fragment.append(
    element(
      "p",
      "page-note",
      state.context?.documentPath
        ? `按文件保存在本机：${state.context.documentPath}`
        : "请先保存当前文档，再设置文件级覆盖。",
    ),
  );

  const preview = element("div", "style-preview");
  preview.append(element("div", "style-preview-title", "当前文件效果示意"));
  const rows = element("div");
  preview.append(rows);
  fragment.append(preview);
  fragment.append(
    element(
      "p",
      "page-note",
      "多位数自动撑宽；嵌套和从指定数字开始的列表保留原有顺序。",
    ),
  );

  function refreshPreview() {
    const global = globalSelect.value;
    const local = localSelect.value;
    renderOrderedPreview(rows, local === "follow" ? global : local);
  }
  globalSelect.addEventListener("change", refreshPreview);
  localSelect.addEventListener("change", refreshPreview);
  queueMicrotask(refreshPreview);
  queueMicrotask(() => {
    state.draft.orderedListStyle = globalSelect.value;
    globalSelect.addEventListener("change", () => {
      state.draft.orderedListStyle = globalSelect.value;
    });
    localSelect.addEventListener("change", () => {
      setCurrentOrderedOverride(localSelect.value);
    });
  });
  return fragment;
}

function renderOrderedPreview(container, style) {
  container.replaceChildren();
  for (const [number, text] of [
    ["1", "第一项内容"],
    ["2", "第二项内容"],
    ["12", "多位数编号"],
  ]) {
    const row = element("div", "style-preview-row");
    const marker = element(
      "span",
      `style-marker ${style}`,
      style === "circle" || style === "square" ? number : `${number}.`,
    );
    row.append(marker, element("span", "", text));
    container.append(row);
  }
}

function renderImageSettings() {
  const main = element("main", "settings-main");
  main.classList.add("compact-settings");
  main.append(
    header(
      "图片插入与远程预览",
      "选择图片或粘贴微信截图时，WIMD 会按这里的方式保存图片并插入 Markdown。普通文本粘贴不受影响。",
    ),
  );
  const page = element("section", "single-page");
  const modeCard = element("div", "content-card");
  modeCard.append(element("h3", "", "保存方式"));
  const storageMode = selectInput(
    [
      ["local", "保存到本地图片目录（推荐）"],
      ["imgBb", "上传到 ImgBB 免费图床"],
    ],
    state.draft.imageStorageMode || "local",
  );
  modeCard.append(storageMode);

  const localPanel = element("div");
  localPanel.style.marginTop = "14px";
  const localField = field("相对于当前 Markdown 文件的图片目录");
  const localDirectory = element("input");
  localDirectory.value = state.draft.localImageDirectory || "img";
  localDirectory.placeholder = "img";
  localField.append(localDirectory);
  localPanel.append(
    localField,
    element(
      "p",
      "page-note",
      "只允许文档目录内的相对路径，不允许 ..、绝对路径或符号链接越界。",
    ),
  );

  const imgbbPanel = element("div");
  imgbbPanel.style.marginTop = "14px";
  imgbbPanel.append(
    element(
      "p",
      "page-note",
      "上传模式只发送所选或粘贴的图片，不发送 Markdown 正文。需要使用你自己的 ImgBB API Key。",
    ),
  );
  const keyRow = element("div", "button-row");
  keyRow.style.marginTop = "10px";
  const keyInput = element("input");
  keyInput.type = "password";
  keyInput.autocomplete = "off";
  keyInput.placeholder = "留空表示保留已保存的 API Key";
  keyInput.style.flex = "1";
  keyInput.value = state.apiKeyDraft;
  const apiButton = actionButton("获取 API Key", "", () => {
    void invoke("open_external", { url: "https://api.imgbb.com/" }).catch((error) =>
      setStatus(`无法打开 ImgBB API 页面：${formatError(error)}`, "error"),
    );
  });
  keyRow.append(keyInput, apiButton);
  const keyStatusRow = element("div", "switch-row");
  const keyStatus = element("span", "page-note");
  const clearKey = actionButton("清除已保存密钥", "subtle", () => {
    state.clearStoredKey = true;
    state.apiKeyDraft = "";
    keyInput.value = "";
    keyStatus.textContent = "保存后将清除已存密钥。";
  });
  keyStatusRow.append(keyStatus, clearKey);
  imgbbPanel.append(keyRow, keyStatusRow);
  modeCard.append(localPanel, imgbbPanel);
  page.append(modeCard);
  page.append(
    element(
      "p",
      "page-note",
      "图片链接默认允许加载，支持 HTTP、HTTPS、SVG 和没有文件后缀的图片地址。",
    ),
  );

  function syncImageMode() {
    state.draft.imageStorageMode = storageMode.value;
    const usesImgBb = storageMode.value === "imgBb";
    localPanel.hidden = usesImgBb;
    imgbbPanel.hidden = !usesImgBb;
  }
  storageMode.addEventListener("change", syncImageMode);
  localDirectory.addEventListener("input", () => {
    state.draft.localImageDirectory = localDirectory.value;
  });
  keyInput.addEventListener("input", () => {
    state.apiKeyDraft = keyInput.value;
    if (keyInput.value.trim()) state.clearStoredKey = false;
  });
  keyStatus.textContent = state.draft.protectedImgbbApiKey
    ? "已保存 API Key；输入新值可替换，留空则保留。"
    : "尚未保存 API Key。";
  clearKey.disabled = !state.draft.protectedImgbbApiKey && !state.clearStoredKey;
  syncImageMode();

  main.append(page);
  main.append(
    footer({
      cancel: closeSettingsWindow,
      actions: [{ label: "保存", action: () => void persistSettings(true), primary: true }],
    }),
  );
  return main;
}

function renderShortcutSettings() {
  const main = element("main", "settings-main");
  main.classList.add("compact-settings");
  main.append(
    header(
      "快捷键",
      "点击某一项的组合键按钮，然后按下新的快捷键。重复组合和保留键不会被保存。",
    ),
  );
  const page = element("section", "single-page");
  const table = element("div", "shortcut-table");
  table.append(
    rowHeader(["功能", "当前快捷键"], "shortcut-table-header"),
  );
  const list = element("div", "shortcut-list");
  list.id = "shortcut-list";
  const assignments = effectiveShortcutMap();
  for (const shortcut of state.bootstrap?.shortcuts || []) {
    const row = rowHeader([
      shortcut.label,
      "",
    ], "shortcut-row");
    const gestureButton = actionButton(
      state.capturingShortcut === shortcut.id
        ? "请按新的快捷键..."
        : assignments[shortcut.id] || shortcut.defaultGesture,
      "shortcut-button",
      () => {
        state.capturingShortcut = shortcut.id;
        state.shortcutMessage = "现在按下新的组合键；按 Esc 取消本次设置。";
        state.shortcutMessageTone = "normal";
        render();
      },
    );
    gestureButton.classList.toggle("capturing", state.capturingShortcut === shortcut.id);
    row.children[1].append(gestureButton);
    list.append(row);
  }
  table.append(list);
  page.append(table);
  const message = element(
    "div",
    `validation-message${state.shortcutMessageTone === "success" ? " success" : ""}`,
    state.shortcutMessage,
  );
  page.append(message);

  main.append(page);
  main.append(
    footer({
      restore: resetShortcuts,
      cancel: closeSettingsWindow,
      actions: [{ label: "保存", action: () => void persistSettings(true), primary: true }],
    }),
  );
  return main;
}

function renderBehaviorSettings() {
  const main = element("main", "settings-main");
  main.append(
    header(
      "文件打开与外部修改",
      "这些行为按 Windows 当前用户保存在本机；有未保存修改时始终询问。",
    ),
  );
  const page = element("section", "single-page");

  const openCard = element("div", "content-card");
  openCard.append(element("h3", "", "打开文件时的显示模式"));
  const mode = selectInput(
    [
      ["rememberLast", "沿用上次模式"],
      ["editorOnly", "仅编辑"],
      ["editorAndPreview", "编辑 + 预览"],
      ["previewOnly", "仅预览"],
    ],
    state.draft.fileOpenViewMode,
  );
  mode.addEventListener("change", () => {
    state.draft.fileOpenViewMode = mode.value;
  });
  openCard.append(mode);
  page.append(openCard);

  const externalCard = element("div", "content-card");
  externalCard.append(element("h3", "", "文件在外部修改时"));
  const externalFields = element("div", "two-fields");
  const defaultField = field("所有文件默认行为");
  const externalDefault = selectInput(
    [
      ["prompt", "弹窗提醒"],
      ["autoReload", "自动更新"],
    ],
    state.draft.externalFileChangeBehavior || "prompt",
  );
  defaultField.append(externalDefault);
  const currentField = field("当前文件单独设置");
  const currentValue = currentExternalOverride() || "follow";
  const externalCurrent = selectInput(
    [
      ["follow", "跟随默认"],
      ["prompt", "弹窗提醒"],
      ["autoReload", "自动更新"],
    ],
    currentValue,
  );
  externalCurrent.disabled = !state.context?.documentPath;
  currentField.append(externalCurrent);
  externalFields.append(defaultField, currentField);
  externalCard.append(
    externalFields,
    element(
      "p",
      "page-note",
      "自动更新只重新读取磁盘版本，不覆盖有未保存修改的文档。",
    ),
  );
  externalDefault.addEventListener("change", () => {
    state.draft.externalFileChangeBehavior = externalDefault.value;
  });
  externalCurrent.addEventListener("change", () => {
    setCurrentExternalOverride(externalCurrent.value);
  });
  page.append(externalCard);

  const generalCard = element("div", "content-card");
  generalCard.append(element("h3", "", "侧栏与更新"));
  const sidebar = switchRow("显示最近文件或工作区侧栏", state.draft.sidebarVisible);
  const updates = switchRow(
    "启动时检查 WIMD 更新",
    Boolean(state.draft.checkForUpdatesOnStartup),
  );
  sidebar.input.addEventListener("change", () => {
    state.draft.sidebarVisible = sidebar.input.checked;
  });
  updates.input.addEventListener("change", () => {
    state.draft.checkForUpdatesOnStartup = updates.input.checked;
  });
  generalCard.append(sidebar.row, updates.row);
  page.append(generalCard);
  main.append(page);
  main.append(
    footer({
      restore: resetBehaviorDraft,
      cancel: closeSettingsWindow,
      actions: [{ label: "保存", action: () => void persistSettings(true), primary: true }],
    }),
  );
  return main;
}

async function persistSettings(closeAfterSave) {
  if (state.busy) return;
  setBusy(true);
  setStatus(closeAfterSave ? "正在保存..." : "正在应用...");
  try {
    prepareDraftForSave();
    if (state.mode === "images") {
      await persistImageCredentials();
    }
    const merged = await invoke("save_settings", { settings: state.draft });
    state.settings = merged;
    state.draft = structuredClone(merged);
    applyPageTheme(merged.theme);
    setStatus(closeAfterSave ? "已保存。" : "已应用。", "success");
    if (closeAfterSave) {
      await closeSettingsWindow(true);
    } else {
      render();
    }
  } catch (error) {
    setStatus(formatError(error), "error");
  } finally {
    setBusy(false);
  }
}

function prepareDraftForSave() {
  if (state.mode === "preview") {
    prepareOrderedListOverrides();
  }
  if (state.mode === "behavior") {
    prepareExternalOverrides();
  }
  if (state.mode === "images") {
    state.draft.localImageDirectory =
      String(state.draft.localImageDirectory || "").trim() || "img";
  }
}

async function persistImageCredentials() {
  const key = state.apiKeyDraft.trim();
  if (key) {
    const keySettings = await invoke("save_imgbb_api_key", { secret: key });
    state.draft.protectedImgbbApiKey = keySettings.protectedImgbbApiKey;
    state.apiKeyDraft = "";
  }
  if (state.clearStoredKey) {
    const cleared = await invoke("clear_imgbb_api_key");
    state.draft.protectedImgbbApiKey = cleared.protectedImgbbApiKey;
    state.draft.imageStorageMode = "local";
    state.clearStoredKey = false;
  }
  if (
    state.draft.imageStorageMode === "imgBb"
    && !state.draft.protectedImgbbApiKey
  ) {
    throw new Error("使用 ImgBB 上传模式前，请填写并保存自己的 API Key。");
  }
}

function prepareOrderedListOverrides() {
  const path = state.context?.documentPath;
  if (!path) return;
  state.draft.previewStyleFiles = {
    ...(state.draft.previewStyleFiles || {}),
    fileOverrides: {
      ...(state.draft.previewStyleFiles?.fileOverrides || {}),
    },
  };
  const overrides = state.draft.previewStyleFiles.fileOverrides;
  const existing = Object.entries(overrides).find(([candidate]) => samePath(candidate, path));
  for (const candidate of Object.keys(overrides)) {
    if (samePath(candidate, path)) delete overrides[candidate];
  }
  const current = currentOrderedOverride();
  if (current && current !== "follow") {
    overrides[path] = { ...(existing?.[1] || {}), orderedList: current };
  }
}

function prepareExternalOverrides() {
  const path = state.context?.documentPath;
  if (!path) return;
  state.draft.externalFileOverrides = {
    ...(state.draft.externalFileOverrides || {}),
  };
  for (const candidate of Object.keys(state.draft.externalFileOverrides)) {
    if (samePath(candidate, path)) delete state.draft.externalFileOverrides[candidate];
  }
  const current = currentExternalOverride();
  if (current && current !== "follow") {
    state.draft.externalFileOverrides[path] = current;
  }
}

function resetAppearanceDraft() {
  state.draft.theme = "system";
  state.draft.editorFontFamily = "";
  state.draft.editorFontSize = 15;
  state.draft.previewFontFamily = "";
  state.draft.previewFontSize = 16;
  state.draft.backgroundImagePath = null;
  state.draft.backgroundOpacity = 0.18;
  state.backgroundDataUri = "";
  setStatus("已恢复外观默认值；点击“应用”或“保存”后生效。");
  render();
}

function resetShortcuts() {
  state.draft.shortcutOverrides = {};
  state.capturingShortcut = null;
  state.shortcutMessage = "已恢复默认快捷键；点击“保存”后生效。";
  state.shortcutMessageTone = "success";
  render();
}

function resetBehaviorDraft() {
  state.draft.fileOpenViewMode = "rememberLast";
  state.draft.externalFileChangeBehavior = "prompt";
  state.draft.sidebarVisible = true;
  state.draft.checkForUpdatesOnStartup = false;
  if (state.context?.documentPath) {
    for (const candidate of Object.keys(state.draft.externalFileOverrides || {})) {
      if (samePath(candidate, state.context.documentPath)) {
        delete state.draft.externalFileOverrides[candidate];
      }
    }
  }
  setStatus("已恢复行为默认值；点击“保存”后生效。");
  render();
}

function installKeyboardHandling() {
  document.addEventListener(
    "keydown",
    (event) => {
      if (state.capturingShortcut) {
        event.preventDefault();
        event.stopPropagation();
        captureShortcut(event);
        return;
      }
      if (event.key === "Escape" && !state.busy) {
        event.preventDefault();
        void closeSettingsWindow();
      }
    },
    true,
  );
}

function captureShortcut(event) {
  if (event.key === "Escape") {
    state.capturingShortcut = null;
    state.shortcutMessage = "";
    state.shortcutMessageTone = "normal";
    render();
    return;
  }
  if (["Control", "Shift", "Alt", "Meta"].includes(event.key)) return;
  const gesture = gestureFromKeyboardEvent(event);
  if (!gesture) {
    state.shortcutMessage = "请使用 Ctrl、Alt 或功能键组合。";
    state.shortcutMessageTone = "normal";
    render();
    return;
  }
  const target = state.bootstrap.shortcuts.find(
    (shortcut) => shortcut.id === state.capturingShortcut,
  );
  if (!target) return;
  const assignments = effectiveShortcutMap();
  const conflict = state.bootstrap.shortcuts.find(
    (shortcut) =>
      shortcut.id !== target.id
      && (assignments[shortcut.id] || shortcut.defaultGesture).toUpperCase()
        === gesture.toUpperCase(),
  );
  if (conflict) {
    state.shortcutMessage = `${gesture} 已用于“${conflict.label}”。`;
    state.shortcutMessageTone = "normal";
    render();
    return;
  }
  state.draft.shortcutOverrides = {
    ...(state.draft.shortcutOverrides || {}),
  };
  if (gesture === target.defaultGesture) {
    delete state.draft.shortcutOverrides[target.id];
  } else {
    state.draft.shortcutOverrides[target.id] = gesture;
  }
  state.shortcutMessage = `已设置“${target.label}”：${gesture}`;
  state.shortcutMessageTone = "success";
  state.capturingShortcut = null;
  render();
}

function effectiveShortcutMap() {
  const result = {};
  for (const shortcut of state.bootstrap?.shortcuts || []) {
    result[shortcut.id] =
      state.draft.shortcutOverrides?.[shortcut.id] || shortcut.defaultGesture;
  }
  return result;
}

function gestureFromKeyboardEvent(event) {
  const key = normalizedKey(event.key, event.code);
  if (!key) return null;
  const modifiers = [];
  if (event.ctrlKey) modifiers.push("Ctrl");
  if (event.shiftKey) modifiers.push("Shift");
  if (event.altKey) modifiers.push("Alt");
  if (!modifiers.length && !/^F\d{1,12}$/.test(key)) return null;
  return [...modifiers, key].join("+");
}

function normalizedKey(key, code) {
  if (/^F\d{1,12}$/.test(key)) return key.toUpperCase();
  if (key.length === 1) return key.toUpperCase();
  if (code === "Space") return "Space";
  if (code === "Tab") return "Tab";
  if (code.startsWith("Arrow")) return code;
  return null;
}

function currentOrderedOverride() {
  const path = state.context?.documentPath;
  if (!path) return null;
  const entry = Object.entries(state.draft.previewStyleFiles?.fileOverrides || {}).find(
    ([candidate]) => samePath(candidate, path),
  );
  return entry?.[1]?.orderedList || null;
}

function setCurrentOrderedOverride(value) {
  const path = state.context?.documentPath;
  if (!path) return;
  state.draft.previewStyleFiles = {
    ...(state.draft.previewStyleFiles || {}),
    fileOverrides: {
      ...(state.draft.previewStyleFiles?.fileOverrides || {}),
    },
  };
  const overrides = state.draft.previewStyleFiles.fileOverrides;
  const existing = Object.entries(overrides).find(([candidate]) => samePath(candidate, path));
  for (const candidate of Object.keys(overrides)) {
    if (samePath(candidate, path)) delete overrides[candidate];
  }
  if (value !== "follow") {
    overrides[path] = { ...(existing?.[1] || {}), orderedList: value };
  }
}

function currentExternalOverride() {
  const path = state.context?.documentPath;
  if (!path) return null;
  return Object.entries(state.draft.externalFileOverrides || {}).find(
    ([candidate]) => samePath(candidate, path),
  )?.[1] || null;
}

function setCurrentExternalOverride(value) {
  const path = state.context?.documentPath;
  if (!path) return;
  state.draft.externalFileOverrides = {
    ...(state.draft.externalFileOverrides || {}),
  };
  for (const candidate of Object.keys(state.draft.externalFileOverrides)) {
    if (samePath(candidate, path)) delete state.draft.externalFileOverrides[candidate];
  }
  if (value !== "follow") state.draft.externalFileOverrides[path] = value;
}

function header(title, subtitle) {
  const node = element("header", "settings-header");
  node.append(
    element("h1", "", title),
    element("p", "", subtitle),
  );
  return node;
}

function pageHeading(title, subtitle) {
  const fragment = document.createDocumentFragment();
  const heading = element("div", "page-heading");
  heading.append(element("h2", "", title), element("p", "", subtitle));
  fragment.append(heading);
  return fragment;
}

function footer({ restore = null, cancel, actions }) {
  const node = element(
    "footer",
    restore ? "settings-footer" : "settings-footer no-restore",
  );
  if (restore) {
    const restoreButton = actionButton("恢复默认", "", restore);
    restoreButton.dataset.settingsAction = "restore";
    node.append(restoreButton);
  }
  node.append(
    element(
      "div",
      "settings-footer-status",
      state.status,
    ),
  );
  const actionRow = element("div", "settings-actions");
  const cancelButton = actionButton("取消", "", cancel);
  cancelButton.dataset.settingsAction = "cancel";
  actionRow.append(cancelButton);
  for (const action of actions) {
    const button = actionButton(action.label, action.primary ? "primary" : "", action.action);
    button.dataset.settingsAction = action.label === "应用" ? "apply" : "save";
    actionRow.append(button);
  }
  node.append(actionRow);
  const status = node.querySelector(".settings-footer-status");
  if (state.statusTone === "error") status.style.color = "var(--danger)";
  if (state.statusTone === "success") status.style.color = "var(--success)";
  return node;
}

function setStatus(message, tone = "normal") {
  state.status = message;
  state.statusTone = tone;
  const status = mount.querySelector(".settings-footer-status");
  if (!status) return;
  status.textContent = message;
  status.style.color =
    tone === "error"
      ? "var(--danger)"
      : tone === "success"
        ? "var(--success)"
        : "var(--text-secondary)";
}

function setBusy(busy) {
  state.busy = busy;
  mount.querySelectorAll("[data-settings-action]").forEach((button) => {
    button.disabled = busy;
  });
}

async function closeSettingsWindow(force = false) {
  if (state.busy && !force) return;
  try {
    await invoke("close_settings_window");
  } catch {
    try {
      await currentWindow?.close();
    } catch {
      window.close();
    }
  }
}

function field(labelText) {
  const node = element("label", "field");
  node.append(element("span", "", labelText));
  return node;
}

function switchRow(labelText, checked) {
  const row = element("label", "switch-row");
  const text = element("span", "", labelText);
  const input = element("input");
  input.type = "checkbox";
  input.checked = Boolean(checked);
  row.append(text, input);
  return { row, input };
}

function numberInput(value, min, max) {
  const input = element("input");
  input.type = "number";
  input.min = String(min);
  input.max = String(max);
  input.step = "1";
  input.value = String(value);
  return input;
}

function selectInput(options, value) {
  const select = element("select");
  for (const [optionValue, label] of options) {
    const option = element("option", "", label);
    option.value = optionValue;
    select.append(option);
  }
  select.value = value;
  return select;
}

function rowHeader(values, className) {
  const row = element("div", className);
  for (const value of values) row.append(element("span", "", value));
  return row;
}

function actionButton(label, className, onClick) {
  const button = element(
    "button",
    `action-button${className ? ` ${className}` : ""}`,
    label,
  );
  button.type = "button";
  button.addEventListener("click", onClick);
  return button;
}

function element(tag, className = "", text = null) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== null) node.textContent = text;
  return node;
}

function fontDisplayName(value) {
  const bundled = bundledFonts.find(
    (font) => font.id.toLowerCase() === String(value || "").toLowerCase(),
  );
  return bundled ? `内置 · ${bundled.label}` : value || "";
}

function populateFontFamilyOptions() {
  const datalist = document.getElementById("font-families");
  if (!datalist) return;
  const values = new Set();
  for (const font of bundledFonts) {
    values.add(`内置 · ${font.label}`);
    values.add(font.family);
    values.add(font.label);
  }
  for (const font of state.installedFonts || []) {
    if (typeof font === "string" && font.trim()) values.add(font.trim());
  }
  const fragment = document.createDocumentFragment();
  for (const value of values) {
    const option = document.createElement("option");
    option.value = value;
    fragment.append(option);
  }
  datalist.replaceChildren(fragment);
}

function normalizeFontValue(value) {
  const text = String(value || "").trim();
  if (!text || text === "跟随 WIMD 默认") return null;
  const bundled = bundledFonts.find(
    (font) =>
      text.toLowerCase() === font.id.toLowerCase()
      || text.toLowerCase() === font.family.toLowerCase()
      || text === font.label
      || text === `内置 · ${font.label}`,
  );
  return bundled ? bundled.id : text;
}

function fontStack(value, code) {
  const bundled = bundledFonts.find(
    (font) => font.id.toLowerCase() === String(value || "").toLowerCase(),
  );
  const family = bundled
    ? `"WIMD ${bundled.family}", "${bundled.family}"`
    : value
      ? `"${String(value).replaceAll('"', '\\"')}"`
      : code
        ? '"Cascadia Code", Consolas, "Microsoft YaHei UI"'
        : '"Segoe UI Variable Text", "Segoe UI", "Microsoft YaHei UI"';
  return `${family}, ${code ? "monospace" : "sans-serif"}`;
}

function clampNumber(value, minimum, maximum, fallback) {
  const numeric = Number(value);
  return Number.isFinite(numeric)
    ? Math.min(maximum, Math.max(minimum, numeric))
    : fallback;
}

function samePath(left, right) {
  const normalize = (value) =>
    String(value || "")
      .replaceAll("/", "\\")
      .replace(/\\+$/, "")
      .toLowerCase();
  return normalize(left) === normalize(right);
}
