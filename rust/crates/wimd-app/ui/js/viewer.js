import { formatError, getCurrentWindow, invoke, listen } from "./api.js";
import { ImageViewer } from "./image-viewer.js";

const dom = {
  root: document.getElementById("image-viewer"),
  stage: document.getElementById("image-viewer-stage"),
  image: document.getElementById("image-viewer-content"),
  title: document.getElementById("image-viewer-title"),
  status: document.getElementById("image-viewer-status"),
  format: document.getElementById("image-save-format"),
};

let currentRequestId = 0;

const viewer = new ImageViewer({
  root: dom.root,
  stage: dom.stage,
  image: dom.image,
  title: dom.title,
  onSave: saveImage,
  onClose: () => {
    const appWindow = getCurrentWindow();
    void appWindow?.close();
  },
});

function applyPayload(payload) {
  if (!payload?.source || payload.requestId < currentRequestId) return;
  currentRequestId = payload.requestId;
  document.documentElement.dataset.theme = payload.theme || "system";
  dom.status.textContent = `${payload.width || "?"} × ${payload.height || "?"}`;
  viewer.open(payload.source, payload.title || "图片查看");
  document.title = `${payload.title || "图片"} - WIMD`;
}

async function initialize() {
  dom.image.addEventListener("load", () => {
    dom.status.textContent = `${dom.image.naturalWidth} × ${dom.image.naturalHeight}`;
  });
  dom.image.addEventListener("error", () => {
    dom.status.textContent = "图片载入失败";
  });
  try {
    await listen("wimd://viewer-image", (event) => applyPayload(event.payload));
    const payload = await invoke("get_viewer_image");
    if (payload) applyPayload(payload);
  } catch (error) {
    dom.status.textContent = `图片查看器初始化失败：${formatError(error)}`;
  }
}

async function saveImage(source, title, format) {
  try {
    const prepared = await invoke("prepare_preview_image", { source });
    const baseName =
      String(title || "image")
        .replace(/\.(?:png|jpe?g|gif|bmp|webp|svg)$/i, "")
        .replace(/[<>:"/\\|?*\x00-\x1f]/g, "_")
        .slice(0, 80) || "image";
    const extension =
      format === "png" ? "png" : format === "html" ? "html" : prepared.extension;
    const destination = await invoke("pick_preview_image_save_path", {
      suggestedName: `${baseName}.${extension}`,
      format,
    });
    if (!destination) return;
    const dataUri =
      format === "png" ? await convertDataUriToPng(prepared.dataUri) : prepared.dataUri;
    const savedPath = await invoke("save_preview_image", {
      dataUri,
      format,
      target: destination,
    });
    dom.status.textContent = `已保存：${savedPath}`;
  } catch (error) {
    dom.status.textContent = `图片另存失败：${formatError(error)}`;
  }
}

async function convertDataUriToPng(dataUri) {
  const image = await new Promise((resolve, reject) => {
    const element = new Image();
    element.onload = () => resolve(element);
    element.onerror = () => reject(new Error("无法解码待转换的图片。"));
    element.src = dataUri;
  });
  const maximumDimension = 4096;
  const scale = Math.min(
    1,
    maximumDimension / Math.max(1, image.naturalWidth),
    maximumDimension / Math.max(1, image.naturalHeight),
  );
  const canvas = document.createElement("canvas");
  canvas.width = Math.max(1, Math.round(image.naturalWidth * scale));
  canvas.height = Math.max(1, Math.round(image.naturalHeight * scale));
  const context = canvas.getContext("2d", { alpha: true });
  if (!context) throw new Error("无法创建 PNG 转换画布。");
  context.drawImage(image, 0, 0, canvas.width, canvas.height);
  return canvas.toDataURL("image/png");
}

void initialize();
