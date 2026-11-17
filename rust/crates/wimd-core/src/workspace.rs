use std::{
    fs,
    path::{Component, Path, PathBuf},
};

use serde::Serialize;

use crate::{
    document::{is_markdown_path, normalize_path},
    error::{CoreError, CoreResult},
};

const MAXIMUM_WORKSPACE_ENTRIES: usize = 20_000;
const MAXIMUM_DIRECTORY_CHILDREN: usize = 5_000;

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct WorkspaceEntry {
    pub name: String,
    pub path: PathBuf,
    pub relative_path: String,
    pub is_directory: bool,
    pub children: Vec<WorkspaceEntry>,
}

#[derive(Clone, Debug)]
pub struct Workspace {
    root: PathBuf,
}

impl Workspace {
    pub fn open(path: impl AsRef<Path>) -> CoreResult<Self> {
        let root = normalize_path(path)?;
        if !root.is_dir() {
            return Err(CoreError::UnsupportedFileType { path: root });
        }
        Ok(Self { root })
    }

    pub fn root(&self) -> &Path {
        &self.root
    }

    pub fn tree(&self) -> CoreResult<Vec<WorkspaceEntry>> {
        let mut count = 0usize;
        read_directory_tree(&self.root, &self.root, &mut count)
    }

    pub fn children(&self, directory: impl AsRef<Path>) -> CoreResult<Vec<WorkspaceEntry>> {
        let directory = self.contained_directory(directory.as_ref())?;
        read_directory_children(&self.root, &directory)
    }

    pub fn create_markdown_file(
        &self,
        parent: impl AsRef<Path>,
        name: &str,
        content: &str,
    ) -> CoreResult<PathBuf> {
        validate_file_name(name)?;
        if !is_markdown_path(name) {
            return Err(CoreError::InvalidName(
                "新文件扩展名必须是 .md 或 .markdown。".to_owned(),
            ));
        }
        let parent = self.contained_directory(parent.as_ref())?;
        let target = parent.join(name);
        if target.exists() {
            return Err(CoreError::InvalidName(format!("目标已存在：{name}")));
        }

        let mut file = fs::OpenOptions::new()
            .create_new(true)
            .write(true)
            .open(&target)
            .map_err(|error| CoreError::io("新建 Markdown 文件", &target, error))?;
        use std::io::Write;
        file.write_all(content.as_bytes())
            .map_err(|error| CoreError::io("写入新 Markdown 文件", &target, error))?;
        Ok(target)
    }

    pub fn create_directory(&self, parent: impl AsRef<Path>, name: &str) -> CoreResult<PathBuf> {
        validate_directory_name(name)?;
        let parent = self.contained_directory(parent.as_ref())?;
        let target = parent.join(name);
        if target.exists() {
            return Err(CoreError::InvalidName(format!("目标已存在：{name}")));
        }
        fs::create_dir(&target).map_err(|error| CoreError::io("新建文件夹", &target, error))?;
        Ok(target)
    }

    pub fn rename_entry(&self, source: impl AsRef<Path>, new_name: &str) -> CoreResult<PathBuf> {
        let source = self.contained_existing(source.as_ref())?;
        if source == self.root {
            return Err(CoreError::InvalidName(
                "不能重命名工作区根目录。".to_owned(),
            ));
        }
        let is_directory = source.is_dir();
        if is_directory {
            validate_directory_name(new_name)?;
        } else if is_markdown_path(&source) {
            validate_file_name(new_name)?;
            if !is_markdown_path(new_name) {
                return Err(CoreError::InvalidName(
                    "Markdown 文件必须保留 .md 或 .markdown 扩展名。".to_owned(),
                ));
            }
        } else {
            return Err(CoreError::UnsupportedFileType { path: source });
        }

        let parent = source
            .parent()
            .ok_or_else(|| CoreError::InvalidName("目标缺少父目录。".to_owned()))?;
        let target = parent.join(new_name);
        if target.exists() {
            return Err(CoreError::InvalidName(format!("目标已存在：{new_name}")));
        }
        fs::rename(&source, &target)
            .map_err(|error| CoreError::io("重命名工作区条目", &source, error))?;
        Ok(target)
    }

    pub fn delete_entry(&self, path: impl AsRef<Path>) -> CoreResult<()> {
        let target = self.contained_existing(path.as_ref())?;
        if target == self.root {
            return Err(CoreError::InvalidName("不能删除工作区根目录。".to_owned()));
        }
        if target.is_dir() {
            fs::remove_dir_all(&target)
                .map_err(|error| CoreError::io("删除工作区文件夹", &target, error))
        } else {
            fs::remove_file(&target)
                .map_err(|error| CoreError::io("删除工作区文件", &target, error))
        }
    }

    pub fn contained_existing(&self, path: &Path) -> CoreResult<PathBuf> {
        let normalized = normalize_path(path)?;
        if !path_is_at_or_below(&normalized, &self.root) {
            return Err(CoreError::OutsideRoot { path: normalized });
        }
        Ok(normalized)
    }

