const HTML_ESCAPE = {
  "&": "&amp;",
  "<": "&lt;",
  ">": "&gt;",
  '"': "&quot;",
};

export class MarkdownEditor {
  constructor({ textarea, highlight, lineNumbers, onChange, onSelection, onScroll }) {
    this.textarea = textarea;
    this.highlight = highlight;
    this.lineNumbers = lineNumbers;
    this.onChange = onChange;
    this.onSelection = onSelection;
    this.onScroll = onScroll;
    this.composing = false;
    this.internalValueChange = false;

    if (!this.lineNumbers.firstElementChild) {
      const content = document.createElement("div");
      content.id = "line-number-content";
      this.lineNumbers.append(content);
    }
    this.lineNumberContent = this.lineNumbers.firstElementChild;
    if (!this.highlight.firstElementChild) {
      const content = document.createElement("code");
      content.id = "highlight-content";
      this.highlight.append(content);
    }
    this.highlightContent = this.highlight.firstElementChild;

    this.textarea.addEventListener("compositionstart", () => {
      this.composing = true;
    });
    this.textarea.addEventListener("compositionend", () => {
      this.composing = false;
      this.refresh();
      this.onChange?.(this.value, { compositionEnd: true });
    });
    this.textarea.addEventListener("input", () => {
      this.refresh();
      if (!this.composing) this.onChange?.(this.value);
    });
    this.textarea.addEventListener("scroll", () => {
      this.syncScroll();
      this.onScroll?.(this.scrollSnapshot());
    });
    this.textarea.addEventListener("click", () => this.emitSelection());
    this.textarea.addEventListener("keyup", () => this.emitSelection());
    this.textarea.addEventListener("select", () => this.emitSelection());
    this.textarea.addEventListener("keydown", (event) => {
      if (this.handleEnter(event)) return;
      this.handleTab(event);
    });
    this.resizeObserver = new ResizeObserver(() => {
      this.syncGeometry();
      this.layoutLineNumbers();
    });
    this.resizeObserver.observe(this.textarea);
  }

  get value() {
    return this.textarea.value;
  }

  get selectionStart() {
    return this.textarea.selectionStart;
  }

  get selectionEnd() {
    return this.textarea.selectionEnd;
  }

  setValue(value, { selectionStart = 0, selectionEnd = selectionStart, scrollTop = 0 } = {}) {
    this.internalValueChange = true;
    this.textarea.value = value;
    this.textarea.selectionStart = Math.min(selectionStart, value.length);
    this.textarea.selectionEnd = Math.min(selectionEnd, value.length);
    this.textarea.scrollTop = scrollTop;
    this.internalValueChange = false;
    this.refresh();
    this.emitSelection();
    this.syncScroll();
  }

  focus() {
    this.textarea.focus();
  }

  refresh() {
    const value = this.value;
    this.highlightContent.innerHTML = highlightMarkdown(value);
    this.syncGeometry();
    this.renderLineNumbers(value);
    this.syncScroll();
    this.emitSelection();
  }

  replaceRange(start, end, replacement, selectionStart, selectionEnd) {
    this.textarea.focus();
    this.textarea.setRangeText(replacement, start, end, "end");
    if (selectionStart != null) {
      this.textarea.selectionStart = selectionStart;
      this.textarea.selectionEnd = selectionEnd ?? selectionStart;
    }
    this.refresh();
    this.onChange?.(this.value);
  }

  applyEdit(edit) {
    this.replaceRange(
      0,
      this.value.length,
      edit.text,
      edit.selectionStart,
      edit.selectionStart + edit.selectionLength,
    );
  }

  insertText(text, { select = false } = {}) {
    const start = this.selectionStart;
    const end = this.selectionEnd;
    this.replaceRange(start, end, text, select ? start : start + text.length, start + text.length);
  }

  replaceCurrentDocument(text, selection = this.selectionStart) {
    this.replaceRange(0, this.value.length, text, selection, selection);
  }

