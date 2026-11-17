use std::{
    borrow::Cow,
    collections::{HashMap, HashSet},
    sync::OnceLock,
};

use ammonia::{Builder, UrlRelative};
use pulldown_cmark::{CowStr, Event, HeadingLevel, Options, Parser, Tag, TagEnd, html::push_html};
use regex::Regex;
use serde::Serialize;

use crate::task::find_task_lines;

const EMPTY_TASK_SENTINEL: &str = "\u{e000}WIMD_EMPTY_TASK\u{e000}";

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RenderedHeading {
    pub level: u8,
    pub id: String,
    pub text: String,
    pub source_line: usize,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RenderedMarkdown {
    pub html: String,
    pub headings: Vec<RenderedHeading>,
    pub task_count: usize,
    pub source_line_count: usize,
}

pub fn render_markdown(markdown: &str) -> RenderedMarkdown {
    let task_lines = find_task_lines(markdown);
    let prepared_markdown = prepare_empty_task_items(markdown, &task_lines);
    let options = Options::ENABLE_TABLES
        | Options::ENABLE_TASKLISTS
        | Options::ENABLE_STRIKETHROUGH
        | Options::ENABLE_FOOTNOTES
        | Options::ENABLE_GFM;
    let parser = Parser::new_ext(&prepared_markdown, options).into_offset_iter();
    let line_starts = line_start_offsets(&prepared_markdown);
    let mut groups = Vec::new();
    let mut depth = 0usize;
    let mut current: Option<SourceGroup> = None;

    for (event, range) in parser {
        let source_line = offset_to_line(&line_starts, range.start);
        match &event {
            Event::Start(_) => {
                if depth == 0 {
                    current = Some(SourceGroup {
                        source_line,
                        events: Vec::new(),
                    });
                }
                depth += 1;
                if let Some(group) = current.as_mut() {
                    group.events.push(event.into_static());
                }
            }
            Event::End(_) => {
                if let Some(group) = current.as_mut() {
                    group.events.push(event.into_static());
                }
                depth = depth.saturating_sub(1);
                if depth == 0
                    && let Some(group) = current.take()
                {
                    groups.push(group);
                }
            }
            _ => {
                if depth == 0 {
                    groups.push(SourceGroup {
                        source_line,
                        events: vec![event.into_static()],
                    });
                } else if let Some(group) = current.as_mut() {
                    group.events.push(event.into_static());
                }
            }
        }
    }
    if let Some(group) = current {
        groups.push(group);
    }

    let mut headings = Vec::new();
    for group in &groups {
        if let Some((level, text)) = heading_from_events(&group.events) {
            let index = headings.len() + 1;
            headings.push(RenderedHeading {
                level,
                id: format!("wimd-heading-{index}"),
                text,
                source_line: group.source_line,
            });
        }
    }
    let toc = build_toc(&headings);
    let mut task_index = 0usize;
    let mut heading_index = 0usize;
    let mut html = String::new();

    for group in groups {
        let heading = heading_from_events(&group.events);
        let mut events = Vec::with_capacity(group.events.len());
        for event in group.events {
            match event {
                Event::TaskListMarker(checked) => {
                    let state = if checked { '1' } else { '0' };
                    let placeholder = format!("\u{1f}WIMD_TASK_{task_index:08}_{state}\u{1f}");
                    events.push(Event::Text(CowStr::from(placeholder)));
                    task_index += 1;
                }
                value => events.push(value),
            }
        }

        let mut unsafe_html = String::new();
        push_html(&mut unsafe_html, events.into_iter());
        let mut safe_html = clean_html_fragment(&unsafe_html).replace(EMPTY_TASK_SENTINEL, "");

        if safe_html.trim() == "<p>[TOC]</p>" {
            safe_html = toc.clone();
        }

        if heading.is_some()
            && let Some(heading) = headings.get(heading_index)
        {
            safe_html = insert_heading_id(&safe_html, heading.level, &heading.id);
            heading_index += 1;
        }

        safe_html = inject_task_placeholders(&safe_html, &task_lines);
        html.push_str(&format!(
            "<section class=\"wimd-source-block\" data-source-line=\"{}\">{}</section>",
            group.source_line, safe_html
        ));
    }

    RenderedMarkdown {
        html,
        headings,
        task_count: task_index,
        source_line_count: markdown.split('\n').count(),
    }
}

fn prepare_empty_task_items<'a>(markdown: &'a str, task_lines: &[usize]) -> Cow<'a, str> {
    if task_lines.is_empty() {
        return Cow::Borrowed(markdown);
    }
    let mut relevant = task_lines.iter().copied().peekable();
    let mut output = String::with_capacity(markdown.len() + task_lines.len() * 16);
    let mut changed = false;
    for (line_index, segment) in markdown.split_inclusive('\n').enumerate() {
        let content = segment.strip_suffix('\n').unwrap_or(segment);
        let content = content.strip_suffix('\r').unwrap_or(content);
        let is_task = relevant.next_if_eq(&line_index).is_some();
        if is_task
            && crate::task::find_task_marker(content)
                .is_some_and(|marker| content[marker.end + 1..].trim().is_empty())
        {
            output.push_str(content);
            if !content.ends_with([' ', '\t']) {
                output.push(' ');
            }
            output.push_str(EMPTY_TASK_SENTINEL);
            output.push_str(&segment[content.len()..]);
            changed = true;
        } else {
            output.push_str(segment);
        }
    }
    if changed {
        Cow::Owned(output)
    } else {
        Cow::Borrowed(markdown)
    }
}

