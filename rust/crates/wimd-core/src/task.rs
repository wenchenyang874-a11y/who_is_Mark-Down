use std::ops::Range;

use serde::Serialize;

use crate::error::{CoreError, CoreResult};

#[derive(Clone, Debug, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TaskToggle {
    pub text: String,
    pub changed: bool,
}

/// The preview cannot submit arbitrary source edits. The host accepts only the
/// zero-based line and target boolean, then re-parses the current source line with
/// this narrow grammar before changing exactly one `[ ]`/`[x]` character.
pub fn toggle_task_line(
    text: &str,
    zero_based_line: usize,
    completed: bool,
) -> CoreResult<TaskToggle> {
    let (line_start, line_end) = line_range(text, zero_based_line)?;
    let line = &text[line_start..line_end];
    let marker = find_task_marker(line).ok_or(CoreError::InvalidTaskLine {
        line: zero_based_line,
    })?;
    let current_checked =
        line.as_bytes()[marker.start] == b'x' || line.as_bytes()[marker.start] == b'X';
    let next_character = if completed { b'x' } else { b' ' };

    if current_checked == completed {
        return Ok(TaskToggle {
            text: text.to_owned(),
            changed: false,
        });
    }

    let mut result = String::with_capacity(text.len());
    result.push_str(&text[..line_start + marker.start]);
    result.push(next_character as char);
    result.push_str(&text[line_start + marker.end..]);
    Ok(TaskToggle {
        text: result,
        changed: true,
    })
}

pub fn find_task_lines(text: &str) -> Vec<usize> {
    text.split('\n')
        .enumerate()
        .filter_map(|(line, value)| {
            let value = value.strip_suffix('\r').unwrap_or(value);
            find_task_marker(value).map(|_| line)
        })
        .collect()
}

pub fn find_task_marker(line: &str) -> Option<Range<usize>> {
    let bytes = line.as_bytes();
    let mut index = 0usize;
    skip_spaces(bytes, &mut index);

    let mut quote_depth = 0usize;
    while bytes.get(index) == Some(&b'>') {
        quote_depth += 1;
        if quote_depth > 32 {
            return None;
        }
        index += 1;
        if bytes.get(index) == Some(&b' ') {
            index += 1;
        }
        skip_spaces(bytes, &mut index);
    }

    if bytes.get(index) == Some(&b'-')
        || bytes.get(index) == Some(&b'*')
        || bytes.get(index) == Some(&b'+')
    {
        index += 1;
        if !is_list_separator(bytes, index) {
            return None;
        }
        index += 1;
    } else {
        let digit_start = index;
        while index < bytes.len() && bytes[index].is_ascii_digit() && index - digit_start < 9 {
            index += 1;
        }
        if index == digit_start
            || !matches!(bytes.get(index), Some(b'.') | Some(b')'))
            || !is_list_separator(bytes, index + 1)
        {
            return None;
        }
        index += 2;
    }

    skip_spaces(bytes, &mut index);
    if bytes.get(index) != Some(&b'[') || bytes.get(index + 2) != Some(&b']') {
        return None;
    }
    let state = *bytes.get(index + 1)?;
    if !matches!(state, b' ' | b'x' | b'X') {
        return None;
    }
    if !matches!(bytes.get(index + 3), None | Some(b' ') | Some(b'\t')) {
        return None;
    }

    Some(index + 1..index + 2)
}

pub fn source_line_for_offset(text: &str, byte_offset: usize) -> usize {
    text.as_bytes()[..byte_offset.min(text.len())]
        .iter()
        .filter(|value| **value == b'\n')
        .count()
}

fn line_range(text: &str, line: usize) -> CoreResult<(usize, usize)> {
    let mut start = 0usize;
    for _ in 0..line {
        let Some(relative_end) = text[start..].find('\n') else {
            return Err(CoreError::InvalidTaskLine { line });
        };
        start += relative_end + 1;
    }
    let absolute_end = text[start..]
        .find('\n')
        .map_or(text.len(), |relative_end| start + relative_end);
    let content_end = if absolute_end > start && text.as_bytes()[absolute_end - 1] == b'\r' {
        absolute_end - 1
    } else {
        absolute_end
    };
    Ok((start, content_end))
}

fn skip_spaces(bytes: &[u8], index: &mut usize) {
    while matches!(bytes.get(*index), Some(b' ') | Some(b'\t')) {
        *index += 1;
    }
}

fn is_list_separator(bytes: &[u8], index: usize) -> bool {
    matches!(bytes.get(index), Some(b' ') | Some(b'\t'))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn finds_nested_blockquote_task() {
        let marker = find_task_marker("> > 1. [X] 已完成").unwrap();
        let line = "> > 1. [X] 已完成";
        assert_eq!(&line[marker], "X");
    }

    #[test]
    fn rejects_non_task_checkbox_and_invalid_state() {
        assert!(find_task_marker("- [?] 未知").is_none());
        assert!(find_task_marker("- [ ]缺少空格").is_none());
        assert!(find_task_marker("普通 [ ] 文本").is_none());
    }

    #[test]
    fn toggles_only_the_selected_source_line() {
        let text = "> - [ ] 第一项\r\n- [x] 第二项\r\n";

        let first = toggle_task_line(text, 0, true).unwrap();
        assert_eq!(first.text, "> - [x] 第一项\r\n- [x] 第二项\r\n");
        assert!(first.changed);

        let second = toggle_task_line(&first.text, 1, false).unwrap();
        assert_eq!(second.text, "> - [x] 第一项\r\n- [ ] 第二项\r\n");
    }

    #[test]
    fn matching_target_returns_an_unchanged_result() {
        let result = toggle_task_line("- [ ] 任务", 0, false).unwrap();
        assert!(!result.changed);
        assert_eq!(result.text, "- [ ] 任务");
    }

    #[test]
    fn reports_all_task_line_numbers_in_source_order() {
        let text = "- [ ] a\n\n> 1. [x] b\nplain\n";
        assert_eq!(find_task_lines(text), vec![0, 2]);
    }
}
