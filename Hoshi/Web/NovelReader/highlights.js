//
//  highlights.js - Hoshi Reader Windows
//  Reader highlight rendering only; persistence stays in C# services.
//

(function () {
  function ensureStyle() {
    if (document.getElementById("hoshi-reader-highlight-style")) return;

    const style = document.createElement("style");
    style.id = "hoshi-reader-highlight-style";
    style.textContent = [
      ".hoshi-highlight { border-radius: 2px; box-decoration-break: clone; -webkit-box-decoration-break: clone; }",
      ".hoshi-highlight-yellow { background-color: rgba(239, 209, 56, 0.35) !important; }",
      ".hoshi-highlight-green { background-color: rgba(152, 220, 129, 0.35) !important; }",
      ".hoshi-highlight-blue { background-color: rgba(149, 185, 255, 0.35) !important; }",
      ".hoshi-highlight-pink { background-color: rgba(255, 155, 180, 0.35) !important; }",
      ".hoshi-highlight-purple { background-color: rgba(197, 175, 251, 0.35) !important; }",
    ].join("\n");
    document.head.appendChild(style);
  }

  function visibleTextWalker() {
    return document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, {
      acceptNode: (node) => {
        const element = node.nodeType === Node.TEXT_NODE ? node.parentElement : node;
        return element?.closest("rt, rp")
          ? NodeFilter.FILTER_REJECT
          : NodeFilter.FILTER_ACCEPT;
      },
    });
  }

  function notifyContentChanged() {
    document.dispatchEvent(new Event("hoshi-reader-content-changed"));
  }

  function unwrap(nodes) {
    for (const span of nodes) {
      const parent = span.parentNode;
      if (!parent) continue;
      while (span.firstChild) parent.insertBefore(span.firstChild, span);
      parent.removeChild(span);
      parent.normalize();
    }
  }

  window.hoshiHighlights = {
    wrappers: new Map(),

    collectSegments(offset, length) {
      const start = Math.max(0, Number(offset) || 0);
      const end = start + Math.max(0, Number(length) || 0);
      const segments = [];
      let cursor = 0;
      let segment = null;

      const flushSegment = () => {
        if (!segment) return;
        segments.push(segment);
        segment = null;
      };

      const walker = visibleTextWalker();
      let node;
      while (cursor < end && (node = walker.nextNode())) {
        const text = node.textContent || "";
        let i = 0;
        while (i < text.length && cursor < end) {
          const char = String.fromCodePoint(text.codePointAt(i));
          const next = i + char.length;
          if (cursor >= start) {
            if (!segment || segment.node !== node) {
              flushSegment();
              segment = { node, start: i, end: next };
            } else {
              segment.end = next;
            }
          }
          cursor += 1;
          i = next;
        }
        flushSegment();
      }

      return segments;
    },

    wrapHighlight(highlight) {
      if (!highlight || !highlight.id || !highlight.text) return;

      const id = String(highlight.id);
      this.removeHighlight(id);
      ensureStyle();

      const color = String(highlight.color || "yellow").toLowerCase();
      const segments = this.collectSegments(
        highlight.offset,
        Array.from(String(highlight.text)).length
      );
      if (!segments.length) return;

      const range = document.createRange();
      const wrappers = [];
      for (let i = segments.length - 1; i >= 0; i--) {
        const segment = segments[i];
        range.setStart(segment.node, segment.start);
        range.setEnd(segment.node, segment.end);

        const wrapper = document.createElement("span");
        wrapper.className = `hoshi-highlight hoshi-highlight-${color}`;
        wrapper.dataset.hoshiHighlightId = id;
        wrapper.appendChild(range.extractContents());
        range.insertNode(wrapper);
        wrappers.push(wrapper);
      }

      wrappers.reverse();
      this.wrappers.set(id, wrappers);
    },

    applyHighlights(highlights) {
      ensureStyle();
      for (const wrappers of this.wrappers.values()) unwrap(wrappers);
      this.wrappers.clear();

      if (Array.isArray(highlights)) {
        for (const highlight of highlights) this.wrapHighlight(highlight);
      }

      notifyContentChanged();
    },

    removeHighlight(id) {
      const key = String(id);
      const wrappers = this.wrappers.get(key);
      if (!wrappers) return;

      unwrap(wrappers);
      this.wrappers.delete(key);
      notifyContentChanged();
    },
  };
})();