fn clean_html_fragment(html: &str) -> String {
    let tags = HashSet::from([
        "a",
        "abbr",
        "blockquote",
        "br",
        "code",
        "del",
        "details",
        "div",
        "dl",
        "dt",
        "dd",
        "em",
        "figcaption",
        "figure",
        "h1",
        "h2",
        "h3",
        "h4",
        "h5",
        "h6",
        "hr",
        "img",
        "ins",
        "kbd",
        "li",
        "mark",
        "ol",
        "p",
        "pre",
        "q",
        "s",
        "samp",
        "small",
        "span",
        "strong",
        "sub",
        "summary",
        "sup",
        "table",
        "tbody",
        "td",
        "tfoot",
        "th",
        "thead",
        "tr",
        "u",
        "ul",
        "var",
        "wbr",
    ]);
    let clean_content_tags = HashSet::from([
        "script", "style", "iframe", "object", "embed", "form", "template", "svg",
    ]);
    let generic_attributes = HashSet::from(["align", "class", "id", "title"]);
    let url_schemes = HashSet::from(["http", "https", "mailto", "data"]);
    let mut tag_attributes = HashMap::new();
    tag_attributes.insert("a", HashSet::from(["href"]));
    tag_attributes.insert(
        "img",
        HashSet::from(["src", "alt", "height", "width", "title"]),
    );
    tag_attributes.insert("details", HashSet::from(["open"]));
    tag_attributes.insert("ol", HashSet::from(["start"]));
    tag_attributes.insert(
        "td",
        HashSet::from(["colspan", "rowspan", "headers", "align", "width", "height"]),
    );
    tag_attributes.insert(
        "th",
        HashSet::from([
            "colspan", "rowspan", "headers", "scope", "align", "width", "height",
        ]),
    );
    tag_attributes.insert("table", HashSet::from(["align"]));
    tag_attributes.insert("div", HashSet::from(["align"]));
    tag_attributes.insert("p", HashSet::from(["align"]));

    let mut builder = Builder::empty();
    builder
        .tags(tags)
        .clean_content_tags(clean_content_tags)
        .generic_attributes(generic_attributes)
        .tag_attributes(tag_attributes)
        .url_schemes(url_schemes)
        .url_relative(UrlRelative::PassThrough)
        .link_rel(Some("noopener noreferrer"))
        .strip_comments(true)
        .attribute_filter(filter_attribute);
    builder.clean(html).to_string()
}

fn filter_attribute<'a>(element: &str, attribute: &str, value: &'a str) -> Option<Cow<'a, str>> {
    match (element, attribute) {
        ("a", "href") => is_safe_link(value).then_some(Cow::Borrowed(value)),
        ("img", "src") => is_safe_image_source(value).then_some(Cow::Borrowed(value)),
        (_, "class") => sanitize_classes(value),
        (_, "id") => is_safe_identifier(value).then_some(Cow::Borrowed(value)),
        (_, "title") if value.len() <= 512 && !value.chars().any(char::is_control) => {
            Some(Cow::Borrowed(value))
        }
        (_, "align") if matches!(value, "left" | "center" | "right" | "justify") => {
            Some(Cow::Borrowed(value))
        }
        (_, "width") if valid_dimension(value, true) => Some(Cow::Borrowed(value)),
        (_, "height") if valid_dimension(value, false) => Some(Cow::Borrowed(value)),
        (_, "colspan" | "rowspan") if valid_positive_integer(value, 100) => {
            Some(Cow::Borrowed(value))
        }
        (_, "start") if valid_positive_integer(value, i32::MAX as usize) => {
            Some(Cow::Borrowed(value))
        }
        (_, "scope") if matches!(value, "row" | "col" | "rowgroup" | "colgroup") => {
            Some(Cow::Borrowed(value))
        }
        (_, "headers" | "alt" | "open") => Some(Cow::Borrowed(value)),
        _ => Some(Cow::Borrowed(value)),
    }
}

