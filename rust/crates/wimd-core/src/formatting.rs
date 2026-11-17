use serde::Serialize;

use crate::error::{CoreError, CoreResult};

#[derive(Clone, Debug, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct MarkdownTextEdit {
    pub text: String,
    pub selection_start: usize,
    pub selection_length: usize,
    pub caret_offset: usize,
}

pub fn apply_format(
    text: &str,
    selection_start_utf16: usize,
    selection_length_utf16: usize,
    format: &str,
) -> CoreResult<MarkdownTextEdit> {
    let (start, length) =
        utf16_selection_to_bytes(text, selection_start_utf16, selection_length_utf16)?;

    let edit = match format {
        "bold" => toggle_wrap(text, start, length, "**", "**", "粗体文本"),
        "italic" => toggle_wrap(text, start, length, "*", "*", "斜体文本"),
        "strike" => toggle_wrap(text, start, length, "~~", "~~", "删除线文本"),
        "inline-code" => toggle_wrap(text, start, length, "`", "`", "代码"),
        "link" => wrap(text, start, length, "[", "](https://)", "链接文字"),
        "image" => wrap(text, start, length, "![", "](img/image.png)", "图片说明"),
        "code-block" => wrap(text, start, length, "```\n", "\n```", "代码"),
        "quote" => prefix_lines(text, start, length, "> "),
        "unordered-list" => prefix_lines(text, start, length, "- "),
        "ordered-list" => prefix_lines(text, start, length, "1. "),
        "task-list" => prefix_lines(text, start, length, "- [ ] "),
        "table" => apply_table_bytes(text, start, length, 3, 3),
        "separator" => insert_separator(text, start, length),
        value if value.starts_with('h') => {
            let level = value[1..]
                .parse::<usize>()
                .ok()
                .filter(|level| (1..=6).contains(level))
                .ok_or_else(|| CoreError::UnsupportedFormat(value.to_owned()))?;
            apply_heading(text, start, length, level)
        }
        value => return Err(CoreError::UnsupportedFormat(value.to_owned())),
    };

    Ok(with_utf16_offsets(edit))
}

pub fn apply_table(
    text: &str,
    selection_start_utf16: usize,
    selection_length_utf16: usize,
    row_count: usize,
    column_count: usize,
) -> CoreResult<MarkdownTextEdit> {
    if !(2..=20).contains(&row_count) {
        return Err(CoreError::InvalidSelection(
            "表格行数必须在 2 到 20 之间。".to_owned(),
        ));
    }
    if !(1..=12).contains(&column_count) {
        return Err(CoreError::InvalidSelection(
            "表格列数必须在 1 到 12 之间。".to_owned(),
        ));
    }

    let (start, length) =
        utf16_selection_to_bytes(text, selection_start_utf16, selection_length_utf16)?;
    let edit = apply_table_bytes(text, start, length, row_count, column_count);
    Ok(with_utf16_offsets(edit))
}

fn toggle_wrap(
    text: &str,
    start: usize,
    length: usize,
    prefix: &str,
    suffix: &str,
    placeholder: &str,
) -> PendingEdit {
    if length > 0 && is_exact_wrapped_selection(text, start, length, prefix, suffix) {
        let content_start = start + prefix.len();
        let content_end = start + length - suffix.len();
        let content = &text[content_start..content_end];
        return replace_and_select(text, start, length, content, 0, content.len());
    }

    if length > 0 && is_surrounded(text, start, length, prefix, suffix) {
        let full_start = start - prefix.len();
        let full_end = start + length + suffix.len();
        let content = &text[start..start + length];
        return replace_and_select(
            text,
            full_start,
            full_end - full_start,
            content,
            0,
            content.len(),
        );
    }

    wrap(text, start, length, prefix, suffix, placeholder)
}

