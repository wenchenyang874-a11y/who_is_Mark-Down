using System.Windows.Input;
using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.App.Shortcuts;

internal sealed record ShortcutCommandDefinition(
    string Id,
    string DisplayName,
    ShortcutGesture DefaultGesture);

/// <summary>
/// Provides the single source of truth for shortcut defaults, parsing, display,
/// conflict checks, and defensive recovery from malformed persisted overrides.
/// </summary>
internal static class ShortcutCatalog
{
    public static IReadOnlyList<ShortcutCommandDefinition> Definitions { get; } =
    [
        Define("file.new", "新建文档", Key.N, control: true),
        Define("file.open", "打开文档", Key.O, control: true),
        Define("file.save", "保存文档", Key.S, control: true),
        Define("file.save-as", "另存为", Key.S, control: true, shift: true),
        Define("view.cycle", "循环切换视图", Key.F9),
        Define("heading.1", "一级标题", Key.D1, control: true),
        Define("heading.2", "二级标题", Key.D2, control: true),
        Define("heading.3", "三级标题", Key.D3, control: true),
        Define("heading.4", "四级标题", Key.D4, control: true),
        Define("heading.5", "五级标题", Key.D5, control: true),
        Define("heading.6", "六级标题", Key.D6, control: true),
        Define("format.bold", "粗体", Key.B, control: true),
        Define("format.italic", "斜体", Key.I, control: true),
        // Ctrl+Shift+X avoids the IME interception commonly seen with Ctrl+backtick
        // while remaining familiar to users of other Markdown-oriented editors.
        Define("format.strike", "删除线", Key.X, control: true, shift: true),
        Define("format.inline-code", "行内代码", Key.E, control: true),
        Define("format.code-block", "代码块", Key.K, control: true, shift: true),
        Define("format.link", "链接", Key.K, control: true),
        Define("format.image", "图片", Key.I, control: true, shift: true),
        Define("format.ordered-list", "有序列表", Key.D7, control: true, shift: true),
        Define("format.unordered-list", "无序列表", Key.D8, control: true, shift: true),
        Define("format.quote", "引用", Key.D9, control: true, shift: true),
        Define("format.task-list", "任务列表", Key.L, control: true, shift: true),
    ];

