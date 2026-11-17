use std::collections::{HashMap, HashSet};

use serde::Serialize;

use crate::error::{CoreError, CoreResult};

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ShortcutDefinition {
    pub id: &'static str,
    pub label: &'static str,
    pub default_gesture: &'static str,
}

pub const SHORTCUTS: &[ShortcutDefinition] = &[
    ShortcutDefinition {
        id: "new",
        label: "新建文档",
        default_gesture: "Ctrl+N",
    },
    ShortcutDefinition {
        id: "open",
        label: "打开文档",
        default_gesture: "Ctrl+O",
    },
    ShortcutDefinition {
        id: "save",
        label: "保存",
        default_gesture: "Ctrl+S",
    },
    ShortcutDefinition {
        id: "save-as",
        label: "另存为",
        default_gesture: "Ctrl+Shift+S",
    },
    ShortcutDefinition {
        id: "reload",
        label: "重新加载",
        default_gesture: "F5",
    },
    ShortcutDefinition {
        id: "cycle-view",
        label: "切换视图",
        default_gesture: "F9",
    },
    ShortcutDefinition {
        id: "bold",
        label: "粗体",
        default_gesture: "Ctrl+B",
    },
    ShortcutDefinition {
        id: "italic",
        label: "斜体",
        default_gesture: "Ctrl+I",
    },
    ShortcutDefinition {
        id: "strike",
        label: "删除线",
        default_gesture: "Ctrl+Shift+X",
    },
    ShortcutDefinition {
        id: "inline-code",
        label: "行内代码",
        default_gesture: "Ctrl+E",
    },
    ShortcutDefinition {
        id: "code-block",
        label: "代码块",
        default_gesture: "Ctrl+Shift+K",
    },
    ShortcutDefinition {
        id: "link",
        label: "链接",
        default_gesture: "Ctrl+K",
    },
    ShortcutDefinition {
        id: "image",
        label: "图片",
        default_gesture: "Ctrl+Shift+I",
    },
    ShortcutDefinition {
        id: "ordered-list",
        label: "有序列表",
        default_gesture: "Ctrl+Shift+7",
    },
    ShortcutDefinition {
        id: "unordered-list",
        label: "无序列表",
        default_gesture: "Ctrl+Shift+8",
    },
    ShortcutDefinition {
        id: "quote",
        label: "引用",
        default_gesture: "Ctrl+Shift+9",
    },
    ShortcutDefinition {
        id: "task-list",
        label: "任务列表",
        default_gesture: "Ctrl+Shift+L",
    },
    ShortcutDefinition {
        id: "heading-1",
        label: "一级标题",
        default_gesture: "Ctrl+1",
    },
    ShortcutDefinition {
        id: "heading-2",
        label: "二级标题",
        default_gesture: "Ctrl+2",
    },
    ShortcutDefinition {
        id: "heading-3",
        label: "三级标题",
        default_gesture: "Ctrl+3",
    },
    ShortcutDefinition {
        id: "heading-4",
        label: "四级标题",
        default_gesture: "Ctrl+4",
    },
    ShortcutDefinition {
        id: "heading-5",
        label: "五级标题",
        default_gesture: "Ctrl+5",
    },
    ShortcutDefinition {
        id: "heading-6",
        label: "六级标题",
        default_gesture: "Ctrl+6",
    },
];

pub fn effective_shortcuts(
    overrides: &HashMap<String, String>,
) -> CoreResult<HashMap<&'static str, String>> {
    let known_ids: HashSet<_> = SHORTCUTS.iter().map(|shortcut| shortcut.id).collect();
    if let Some(unknown) = overrides.keys().find(|id| !known_ids.contains(id.as_str())) {
        return Err(CoreError::InvalidName(format!("未知快捷键命令：{unknown}")));
    }

    let mut gestures = HashSet::new();
    let mut result = HashMap::new();
    for shortcut in SHORTCUTS {
        let gesture = overrides.get(shortcut.id).map_or_else(
            || shortcut.default_gesture.to_owned(),
            |value| normalize(value),
        );
        validate_gesture(&gesture)?;
        if !gestures.insert(gesture.to_lowercase()) {
            return Err(CoreError::InvalidName(format!("快捷键组合重复：{gesture}")));
        }
        result.insert(shortcut.id, gesture);
    }
    Ok(result)
}

