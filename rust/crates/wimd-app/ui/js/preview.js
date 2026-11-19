const TRANSPARENT_PIXEL =
  "data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==";
const MAXIMUM_HIGHLIGHT_CHARACTERS = 512 * 1024;
const HIGHLIGHT_LANGUAGE_ALIASES = new Map([
  ["c#", "csharp"],
  ["cs", "csharp"],
  ["html", "xml"],
  ["js", "javascript"],
  ["md", "markdown"],
  ["ps1", "powershell"],
  ["py", "python"],
  ["sh", "bash"],
  ["shell", "bash"],
  ["ts", "typescript"],
  ["yml", "yaml"],
]);

export function highlightCodeText(source, requested, highlighter = globalThis.hljs) {
  const normalized = String(requested || "").toLowerCase();
  if (
    !normalized ||
    normalized === "mermaid" ||
    normalized === "plaintext" ||
    normalized === "text"
  ) {
    return null;
  }
  const language = HIGHLIGHT_LANGUAGE_ALIASES.get(normalized) || normalized;
  if (!highlighter?.getLanguage(language) || source.length > MAXIMUM_HIGHLIGHT_CHARACTERS) {
    return null;
  }
  try {
    return {
      language,
      value: highlighter.highlight(source, {
        language,
        ignoreIllegals: true,
      }).value,
    };
  } catch {
    return null;
  }
}

const PREVIEW_DOCUMENT_SHELL = `<!doctype html>
<html lang="zh-CN" data-theme="light">
  <head>
    <meta charset="UTF-8" />
    <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src data: http: https:; style-src 'unsafe-inline'; font-src 'self'; base-uri 'none'; form-action 'none'; script-src 'none'; object-src 'none'; frame-src 'none'" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <style id="wimd-preview-style"></style>
  </head>
  <body><main id="preview-document" class="preview-document"></main></body>
</html>`;

export class PreviewController {
  constructor({
    frame,
    onTaskToggle,
    onCopyCode,
    onOpenLink,
    onOpenImage,
    onImageContextMenu,
    onLoadLocalImage,
    onScroll,
    onNavigateSourceLine,
  }) {
    this.frame = frame;
    this.onTaskToggle = onTaskToggle;
    this.onCopyCode = onCopyCode;
    this.onOpenLink = onOpenLink;
    this.onOpenImage = onOpenImage;
    this.onImageContextMenu = onImageContextMenu;
    this.onLoadLocalImage = onLoadLocalImage;
    this.onScroll = onScroll;
    this.onNavigateSourceLine = onNavigateSourceLine;
    this.readyPromise = null;
    this.previewCss = "";
    this.imageCache = new Map();
    this.mermaidCache = new Map();
    this.mermaidCacheCharacters = 0;
    this.foldState = new Map();
    this.renderToken = 0;
    this.scrollSyncSuppressed = false;
  }

  async initialize() {
    if (this.readyPromise) return this.readyPromise;
    this.readyPromise = (async () => {
      this.previewCss = await fetch("./preview.css").then((response) => response.text());
      await new Promise((resolve, reject) => {
        const timeout = window.setTimeout(
          () => reject(new Error("预览页面在 2.5 秒内未完成加载。")),
          2500,
        );
        this.frame.addEventListener(
          "load",
          () => {
            window.clearTimeout(timeout);
            resolve();
          },
          { once: true },
        );
        this.frame.srcdoc = PREVIEW_DOCUMENT_SHELL;
      });
      const style = this.document?.getElementById("wimd-preview-style");
      if (style) style.textContent = this.previewCss;
      this.document?.addEventListener("scroll", () => this.emitScroll(), { passive: true });
    })().catch((error) => {
      this.readyPromise = null;
      throw error;
    });
    return this.readyPromise;
  }

  get document() {
    try {
      return this.frame.contentDocument;
    } catch {
      return null;
    }
  }

  get documentElement() {
    return this.document?.documentElement ?? null;
  }