    pub fn contained_file(&self, path: &Path) -> CoreResult<PathBuf> {
        let normalized = self.contained_existing(path)?;
        if !normalized.is_file() {
            return Err(CoreError::UnsupportedFileType { path: normalized });
        }
        Ok(normalized)
    }

    fn contained_directory(&self, path: &Path) -> CoreResult<PathBuf> {
        let normalized = self.contained_existing(path)?;
        if !normalized.is_dir() {
            return Err(CoreError::MissingPath { path: normalized });
        }
        Ok(normalized)
    }
}

pub fn is_safe_relative_directory(value: &str) -> bool {
    let trimmed = value.trim().replace('\\', "/");
    if trimmed.is_empty() || trimmed.len() > 180 {
        return false;
    }
    let path = Path::new(&trimmed);
    !path.is_absolute()
        && path
            .components()
            .all(|component| matches!(component, Component::Normal(value) if !value.is_empty()))
}

pub fn path_is_at_or_below(candidate: &Path, root: &Path) -> bool {
    let candidate = path_key(candidate).trim_end_matches(['\\', '/']).to_owned();
    let root = path_key(root).trim_end_matches(['\\', '/']).to_owned();
    if candidate == root {
        return true;
    }
    candidate
        .strip_prefix(&root)
        .is_some_and(|suffix| suffix.starts_with('\\') || suffix.starts_with('/'))
}

fn read_directory_tree(
    root: &Path,
    directory: &Path,
    count: &mut usize,
) -> CoreResult<Vec<WorkspaceEntry>> {
    let mut entries = Vec::new();
    let read_dir = fs::read_dir(directory)
        .map_err(|error| CoreError::io("读取工作区目录", directory, error))?;

    for entry in read_dir {
        let entry = entry.map_err(|error| CoreError::io("读取工作区条目", directory, error))?;
        let file_type = entry
            .file_type()
            .map_err(|error| CoreError::io("读取条目类型", entry.path(), error))?;
        if file_type.is_symlink() {
            continue;
        }

        let path = entry.path();
        if file_type.is_file() && !is_markdown_path(&path) {
            continue;
        }
        if !file_type.is_file() && !file_type.is_dir() {
            continue;
        }

        *count += 1;
        if *count > MAXIMUM_WORKSPACE_ENTRIES {
            return Err(CoreError::Internal(format!(
                "工作区条目超过 {MAXIMUM_WORKSPACE_ENTRIES} 个，请缩小工作区范围。"
            )));
        }

        let children = if file_type.is_dir() {
            read_directory_tree(root, &path, count)?
        } else {
            Vec::new()
        };
        entries.push(WorkspaceEntry {
            name: entry.file_name().to_string_lossy().into_owned(),
            relative_path: path
                .strip_prefix(root)
                .unwrap_or(&path)
                .to_string_lossy()
                .replace('\\', "/"),
            path,
            is_directory: file_type.is_dir(),
            children,
        });
    }

    entries.sort_by(|left, right| {
        right
            .is_directory
            .cmp(&left.is_directory)
            .then_with(|| left.name.to_lowercase().cmp(&right.name.to_lowercase()))
    });
    Ok(entries)
}

fn read_directory_children(root: &Path, directory: &Path) -> CoreResult<Vec<WorkspaceEntry>> {
    let mut entries = Vec::new();
    let read_dir = fs::read_dir(directory)
        .map_err(|error| CoreError::io("读取工作区目录", directory, error))?;
    for entry in read_dir {
        let entry = entry.map_err(|error| CoreError::io("读取工作区条目", directory, error))?;
        let file_type = entry
            .file_type()
            .map_err(|error| CoreError::io("读取条目类型", entry.path(), error))?;
        if file_type.is_symlink() {
            continue;
        }
        let path = entry.path();
        if file_type.is_file() && !is_markdown_path(&path) {
            continue;
        }
        if !file_type.is_file() && !file_type.is_dir() {
            continue;
        }
        entries.push(WorkspaceEntry {
            name: entry.file_name().to_string_lossy().into_owned(),
            relative_path: path
                .strip_prefix(root)
                .unwrap_or(&path)
                .to_string_lossy()
                .replace('\\', "/"),
            path,
            is_directory: file_type.is_dir(),
            children: Vec::new(),
        });
        if entries.len() > MAXIMUM_DIRECTORY_CHILDREN {
            return Err(CoreError::Internal(format!(
                "单个工作区目录超过 {MAXIMUM_DIRECTORY_CHILDREN} 个可见条目，请缩小目录范围。"
            )));
        }
    }
    entries.sort_by(|left, right| {
        right
            .is_directory
            .cmp(&left.is_directory)
            .then_with(|| left.name.to_lowercase().cmp(&right.name.to_lowercase()))
    });
    Ok(entries)
}

fn validate_file_name(name: &str) -> CoreResult<()> {
    validate_name(name)?;
    if name.contains('.') && name.ends_with('.') {
        return Err(CoreError::InvalidName("文件名不能以句点结尾。".to_owned()));
    }
    Ok(())
}