fn wrap(
    text: &str,
    start: usize,
    length: usize,
    prefix: &str,
    suffix: &str,
    placeholder: &str,
) -> PendingEdit {
    let content = if length == 0 {
        placeholder
    } else {
        &text[start..start + length]
    };
    let replacement = format!("{prefix}{content}{suffix}");
    let result = replace(text, start, length, &replacement);
    let selected_start = start + prefix.len();
    PendingEdit {
        text: result,
        selection_start_bytes: selected_start,
        selection_length_bytes: content.len(),
        caret_bytes: selected_start + content.len(),
    }
}

fn prefix_lines(text: &str, start: usize, length: usize, prefix: &str) -> PendingEdit {
    let (line_start, line_end) = selected_line_range(text, start, length);
    let selected = &text[line_start..line_end];
    let replacement = format!("{prefix}{}", selected.replace('\n', &format!("\n{prefix}")));
    let result = replace(text, line_start, line_end - line_start, &replacement);
    let caret_bytes = if length == 0 {
        (start + prefix.len()).min(line_start + replacement.len())
    } else {
        line_start + replacement.len()
    };
    PendingEdit {
        text: result,
        selection_start_bytes: caret_bytes,
        selection_length_bytes: 0,
        caret_bytes,
    }
}

fn apply_heading(text: &str, start: usize, length: usize, level: usize) -> PendingEdit {
    let (line_start, line_end) = selected_line_range(text, start, length);
    let selected = &text[line_start..line_end];
    let marker = format!("{} ", "#".repeat(level));

    let mut relative_caret = None;
    let mut replaced_lines = Vec::new();
    for (index, line) in selected.split('\n').enumerate() {
        let existing_marker_length = heading_marker_length(line);
        if index == 0 && length == 0 {
            relative_caret = Some(start.saturating_sub(line_start + existing_marker_length));
        }
        replaced_lines.push(format!("{marker}{}", &line[existing_marker_length..]));
    }
    let replacement = replaced_lines.join("\n");
    let result = replace(text, line_start, line_end - line_start, &replacement);
    let caret_bytes = if length == 0 {
        (line_start + marker.len() + relative_caret.unwrap_or(0))
            .min(line_start + replacement.len())
    } else {
        line_start + replacement.len()
    };
    PendingEdit {
        text: result,
        selection_start_bytes: caret_bytes,
        selection_length_bytes: 0,
        caret_bytes,
    }
}

fn apply_table_bytes(
    text: &str,
    start: usize,
    length: usize,
    row_count: usize,
    column_count: usize,
) -> PendingEdit {
    let header = table_row(
        (1..=column_count)
            .map(|index| format!("列 {index}"))
            .collect(),
    );
    let delimiter = table_row(std::iter::repeat_n("---".to_owned(), column_count).collect());
    let content = table_row(std::iter::repeat_n("内容".to_owned(), column_count).collect());
    let mut lines = vec![header, delimiter];
    lines.extend(std::iter::repeat_n(content, row_count - 1));
    insert_block(text, start, length, &lines.join("\n"))
}

fn insert_block(text: &str, start: usize, length: usize, block: &str) -> PendingEdit {
    let leading_break = if start > 0 && !text[..start].ends_with('\n') {
        "\n"
    } else {
        ""
    };
    let original_end = start + length;
    let followed_by_line_break = text[original_end..].starts_with('\n');
    let trailing_break = if followed_by_line_break { "" } else { "\n" };
    let replacement = format!("{leading_break}{block}{trailing_break}");
    let result = replace(text, start, length, &replacement);
    let caret_bytes = start + replacement.len() + if followed_by_line_break { 1 } else { 0 };
    PendingEdit {
        text: result,
        selection_start_bytes: caret_bytes,
        selection_length_bytes: 0,
        caret_bytes,
    }
}

fn insert_separator(text: &str, start: usize, length: usize) -> PendingEdit {
    let prefix = &text[..start];
    let suffix = &text[start + length..];
    let line_break = if text.contains("\r\n") { "\r\n" } else { "\n" };
    let leading_count = if prefix.is_empty() {
        0
    } else {
        trailing_line_break_count(prefix, line_break)
    };
    let trailing_count = leading_line_break_count(suffix, line_break);
    let leading = if prefix.is_empty() {
        String::new()
    } else {
        line_break.repeat(2usize.saturating_sub(leading_count))
    };
    let trailing = line_break.repeat(2usize.saturating_sub(trailing_count));
    let replacement = format!("{leading}---{trailing}");
    let result = replace(text, start, length, &replacement);
    let caret_bytes = start + replacement.len();
    PendingEdit {
        text: result,
        selection_start_bytes: caret_bytes,
        selection_length_bytes: 0,
        caret_bytes,
    }
}