  get main() {
    return this.document?.getElementById("preview-document") ?? null;
  }

  async render(rendered, documentModel, settings, { preservePosition = true } = {}) {
    const token = ++this.renderToken;
    await this.initialize();
    if (token !== this.renderToken) return false;
    const main = this.main;
    if (!main) throw new Error("预览页面尚未初始化。");
    this.currentDocumentPath = documentModel.path;

    const previousRatio = preservePosition ? this.scrollRatio() : 0;
    this.setTheme(settings.theme);
    this.setAppearance(settings);
    main.dataset.listStyle = settings.orderedListStyle || "plain";
    this.applyOrderedListStyle(settings.orderedListStyle);

    main.classList.toggle("is-empty", !rendered.html.trim());
    if (!rendered.html.trim()) {
      main.innerHTML = `
        <div class="empty-document">
          <div class="empty-document-card">
            <span class="empty-document-mark" aria-hidden="true">M↓</span>
            <strong>开始写点什么吧</strong>
            <span>在编辑区输入 Markdown，格式化结果会实时显示在这里。</span>
          </div>
        </div>`;
    } else {
      main.innerHTML = rendered.html;
    }

    this.decorateTasks(Boolean(documentModel.readOnly));
    await this.decorateMermaid();
    this.decorateCodeBlocks();
    this.decorateHeadings();
    this.decorateLinks();
    this.decorateImageContextMenu();
    this.decorateImages(documentModel.path, documentModel.title);
    this.applyFolds();

    if (preservePosition) this.setScrollRatio(previousRatio);
    return true;
  }

  async renderError(message, { stage = "预览处理", contextLine = null } = {}) {
    try {
      await this.initialize();
    } catch {
      return;
    }
    const main = this.main;
    if (!main) return;
    const lineText = Number.isInteger(contextLine)
      ? `<p>当前编辑上下文：第 ${contextLine + 1} 行。此位置用于回到编辑区，不代表已确认的解析错误行。</p>`
      : "<p>当前错误未提供可确认的源行。</p>";
    main.innerHTML = `
      <div class="preview-error" role="alert">
        <strong>${escapeHtml(stage)}失败</strong>
        <p>${escapeHtml(message)}</p>
        ${lineText}
        <p>编辑内容仍保留，可继续修改或切换为“仅编辑”。</p>
      </div>`;
  }

  setTheme(theme) {
    if (this.documentElement) {
      this.documentElement.dataset.theme = theme || "system";
    }
  }

  setAppearance(settings) {
    if (!this.documentElement) return;
    const editorRoot = document.documentElement;
    const previewFamily = bundledFontFamily(settings.previewFontFamily);
    const accent =
      getComputedStyle(editorRoot).getPropertyValue("--accent").trim() || "#176b79";
    this.documentElement.style.setProperty(
      "--preview-font",
      previewFamily
        ? `"${escapeCssString(previewFamily)}", "Microsoft YaHei UI", "Segoe UI", sans-serif`
        : '"Microsoft YaHei UI", "Segoe UI", sans-serif',
    );
    this.documentElement.style.setProperty(
      "--preview-size",
      `${Math.min(32, Math.max(10, Number(settings.previewFontSize) || 16))}px`,
    );
    this.documentElement.style.setProperty("--accent", accent);
  }

  applyOrderedListStyle(style) {
    const main = this.main;
    if (!main) return;
    const normalized = ["plain", "bold", "circle", "square"].includes(style)
      ? style
      : "plain";
    main.querySelectorAll("ol").forEach((list) => {
      if (normalized === "plain") list.removeAttribute("data-wimd-list-style");
      else list.setAttribute("data-wimd-list-style", normalized);
    });
  }

