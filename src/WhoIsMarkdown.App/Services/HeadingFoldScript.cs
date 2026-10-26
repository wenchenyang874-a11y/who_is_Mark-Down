namespace WhoIsMarkdown.App.Services;

/// <summary>
/// Host-only heading interaction, not document-authored script. Keep semantic
/// block wrappers and source anchors intact so incremental patching still works.
/// A linear heading-stack walk handles sections spanning multiple wrappers or
/// sharing one raw-HTML container; no Range extraction or body reparenting.
/// </summary>
internal static class HeadingFoldScript
{
    public const string Script = """
        (() => {
          if (window.wimdHeadingFold) return;
          const headingSelector = 'h1,h2,h3,h4,h5,h6';
          const hiddenClass = 'wimd-heading-fold-hidden';
          const collapsed = new Set();
          const controls = new WeakMap();
          const trustedButtons = new WeakSet();
          let entries = [];
          let headingEntries = new WeakMap();
          let headingAncestors = new WeakSet();
          let hiddenNodes = new Set();
          let visibleOwners = new WeakMap();
          let queued = false;
          let printing = false;

          const clearHidden = () => {
            hiddenNodes.forEach(node => node.classList.remove(hiddenClass));
            hiddenNodes.clear();
            visibleOwners = new WeakMap();
          };
          const updateControl = entry => {
            const expanded = !collapsed.has(entry.key);
            entry.button.setAttribute('aria-expanded', String(expanded));
            entry.button.setAttribute('aria-label', expanded ? '收起标题内容' : '展开标题内容');
            entry.button.title = expanded ? '收起标题内容' : '展开标题内容';
          };
          const report = (started, userAction) => {
            const elapsedMs = performance.now() - started;
            // Fast edit refreshes are quiet; clicks and slow refreshes have numeric
            // diagnostics only. Never send heading text, HTML, paths or state keys.
            if (userAction || elapsedMs >= 32) window.chrome?.webview?.postMessage({
              type: 'heading-fold-state', elapsedMs, userAction,
              collapsedCount: collapsed.size, hiddenCount: hiddenNodes.size
            });
          };
          const apply = (userAction = false) => {
            const started = performance.now();
            const root = document.querySelector('main.preview-document');
            clearHidden();
            entries.forEach(updateControl);
            if (!root || printing) return;
            const stack = [];
            const hide = (node, owner) => {
              node.classList.add(hiddenClass);
              hiddenNodes.add(node);
              visibleOwners.set(node, owner.heading);
            };
            const visit = node => {
              if (node.nodeType === Node.TEXT_NODE) {
                const owner = stack.find(entry => collapsed.has(entry.key));
                if (owner && node.textContent.trim()) {
                  // Raw HTML may mix bare text and headings in the same container.
                  // Wrap only that text leaf; never move a semantic block or heading.
                  const span = document.createElement('span');
                  node.replaceWith(span);
                  span.append(node);
                  hide(span, owner);
                }
                return;
              }
              if (!(node instanceof Element)) return;
              const entry = headingEntries.get(node);
              if (entry) {
                while (stack.length && stack[stack.length - 1].level >= entry.level) stack.pop();
                const owner = stack.find(item => collapsed.has(item.key));
                if (owner) hide(node, owner);
                stack.push(entry);
                return;
              }
              // Display-contents wrappers must stay intact and must not become
              // ordinary print boxes when print CSS restores hidden content.
              if (headingAncestors.has(node) || node.classList.contains('wimd-preview-block')) {
                [...node.childNodes].forEach(visit);
                return;
              }
              const owner = stack.find(item => collapsed.has(item.key));
              if (owner) hide(node, owner);
            };
            [...root.childNodes].forEach(visit);
            report(started, userAction);
          };
          const prepare = () => {
            const root = document.querySelector('main.preview-document');
            const occurrences = new Map();
            const keys = new Set();
            headingEntries = new WeakMap();
            headingAncestors = new WeakSet();
            entries = root ? [...root.querySelectorAll(headingSelector)]
              .filter(heading => !heading.closest('.preview-empty-state'))
              .map(heading => {
                const level = Number(heading.tagName[1]);
                // Buttons have no text nodes, so labels/source anchors stay intact.
                // Include occurrence order to distinguish duplicate heading labels.
                const label = `${level}\0${heading.textContent.trim()}`;
                const occurrence = occurrences.get(label) || 0;
                occurrences.set(label, occurrence + 1);
                const key = `${label}\0${occurrence}`;
                keys.add(key);
                let button = controls.get(heading);
                if (!button || button.parentElement !== heading) {
                  button = document.createElement('button');
                  button.type = 'button';
                  button.className = 'wimd-heading-toggle';
                  trustedButtons.add(button);
                  controls.set(heading, button);
                  heading.append(button);
                }
                const entry = { heading, button, level, key };
                headingEntries.set(heading, entry);
                for (let parent = heading.parentElement; parent && parent !== root; parent = parent.parentElement)
                  headingAncestors.add(parent);
                return entry;
              }) : [];
            // Bound state to headings still in this document. Detached DOM and
            // deleted headings must not accumulate across an editing session.
            for (const key of collapsed) if (!keys.has(key)) collapsed.delete(key);
            const x = scrollX, y = scrollY;
            apply();
            if (!printing) scrollTo(x, y);
          };
          const queuePrepare = () => {
            if (queued) return;
            queued = true;
            // Let code-copy wrappers and other host controls finish first, before
            // deciding which complete nodes belong to a collapsed section.
            queueMicrotask(() => { queued = false; prepare(); });
          };
          document.addEventListener('click', event => {
            const button = event.target instanceof Element ? event.target.closest('button.wimd-heading-toggle') : null;
            if (!button || !trustedButtons.has(button)) return;
            const entry = headingEntries.get(button.parentElement);
            if (!entry) return;
            event.preventDefault();
            event.stopPropagation();
            const x = scrollX, y = scrollY, top = entry.heading.getBoundingClientRect().top;
            if (collapsed.has(entry.key)) collapsed.delete(entry.key); else collapsed.add(entry.key);
            apply(true);
            // Keep the clicked heading under the pointer; the browser may still
            // clamp at the end of a now-shorter document, which is unavoidable.
            scrollTo(x, y + entry.heading.getBoundingClientRect().top - top);
          }, true);
          document.addEventListener('DOMContentLoaded', queuePrepare, { once: true });
          document.addEventListener('wimd:preview-updated', queuePrepare);
          document.addEventListener('wimd:mermaid-rendered', queuePrepare);
          window.addEventListener('beforeprint', () => { printing = true; clearHidden(); });
          window.addEventListener('afterprint', () => { printing = false; apply(); });
          window.wimdHeadingFold = Object.freeze({
            reset() { collapsed.clear(); clearHidden(); entries.forEach(updateControl); },
            visibleTarget(element) {
              for (let node = element; node; node = node.parentElement) {
                const owner = visibleOwners.get(node);
                if (owner?.isConnected) return owner;
              }
              return element;
            }
          });
          if (document.readyState !== 'loading') queuePrepare();
        })();
        """;
}