fn sanitize_classes(value: &str) -> Option<Cow<'_, str>> {
    let normalized = value
        .split_ascii_whitespace()
        .filter(|class| {
            matches!(
                *class,
                "contains-task-list"
                    | "footnote-backref"
                    | "footnote-ref"
                    | "footnotes"
                    | "mermaid"
                    | "task-list-item"
                    | "wimd-toc"
            ) || class
                .strip_prefix("language-")
                .is_some_and(is_safe_language_class)
        })
        .collect::<Vec<_>>()
        .join(" ");
    if normalized.is_empty() {
        None
    } else if normalized == value {
        Some(Cow::Borrowed(value))
    } else {
        Some(Cow::Owned(normalized))
    }
}

fn is_safe_language_class(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 72
        && value
            .bytes()
            .all(|character| character.is_ascii_alphanumeric() || b"_+-#".contains(&character))
}

fn is_safe_identifier(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 128
        && value.bytes().all(|character| {
            character.is_ascii_alphanumeric() || matches!(character, b'-' | b'_' | b':' | b'.')
        })
}

fn is_safe_link(value: &str) -> bool {
    let value = value.trim();
    if value.is_empty() || value.chars().any(char::is_control) {
        return false;
    }
    let lower = value.to_ascii_lowercase();
    lower.starts_with("http://")
        || lower.starts_with("https://")
        || lower.starts_with("mailto:")
        || lower.starts_with('#')
        || is_relative_url(value)
}

fn is_safe_image_source(value: &str) -> bool {
    let value = value.trim();
    if value.is_empty() || value.chars().any(char::is_control) {
        return false;
    }
    let lower = value.to_ascii_lowercase();
    lower.starts_with("http://")
        || lower.starts_with("https://")
        || lower.starts_with("data:image/")
        || is_relative_url(value)
}

fn is_relative_url(value: &str) -> bool {
    !value.starts_with('/')
        && !value.starts_with('\\')
        && !value.contains(':')
        && !value.starts_with("..\\")
        && !value.starts_with("../")
}

fn valid_dimension(value: &str, allow_percentage: bool) -> bool {
    if let Some(number) = value.strip_suffix('%') {
        return allow_percentage
            && number
                .parse::<u16>()
                .is_ok_and(|number| (1..=100).contains(&number));
    }
    value
        .parse::<u16>()
        .is_ok_and(|number| (1..=4096).contains(&number))
}

fn valid_positive_integer(value: &str, maximum: usize) -> bool {
    value
        .parse::<usize>()
        .is_ok_and(|number| (1..=maximum).contains(&number))
}

