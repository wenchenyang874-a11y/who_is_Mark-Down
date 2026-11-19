export function workspaceParentForSelection(root, selection) {
  if (!root) return null;
  if (!selection?.path) return root;
  if (selection.isDirectory) return selection.path;
  const normalized = String(selection.path).replaceAll("/", "\\");
  const separator = normalized.lastIndexOf("\\");
  return separator > 0 ? normalized.slice(0, separator) : root;
}
