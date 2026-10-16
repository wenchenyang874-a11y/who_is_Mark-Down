namespace WhoIsMarkdown.App.Services;

/// <summary>
/// Host-only source index. Cache line-to-element references, never layout positions:
/// images, fonts and resizing can change geometry without changing source lines.
/// DOM replacement clears references immediately; the next lookup rebuilds once.
/// </summary>
internal static class SourceLineNavigationScript
{
    public const string Script = """
        (() => {
          let anchors = [];
          let dirty = true;
          const invalidate = () => { anchors = []; dirty = true; };
          const observer = new MutationObserver(invalidate);
          observer.observe(document, { subtree: true, childList: true, attributes: true, attributeFilter: ['id'] });
          document.addEventListener('wimd:preview-updated', invalidate);
          document.addEventListener('DOMContentLoaded', invalidate, { once: true });
          const rebuild = () => {
            anchors = [...document.querySelectorAll('main.preview-document [id^="pragma-line-"]')]
              .map(element => ({ element, line: /^pragma-line-(\d+)$/.exec(element.id)?.[1] }))
              .filter(item => item.line !== undefined)
              .map(item => ({ element: item.element, line: Number(item.line) }))
              .filter(item => Number.isSafeInteger(item.line))
              .sort((left, right) => left.line - right.line);
            dirty = false;
          };
          window.wimdSourceNavigation = Object.freeze({
            ensureLine(line) {
              // Flush synchronous DOM mutations too; observer callbacks run later.
              if (observer.takeRecords().length) invalidate();
              if (dirty) rebuild();
              if (!anchors.length) return;
              let low = 0, high = anchors.length;
              while (low < high) {
                const middle = low + Math.floor((high - low) / 2);
                if (anchors[middle].line <= line) low = middle + 1;
                else high = middle;
              }
              const target = anchors[Math.max(0, low - 1)].element;
              if (!target.isConnected) { invalidate(); return; }
              const height = Math.max(1, window.innerHeight);
              const top = target.getBoundingClientRect().top;
              const start = height * 0.25, end = height * 0.75;
              if (top >= start && top <= end) return;
              window.scrollTo(0, Math.max(0, top + window.scrollY - (top < start ? start : end)));
            }
          });
        })();
        """;
}
