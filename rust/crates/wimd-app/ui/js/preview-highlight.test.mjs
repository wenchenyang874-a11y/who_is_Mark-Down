import assert from "node:assert/strict";
import { createRequire } from "node:module";
import test from "node:test";

import { highlightCodeText } from "./preview.js";

const require = createRequire(import.meta.url);
const highlighter = require("../vendor/highlight.min.js");

test("bundled highlight.js exposes the pinned common language set", () => {
  assert.equal(highlighter.versionString, "11.12.0");
  for (const language of [
    "javascript",
    "typescript",
    "rust",
    "csharp",
    "python",
    "bash",
    "json",
    "xml",
    "css",
    "sql",
    "yaml",
    "markdown",
  ]) {
    assert.ok(highlighter.getLanguage(language), language);
  }
});

test("preview highlighting resolves aliases and escapes source", () => {
  const js = highlightCodeText("const answer = 42;", "js", highlighter);
  assert.equal(js.language, "javascript");
  assert.match(js.value, /hljs-keyword/);

  const html = highlightCodeText(
    "<script>alert('unsafe')</script>",
    "html",
    highlighter,
  );
  assert.equal(html.language, "xml");
  assert.doesNotMatch(html.value, /<script>/);
  assert.match(html.value, /&lt;/);
});

test("unknown, plain text, Mermaid and oversized blocks remain unmodified", () => {
  assert.equal(highlightCodeText("x", "not-a-language", highlighter), null);
  assert.equal(highlightCodeText("x", "text", highlighter), null);
  assert.equal(highlightCodeText("x", "mermaid", highlighter), null);
  assert.equal(highlightCodeText("x".repeat(512 * 1024 + 1), "javascript", highlighter), null);
});