  currentLineIndex(offset = this.selectionStart) {
    return this.value.slice(0, offset).split("\n").length - 1;
  }

  currentLineInfo() {
    const offset = this.selectionStart;
    const before = this.value.slice(0, offset);
    const line = before.split("\n").length;
    const lastBreak = before.lastIndexOf("\n");
    const column = offset - lastBreak;
    return { line, column, zeroBasedLine: line - 1, offset };
  }

  sourceLineSnapshot() {
    return {
      zeroBasedLine: this.currentLineIndex(),
      scrollTop: this.textarea.scrollTop,
      scrollHeight: this.textarea.scrollHeight,
      clientHeight: this.textarea.clientHeight,
    };
  }

  scrollSnapshot() {
    return {
      top: this.textarea.scrollTop,
      left: this.textarea.scrollLeft,
      height: this.textarea.scrollHeight,
      viewport: this.textarea.clientHeight,
    };
  }

  scrollToLine(line, { center = false } = {}) {
    const lineHeight = this.lineHeight();
    const target = Math.max(0, line * lineHeight - (center ? this.textarea.clientHeight * 0.38 : 0));
    this.textarea.scrollTop = target;
    this.syncScroll();
  }

  setVerticalRatio(ratio) {
    const range = Math.max(0, this.textarea.scrollHeight - this.textarea.clientHeight);
    this.textarea.scrollTop = range * clamp(ratio, 0, 1);
    this.syncScroll();
  }

  lineHeight() {
    const style = getComputedStyle(this.textarea);
    const lineHeight = Number.parseFloat(style.lineHeight);
    if (Number.isFinite(lineHeight)) return lineHeight;
    return Number.parseFloat(style.fontSize) * 1.65;
  }

  restoreCursorAndScroll(state) {
    this.textarea.selectionStart = Math.min(state.selectionStart ?? 0, this.value.length);
    this.textarea.selectionEnd = Math.min(state.selectionEnd ?? 0, this.value.length);
    this.textarea.scrollTop = state.scrollTop ?? 0;
    this.syncScroll();
    this.emitSelection();
  }

  find(query, { forward = true, caseSensitive = false, wrap = true } = {}) {
    if (!query) return false;
    const haystack = caseSensitive ? this.value : this.value.toLocaleLowerCase();
    const needle = caseSensitive ? query : query.toLocaleLowerCase();
    let index;

    if (forward) {
      index = haystack.indexOf(needle, this.selectionEnd);
      if (index === -1 && wrap) index = haystack.indexOf(needle, 0);
    } else {
      const searchEnd = Math.max(0, this.selectionStart - 1);
      index = haystack.lastIndexOf(needle, searchEnd);
      if (index === -1 && wrap) index = haystack.lastIndexOf(needle);
    }

    if (index === -1) return false;
    this.textarea.focus();
    this.textarea.selectionStart = index;
    this.textarea.selectionEnd = index + query.length;
    this.scrollToLine(this.currentLineIndex(index), { center: true });
    this.emitSelection();
    return true;
  }

  replaceCurrent(query, replacement, caseSensitive = false) {
    const selected = this.value.slice(this.selectionStart, this.selectionEnd);
    const matches = caseSensitive
      ? selected === query
      : selected.toLocaleLowerCase() === query.toLocaleLowerCase();
    if (!matches) return false;
    this.replaceRange(
      this.selectionStart,
      this.selectionEnd,
      replacement,
      this.selectionStart + replacement.length,
      this.selectionStart + replacement.length,
    );
    return true;
  }

  replaceAll(query, replacement, caseSensitive = false) {
    if (!query) return 0;
    const expression = new RegExp(escapeRegExp(query), caseSensitive ? "g" : "gi");
    let count = 0;
    const next = this.value.replace(expression, () => {
      count += 1;
      return replacement;
    });
    if (count > 0) this.replaceCurrentDocument(next, 0);
    return count;
  }