  decorateTasks(readOnly = false) {
    const main = this.main;
    if (!main) return;
    main.querySelectorAll('input[type="checkbox"][data-task-line]').forEach((checkbox) => {
      checkbox.disabled = readOnly;
      if (readOnly) return;
      checkbox.addEventListener("change", async () => {
        const desired = checkbox.checked;
        checkbox.disabled = true;
        try {
          await this.onTaskToggle?.(
            Number(checkbox.dataset.taskLine),
            desired,
            checkbox,
          );
        } catch {
          checkbox.checked = !desired;
        } finally {
          checkbox.disabled = false;
        }
      });
    });
  }

  async decorateMermaid() {
    const main = this.main;
    if (!main) return;
    const blocks = [...main.querySelectorAll("pre > code.language-mermaid")].slice(0, 64);
    if (!blocks.length) return;
    try {
      const mermaid = await loadMermaid(this.documentElement?.dataset.theme || "system");
      for (const [index, code] of blocks.entries()) {
        const source = code.textContent || "";
        const pre = code.parentElement;
        if (!pre) continue;
        if (source.length > 50_000) {
          replaceMermaidWithError(pre, "图表源码超过 50,000 字符上限。", source);
          continue;
        }
        if (countMermaidEdges(source) > 500) {
          replaceMermaidWithError(pre, "图表边数超过 500 条上限。", source);
          continue;
        }
        try {
          const cacheKey = `${this.documentElement?.dataset.theme || "system"}\n${source}`;
          let safeSvg = this.getCachedMermaid(cacheKey);
          if (!safeSvg) {
            const { svg } = await mermaid.render(
              `wimd-mermaid-${Date.now()}-${index}`,
              source,
            );
            safeSvg = sanitizeMermaidSvg(svg);
            if (safeSvg) this.cacheMermaid(cacheKey, source, safeSvg);
          }
          if (!safeSvg) throw new Error("生成的 SVG 未通过安全校验。");
          const figure = this.document.createElement("figure");
          figure.className = "mermaid-figure";
          const image = this.document.createElement("img");
          image.alt = "Mermaid 图表";
          image.src = `data:image/svg+xml;charset=utf-8,${encodeURIComponent(safeSvg)}`;
          image.addEventListener("click", () => {
            this.onOpenImage?.({
              source: image.src,
              title: "Mermaid 图表",
            });
          });
          figure.append(image);
          pre.replaceWith(figure);
        } catch (error) {
          replaceMermaidWithError(
            pre,
            `Mermaid 渲染失败：${error instanceof Error ? error.message : String(error)}`,
            source,
          );
        }
      }
    } catch (error) {
      const fallbackMessage = `Mermaid 离线运行库加载失败：${
        error instanceof Error ? error.message : String(error)
      }`;
      for (const code of blocks) {
        const pre = code.parentElement;
        if (pre) replaceMermaidWithError(pre, fallbackMessage, code.textContent || "");
      }
    }
  }

  getCachedMermaid(key) {
    const cached = this.mermaidCache.get(key);
    if (!cached) return null;
    this.mermaidCache.delete(key);
    this.mermaidCache.set(key, cached);
    return cached.svg;
  }

  cacheMermaid(key, source, svg) {
    const characters = key.length + source.length + svg.length;
    if (characters > 1024 * 1024) return;
    const previous = this.mermaidCache.get(key);
    if (previous) {
      this.mermaidCacheCharacters -= previous.characters;
      this.mermaidCache.delete(key);
    }
    this.mermaidCache.set(key, { svg, characters });
    this.mermaidCacheCharacters += characters;
    while (
      this.mermaidCache.size > 32 ||
      this.mermaidCacheCharacters > 1024 * 1024
    ) {
      const oldest = this.mermaidCache.keys().next().value;
      if (oldest == null) break;
      const removed = this.mermaidCache.get(oldest);
      this.mermaidCache.delete(oldest);
      this.mermaidCacheCharacters -= removed?.characters || 0;
    }
  }

