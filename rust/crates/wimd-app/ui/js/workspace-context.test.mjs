import assert from "node:assert/strict";
import test from "node:test";

import { workspaceParentForSelection } from "./workspace-context.js";

test("workspace Ctrl+N uses the root when nothing is selected", () => {
  assert.equal(workspaceParentForSelection("C:\\docs", null), "C:\\docs");
});

test("workspace Ctrl+N uses a selected directory directly", () => {
  assert.equal(
    workspaceParentForSelection("C:\\docs", {
      path: "C:\\docs\\chapters",
      isDirectory: true,
    }),
    "C:\\docs\\chapters",
  );
});

test("workspace Ctrl+N uses the selected file parent", () => {
  assert.equal(
    workspaceParentForSelection("C:\\docs", {
      path: "C:\\docs\\chapters\\one.md",
      isDirectory: false,
    }),
    "C:\\docs\\chapters",
  );
  assert.equal(
    workspaceParentForSelection("C:\\docs", {
      path: "C:/docs/chapters/two.md",
      isDirectory: false,
    }),
    "C:\\docs\\chapters",
  );
});

test("workspace Ctrl+N returns null outside workspace mode", () => {
  assert.equal(workspaceParentForSelection(null, null), null);
});