  indentSelection(outdent = false) {
    const start = this.selectionStart;
    const end = this.selectionEnd;
    const value = this.value;
    const lineStart = value.lastIndexOf("\n", Math.max(0, start - 1)) + 1;
    const nextBreak = value.indexOf("\n", end);
    const lineEnd = nextBreak === -1 ? value.length : nextBreak;
    const selectedLines = value.slice(lineStart, lineEnd).split("\n");
    const replacement = selectedLines
      .map((line) => {
        if (outdent) return line.replace(/^( {1,4}|\t)/, "");
        return `\t${line}`;
      })
      .join("\n");
    this.replaceRange(lineStart, lineEnd, replacement, lineStart, lineStart + replacement.length);
  }

  handleTab(event) {
    if (event.key !== "Tab" || event.ctrlKey || event.altKey || event.metaKey) return;
    event.preventDefault();
    this.indentSelection(event.shiftKey);
  }

  handleEnter(event) {
    if (
      event.key !== "Enter" ||
      event.shiftKey ||
      event.ctrlKey ||
      event.altKey ||
      event.metaKey ||
      this.selectionStart !== this.selectionEnd ||
      isInsideFencedCode(this.value, this.selectionStart)
    ) {
      return false;
    }

    const caret = this.selectionStart;
    const lineStart = this.value.lastIndexOf("\n", Math.max(0, caret - 1)) + 1;
    const currentLine = this.value.slice(lineStart, caret);
    const continuation = nextListContinuation(currentLine);
    if (!continuation) return false;

    event.preventDefault();
    const replacement = continuation.empty
      ? continuation.emptyLine
      : `\n${continuation.nextPrefix}`;
    const start = continuation.empty ? lineStart : caret;
    this.replaceRange(
      start,
      caret,
      replacement,
      start + replacement.length,
      start + replacement.length,
    );
    return true;
  }

  renderLineNumbers(value) {
    const count = Math.max(1, value.split("\n").length);
    const heights = this.measureLineHeights(count);
    const fragment = document.createDocumentFragment();
    for (let index = 0; index < count; index += 1) {
      const row = document.createElement("div");
      row.className = "line-number-row";
      row.style.height = `${heights[index]}px`;
      row.textContent = String(index + 1);
      fragment.append(row);
    }
    this.lineNumberContent.replaceChildren(fragment);
  }

  measureLineHeights(count) {
    const lines = this.highlight.querySelectorAll(".md-line");
    const fallback = this.lineHeight();
    return Array.from({ length: count }, (_, index) => {
      const height = lines[index]?.getBoundingClientRect().height;
      return Number.isFinite(height) && height > 0 ? height : fallback;
    });
  }

  layoutLineNumbers() {
    const count = Math.max(1, this.value.split("\n").length);
    const heights = this.measureLineHeights(count);
    const rows = this.lineNumberContent.querySelectorAll(".line-number-row");
    rows.forEach((row, index) => {
      if (index < heights.length) row.style.height = `${heights[index]}px`;
    });
  }

  syncGeometry() {
    const scrollbarWidth = Math.max(0, this.textarea.offsetWidth - this.textarea.clientWidth);
    this.highlight.style.right = `${scrollbarWidth}px`;
  }

  syncScroll() {
    // Bug fix: transform the inner text layer. Transforming this clipped container
    // moved its own overflow box offscreen and made selected source text disappear.
    const transform = `translate(${-this.textarea.scrollLeft}px, ${-this.textarea.scrollTop}px)`;
    this.highlightContent.style.transform = transform;
    this.lineNumberContent.style.transform = `translateY(${-this.textarea.scrollTop}px)`;
  }

  emitSelection() {
    const info = this.currentLineInfo();
    this.onSelection?.({
      ...info,
      selectionLength: this.selectionEnd - this.selectionStart,
      characterCount: this.value.length,
    });
  }
}