  decorateCodeBlocks() {
    const main = this.main;
    if (!main) return;
    main.querySelectorAll("pre > code").forEach((code) => {
      if (!code.textContent?.trim()) return;
      this.highlightCodeBlock(code);
      const pre = code.parentElement;
      if (!pre || pre.querySelector(":scope > .code-copy-button")) return;
      const button = document.createElement("button");
      button.type = "button";
      button.className = "code-copy-button";
      button.textContent = "复制";
      button.setAttribute("aria-label", "复制代码块");
      button.addEventListener("click", async () => {
        button.dataset.state = "working";
        button.textContent = "处理中";
        try {
          await this.onCopyCode?.(code.textContent.replace(/\n$/, ""));
          button.dataset.state = "success";
          button.textContent = "已复制";
        } catch {
          button.dataset.state = "error";
          button.textContent = "失败";
        }
        window.setTimeout(() => {
          if (!button.isConnected) return;
          delete button.dataset.state;
          button.textContent = "复制";
        }, 1500);
      });
      pre.append(button);
    });
  }

  highlightCodeBlock(code) {
    if (code.dataset.highlighted === "yes") return;
    const highlighter = globalThis.hljs;
    if (!highlighter) return;
    const languageClass = [...code.classList].find((name) => name.startsWith("language-"));
    const requested = languageClass?.slice("language-".length)?.toLowerCase();
    const source = code.textContent || "";
    const highlighted = highlightCodeText(source, requested, highlighter);
    if (!highlighted && source.length > MAXIMUM_HIGHLIGHT_CHARACTERS) {
      code.dataset.highlightSkipped = "size";
      return;
    }
    if (!highlighted) return;
    code.innerHTML = highlighted.value;
    code.classList.add("hljs");
    code.dataset.highlighted = "yes";
    code.dataset.language = highlighted.language;
  }

  decorateHeadings() {
    const headings = this.headingElements();
    const seen = new Map();
    headings.forEach((heading) => {
      const level = Number(heading.tagName.slice(1));
      const text = heading.textContent?.trim() || "";
      const occurrenceKey = `${level}|${text}`;
      const occurrence = (seen.get(occurrenceKey) || 0) + 1;
      seen.set(occurrenceKey, occurrence);
      const key = `${occurrenceKey}|${occurrence}`;
      heading.dataset.foldKey = key;
      if (heading.querySelector(":scope > .heading-fold-button")) return;
      const button = document.createElement("button");
      button.type = "button";
      button.className = "heading-fold-button";
      button.setAttribute("aria-label", "折叠或展开标题");
      button.setAttribute("aria-expanded", String(!this.foldState.get(key)));
      button.textContent = "›";
      button.addEventListener("click", (event) => {
        event.stopPropagation();
        this.foldState.set(key, !this.foldState.get(key));
        this.applyFolds();
      });
      heading.prepend(button);
    });
  }

  applyFolds() {
    const main = this.main;
    if (!main) return;
    const collapsedStack = [];
    const sections = [...main.querySelectorAll(":scope > .wimd-source-block")];
    for (const section of sections) {
      const heading = section.querySelector(":scope > h1, :scope > h2, :scope > h3, :scope > h4, :scope > h5, :scope > h6");
      if (heading) {
        const level = Number(heading.tagName.slice(1));
        while (collapsedStack.length && collapsedStack.at(-1).level >= level) {
          collapsedStack.pop();
        }
        const inheritedCollapsed = collapsedStack.some((item) => item.collapsed);
        section.classList.toggle("wimd-folded", inheritedCollapsed);
        const key = heading.dataset.foldKey;
        const collapsed = Boolean(key && this.foldState.get(key));
        const button = heading.querySelector(":scope > .heading-fold-button");
        if (button) {
          button.setAttribute("aria-expanded", String(!collapsed));
          button.textContent = collapsed ? "›" : "⌄";
        }
        collapsedStack.push({ level, collapsed });
      } else {
        section.classList.toggle(
          "wimd-folded",
          collapsedStack.some((item) => item.collapsed),
        );
      }
    }
  }

