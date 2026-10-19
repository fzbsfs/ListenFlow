(function (global) {
  "use strict";

  class ListenFlowHighlighter {
    constructor(snapshot) {
      this.snapshot = snapshot;
      this.ranges = [];
      this.overlays = [];
      this.usesCustomHighlight = Boolean(global.CSS?.highlights && typeof global.Highlight === "function");
      this.refreshOverlay = () => this.renderOverlay();
      this.usesOverlay = false;
      addEventListener("scroll", this.refreshOverlay, true);
      addEventListener("resize", this.refreshOverlay, true);
    }

    set(start, length) {
      this.ranges = global.ListenFlowDomSnapshot.createRanges(this.snapshot, start, length);
      if (this.usesCustomHighlight) {
        global.CSS.highlights.set("listenflow-current-sentence", new global.Highlight(...this.ranges));
        this.usesOverlay = !this.hasVisibleCustomStyle();
      } else {
        this.usesOverlay = true;
      }
      if (this.usesOverlay)
        this.renderOverlay();
      else
        this.removeOverlays();
      this.scrollIntoView();
    }

    clear() {
      if (this.usesCustomHighlight)
        global.CSS.highlights.delete("listenflow-current-sentence");
      this.removeOverlays();
      removeEventListener("scroll", this.refreshOverlay, true);
      removeEventListener("resize", this.refreshOverlay, true);
      this.ranges = [];
      this.usesOverlay = false;
    }

    scrollIntoView() {
      const rect = this.ranges[0]?.getBoundingClientRect();
      if (!rect || (rect.top >= 80 && rect.bottom <= innerHeight - 60)) return;
      const node = this.ranges[0]?.startContainer?.parentElement;
      node?.scrollIntoView({ block: "center", behavior: "smooth" });
    }

    renderOverlay() {
      if (!this.usesOverlay) return;
      this.removeOverlays();
      for (const range of this.ranges) {
        for (const rect of range.getClientRects()) {
          if (rect.width <= 0 || rect.height <= 0) continue;
          const overlay = document.createElement("div");
          overlay.className = "listenflow-highlight-overlay";
          overlay.style.left = `${rect.left}px`;
          overlay.style.top = `${rect.top}px`;
          overlay.style.width = `${rect.width}px`;
          overlay.style.height = `${rect.height}px`;
          document.documentElement.appendChild(overlay);
          this.overlays.push(overlay);
        }
      }
    }

    hasVisibleCustomStyle() {
      const element = this.ranges[0]?.startContainer?.parentElement;
      if (!element) return false;
      try {
        const style = getComputedStyle(element, "::highlight(listenflow-current-sentence)");
        const background = style?.backgroundColor ?? "";
        return background !== "" && background !== "transparent" && background !== "rgba(0, 0, 0, 0)";
      } catch {
        return false;
      }
    }

    removeOverlays() {
      for (const overlay of this.overlays) overlay.remove();
      this.overlays = [];
    }
  }

  global.ListenFlowHighlighter = ListenFlowHighlighter;
})(globalThis);
