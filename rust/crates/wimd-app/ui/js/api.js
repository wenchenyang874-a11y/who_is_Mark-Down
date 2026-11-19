const tauri = window.__TAURI__;

export const isTauri = Boolean(tauri?.core?.invoke);

const fallbackStorageKey = "wimd-rust-preview-settings";

const defaultSettings = {
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

export async function invoke(command, args = {}) {
  if (isTauri) {
    return tauri.core.invoke(command, args);
  }
  return fallbackInvoke(command, args);
}

export async function listen(eventName, handler) {
  if (tauri?.event?.listen) {
    return tauri.event.listen(eventName, handler);
  }
  return () => {};
}

export function getCurrentWindow() {
  if (tauri?.window?.getCurrentWindow) {
    return tauri.window.getCurrentWindow();
  }
  return null;
}

async function fallbackInvoke(command, args) {
  switch (command) {
    case "bootstrap":
      return {
        settings: loadFallbackSettings(),
        settingsWarning: null,
        shortcuts: fallbackShortcuts,
        startupPaths: [],
        version: "2.0.0-dev",
        settingsPath: "浏览器开发模式（未连接 Rust 后端）",
      };
    case "settings_context":
      return {
        kind: args.kind || "appearance",
        documentPath: args.documentPath || null,
      };
    case "installed_fonts":
      return [];
    case "inspect_recent_paths":
      return (args.paths || []).map((path) => ({
        path,
        exists: true,
        isDirectory: false,
      }));
    case "open_settings_window":
      return null;
    case "close_settings_window":
      return null;
    case "mark_pdf_window_ready":
      return null;
    case "render_preview":
      return renderFallbackMarkdown(args.markdown ?? "");
    case "format_markdown":
      return fallbackFormat(args);
    case "toggle_task":
      return fallbackToggleTask(args);
    case "save_settings": {
      const settings = normalizeFallbackSettings(args.settings);
      localStorage.setItem(fallbackStorageKey, JSON.stringify(settings));
      return settings;
    }
    case "save_imgbb_api_key":
    case "clear_imgbb_api_key":
      throw new Error("浏览器开发模式不能调用 Windows DPAPI。");
    case "upload_image_to_imgbb":
      throw new Error("浏览器开发模式不能调用 Rust ImgBB 客户端。");
    case "prepare_preview_image":
    case "save_preview_image":
      throw new Error("浏览器开发模式不能保存预览图片。");
    case "open_image_viewer":
    case "get_viewer_image":
      throw new Error("浏览器开发模式不能创建独立图片查看器。");
    case "close_image_viewer":
      return null;
    case "pick_preview_image_save_path":
      return null;
    case "check_update":
      return null;
    case "download_update":
    case "install_update":
      throw new Error("浏览器开发模式不能安装 WIMD 更新。");
    case "record_recent_path":
    case "remove_recent_path":
    case "record_recent_workspace":
    case "remove_recent_workspace":
      return loadFallbackSettings();
    case "list_recovery_snapshots":
      return [];
    case "save_recovery_snapshot":
    case "remove_recovery_snapshot":
    case "save_update_restart_window":
    case "finish_update_restart_window":
      return null;
    case "installer_capture_requested":
      return false;
    case "consume_installer_restore":
      return false;
    case "current_process_usage":
      return {
        processorTime100ns: 0,
        workingSetBytes: 0,
        logicalProcessors: navigator.hardwareConcurrency || 1,
      };
    case "pick_document":
    case "pick_save_path":
    case "pick_pdf_save_path":
    case "pick_folder":
    case "pick_image":
    case "choose_background_image":
      return null;
    case "copy_text":
      await navigator.clipboard.writeText(args.text ?? "");
      return null;
    case "open_external":
      window.open(args.url, "_blank", "noopener,noreferrer");
      return null;
    case "resolve_local_link":
    case "open_local_link":
      throw new Error("浏览器开发模式不能打开本地链接。");
    case "reveal_in_explorer":
    case "watch_document":
      return null;
    case "open_logs_folder":
      return null;
    case "load_background_image":
      throw new Error("浏览器开发模式不能读取托管背景副本。");
    case "export_pdf":
      throw new Error("浏览器开发模式不能调用 WebView2 PDF 导出。");
    case "recent_path_is_directory":
      return false;
    case "load_document":
      throw new Error("浏览器开发模式不能直接读取本机路径。请使用 Tauri 桌面窗口。");
    case "save_document":
      return {
        length: new TextEncoder().encode(args.text ?? "").length,
        modifiedUnixMs: Date.now(),
      };
    case "inspect_document_stamp":
      return { length: 0, modifiedUnixMs: null };
    case "open_workspace":
      return { root: args.path, entries: [] };
    case "watch_workspace":
      return null;
    default:
      throw new Error(`浏览器开发模式不支持命令：${command}`);
  }
}

function loadFallbackSettings() {
  try {
    const stored = JSON.parse(localStorage.getItem(fallbackStorageKey) || "{}");
    return normalizeFallbackSettings({ ...defaultSettings, ...stored });
  } catch {
    return { ...defaultSettings };
  }
}

function normalizeFallbackSettings(settings) {
  return {
    ...defaultSettings,
    ...settings,
    editorFontSize: Math.min(32, Math.max(10, Number(settings.editorFontSize) || 15)),
    previewFontSize: Math.min(32, Math.max(10, Number(settings.previewFontSize) || 16)),
    sidebarWidth: Math.min(520, Math.max(180, Number(settings.sidebarWidth) || 248)),
    splitRatio: Math.min(0.82, Math.max(0.18, Number(settings.splitRatio) || 0.5)),
    recentFiles: Array.isArray(settings.recentFiles) ? settings.recentFiles.slice(0, 10) : [],
    recentWorkspaces: Array.isArray(settings.recentWorkspaces)
      ? settings.recentWorkspaces.slice(0, 10)
      : [],
    shortcutOverrides:
      settings.shortcutOverrides && typeof settings.shortcutOverrides === "object"
        ? settings.shortcutOverrides
        : {},
  };
}

const fallbackShortcuts = [
  ["new", "新建文档", "Ctrl+N"],
  ["open", "打开文档", "Ctrl+O"],
  ["save", "保存", "Ctrl+S"],
  ["save-as", "另存为", "Ctrl+Shift+S"],
  ["reload", "重新加载", "F5"],
  ["cycle-view", "切换视图", "F9"],
  ["bold", "粗体", "Ctrl+B"],
  ["italic", "斜体", "Ctrl+I"],
  ["strike", "删除线", "Ctrl+Shift+X"],
  ["inline-code", "行内代码", "Ctrl+E"],
  ["link", "链接", "Ctrl+K"],
  ["heading-1", "一级标题", "Ctrl+1"],
  ["heading-2", "二级标题", "Ctrl+2"],
  ["heading-3", "三级标题", "Ctrl+3"],
  ["heading-4", "四级标题", "Ctrl+4"],
  ["heading-5", "五级标题", "Ctrl+5"],
  ["heading-6", "六级标题", "Ctrl+6"],
].map(([id, label, defaultGesture]) => ({ id, label, defaultGesture }));

function fallbackFormat(args) {
  const {
    text = "",
    selectionStart = 0,
    selectionLength = 0,
    format = "bold",
    rowCount = 3,
    columnCount = 3,
  } = args;
  const selection = text.slice(selectionStart, selectionStart + selectionLength);
  const wrap = (prefix, suffix, placeholder) => {
    const content = selection || placeholder;
    const next = `${text.slice(0, selectionStart)}${prefix}${content}${suffix}${text.slice(
      selectionStart + selectionLength,
    )}`;
    return {
      text: next,
      selectionStart: selectionStart + prefix.length,
      selectionLength: content.length,
      caretOffset: selectionStart + prefix.length + content.length,
    };
  };

  if (format === "bold") return wrap("**", "**", "粗体文本");
  if (format === "italic") return wrap("*", "*", "斜体文本");
  if (format === "strike") return wrap("~~", "~~", "删除线文本");
  if (format === "inline-code") return wrap("`", "`", "代码");
  if (format === "link") return wrap("[", "](https://)", "链接文字");
  if (format === "image") return wrap("![", "](img/image.png)", "图片说明");
  if (format === "code-block") return wrap("```\n", "\n```", "代码");

  if (format === "table" || format === "table-custom") {
    const rows = Math.max(2, Math.min(20, Number(rowCount) || 3));
    const columns = Math.max(1, Math.min(12, Number(columnCount) || 3));
    const row = (cells) => `| ${cells.join(" | ")} |`;
    const header = row(Array.from({ length: columns }, (_, index) => `列 ${index + 1}`));
    const delimiter = row(Array.from({ length: columns }, () => "---"));
    const content = row(Array.from({ length: columns }, () => "内容"));
    const table = [header, delimiter, ...Array.from({ length: rows - 1 }, () => content)].join(
      "\n",
    );
    return insertFallbackBlock(text, selectionStart, selectionLength, table);
  }

  if (format === "separator") {
    return insertFallbackBlock(text, selectionStart, selectionLength, "---");
  }

  if (/^h[1-6]$/.test(format)) {
    const level = Number(format.slice(1));
    const lineStart = text.lastIndexOf("\n", Math.max(0, selectionStart - 1)) + 1;
    const lineEndIndex = text.indexOf("\n", selectionStart + selectionLength);
    const lineEnd = lineEndIndex === -1 ? text.length : lineEndIndex;
    const prefix = `${"#".repeat(level)} `;
    const line = text.slice(lineStart, lineEnd).replace(/^#{1,6}\s+/, "");
    const next = `${text.slice(0, lineStart)}${prefix}${line}${text.slice(lineEnd)}`;
    return {
      text: next,
      selectionStart: lineStart + prefix.length,
      selectionLength: 0,
      caretOffset: lineStart + prefix.length,
    };
  }

  const prefixes = {
    quote: "> ",
    "unordered-list": "- ",
    "ordered-list": "1. ",
    "task-list": "- [ ] ",
  };
  if (prefixes[format]) {
    const prefix = prefixes[format];
    const lineStart = text.lastIndexOf("\n", Math.max(0, selectionStart - 1)) + 1;
    const lineEndIndex = text.indexOf("\n", selectionStart + selectionLength);
    const lineEnd = lineEndIndex === -1 ? text.length : lineEndIndex;
    const lines = text.slice(lineStart, lineEnd).split("\n");
    const replacement = lines.map((line) => `${prefix}${line}`).join("\n");
    const next = `${text.slice(0, lineStart)}${replacement}${text.slice(lineEnd)}`;
    const caret = lineStart + replacement.length;
    return { text: next, selectionStart: caret, selectionLength: 0, caretOffset: caret };
  }

  throw new Error(`不支持的 Markdown 格式：${format}`);
}

function insertFallbackBlock(text, start, length, block) {
  const before = text.slice(0, start);
  const after = text.slice(start + length);
  const leading = before && !before.endsWith("\n") ? "\n" : "";
  const trailing = after.startsWith("\n") ? "" : "\n";
  const replacement = `${leading}${block}${trailing}`;
  const next = `${before}${replacement}${after}`;
  const caret = start + replacement.length + (after.startsWith("\n") ? 1 : 0);
  return { text: next, selectionStart: caret, selectionLength: 0, caretOffset: caret };
}

function fallbackToggleTask(args) {
  const text = args.text ?? "";
  const line = Number(args.sourceLine);
  const completed = Boolean(args.completed);
  const lines = text.split("\n");
  if (!lines[line] || !/^(\s*>\s*)*\s*(?:[-+*]|\d+[.)])\s+\[[ xX]\](?:\s|$)/.test(lines[line])) {
    throw new Error("当前行不是合法 Markdown 任务。");
  }
  const before = lines[line];
  lines[line] = before.replace(/\[[ xX]\]/, completed ? "[x]" : "[ ]");
  return { text: lines.join("\n"), changed: before !== lines[line] };
}

function renderFallbackMarkdown(markdown) {
  const lines = markdown.split("\n");
  const output = [];
  const headings = [];
  const taskLines = [];
  let inCode = false;
  let codeLanguage = "";
  let codeLines = [];
  let listOpen = false;
  let paragraph = [];

  const flushParagraph = () => {
    if (!paragraph.length) return;
    output.push(`<section class="wimd-source-block" data-source-line="${paragraph[0].line}"><p>${inline(
      paragraph.map((item) => item.text).join("\n"),
    )}</p></section>`);
    paragraph = [];
  };

  const closeList = () => {
    if (listOpen) {
      output.push("</ul>");
      listOpen = false;
    }
  };

  lines.forEach((line, index) => {
    if (inCode) {
      if (line.startsWith("```")) {
        output.push(
          `<section class="wimd-source-block" data-source-line="${index}"><pre><code class="language-${escapeHtml(
            codeLanguage,
          )}">${escapeHtml(codeLines.join("\n"))}</code></pre></section>`,
        );
        inCode = false;
        codeLines = [];
        codeLanguage = "";
      } else {
        codeLines.push(line);
      }
      return;
    }

    const fence = line.match(/^```(.*)$/);
    if (fence) {
      flushParagraph();
      closeList();
      inCode = true;
      codeLanguage = fence[1].trim().replace(/[^a-zA-Z0-9_+.#-]/g, "");
      return;
    }

    const heading = line.match(/^(#{1,6})\s+(.+)$/);
    if (heading) {
      flushParagraph();
      closeList();
      const level = heading[1].length;
      const id = `wimd-heading-${headings.length + 1}`;
      const text = heading[2];
      headings.push({ level, id, text, sourceLine: index });
      output.push(
        `<section class="wimd-source-block" data-source-line="${index}"><h${level} id="${id}">${inline(
          text,
        )}</h${level}></section>`,
      );
      return;
    }

    if (/^\s*[-+*]\s+\[[ xX]\]/.test(line)) {
      flushParagraph();
      const checked = /\[[xX]\]/.test(line);
      const content = line.replace(/^\s*[-+*]\s+\[[ xX]\]\s?/, "");
      taskLines.push(index);
      output.push(
        `<section class="wimd-source-block" data-source-line="${index}"><ul class="contains-task-list"><li class="task-list-item"><input type="checkbox" data-task-line="${index}"${
          checked ? " checked" : ""
        } aria-label="切换任务状态" />${inline(content)}</li></ul></section>`,
      );
      return;
    }

    if (/^\s*[-+*]\s+/.test(line)) {
      flushParagraph();
      if (!listOpen) {
        output.push(`<section class="wimd-source-block" data-source-line="${index}"><ul>`);
        listOpen = true;
      }
      output.push(`<li>${inline(line.replace(/^\s*[-+*]\s+/, ""))}</li>`);
      return;
    }

    if (!line.trim()) {
      flushParagraph();
      closeList();
      return;
    }

    if (/^>\s?/.test(line)) {
      flushParagraph();
      closeList();
      output.push(
        `<section class="wimd-source-block" data-source-line="${index}"><blockquote><p>${inline(
          line.replace(/^>\s?/, ""),
        )}</p></blockquote></section>`,
      );
      return;
    }

    if (/^\s*(---+|\*\*\*+)\s*$/.test(line)) {
      flushParagraph();
      closeList();
      output.push(`<section class="wimd-source-block" data-source-line="${index}"><hr></section>`);
      return;
    }

    paragraph.push({ line: index, text: line });
  });

  if (inCode) {
    output.push(
      `<section class="wimd-source-block"><pre><code>${escapeHtml(
        codeLines.join("\n"),
      )}</code></pre></section>`,
    );
  }
  flushParagraph();
  closeList();

  let html = output.join("");
  if (html.includes("<p>[TOC]</p>")) {
    const toc = headings.length
      ? `<nav class="wimd-toc" aria-label="文档目录"><p>目录</p><ul>${headings
          .map(
            (heading) =>
              `<li class="wimd-toc-level-${heading.level}"><a href="#${heading.id}">${escapeHtml(
                heading.text,
              )}</a></li>`,
          )
          .join("")}</ul></nav>`
      : `<nav class="wimd-toc" aria-label="文档目录"><p>当前文档没有标题。</p></nav>`;
    html = html.replace("<p>[TOC]</p>", toc);
  }

  return {
    html,
    headings,
    taskCount: taskLines.length,
    sourceLineCount: lines.length,
  };
}

function inline(value) {
  let escaped = escapeHtml(value);
  escaped = escaped.replace(/!\[([^\]]*)\]\(([^)]+)\)/g, (_, alt, source) =>
    isSafeImageFallback(source)
      ? `<img alt="${alt}" src="${source}" />`
      : `<span>${alt}</span>`,
  );
  escaped = escaped.replace(
    /\[([^\]]+)\]\(([^)]+)\)/g,
    (_, label, href) =>
      isSafeLinkFallback(href)
        ? `<a href="${href}" rel="noopener noreferrer">${label}</a>`
        : label,
  );
  escaped = escaped.replace(/`([^`]+)`/g, "<code>$1</code>");
  escaped = escaped.replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>");
  escaped = escaped.replace(/~~([^~]+)~~/g, "<del>$1</del>");
  escaped = escaped.replace(/(^|[^*])\*([^*]+)\*/g, "$1<em>$2</em>");
  return escaped.replace(/\n/g, "<br />");
}

function isSafeImageFallback(value) {
  const normalized = value.trim().toLowerCase();
  return (
    normalized.startsWith("http://") ||
    normalized.startsWith("https://") ||
    normalized.startsWith("data:image/") ||
    (!normalized.startsWith("/") && !normalized.startsWith("\\") && !normalized.includes(":"))
  );
}

function isSafeLinkFallback(value) {
  const normalized = value.trim().toLowerCase();
  return (
    normalized.startsWith("http://") ||
    normalized.startsWith("https://") ||
    normalized.startsWith("mailto:") ||
    normalized.startsWith("#") ||
    (!normalized.startsWith("/") && !normalized.startsWith("\\") && !normalized.includes(":"))
  );
}

function escapeHtml(value) {
  return String(value)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;");
}

export function pathFileName(path) {
  if (!path) return "未命名.md";
  return String(path).split(/[\\/]/).filter(Boolean).at(-1) || path;
}

export function pathDirectory(path) {
  if (!path) return "";
  const normalized = String(path).replaceAll("\\", "/");
  return normalized.slice(0, Math.max(0, normalized.lastIndexOf("/")));
}

export function displayPath(path) {
  if (!path) return "";
  const normalized = String(path).replaceAll("/", "\\");
  if (normalized.startsWith("\\\\?\\UNC\\")) {
    return `\\\\${normalized.slice(8)}`;
  }
  if (normalized.startsWith("\\\\?\\")) {
    return normalized.slice(4);
  }
  return normalized;
}

export function formatError(error) {
  if (typeof error === "string") return error;
  if (error instanceof Error) return error.message;
  if (error?.message) return error.message;
  return String(error);
}

export function defaultShortcutMap(shortcuts) {
  return Object.fromEntries(shortcuts.map((shortcut) => [shortcut.id, shortcut.defaultGesture]));
}
