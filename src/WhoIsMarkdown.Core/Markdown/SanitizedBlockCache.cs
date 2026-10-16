namespace WhoIsMarkdown.Core.Markdown;

/// <summary>
/// A renderer-local LRU of raw-to-allowlisted HTML, bounded by both entries and
/// UTF-16 characters (keys included). No file paths, policies or DOM nodes are held.
/// Only sanitizer output is stored; image rewriting is performed on every request.
/// </summary>
internal sealed class SanitizedBlockCache
{
    private readonly int maximumEntries;
    private readonly int maximumCharacters;
    private readonly Dictionary<string, LinkedListNode<Entry>> entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> recent = new();
    private int characters;

    public SanitizedBlockCache(int maximumEntries = 16384, int maximumCharacters = 16 * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);
        this.maximumEntries = maximumEntries;
        this.maximumCharacters = maximumCharacters;
    }

    public string GetOrAdd(string html, Func<string, string> sanitize)
    {
        if (entries.TryGetValue(html, out LinkedListNode<Entry>? found))
        {
            recent.Remove(found);
            recent.AddLast(found);
            return found.Value.SafeHtml;
        }

        string safe = sanitize(html);
        long size = (long)html.Length + safe.Length;
        if (size > maximumCharacters) return safe;
        while (entries.Count >= maximumEntries || characters + size > maximumCharacters)
        {
            LinkedListNode<Entry> oldest = recent.First!;
            characters -= oldest.Value.Html.Length + oldest.Value.SafeHtml.Length;
            entries.Remove(oldest.Value.Html);
            recent.RemoveFirst();
        }

        LinkedListNode<Entry> added = recent.AddLast(new Entry(html, safe));
        entries.Add(html, added);
        characters += (int)size;
        return safe;
    }

    private sealed record Entry(string Html, string SafeHtml);
}