pub fn validate_gesture(gesture: &str) -> CoreResult<()> {
    let gesture = normalize(gesture);
    if gesture.contains("Win+") {
        return Err(CoreError::InvalidName("不支持 Windows 键组合。".to_owned()));
    }
    let parts = gesture.split('+').collect::<Vec<_>>();
    let Some(key) = parts.last().copied().filter(|key| !key.is_empty()) else {
        return Err(CoreError::InvalidName("快捷键缺少主键。".to_owned()));
    };
    if matches!(key, "Ctrl" | "Shift" | "Alt" | "Win") {
        return Err(CoreError::InvalidName(
            "快捷键必须包含非修饰键。".to_owned(),
        ));
    }
    let control = parts.contains(&"Ctrl");
    let shift = parts.contains(&"Shift");
    let alt = parts.contains(&"Alt");
    let function_key = key
        .strip_prefix('F')
        .and_then(|number| number.parse::<u8>().ok())
        .is_some_and(|number| (1..=24).contains(&number));
    if !control && !alt && !function_key {
        return Err(CoreError::InvalidName(
            "字母、数字和编辑键必须搭配 Ctrl 或 Alt。".to_owned(),
        ));
    }
    let reserved = (alt && !control && !shift && matches!(key, "F4" | "F" | "E" | "V" | "H" | "K"))
        || (control
            && !alt
            && !shift
            && matches!(key, "A" | "C" | "V" | "X" | "Y" | "Z" | "Insert"))
        || (shift && !control && !alt && matches!(key, "Insert" | "Delete"));
    if reserved {
        return Err(CoreError::InvalidName(format!(
            "{gesture} 是系统或编辑器保留组合。"
        )));
    }
    Ok(())
}

fn normalize(value: &str) -> String {
    let parts = value
        .split('+')
        .map(str::trim)
        .filter(|part| !part.is_empty())
        .map(|part| match part.to_ascii_lowercase().as_str() {
            "ctrl" | "control" => "Ctrl".to_owned(),
            "shift" => "Shift".to_owned(),
            "alt" => "Alt".to_owned(),
            _ if part.len() == 1 => part.to_ascii_uppercase(),
            _ => {
                let mut characters = part.chars();
                match characters.next() {
                    Some(first) => first.to_uppercase().collect::<String>() + characters.as_str(),
                    None => String::new(),
                }
            }
        })
        .collect::<Vec<_>>();
    let key = parts.last().cloned().unwrap_or_default();
    ["Ctrl", "Shift", "Alt"]
        .into_iter()
        .filter(|modifier| parts.iter().any(|part| part == modifier))
        .chain(std::iter::once(key.as_str()))
        .collect::<Vec<_>>()
        .join("+")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn defaults_are_unique_and_valid() {
        let result = effective_shortcuts(&HashMap::new()).unwrap();
        assert_eq!(result["strike"], "Ctrl+Shift+X");
        assert_eq!(result.len(), SHORTCUTS.len());
    }

    #[test]
    fn rejects_duplicates_and_reserved_editing_commands() {
        let duplicate = HashMap::from([
            ("bold".to_owned(), "Ctrl+I".to_owned()),
            ("italic".to_owned(), "Ctrl+I".to_owned()),
        ]);
        assert!(effective_shortcuts(&duplicate).is_err());

        let reserved = HashMap::from([("save".to_owned(), "Ctrl+V".to_owned())]);
        assert!(effective_shortcuts(&reserved).is_err());
    }

    #[test]
    fn modifier_order_is_canonical_and_all_legacy_commands_exist() {
        let overrides = HashMap::from([("code-block".to_owned(), "Alt+Shift+Ctrl+K".to_owned())]);
        let result = effective_shortcuts(&overrides).unwrap();

        assert_eq!(result["code-block"], "Ctrl+Shift+Alt+K");
        for id in [
            "image",
            "ordered-list",
            "unordered-list",
            "quote",
            "task-list",
        ] {
            assert!(result.contains_key(id));
        }
    }
}