fn heading_from_events(events: &[Event<'static>]) -> Option<(u8, String)> {
    let Event::Start(Tag::Heading { level, .. }) = events.first()? else {
        return None;
    };
    let mut text = String::new();
    for event in &events[1..] {
        match event {
            Event::Text(value) | Event::Code(value) => text.push_str(value),
            Event::SoftBreak | Event::HardBreak => text.push(' '),
            Event::End(TagEnd::Heading(_)) => break,
            _ => {}
        }
    }
    let text = collapse_whitespace(&text);
    Some((heading_level_number(*level), text))
}

fn heading_level_number(level: HeadingLevel) -> u8 {
    level as u8
}

fn collapse_whitespace(value: &str) -> String {
    value.split_whitespace().collect::<Vec<_>>().join(" ")
}

fn build_toc(headings: &[RenderedHeading]) -> String {
    if headings.is_empty() {
        return "<nav class=\"wimd-toc\" aria-label=\"文档目录\"><p>当前文档没有标题。</p></nav>"
            .to_owned();
    }
    let mut html = String::from("<nav class=\"wimd-toc\" aria-label=\"文档目录\"><p>目录</p><ul>");
    for heading in headings {
        html.push_str(&format!(
            "<li class=\"wimd-toc-level-{}\"><a href=\"#{}\">{}</a></li>",
            heading.level,
            escape_html(&heading.id),
            escape_html(&heading.text)
        ));
    }
    html.push_str("</ul></nav>");
    html
}

fn insert_heading_id(html: &str, level: u8, id: &str) -> String {
    let open = format!("<h{level}>");
    if html.contains(&open) {
        return html.replacen(&open, &format!("<h{level} id=\"{id}\">"), 1);
    }

    // An author-provided heading id wins. No regex is applied to untrusted HTML;
    // this branch only operates on the sanitized renderer output.
    if let Ok(regex) = Regex::new(&format!(r#"<h{level}\s+id="([^"]+)""#))
        && let Some(captures) = regex.captures(html)
    {
        return html.replace(&captures[1], id);
    }
    html.to_owned()
}

fn inject_task_placeholders(html: &str, task_lines: &[usize]) -> String {
    static PLACEHOLDER: OnceLock<Regex> = OnceLock::new();
    let regex = PLACEHOLDER.get_or_init(|| {
        Regex::new(r"\u{1f}WIMD_TASK_([0-9]{8})_([01])\u{1f}")
            .expect("task placeholder regex is valid")
    });
    regex
        .replace_all(html, |captures: &regex::Captures<'_>| {
            let index = captures[1]
                .parse::<usize>()
                .unwrap_or(usize::MAX);
            let Some(line) = task_lines.get(index) else {
                return String::from("☐");
            };
            let checked = &captures[2] == "1";
            format!(
                "<input type=\"checkbox\" data-task-line=\"{line}\"{} aria-label=\"切换任务状态\" />",
                if checked { " checked" } else { "" }
            )
        })
        .into_owned()
}

fn escape_html(value: &str) -> String {
    value
        .replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
        .replace('"', "&quot;")
}

fn line_start_offsets(markdown: &str) -> Vec<usize> {
    let mut starts = vec![0usize];
    for (index, byte) in markdown.bytes().enumerate() {
        if byte == b'\n' {
            starts.push(index + 1);
        }
    }
    starts
}

fn offset_to_line(line_starts: &[usize], offset: usize) -> usize {
    line_starts
        .partition_point(|start| *start <= offset)
        .saturating_sub(1)
}

#[derive(Debug)]
struct SourceGroup {
    source_line: usize,
    events: Vec<Event<'static>>,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn renders_common_markdown_and_source_sections() {
        let rendered = render_markdown("# 标题\n\n| A | B |\n|---|---|\n| 1 | 2 |\n");

        assert!(rendered.html.contains("<h1"));
        assert!(rendered.html.contains("<table>"));
        assert!(rendered.html.contains("data-source-line=\"0\""));
        assert_eq!(rendered.headings[0].text, "标题");
    }

    #[test]
    fn strips_scripts_events_styles_and_dangerous_links() {
        let rendered = render_markdown(
            r#"<script>alert(1)</script>
<img src="javascript:alert(1)" onerror="alert(2)">
<a href="javascript:alert(3)" style="color:red">bad</a>
<div onclick="alert(4)">safe text</div>"#,
        );

        assert!(!rendered.html.contains("<script"));
        assert!(!rendered.html.contains("onerror"));
        assert!(!rendered.html.contains("onclick"));
        assert!(!rendered.html.contains("javascript:"));
        assert!(!rendered.html.contains("style="));
        assert!(rendered.html.contains("safe text"));
    }

    #[test]
    fn keeps_remote_and_local_image_sources_for_host_loading() {
        let rendered =
            render_markdown("![remote](https://example.com/a.svg)\n\n![local](./img/a.png)\n");

        assert!(rendered.html.contains("https://example.com/a.svg"));
        assert!(rendered.html.contains("./img/a.png"));
    }

    #[test]
    fn generates_toc_and_unique_heading_ids() {
        let rendered = render_markdown("[TOC]\n\n# 标题\n\n## 子标题\n\n# 标题\n");

        assert_eq!(rendered.headings.len(), 3);
        assert_eq!(rendered.headings[0].id, "wimd-heading-1");
        assert_eq!(rendered.headings[2].id, "wimd-heading-3");
        assert!(rendered.html.contains("wimd-toc"));
    }

    #[test]
    fn task_markers_are_host_generated_with_source_lines() {
        let rendered = render_markdown("- [ ] 第一项\n- [x] 第二项\n");

        assert_eq!(rendered.task_count, 2);
        assert!(rendered.html.contains("data-task-line=\"0\""));
        assert!(rendered.html.contains("data-task-line=\"1\""));
        assert!(!rendered.html.contains("<input disabled"));
    }

    #[test]
    fn empty_task_items_render_as_host_checkboxes() {
        let rendered = render_markdown("- [x]\r\n> - [ ]\r\n");

        assert_eq!(rendered.task_count, 2);
        assert!(rendered.html.contains("data-task-line=\"0\""));
        assert!(rendered.html.contains("data-task-line=\"1\""));
        assert!(rendered.html.contains("checked"));
        assert!(!rendered.html.contains(EMPTY_TASK_SENTINEL));
        assert!(!rendered.html.contains("[x]"));
    }
}