fn validate_directory_name(name: &str) -> CoreResult<()> {
    validate_name(name)?;
    if name.contains('.') && is_markdown_path(name) {
        return Err(CoreError::InvalidName(
            "文件夹名称不能使用 Markdown 文件扩展名。".to_owned(),
        ));
    }
    Ok(())
}

fn validate_name(name: &str) -> CoreResult<()> {
    let trimmed = name.trim();
    if trimmed.is_empty() || trimmed.len() > 120 {
        return Err(CoreError::InvalidName(
            "名称不能为空且不能超过 120 个字符。".to_owned(),
        ));
    }
    if trimmed != name {
        return Err(CoreError::InvalidName("名称首尾不能包含空格。".to_owned()));
    }
    if name.ends_with([' ', '.']) {
        return Err(CoreError::InvalidName(
            "名称不能以空格或句点结尾。".to_owned(),
        ));
    }
    if name
        .chars()
        .any(|character| character.is_control() || r#"<>:"/\|?*"#.contains(character))
    {
        return Err(CoreError::InvalidName(
            r#"名称不能包含控制字符或 <>:"/\|?*。"#.to_owned(),
        ));
    }

    let stem = name.split('.').next().unwrap_or(name).to_ascii_uppercase();
    let reserved = matches!(stem.as_str(), "CON" | "PRN" | "AUX" | "NUL")
        || stem.strip_prefix("COM").is_some_and(|number| {
            matches!(number, "1" | "2" | "3" | "4" | "5" | "6" | "7" | "8" | "9")
        })
        || stem.strip_prefix("LPT").is_some_and(|number| {
            matches!(number, "1" | "2" | "3" | "4" | "5" | "6" | "7" | "8" | "9")
        });
    if reserved {
        return Err(CoreError::InvalidName(format!(
            "“{name}”是 Windows 保留设备名。"
        )));
    }
    Ok(())
}

fn path_key(path: &Path) -> String {
    path.to_string_lossy().to_lowercase()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn workspace_tree_is_directory_first_and_skips_other_files() {
        let directory = tempfile::tempdir().unwrap();
        fs::write(directory.path().join("b.md"), "b").unwrap();
        fs::write(directory.path().join("ignored.txt"), "x").unwrap();
        fs::create_dir(directory.path().join("A")).unwrap();

        let tree = Workspace::open(directory.path()).unwrap().tree().unwrap();

        assert_eq!(tree[0].name, "A");
        assert_eq!(tree[1].name, "b.md");
        assert_eq!(tree.len(), 2);
    }

    #[test]
    fn workspace_children_are_loaded_one_directory_at_a_time() {
        let directory = tempfile::tempdir().unwrap();
        let nested = directory.path().join("nested");
        fs::create_dir(&nested).unwrap();
        fs::write(directory.path().join("top.md"), "top").unwrap();
        fs::write(nested.join("inside.md"), "inside").unwrap();
        let workspace = Workspace::open(directory.path()).unwrap();

        let root = workspace.children(directory.path()).unwrap();
        assert_eq!(root.len(), 2);
        assert!(root[0].is_directory);
        assert!(root[0].children.is_empty());

        let nested_entries = workspace.children(&nested).unwrap();
        assert_eq!(nested_entries[0].name, "inside.md");
    }

    #[test]
    fn create_and_rename_are_confined_to_the_workspace() {
        let directory = tempfile::tempdir().unwrap();
        let workspace = Workspace::open(directory.path()).unwrap();

        let path = workspace
            .create_markdown_file(directory.path(), "新文档.md", "# 标题")
            .unwrap();
        let renamed = workspace.rename_entry(&path, "改名.markdown").unwrap();

        assert!(renamed.exists());
        assert!(!path.exists());
        assert_eq!(fs::read_to_string(renamed).unwrap(), "# 标题");
    }

    #[test]
    fn rejects_traversal_and_reserved_names() {
        let directory = tempfile::tempdir().unwrap();
        let workspace = Workspace::open(directory.path()).unwrap();

        assert!(
            workspace
                .create_markdown_file(directory.path(), "../escape.md", "")
                .is_err()
        );
        assert!(
            workspace
                .create_markdown_file(directory.path(), "CON.md", "")
                .is_err()
        );
        assert!(!is_safe_relative_directory("../img"));
        assert!(!is_safe_relative_directory("C:\\img"));
        assert!(is_safe_relative_directory("assets/images"));
    }

    #[test]
    fn delete_refuses_the_workspace_root() {
        let directory = tempfile::tempdir().unwrap();
        let workspace = Workspace::open(directory.path()).unwrap();

        assert!(workspace.delete_entry(directory.path()).is_err());
        assert!(directory.path().exists());
    }

    #[test]
    fn symlinks_cannot_be_used_as_an_inside_path_to_outside_data() {
        let root = tempfile::tempdir().unwrap();
        let outside = tempfile::tempdir().unwrap();
        let outside_file = outside.path().join("secret.md");
        fs::write(&outside_file, "secret").unwrap();
        let link = root.path().join("link.md");

        if std::os::windows::fs::symlink_file(&outside_file, &link).is_ok() {
            let workspace = Workspace::open(root.path()).unwrap();
            assert!(workspace.contained_file(&link).is_err());
        }
    }
}