  decorateLinks() {
    const main = this.main;
    if (!main) return;
    if (main.dataset.wimdLinksBound === "true") return;
    main.dataset.wimdLinksBound = "true";
    main.addEventListener(
      "click",
      (event) => {
        const link = event.target.closest("a[href]");
        if (!link) return;
        event.preventDefault();
        const href = link.getAttribute("href") || "";
        if (href.startsWith("#")) {
          const target = this.document?.getElementById(href.slice(1));
          target?.scrollIntoView({ block: "start" });
          this.onNavigateSourceLine?.(target);
          return;
        }
        this.onOpenLink?.(href);
      },
      { capture: true },
    );
  }

  decorateImageContextMenu() {
    const main = this.main;
    if (!main || main.dataset.wimdImageContextMenuBound === "true") return;
    main.dataset.wimdImageContextMenuBound = "true";
    main.addEventListener("contextmenu", (event) => {
      const image = event.target.closest("img[data-wimd-source]");
      if (!image) return;
      event.preventDefault();
      const link = image.closest("a[href]");
      this.onImageContextMenu?.({
        source: image.currentSrc || image.src,
        originalSource: image.dataset.wimdSource || "",
        documentPath: this.currentDocumentPath || null,
        title: image.alt || "图片",
        link: link?.getAttribute("href") || "",
        clientX: event.clientX,
        clientY: event.clientY,
      });
    });
  }

  decorateImages(documentPath, title) {
    const main = this.main;
    if (!main) return;
    main.querySelectorAll("img").forEach((image) => {
      const original = image.getAttribute("src") || "";
      image.dataset.wimdSource = original;
      image.alt ||= title || "文档图片";
      if (!isRemoteOrDataImage(original)) {
        void this.loadLocalImage(image, documentPath, original);
      }
      const linkedImage = image.closest("a[href]");
      if (!linkedImage) {
        image.addEventListener("click", (event) => {
          event.preventDefault();
          event.stopPropagation();
          this.onOpenImage?.({
            source: image.currentSrc || image.src,
            title: image.alt || pathFileName(original),
          });
        });
        image.addEventListener("keydown", (event) => {
          if (event.key !== "Enter" && event.key !== " ") return;
          event.preventDefault();
          this.onOpenImage?.({
            source: image.currentSrc || image.src,
            title: image.alt || pathFileName(original),
          });
        });
        image.tabIndex = 0;
      }
    });
  }

  async loadLocalImage(image, documentPath, source) {
    image.src = TRANSPARENT_PIXEL;
    image.style.minHeight = "58px";
    if (!documentPath) {
      this.replaceBrokenImage(image, "保存文档后可加载本地图片");
      return;
    }

    const key = `${documentPath}\n${source}`;
    let pending = this.imageCache.get(key);
    if (!pending) {
      pending = this.onLoadLocalImage?.(documentPath, source);
      if (pending) this.imageCache.set(key, pending);
    }
    try {
      const result = await pending;
      if (!image.isConnected) return;
      image.src = result.dataUri;
      image.style.minHeight = "";
    } catch {
      if (image.isConnected) this.replaceBrokenImage(image, "无法加载本地图片");
    }
  }

  replaceBrokenImage(image, message) {
    const placeholder = this.document.createElement("span");
    placeholder.className = "image-error";
    placeholder.textContent = message;
    image.replaceWith(placeholder);
  }

  scrollRatio() {
    const document = this.document;
    if (!document) return 0;
    const root = document.scrollingElement || document.documentElement;
    const range = Math.max(0, root.scrollHeight - root.clientHeight);
    return range ? root.scrollTop / range : 0;
  }

  setScrollRatio(ratio) {
    const document = this.document;
    if (!document) return;
    const root = document.scrollingElement || document.documentElement;
    const range = Math.max(0, root.scrollHeight - root.clientHeight);
    this.scrollSyncSuppressed = true;
    root.scrollTop = range * clamp(ratio, 0, 1);
    requestAnimationFrame(() => {
      this.scrollSyncSuppressed = false;
    });
  }

