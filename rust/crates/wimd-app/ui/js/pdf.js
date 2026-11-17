import { invoke } from "./api.js";

let completed = false;

async function reportReady(error = null) {
  if (completed) return;
  completed = true;
  await invoke("mark_pdf_window_ready", { error }).catch(() => {});
}

async function waitForImages() {
  const images = [...document.querySelectorAll("img")];
  if (!images.length) return;
  await Promise.race([
    Promise.all(
      images.map(
        (image) =>
          new Promise((resolve) => {
            if (image.complete) {
              resolve();
              return;
            }
            const finish = () => {
              image.removeEventListener("load", finish);
              image.removeEventListener("error", finish);
              resolve();
            };
            image.addEventListener("load", finish, { once: true });
            image.addEventListener("error", finish, { once: true });
          }),
      ),
    ),
    new Promise((resolve) => window.setTimeout(resolve, 10000)),
  ]);
}

async function initialize() {
  try {
    const payload = window.__WIMD_PDF_PAYLOAD__;
    if (!payload?.html || !payload?.css) {
      throw new Error("PDF 内容未传入。");
    }

    document.documentElement.dataset.theme = payload.theme || "light";
    document.documentElement.style.cssText = payload.inlineStyle || "";
    document.getElementById("wimd-pdf-style").textContent = payload.css;

    const main = document.getElementById("preview-document");
    main.innerHTML = payload.html;
    main
      .querySelectorAll(".heading-fold-button, .code-copy-button")
      .forEach((node) => node.remove());
    main
      .querySelectorAll(".wimd-folded")
      .forEach((node) => node.classList.remove("wimd-folded"));
    main
      .querySelectorAll('input[type="checkbox"][data-task-line]')
      .forEach((input) => {
        input.disabled = true;
      });

    await Promise.race([
      document.fonts?.ready || Promise.resolve(),
      new Promise((resolve) => window.setTimeout(resolve, 5000)),
    ]);
    await waitForImages();
    await new Promise((resolve) => window.setTimeout(resolve, 80));
    await reportReady();
  } catch (error) {
    await reportReady(String(error?.message || error));
  }
}

void initialize();