    private static Dictionary<string, ShortcutCommandDefinition> DefinitionsById { get; }
        = Definitions.ToDictionary(item => item.Id, StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, ShortcutGesture> CreateDefaultAssignments() =>
        Definitions.ToDictionary(item => item.Id, item => item.DefaultGesture, StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, ShortcutGesture> ResolveAssignments(
        IReadOnlyDictionary<string, ShortcutGesture> persistedOverrides)
    {
        Dictionary<string, ShortcutGesture> resolved =
            new(CreateDefaultAssignments(), StringComparer.Ordinal);
        HashSet<string> appliedOverrides = new(StringComparer.Ordinal);

        foreach ((string commandId, ShortcutGesture gesture) in persistedOverrides)
        {
            if (!DefinitionsById.ContainsKey(commandId)
                || !TryParseKey(gesture, out _)
                || !TryValidateGesture(gesture, out _))
            {
                continue;
            }

            resolved[commandId] = gesture.Normalize();
            appliedOverrides.Add(commandId);
        }

        // Corrupted or hand-edited settings must not create ambiguous runtime
        // dispatch. Revert only conflicting overrides, iterating in case a revert
        // exposes a second conflict with another override.
        while (true)
        {
            IReadOnlyList<IReadOnlyList<string>> conflicts =
                ShortcutConflictDetector.FindConflicts(resolved);
            if (conflicts.Count == 0)
            {
                break;
            }

            bool revertedAny = false;
            foreach (string commandId in conflicts.SelectMany(item => item).Distinct())
            {
                if (appliedOverrides.Remove(commandId))
                {
                    resolved[commandId] = DefinitionsById[commandId].DefaultGesture;
                    revertedAny = true;
                }
            }

            if (!revertedAny)
            {
                break;
            }
        }

        return resolved;
    }

    public static IReadOnlyDictionary<string, ShortcutGesture> CreateOverrides(
        IReadOnlyDictionary<string, ShortcutGesture> assignments)
    {
        Dictionary<string, ShortcutGesture> overrides = new(StringComparer.Ordinal);
        foreach (ShortcutCommandDefinition definition in Definitions)
        {
            if (assignments.TryGetValue(definition.Id, out ShortcutGesture? gesture)
                && gesture.GetIdentity() != definition.DefaultGesture.GetIdentity())
            {
                overrides[definition.Id] = gesture.Normalize();
            }
        }

        return overrides;
    }

    public static ShortcutGesture FromInput(Key key, ModifierKeys modifiers) => new()
    {
        Key = key.ToString(),
        Control = modifiers.HasFlag(ModifierKeys.Control),
        Shift = modifiers.HasFlag(ModifierKeys.Shift),
        Alt = modifiers.HasFlag(ModifierKeys.Alt),
    };

    public static bool TryParseKey(ShortcutGesture gesture, out Key key) =>
        Enum.TryParse(gesture.Key, ignoreCase: true, out key)
        && key is not Key.None
        && !IsModifierKey(key);

    public static bool TryValidateGesture(ShortcutGesture gesture, out string error)
    {
        if (!TryParseKey(gesture, out Key key))
        {
            error = "请选择一个非修饰键。";
            return false;
        }

        if (!gesture.Control && !gesture.Alt && key is < Key.F1 or > Key.F24)
        {
            error = "字母、数字和编辑键必须搭配 Ctrl 或 Alt，避免影响正常输入。";
            return false;
        }

        if (IsReservedGesture(gesture, key))
        {
            error = $"{FormatGesture(gesture)} 已被编辑器或系统保留。";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static string FormatGesture(ShortcutGesture gesture)
    {
        List<string> parts = [];
        if (gesture.Control)
        {
            parts.Add("Ctrl");
        }

        if (gesture.Shift)
        {
            parts.Add("Shift");
        }

        if (gesture.Alt)
        {
            parts.Add("Alt");
        }

        parts.Add(FormatKey(gesture.Key));
        return string.Join('+', parts);
    }

    public static string GetIdentity(ShortcutGesture gesture) => gesture.GetIdentity();

    private static ShortcutCommandDefinition Define(
        string id,
        string displayName,
        Key key,
        bool control = false,
        bool shift = false,
        bool alt = false) =>
        new(
            id,
            displayName,
            new ShortcutGesture
            {
                Key = key.ToString(),
                Control = control,
                Shift = shift,
                Alt = alt,
            });

    private static bool IsReservedGesture(ShortcutGesture gesture, Key key)
    {
        if (gesture.Alt && !gesture.Control && !gesture.Shift
            && key is Key.F4 or Key.F or Key.E or Key.V or Key.H or Key.K)
        {
            return true;
        }

        if (gesture.Control && !gesture.Alt && !gesture.Shift
            && key is Key.A or Key.C or Key.V or Key.X or Key.Y or Key.Z or Key.Insert)
        {
            return true;
        }

        return gesture.Shift && !gesture.Control && !gesture.Alt
            && key is Key.Insert or Key.Delete;
    }

    private static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin
            or Key.System;

    private static string FormatKey(string key) => key switch
    {
        nameof(Key.D0) => "0",
        nameof(Key.D1) => "1",
        nameof(Key.D2) => "2",
        nameof(Key.D3) => "3",
        nameof(Key.D4) => "4",
        nameof(Key.D5) => "5",
        nameof(Key.D6) => "6",
        nameof(Key.D7) => "7",
        nameof(Key.D8) => "8",
        nameof(Key.D9) => "9",
        nameof(Key.OemTilde) or nameof(Key.Oem3) => "`",
        nameof(Key.OemPlus) => "+",
        nameof(Key.OemMinus) => "-",
        nameof(Key.Return) => "Enter",
        _ => key,
    };
}
