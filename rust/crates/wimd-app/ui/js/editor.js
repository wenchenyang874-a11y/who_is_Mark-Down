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
    this.textarea.addEventListener("keydown", (event) => this.handleTab(event));
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
    this.highlight.innerHTML = highlightMarkdown(value) + (value.endsWith("\n") ? " " : "");
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

  renderLineNumbers(value) {
    const count = Math.max(1, value.split("\n").length);
    this.lineNumberContent.textContent = Array.from({ length: count }, (_, index) => index + 1).join(
      "\n",
    );
  }

  syncScroll() {
    const transform = `translate(${-this.textarea.scrollLeft}px, ${-this.textarea.scrollTop}px)`;
    this.highlight.style.transform = transform;
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
  return value
    .split("\n")
    .map((line) => {
      const escaped = escapeHtml(line);
      const fence = escaped.match(/^(\s*)(```+|~~~+)(.*)$/);
      if (fence) {
        return `${fence[1]}<span class="md-marker">${fence[2]}${escapeHtml(
          fence[3],
        )}</span>`;
      }

      let rendered = escaped;
      const heading = rendered.match(/^(#{1,6})(\s+)(.*)$/);
      if (heading) {
        rendered = `<span class="md-heading">${heading[1]}${heading[2]}${highlightInline(
          heading[3],
        )}</span>`;
      } else {
        const quote = rendered.match(/^((?:\s*&gt;\s*)+)(.*)$/);
        if (quote) {
          rendered = `<span class="md-quote">${quote[1]}</span>${highlightInline(quote[2])}`;
        } else {
          const list = rendered.match(/^(\s*(?:[-+*]|\d+[.)]|[-+*]\s+\[[ xX]\])\s+)(.*)$/);
          if (list) {
            rendered = `<span class="md-marker">${list[1]}</span>${highlightInline(list[2])}`;
          } else {
            rendered = highlightInline(rendered);
          }
        }
      }
      return rendered;
    })
    .join("\n");
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
