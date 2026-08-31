namespace WhoIsMarkdown.App.Services;

/// <summary>
/// Builds the host-owned Mermaid preview script. The Mermaid runtime is loaded
/// from WIMD's embedded resources, so diagrams remain available offline.
/// </summary>
internal static class MermaidPreviewScript
{
    private const int MaximumLibraryLength = 8 * 1024 * 1024;

    private const string IntegrationScript = """
        ;(() => {
          const mermaidSelector = 'main.preview-document pre.mermaid, main.preview-document pre > code.language-mermaid';
          const maximumDiagramCount = 64;
          const maximumSourceLength = 50000;
          let renderQueue = Promise.resolve();
          let nextDiagramId = 1;

          // Security note: Mermaid is configured by the trusted host only. Theme
          // directives and other security-sensitive options are locked so Markdown
          // content cannot override the strict rendering boundary.
          mermaid.initialize({
            startOnLoad: false,
            securityLevel: 'strict',
            suppressErrorRendering: true,
            maxTextSize: maximumSourceLength,
            maxEdges: 500,
            htmlLabels: false,
            deterministicIds: true,
            deterministicIDSeed: 'wimd',
            logLevel: 'fatal',
            theme: 'base',
            themeVariables: {
              background: 'transparent',
              primaryColor: '#f1f3f8',
              primaryTextColor: '#20283a',
              primaryBorderColor: '#8a93a8',
              lineColor: '#596175',
              secondaryColor: '#eef0fa',
              tertiaryColor: '#f8f9fc',
              fontFamily: 'Segoe UI, Microsoft YaHei UI, sans-serif'
            },
            secure: [
              'secure',
              'securityLevel',
              'startOnLoad',
              'maxTextSize',
              'suppressErrorRendering',
              'maxEdges',
              'theme',
              'themeCSS',
              'themeVariables',
              'fontFamily',
              'altFontFamily',
              'htmlLabels'
            ]
          });

          const copySourceAnchor = (sourceBlock, target) => {
            const anchor = sourceBlock.id?.startsWith('pragma-line-')
              ? sourceBlock.id
              : sourceBlock.querySelector('[id^="pragma-line-"]')?.id;
            if (anchor) target.id = anchor;
          };

          const compactErrorMessage = error => {
            const raw = error instanceof Error ? error.message : String(error || '未知错误');
            const firstLine = raw.split(/\r?\n/, 1)[0].trim();
            return firstLine.slice(0, 180) || '图表语法无法解析。';
          };

          const createErrorSurface = (sourceBlock, source, message) => {
            const surface = document.createElement('section');
            surface.className = 'wimd-mermaid-error';
            surface.setAttribute('role', 'note');
            copySourceAnchor(sourceBlock, surface);

            const title = document.createElement('strong');
            title.textContent = 'Mermaid 图表语法有误';
            const description = document.createElement('p');
            description.textContent = message;
            const details = document.createElement('details');
            const summary = document.createElement('summary');
            summary.textContent = '查看图表源码';
            const pre = document.createElement('pre');
            const code = document.createElement('code');
            code.className = 'language-text wimd-mermaid-source';
            code.textContent = source;
            pre.append(code);
            details.append(summary, pre);
            surface.append(title, description, details);
            return surface;
          };

          const hasUnsafeCss = value => {
            const css = String(value || '').toLowerCase();
            if (/@import|expression\s*\(|javascript\s*:|data\s*:|https?\s*:/.test(css)) return true;
            return [...css.matchAll(/url\s*\(([^)]*)\)/g)]
              .some(match => !match[1].trim().replace(/^['"]|['"]$/g, '').startsWith('#'));
          };

          const sanitizeSvg = svgMarkup => {
            const template = document.createElement('template');
            template.innerHTML = svgMarkup;
            // Bug fix: Chromium does not consistently resolve :scope against a
            // DocumentFragment. Inspect the fragment's direct first element instead
            // so a valid Mermaid SVG is not rejected as an empty result.
            const svg = template.content.firstElementChild;
            if (!(svg instanceof SVGSVGElement) || template.content.childElementCount !== 1) {
              throw new Error('渲染结果不是有效的 SVG。');
            }

            // Bug fix/security boundary: never insert Mermaid's inline SVG into the
            // preview DOM. Strip active/external content first, then display the SVG
            // through an image data URL so its CSS cannot affect the Markdown page.
            svg.querySelectorAll('script, foreignObject, iframe, object, embed, image, audio, video, link, meta')
              .forEach(element => element.remove());
            svg.querySelectorAll('*').forEach(element => {
              [...element.attributes].forEach(attribute => {
                const name = attribute.name.toLowerCase();
                const value = attribute.value.trim();
                if (name.startsWith('on') || name === 'src') {
                  element.removeAttribute(attribute.name);
                } else if ((name === 'href' || name === 'xlink:href') && !value.startsWith('#')) {
                  element.removeAttribute(attribute.name);
                } else if (name === 'style' && hasUnsafeCss(value)) {
                  element.removeAttribute(attribute.name);
                }
              });
            });
            svg.querySelectorAll('style').forEach(style => {
              if (hasUnsafeCss(style.textContent)) style.remove();
            });
            svg.setAttribute('xmlns', 'http://www.w3.org/2000/svg');
            svg.setAttribute('role', 'img');
            svg.removeAttribute('height');
            return svg.outerHTML;
          };

          const toSvgDataUri = svgMarkup => {
            const bytes = new TextEncoder().encode(svgMarkup);
            let binary = '';
            for (let offset = 0; offset < bytes.length; offset += 32768) {
              binary += String.fromCharCode(...bytes.subarray(offset, offset + 32768));
            }
            return `data:image/svg+xml;base64,${btoa(binary)}`;
          };

          const renderBlock = async sourceElement => {
            const sourceBlock = sourceElement instanceof HTMLPreElement
              ? sourceElement
              : sourceElement.parentElement;
            if (!(sourceBlock instanceof HTMLPreElement) || sourceBlock.dataset.wimdMermaid === 'done') return;
            sourceBlock.dataset.wimdMermaid = 'pending';
            const source = (sourceElement.textContent || '').replace(/\r?\n$/, '');
            if (!source.trim() || source.length > maximumSourceLength) {
              sourceBlock.replaceWith(createErrorSurface(
                sourceBlock,
                source,
                source.length > maximumSourceLength ? '图表源码超过 50,000 个字符。' : '图表源码为空。'));
              return;
            }

            try {
              const renderId = `wimd-mermaid-${nextDiagramId++}`;
              const result = await mermaid.render(renderId, source);
              const safeSvg = sanitizeSvg(result.svg);
              const figure = document.createElement('figure');
              figure.className = 'wimd-mermaid-diagram';
              copySourceAnchor(sourceBlock, figure);
              const surface = document.createElement('div');
              surface.className = 'wimd-mermaid-surface';
              const image = document.createElement('img');
              image.className = 'wimd-mermaid-image';
              image.alt = 'Mermaid 图表';
              image.draggable = false;
              image.decoding = 'async';
              image.dataset.wimdGeneratedDiagram = 'true';
              image.src = toSvgDataUri(safeSvg);
              surface.append(image);
              figure.append(surface);
              sourceBlock.replaceWith(figure);
            } catch (error) {
              sourceBlock.replaceWith(createErrorSurface(
                sourceBlock,
                source,
                compactErrorMessage(error)));
            }
          };

          const renderAll = async () => {
            const blocks = [...document.querySelectorAll(mermaidSelector)];
            if (blocks.length > maximumDiagramCount) {
              blocks.slice(maximumDiagramCount).forEach(sourceElement => {
                const sourceBlock = sourceElement instanceof HTMLPreElement
                  ? sourceElement
                  : sourceElement.parentElement;
                if (sourceBlock instanceof HTMLPreElement) {
                  sourceBlock.replaceWith(createErrorSurface(
                    sourceBlock,
                    sourceElement.textContent || '',
                    `单篇文档最多渲染 ${maximumDiagramCount} 个 Mermaid 图表。`));
                }
              });
            }

            for (const sourceElement of blocks.slice(0, maximumDiagramCount)) {
              await renderBlock(sourceElement);
            }
            document.dispatchEvent(new CustomEvent('wimd:mermaid-rendered'));
          };

          const queueRender = () => {
            renderQueue = renderQueue.then(renderAll, renderAll);
            return renderQueue;
          };

          window.wimdMermaid = Object.freeze({
            renderAll: queueRender,
            whenIdle: () => renderQueue
          });
          document.addEventListener('DOMContentLoaded', queueRender, { once: true });
          document.addEventListener('wimd:preview-updated', queueRender);
        })();
        """;

    public static string Build(string libraryScript)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryScript);
        if (libraryScript.Length > MaximumLibraryLength)
        {
            throw new ArgumentException("Mermaid 运行库大小异常。", nameof(libraryScript));
        }

        return string.Concat(libraryScript, Environment.NewLine, IntegrationScript);
    }
}
