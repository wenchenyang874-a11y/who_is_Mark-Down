import assert from "node:assert/strict";
import test from "node:test";

import {
  highlightMarkdown,
  isInsideFencedCode,
  nextListContinuation,
} from "./editor.js";

test("editor highlighter keeps every source line addressable", () => {
  const html = highlightMarkdown("# 标题\n\n- [x] 完成\n\n```rust\nlet value = 1;\n```");

  assert.equal((html.match(/class="md-line"/g) || []).length, 7);
  assert.match(html, /data-editor-line="0"/);
  assert.match(html, /md-heading/);
  assert.match(html, /md-fence-content/);
  assert.doesNotMatch(html, /<script/i);
});

test("editor highlighter escapes fenced content without inline Markdown rewriting", () => {
  const html = highlightMarkdown("```html\n<strong>*literal*</strong>\n```");

  assert.match(html, /&lt;strong&gt;\*literal\*&lt;\/strong&gt;/);
  assert.doesNotMatch(html, /md-strong/);
});

test("list continuation preserves marker type and increments ordered lists", () => {
  assert.equal(nextListContinuation("- item").nextPrefix, "- ");
  assert.equal(nextListContinuation("* item").nextPrefix, "* ");
  assert.equal(nextListContinuation("2. item").nextPrefix, "3. ");
  assert.equal(nextListContinuation("9) item").nextPrefix, "10) ");
  assert.equal(nextListContinuation("- [x] done").nextPrefix, "- [ ] ");
  assert.equal(nextListContinuation("> 1. quoted").nextPrefix, "> 2. ");
});

test("empty list items terminate the list instead of creating another marker", () => {
  assert.deepEqual(nextListContinuation("- "), {
    empty: true,
    emptyLine: "\n",
    nextPrefix: "- ",
  });
  assert.deepEqual(nextListContinuation("> - [ ] "), {
    empty: true,
    emptyLine: "> \n",
    nextPrefix: "> - [ ] ",
  });
  assert.equal(nextListContinuation("plain text"), null);
});

test("fence state prevents list continuation inside code blocks", () => {
  const text = "```md\n- not a list\n```\n- real list";

  assert.equal(isInsideFencedCode(text, text.indexOf("- not a list")), true);
  assert.equal(isInsideFencedCode(text, text.indexOf("- real list")), false);
});