fn with_utf16_offsets(edit: PendingEdit) -> MarkdownTextEdit {
    let PendingEdit {
        text: result,
        selection_start_bytes,
        selection_length_bytes,
        caret_bytes,
    } = edit;
    let selection_start = byte_to_utf16_offset(&result, selection_start_bytes);
    let selection_end =
        byte_to_utf16_offset(&result, selection_start_bytes + selection_length_bytes);
    let caret_offset = byte_to_utf16_offset(&result, caret_bytes);
    MarkdownTextEdit {
        text: result,
        selection_start,
        selection_length: selection_end - selection_start,
        caret_offset,
    }
}

fn replace_and_select(
    text: &str,
    start: usize,
    length: usize,
    replacement: &str,
    relative_start: usize,
    relative_length: usize,
) -> PendingEdit {
    let result = replace(text, start, length, replacement);
    PendingEdit {
        text: result,
        selection_start_bytes: start + relative_start,
        selection_length_bytes: relative_length,
        caret_bytes: start + relative_start + relative_length,
    }
}

fn replace(text: &str, start: usize, length: usize, replacement: &str) -> String {
    let mut result = String::with_capacity(text.len() + replacement.len());
    result.push_str(&text[..start]);
    result.push_str(replacement);
    result.push_str(&text[start + length..]);
    result
}

fn is_exact_wrapped_selection(
    text: &str,
    start: usize,
    length: usize,
    prefix: &str,
    suffix: &str,
) -> bool {
    if length < prefix.len() + suffix.len() {
        return false;
    }
    let selection = &text[start..start + length];
    if !selection.starts_with(prefix) || !selection.ends_with(suffix) {
        return false;
    }

    // Selecting **bold** must not be reduced to *bold* when the italic action runs.
    prefix != "*"
        || length == 2
        || selection
            .as_bytes()
            .get(1)
            .is_none_or(|value| *value != b'*')
        || selection
            .as_bytes()
            .get(selection.len().saturating_sub(2))
            .is_none_or(|value| *value != b'*')
}

fn is_surrounded(text: &str, start: usize, length: usize, prefix: &str, suffix: &str) -> bool {
    if start < prefix.len() || start + length + suffix.len() > text.len() {
        return false;
    }
    if !text[..start].ends_with(prefix) || !text[start + length..].starts_with(suffix) {
        return false;
    }
    if prefix != "*" {
        return true;
    }

    let extra_opening = start > prefix.len() && text.as_bytes()[start - prefix.len() - 1] == b'*';
    let closing = start + length + suffix.len();
    let extra_closing = closing < text.len() && text.as_bytes()[closing] == b'*';
    !extra_opening && !extra_closing
}

fn selected_line_range(text: &str, start: usize, length: usize) -> (usize, usize) {
    let line_start = text[..start].rfind('\n').map_or(0, |index| index + 1);
    let selected_end = start + length;
    let line_end = text[selected_end..]
        .find('\n')
        .map_or(text.len(), |index| selected_end + index);
    (line_start, line_end)
}

fn heading_marker_length(line: &str) -> usize {
    let hash_count = line.bytes().take_while(|value| *value == b'#').count();
    if (1..=6).contains(&hash_count) && line.as_bytes().get(hash_count) == Some(&b' ') {
        hash_count + 1
    } else {
        0
    }
}

fn table_row(cells: Vec<String>) -> String {
    format!("| {} |", cells.join(" | "))
}

fn trailing_line_break_count(value: &str, line_break: &str) -> usize {
    let mut count = 0;
    let mut remaining = value;
    while let Some(prefix) = remaining.strip_suffix(line_break) {
        count += 1;
        remaining = prefix;
    }
    count
}

