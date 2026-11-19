// Bug fix: the font fields previously used a short hard-coded datalist, so
// installed Windows families were not discoverable. Enumerate DirectWrite's
// localized family names on a background thread, filter vertical aliases and
// sort common Chinese families first without modifying system fonts.

use std::collections::HashSet;

#[cfg(windows)]
use windows::{
    Win32::{
        Graphics::DirectWrite::{
            DWRITE_FACTORY_TYPE_SHARED, DWriteCreateFactory, IDWriteFactory,
            IDWriteLocalizedStrings,
        },
        System::Com::{COINIT_MULTITHREADED, CoInitializeEx, CoUninitialize},
    },
    core::{BOOL, w},
};

const PREFERRED_FAMILIES: &[&str] = &[
    "Microsoft YaHei UI",
    "Microsoft YaHei",
    "微软雅黑",
    "SimSun",
    "宋体",
    "SimHei",
    "黑体",
    "DengXian",
    "等线",
    "KaiTi",
    "楷体",
    "FangSong",
    "仿宋",
    "Segoe UI",
    "Segoe UI Variable Text",
    "Cascadia Code",
    "Consolas",
    "LXGW WenKai",
    "霞鹜文楷",
    "JetBrains Mono",
    "Fira Code",
];

pub fn installed_font_families() -> Result<Vec<String>, String> {
    #[cfg(windows)]
    {
        enumerate_direct_write_fonts()
    }
    #[cfg(not(windows))]
    {
        Ok(Vec::new())
    }
}

#[cfg(windows)]
fn enumerate_direct_write_fonts() -> Result<Vec<String>, String> {
    let initialized = unsafe { CoInitializeEx(None, COINIT_MULTITHREADED) }.is_ok();
    let result = (|| {
        let factory: IDWriteFactory = unsafe { DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED) }
            .map_err(|error| format!("创建 DirectWrite 工厂失败：{error}"))?;
        let mut collection = None;
        unsafe { factory.GetSystemFontCollection(&mut collection, false) }
            .map_err(|error| format!("读取系统字体集合失败：{error}"))?;
        let collection = collection.ok_or_else(|| "系统字体集合不可用。".to_owned())?;
        let mut names = Vec::new();
        for index in 0..unsafe { collection.GetFontFamilyCount() } {
            let Ok(family) = (unsafe { collection.GetFontFamily(index) }) else {
                continue;
            };
            let Ok(localized_names) = (unsafe { family.GetFamilyNames() }) else {
                continue;
            };
            if let Some(name) = unsafe { preferred_localized_name(&localized_names) } {
                names.push(name);
            }
        }
        Ok(normalize_font_families(names))
    })();
    if initialized {
        unsafe { CoUninitialize() };
    }
    result
}

#[cfg(windows)]
unsafe fn preferred_localized_name(names: &IDWriteLocalizedStrings) -> Option<String> {
    for locale in [
        w!("zh-CN"),
        w!("zh-Hans"),
        w!("zh-TW"),
        w!("zh-Hant"),
        w!("en-US"),
    ] {
        let mut index = 0u32;
        let mut exists = BOOL(0);
        if unsafe { names.FindLocaleName(locale, &mut index, &mut exists) }.is_ok()
            && exists.as_bool()
            && let Some(name) = unsafe { localized_string_at(names, index) }
        {
            return Some(name);
        }
    }
    let count = unsafe { names.GetCount() };
    (0..count).find_map(|index| unsafe { localized_string_at(names, index) })
}

#[cfg(windows)]
unsafe fn localized_string_at(names: &IDWriteLocalizedStrings, index: u32) -> Option<String> {
    let length = unsafe { names.GetStringLength(index) }.ok()? as usize;
    if length == 0 || length > 512 {
        return None;
    }
    let mut buffer = vec![0u16; length + 1];
    unsafe { names.GetString(index, &mut buffer) }.ok()?;
    let value = String::from_utf16_lossy(&buffer[..length]);
    (is_displayable_font_name(&value)).then_some(value)
}

fn is_displayable_font_name(name: &str) -> bool {
    let name = name.trim();
    !name.is_empty()
        && !name.starts_with('@')
        && name.len() <= 512
        && !name.chars().any(char::is_control)
}

fn normalize_font_families<I>(names: I) -> Vec<String>
where
    I: IntoIterator<Item = String>,
{
    let mut seen = HashSet::new();
    let mut unique = Vec::new();
    for raw in names {
        let name = raw.trim();
        if !is_displayable_font_name(name) || !seen.insert(name.to_lowercase()) {
            continue;
        }
        unique.push(name.to_owned());
    }
    unique.sort_by(|left, right| {
        font_priority(left)
            .cmp(&font_priority(right))
            .then_with(|| left.to_lowercase().cmp(&right.to_lowercase()))
    });
    unique
}

fn font_priority(name: &str) -> usize {
    PREFERRED_FAMILIES
        .iter()
        .position(|candidate| candidate.eq_ignore_ascii_case(name))
        .unwrap_or(PREFERRED_FAMILIES.len())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn installed_font_names_are_filtered_deduplicated_and_priority_sorted() {
        let names = normalize_font_families(vec![
            "@Microsoft YaHei".to_owned(),
            "Zeta Font".to_owned(),
            "Consolas".to_owned(),
            "Microsoft YaHei".to_owned(),
            "Microsoft YaHei".to_owned(),
            "  宋体  ".to_owned(),
            "".to_owned(),
        ]);

        assert_eq!(
            names,
            vec!["Microsoft YaHei", "宋体", "Consolas", "Zeta Font"]
        );
    }

    #[test]
    fn vertical_alias_and_control_characters_are_not_displayed() {
        assert!(!is_displayable_font_name("@DengXian"));
        assert!(!is_displayable_font_name("bad\u{7f}font"));
        assert!(is_displayable_font_name("Segoe UI"));
    }

    #[cfg(windows)]
    #[test]
    fn direct_write_enumerates_usable_windows_font_families() {
        let names = installed_font_families().expect("DirectWrite font enumeration");

        assert!(!names.is_empty());
        assert!(names.iter().all(|name| is_displayable_font_name(name)));
        assert!(names.iter().any(|name| {
            name.eq_ignore_ascii_case("Microsoft YaHei")
                || name.eq_ignore_ascii_case("Microsoft YaHei UI")
                || name == "微软雅黑"
        }));
    }
}