export function highlightMarkdown(value) {
  let fence = null;
  return value
    .split("\n")
    .map((line, index) => {
      const escaped = escapeHtml(line);
      const fenceMatch = escaped.match(/^(\s*)(`{3,}|~{3,})(.*)$/);
      let rendered;

      if (fence) {
        const closes =
          fenceMatch &&
          fenceMatch[2][0] === fence.character &&
          fenceMatch[2].length >= fence.length &&
          fenceMatch[3].trim() === "";
        if (closes) {
          rendered = `${fenceMatch[1]}<span class="md-marker">${fenceMatch[2]}</span>`;
          fence = null;
        } else {
          rendered = `<span class="md-fence-content">${escaped}</span>`;
        }
      } else if (fenceMatch) {
        fence = { character: fenceMatch[2][0], length: fenceMatch[2].length };
        rendered = `${fenceMatch[1]}<span class="md-marker">${fenceMatch[2]}${fenceMatch[3]}</span>`;
      } else {
        rendered = highlightMarkdownLine(escaped);
      }

      return `<span class="md-line" data-editor-line="${index}">${rendered}</span>`;
    })
    .join("");
}

function highlightMarkdownLine(rendered) {
  const heading = rendered.match(/^(#{1,6})(\s+)(.*)$/);
  if (heading) {
    return `<span class="md-heading">${heading[1]}${heading[2]}${highlightInline(
      heading[3],
    )}</span>`;
  }
  const quote = rendered.match(/^((?:\s*&gt;\s*)+)(.*)$/);
  if (quote) {
    return `<span class="md-quote">${quote[1]}</span>${highlightInline(quote[2])}`;
  }
  const list = rendered.match(/^(\s*(?:[-+*]|\d+[.)])\s+(?:\[[ xX]\]\s+)?)(.*)$/);
  if (list) {
    return `<span class="md-marker">${list[1]}</span>${highlightInline(list[2])}`;
  }
  return highlightInline(rendered);
}

function highlightInline(value) {
  return value
    .replace(/(`[^`]+`)/g, '<span class="md-code">$1</span>')
    .replace(/(\*\*[^*]+\*\*)/g, '<span class="md-strong">$1</span>')
    .replace(/(~~[^~]+~~)/g, '<span class="md-marker">$1</span>')
    .replace(/(?<!\*)(\*[^*]+\*)(?!\*)/g, '<span class="md-emphasis">$1</span>')
    .replace(/(\[[^\]]+\]\([^)]+\))/g, '<span class="md-link">$1</span>');
}

function escapeHtml(value) {
  return String(value).replace(/[&<>"]/g, (character) => HTML_ESCAPE[character]);
}

function escapeRegExp(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function clamp(value, minimum, maximum) {
  return Math.min(maximum, Math.max(minimum, value));
}

export function isInsideFencedCode(value, offset) {
  let fence = null;
  const beforeCaret = value.slice(0, Math.max(0, offset));
  for (const line of beforeCaret.split("\n")) {
    const match = line.match(/^ {0,3}(`{3,}|~{3,})/);
    if (!match) continue;
    if (!fence) {
      fence = { character: match[1][0], length: match[1].length };
      continue;
    }
    if (match[1][0] === fence.character && match[1].length >= fence.length) {
      const rest = line.slice(line.indexOf(match[1]) + match[1].length);
      if (rest.trim() === "") fence = null;
    }
  }
  return fence !== null;
}

export function nextListContinuation(line) {
  const match = line.match(/^(\s*(?:>\s*)*)([-+*]|\d+[.)])\s+(?:\[([ xX])\]\s+)?(.*)$/);
  if (!match) return null;

  const [, linePrefix, marker, taskState, content] = match;
  let nextMarker = marker;
  if (/^\d/.test(marker)) {
    const delimiter = marker.at(-1);
    const number = Number.parseInt(marker.slice(0, -1), 10);
    if (!Number.isFinite(number)) return null;
    nextMarker = `${number + 1}${delimiter}`;
  }
  const taskPrefix = taskState === undefined ? "" : "[ ] ";
  return {
    empty: content.trim() === "",
    emptyLine: `${linePrefix}\n`,
    nextPrefix: `${linePrefix}${nextMarker} ${taskPrefix}`,
  };
}