fn leading_line_break_count(value: &str, line_break: &str) -> usize {
    let mut count = 0;
    let mut remaining = value;
    while let Some(suffix) = remaining.strip_prefix(line_break) {
        count += 1;
        remaining = suffix;
    }
    count
}

fn utf16_selection_to_bytes(
    text: &str,
    start_utf16: usize,
    length_utf16: usize,
) -> CoreResult<(usize, usize)> {
    let start = utf16_to_byte_offset(text, start_utf16)?;
    let end_utf16 = start_utf16
        .checked_add(length_utf16)
        .ok_or_else(|| CoreError::InvalidSelection("选区范围溢出。".to_owned()))?;
    let end = utf16_to_byte_offset(text, end_utf16)?;
    Ok((start, end - start))
}

fn utf16_to_byte_offset(text: &str, target_utf16: usize) -> CoreResult<usize> {
    if target_utf16 == 0 {
        return Ok(0);
    }

    let mut units = 0usize;
    for (byte_offset, character) in text.char_indices() {
        if units == target_utf16 {
            return Ok(byte_offset);
        }
        units += character.len_utf16();
        if units > target_utf16 {
            return Err(CoreError::InvalidSelection(
                "选区落在一个 UTF-16 代理对中间。".to_owned(),
            ));
        }
    }
    if units == target_utf16 {
        Ok(text.len())
    } else {
        Err(CoreError::InvalidSelection(format!(
            "选区超出文本范围：{target_utf16}"
        )))
    }
}

fn byte_to_utf16_offset(text: &str, target_byte: usize) -> usize {
    text[..target_byte.min(text.len())].encode_utf16().count()
}

#[derive(Debug)]
struct PendingEdit {
    text: String,
    selection_start_bytes: usize,
    selection_length_bytes: usize,
    caret_bytes: usize,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn bold_wrap_uses_a_placeholder_and_selects_it() {
        let edit = apply_format("", 0, 0, "bold").unwrap();

        assert_eq!(edit.text, "**粗体文本**");
        assert_eq!(edit.selection_start, 2);
        assert_eq!(edit.selection_length, 4);
    }

    #[test]
    fn repeating_bold_removes_markers_and_preserves_selection() {
        let first = apply_format("hello", 0, 5, "bold").unwrap();

        let second = apply_format(
            &first.text,
            first.selection_start,
            first.selection_length,
            "bold",
        )
        .unwrap();

        assert_eq!(second.text, "hello");
        assert_eq!(second.selection_start, 0);
        assert_eq!(second.selection_length, 5);
    }

    #[test]
    fn italic_does_not_reduce_bold_markers() {
        let edit = apply_format("**正文**", 0, 6, "italic").unwrap();

        assert_eq!(edit.text, "***正文***");
    }

    #[test]
    fn heading_removes_an_existing_heading_marker() {
        let edit = apply_format("## 旧标题", 0, 0, "h3").unwrap();

        assert_eq!(edit.text, "### 旧标题");
    }

    #[test]
    fn utf16_selection_supports_emoji_and_chinese() {
        let text = "前😀后";
        let edit = apply_format(text, 1, 2, "bold").unwrap();

        assert_eq!(edit.text, "前**😀**后");
        assert_eq!(edit.selection_start, 3);
        assert_eq!(edit.selection_length, 2);
    }

    #[test]
    fn separator_keeps_blank_lines_around_content() {
        let edit = apply_format("上文下文", 2, 0, "separator").unwrap();

        assert_eq!(edit.text, "上文\n\n---\n\n下文");
    }

    #[test]
    fn table_uses_requested_dimensions_and_places_caret_after_it() {
        let edit = apply_table("", 0, 0, 2, 2).unwrap();

        assert_eq!(
            edit.text,
            "| 列 1 | 列 2 |\n| --- | --- |\n| 内容 | 内容 |\n"
        );
        assert_eq!(edit.caret_offset, edit.text.encode_utf16().count());
    }
}