  revealSourceLine(line) {
    const main = this.main;
    const document = this.document;
    if (!main || !document) return;
    const sections = [...main.querySelectorAll(":scope > .wimd-source-block[data-source-line]")];
    if (!sections.length) return;
    let target = sections[0];
    for (const section of sections) {
      const sectionLine = Number(section.dataset.sourceLine);
      if (!Number.isFinite(sectionLine) || sectionLine > line) break;
      target = section;
    }
    if (target.classList.contains("wimd-folded")) return;
    const root = document.scrollingElement || document.documentElement;
    // Bug fix: source sections use display: contents and therefore have a zero
    // client rect. Measure their actual rendered contents to avoid repeatedly
    // applying "0 - safeTop" on every click to the same source line.
    const range = document.createRange();
    range.selectNodeContents(target);
    const rect = range.getBoundingClientRect();
    if (!Number.isFinite(rect.top) || (rect.width === 0 && rect.height === 0)) return;
    const viewportHeight = root.clientHeight;
    const safeTop = viewportHeight * 0.25;
    const safeBottom = viewportHeight * 0.75;
    if (rect.top >= safeTop && rect.top <= safeBottom) return;
    const rangeHeight = Math.max(0, root.scrollHeight - root.clientHeight);
    const targetScrollTop = clamp(root.scrollTop + rect.top - safeTop, 0, rangeHeight);
    if (Math.abs(targetScrollTop - root.scrollTop) < 1) return;
    this.scrollSyncSuppressed = true;
    root.scrollTop = targetScrollTop;
    requestAnimationFrame(() => {
      this.scrollSyncSuppressed = false;
    });
  }

  async createPdfSnapshot() {
    await this.waitForImages(10000);
    await Promise.race([
      this.document?.fonts?.ready || Promise.resolve(),
      new Promise((resolve) => window.setTimeout(resolve, 5000)),
    ]);
    const main = this.main;
    if (!main) throw new Error("预览页面尚未初始化。");

    const clone = main.cloneNode(true);
    clone
      .querySelectorAll(".heading-fold-button, .code-copy-button")
      .forEach((node) => node.remove());
    clone
      .querySelectorAll(".wimd-folded")
      .forEach((node) => node.classList.remove("wimd-folded"));
    clone
      .querySelectorAll('input[type="checkbox"][data-task-line]')
      .forEach((input) => {
        input.disabled = true;
      });

    return {
      html: clone.innerHTML,
      css: this.previewCss,
      theme: this.documentElement?.dataset.theme || "light",
      inlineStyle: this.documentElement?.getAttribute("style") || "",
    };
  }

  scrollSnapshot() {
    return { ratio: this.scrollRatio(), suppressed: this.scrollSyncSuppressed };
  }

  headingElements() {
    return [...(this.main?.querySelectorAll("h1, h2, h3, h4, h5, h6") || [])];
  }

  emitScroll() {
    this.onScroll?.(this.scrollSnapshot());
  }

  clearFolds() {
    this.foldState.clear();
  }

  clearImageCache() {
    this.imageCache.clear();
  }

  async waitForImages(timeoutMs) {
    const images = [...(this.main?.querySelectorAll("img") || [])];
    if (!images.length) return;
    await Promise.race([
      Promise.all(
        images.map(
          (image) =>
            new Promise((resolve) => {
              if (image.complete) {
                resolve();
                return;
              }
              const finish = () => {
                image.removeEventListener("load", finish);
                image.removeEventListener("error", finish);
                resolve();
              };
              image.addEventListener("load", finish, { once: true });
              image.addEventListener("error", finish, { once: true });
            }),
        ),
      ),
      new Promise((resolve) => window.setTimeout(resolve, timeoutMs)),
    ]);
  }

}

