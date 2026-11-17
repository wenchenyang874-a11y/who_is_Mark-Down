export class ImageViewer {
  constructor({ root, stage, image, title, onSave, onClose }) {
    this.root = root;
    this.stage = stage;
    this.image = image;
    this.title = title;
    this.onSave = onSave;
    this.onClose = onClose;
    this.format = root.querySelector("#image-save-format");
    this.currentSource = "";
    this.currentTitle = "图片查看";
    this.saving = false;
    this.scale = 1;
    this.x = 0;
    this.y = 0;
    this.naturalWidth = 0;
    this.naturalHeight = 0;
    this.dragging = false;
    this.dragStart = null;
    this.visible = false;

    this.image.addEventListener("load", () => {
      this.naturalWidth = this.image.naturalWidth || 1;
      this.naturalHeight = this.image.naturalHeight || 1;
      if (this.visible) this.fit();
    });
    this.root.addEventListener("click", (event) => {
      const action = event.target.closest("[data-image-action]")?.dataset.imageAction;
      if (!action) return;
      if (action === "close") this.close();
      if (action === "fit") this.fit();
      if (action === "actual") this.actualSize();
      if (action === "zoom-in") this.zoomBy(1.25);
      if (action === "zoom-out") this.zoomBy(0.8);
      if (action === "save") void this.save();
    });
    this.stage.addEventListener(
      "wheel",
      (event) => {
        event.preventDefault();
        const factor = Math.exp(-event.deltaY * 0.0015);
        this.zoomAt(event.clientX, event.clientY, factor);
      },
      { passive: false },
    );
    this.stage.addEventListener("pointerdown", (event) => {
      if (event.button !== 0) return;
      this.dragging = true;
      this.dragStart = {
        clientX: event.clientX,
        clientY: event.clientY,
        x: this.x,
        y: this.y,
      };
      this.stage.classList.add("dragging");
      this.stage.setPointerCapture(event.pointerId);
    });
    this.stage.addEventListener("pointermove", (event) => {
      if (!this.dragging || !this.dragStart) return;
      this.x = this.dragStart.x + event.clientX - this.dragStart.clientX;
      this.y = this.dragStart.y + event.clientY - this.dragStart.clientY;
      this.applyTransform();
    });
    const endDrag = (event) => {
      if (!this.dragging) return;
      this.dragging = false;
      this.dragStart = null;
      this.stage.classList.remove("dragging");
      if (this.stage.hasPointerCapture(event.pointerId)) {
        this.stage.releasePointerCapture(event.pointerId);
      }
    };
    this.stage.addEventListener("pointerup", endDrag);
    this.stage.addEventListener("pointercancel", endDrag);
    window.addEventListener("keydown", (event) => {
      if (!this.visible) return;
      if (event.key === "Escape") {
        event.preventDefault();
        this.close();
      }
    });
  }

  open(source, title = "图片查看") {
    this.visible = true;
    this.root.hidden = false;
    this.title.textContent = title;
    this.currentSource = source;
    this.currentTitle = title;
    this.image.alt = title;
    this.image.src = source;
    this.scale = 1;
    this.x = 0;
    this.y = 0;
    this.applyTransform();
    requestAnimationFrame(() => {
      if (this.image.complete) this.fit();
    });
  }

  async save() {
    if (this.saving || !this.currentSource) return;
    this.saving = true;
    try {
      await this.onSave?.(
        this.currentSource,
        this.currentTitle,
        this.format?.value || "original",
      );
    } finally {
      this.saving = false;
    }
  }

  close() {
    this.visible = false;
    this.root.hidden = true;
    this.image.removeAttribute("src");
    this.onClose?.();
  }

  fit() {
    const stageRect = this.stage.getBoundingClientRect();
    if (!stageRect.width || !stageRect.height) return;
    const scale = Math.min(
      stageRect.width / this.naturalWidth,
      stageRect.height / this.naturalHeight,
      1,
    );
    this.scale = clamp(scale, 0.05, 20);
    this.x = 0;
    this.y = 0;
    this.applyTransform();
  }

  actualSize() {
    this.scale = 1;
    this.x = 0;
    this.y = 0;
    this.applyTransform();
  }

  zoomBy(factor) {
    const rect = this.stage.getBoundingClientRect();
    this.zoomAt(rect.left + rect.width / 2, rect.top + rect.height / 2, factor);
  }

  zoomAt(clientX, clientY, factor) {
    const rect = this.stage.getBoundingClientRect();
    const centerX = rect.left + rect.width / 2;
    const centerY = rect.top + rect.height / 2;
    const oldScale = this.scale;
    const nextScale = clamp(oldScale * factor, 0.05, 20);
    if (nextScale === oldScale) return;
    const pointerX = clientX - centerX - this.x;
    const pointerY = clientY - centerY - this.y;
    const ratio = nextScale / oldScale;
    this.x -= pointerX * (ratio - 1);
    this.y -= pointerY * (ratio - 1);
    this.scale = nextScale;
    this.applyTransform();
  }

  applyTransform() {
    this.image.style.transform = `translate(calc(-50% + ${this.x}px), calc(-50% + ${this.y}px)) scale(${this.scale})`;
  }
}

function clamp(value, minimum, maximum) {
  return Math.min(maximum, Math.max(minimum, value));
}