function bundledFontFamily(value) {
  const normalized = String(value || "").trim();
  const fonts = {
    "wimd:lxgw-wenkai": "WIMD LXGW WenKai",
    "wimd:lxgw-wenkai-mono": "WIMD LXGW WenKai Mono",
    "wimd:lxgw-marker-gothic": "WIMD LXGW Marker Gothic",
    "wimd:jetbrains-mono": "WIMD JetBrains Mono",
    "wimd:fira-code": "WIMD Fira Code",
  };
  if (fonts[normalized]) return fonts[normalized];
  for (const family of Object.values(fonts)) {
    if (family === normalized) return family;
  }
  return normalized;
}

let mermaidLoadPromise = null;

function loadMermaid(theme) {
  if (!mermaidLoadPromise) {
    mermaidLoadPromise = new Promise((resolve, reject) => {
      if (globalThis.mermaid) {
        resolve(globalThis.mermaid);
        return;
      }
      const script = document.createElement("script");
      script.src = "./vendor/mermaid.min.js";
      script.async = true;
      script.addEventListener("load", () => resolve(globalThis.mermaid), { once: true });
      script.addEventListener(
        "error",
        () => reject(new Error("无法加载本地 mermaid.min.js")),
        { once: true },
      );
      document.head.append(script);
    });
  }
  return mermaidLoadPromise.then((mermaid) => {
    mermaid.initialize({
      startOnLoad: false,
      securityLevel: "strict",
      suppressErrorRendering: true,
      maxTextSize: 50_000,
      maxEdges: 500,
      theme: /(?:dark|midnight)/.test(theme) ? "dark" : "default",
      flowchart: {
        htmlLabels: false,
        securityLevel: "strict",
      },
      sequence: {
        useMaxWidth: true,
      },
    });
    return mermaid;
  });
}

function countMermaidEdges(source) {
  return (source.match(/(?:-->|---|==>|-\.->|--x|--o)/g) || []).length;
}

function sanitizeMermaidSvg(svgText) {
  const parser = new DOMParser();
  const document = parser.parseFromString(svgText, "image/svg+xml");
  if (document.querySelector("parsererror")) return "";
  const root = document.documentElement;
  if (root.localName !== "svg") return "";
  document
    .querySelectorAll("script, foreignObject, iframe, object, embed, audio, video")
    .forEach((element) => element.remove());
  for (const element of [root, ...document.querySelectorAll("*")]) {
    for (const attribute of [...element.attributes]) {
      const name = attribute.name.toLowerCase();
      const value = attribute.value.trim();
      if (name.startsWith("on")) {
        element.removeAttribute(attribute.name);
        continue;
      }
      if ((name === "href" || name === "xlink:href") && !value.startsWith("#")) {
        element.removeAttribute(attribute.name);
        continue;
      }
      if (
        name === "style" &&
        /(?:javascript:|expression\s*\(|url\s*\(\s*['"]?https?:)/i.test(value)
      ) {
        element.removeAttribute(attribute.name);
      }
    }
  }
  return new XMLSerializer().serializeToString(root);
}

function replaceMermaidWithError(pre, message, source) {
  const container = document.createElement("section");
  container.className = "mermaid-error";
  const heading = document.createElement("strong");
  const sourceLine = Number(
    pre.closest(".wimd-source-block")?.dataset.sourceLine,
  );
  heading.textContent = Number.isInteger(sourceLine)
    ? `${message}（源码起始于第 ${sourceLine + 1} 行）`
    : message;
  const details = document.createElement("details");
  const summary = document.createElement("summary");
  summary.textContent = "查看图表源码";
  const code = document.createElement("pre");
  code.textContent = source;
  details.append(summary, code);
  container.append(heading, details);
  pre.replaceWith(container);
}

function isRemoteOrDataImage(source) {
  return /^(?:https?:|data:)/i.test(source);
}

function pathFileName(path) {
  return String(path || "图片").split(/[\\/]/).filter(Boolean).at(-1) || "图片";
}

function escapeHtml(value) {
  return String(value)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;");
}

function escapeCssString(value) {
  return String(value).replaceAll("\\", "\\\\").replaceAll('"', '\\"');
}

function clamp(value, minimum, maximum) {
  return Math.min(maximum, Math.max(minimum, value));
}
