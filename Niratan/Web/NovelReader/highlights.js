//
//  highlights.js - Niratan Reader Windows
//  Reader highlight rendering only; persistence stays in C# services.
//

(function () {
  const HIGHLIGHT_COLORS = ["yellow", "green", "blue", "pink", "purple"];
  const MATCHABLE_CHARACTER = /[0-9A-Za-z○◯々-〇〻ぁ-ゖゝ-ゞァ-ヺー０-９Ａ-Ｚａ-ｚｦ-ﾝ\p{Radical}\p{Unified_Ideograph}]/u;
  const MAX_HIGHLIGHT_TEXT_LENGTH = 1_048_576;

  function ensureStyle() {
    if (document.getElementById("niratan-reader-highlight-style")) return;

    const style = document.createElement("style");
    style.id = "niratan-reader-highlight-style";
    style.textContent = [
      ".niratan-highlight { border-radius: 2px; box-decoration-break: clone; -webkit-box-decoration-break: clone; }",
      ".niratan-highlight-yellow { background-color: rgba(239, 209, 56, 0.35) !important; }",
      ".niratan-highlight-green { background-color: rgba(152, 220, 129, 0.35) !important; }",
      ".niratan-highlight-blue { background-color: rgba(149, 185, 255, 0.35) !important; }",
      ".niratan-highlight-pink { background-color: rgba(255, 155, 180, 0.35) !important; }",
      ".niratan-highlight-purple { background-color: rgba(197, 175, 251, 0.35) !important; }",
      "::highlight(niratan-highlight-yellow) { background-color: rgba(239, 209, 56, 0.35); color: inherit; }",
      "::highlight(niratan-highlight-green) { background-color: rgba(152, 220, 129, 0.35); color: inherit; }",
      "::highlight(niratan-highlight-blue) { background-color: rgba(149, 185, 255, 0.35); color: inherit; }",
      "::highlight(niratan-highlight-pink) { background-color: rgba(255, 155, 180, 0.35); color: inherit; }",
      "::highlight(niratan-highlight-purple) { background-color: rgba(197, 175, 251, 0.35); color: inherit; }",
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
    document.dispatchEvent(new Event("niratan-reader-content-changed"));
  }

  function createHighlightWrapper(id, color) {
    const wrapper = document.createElement("span");
    wrapper.className = `niratan-highlight niratan-highlight-${color}`;
    wrapper.dataset.niratanHighlightId = id;
    return wrapper;
  }

  function rubyAncestor(node) {
    const element = node.nodeType === Node.TEXT_NODE ? node.parentElement : node;
    return element?.closest("ruby") || null;
  }

  function supportsCssHighlights() {
    return typeof CSS !== "undefined" &&
      !!CSS.highlights &&
      typeof Highlight !== "undefined";
  }

  function normalizeColor(color) {
    const normalized = String(color || "yellow").toLowerCase();
    return HIGHLIGHT_COLORS.includes(normalized) ? normalized : "yellow";
  }

  function cssHighlightName(color) {
    return `niratan-highlight-${color}`;
  }

  function countNormalizedCharacters(text) {
    let count = 0;
    for (const character of Array.from(text || "")) {
      if (MATCHABLE_CHARACTER.test(character)) count += 1;
    }
    return count;
  }

  function selectionBoundaryOffset(targetNode, targetOffset, normalized) {
    if (!targetNode || targetNode.nodeType !== Node.TEXT_NODE) return null;

    const walker = visibleTextWalker();
    let offset = 0;
    let node;
    while ((node = walker.nextNode())) {
      const text = node.textContent || "";
      if (node === targetNode) {
        const prefix = text.slice(0, Math.max(0, Math.min(targetOffset, text.length)));
        return offset + (normalized
          ? countNormalizedCharacters(prefix)
          : Array.from(prefix).length);
      }
      offset += normalized
        ? countNormalizedCharacters(text)
        : Array.from(text).length;
    }
    return null;
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

  window.niratanHighlights = {
    wrappers: new Map(),
    cssEntries: new Map(),

    createHighlight(color, id) {
      const selection = window.getSelection?.();
      if (!selection || selection.rangeCount !== 1 || selection.isCollapsed) return null;

      const range = selection.getRangeAt(0);
      const start = selectionBoundaryOffset(
        range.startContainer,
        range.startOffset,
        true
      );
      const rawStart = selectionBoundaryOffset(
        range.startContainer,
        range.startOffset,
        false
      );
      const rawEnd = selectionBoundaryOffset(
        range.endContainer,
        range.endOffset,
        false
      );
      if (start === null || rawStart === null || rawEnd === null || rawEnd <= rawStart) {
        return null;
      }

      const fragment = range.cloneContents();
      fragment.querySelectorAll?.("rt, rp").forEach(element => element.remove());
      const text = fragment.textContent || "";
      const textLength = Array.from(text).length;
      if (textLength <= 0 || textLength > MAX_HIGHLIGHT_TEXT_LENGTH) return null;

      selection.removeAllRanges();
      window.niratanSelection?.clearSelection?.();
      this.wrapHighlight({ id: String(id), color, offset: rawStart, text });
      notifyContentChanged();
      return { start, offset: rawStart, text };
    },

    rebuildCssHighlight(color) {
      if (!supportsCssHighlights()) return;

      const name = cssHighlightName(color);
      CSS.highlights.get(name)?.clear();
      const ranges = [];
      for (const entry of this.cssEntries.values()) {
        if (entry.color === color) ranges.push(...entry.ranges);
      }
      if (ranges.length) CSS.highlights.set(name, new Highlight(...ranges));
    },

    clearCssHighlights() {
      if (supportsCssHighlights()) {
        for (const color of HIGHLIGHT_COLORS) {
          CSS.highlights.get(cssHighlightName(color))?.clear();
        }
      }
      this.cssEntries.clear();
    },

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

    wrapHighlight(highlight, deferCssRebuild) {
      if (!highlight || !highlight.id || !highlight.text) return;

      const id = String(highlight.id);
      this.removeHighlight(id);
      ensureStyle();

      const color = normalizeColor(highlight.color);
      const segments = this.collectSegments(
        highlight.offset,
        Array.from(String(highlight.text)).length
      );
      if (!segments.length) return;

      if (supportsCssHighlights()) {
        const ranges = segments.map(segment => {
          const range = document.createRange();
          range.setStart(segment.node, segment.start);
          range.setEnd(segment.node, segment.end);
          return range;
        });
        this.cssEntries.set(id, { color, ranges });
        if (!deferCssRebuild) this.rebuildCssHighlight(color);
        return;
      }

      const range = document.createRange();
      const wrappers = [];
      const targets = [];
      let previousRuby = null;
      for (const segment of segments) {
        const ruby = rubyAncestor(segment.node);
        if (ruby) {
          // Keep the ruby formatting context intact. Inserting a span between
          // <ruby> and its base text makes Chromium position or clip <rt>
          // incorrectly in vertical writing mode.
          if (ruby !== previousRuby) targets.push({ ruby });
          previousRuby = ruby;
          continue;
        }

        previousRuby = null;
        targets.push({ segment });
      }

      for (let i = targets.length - 1; i >= 0; i--) {
        const target = targets[i];
        const wrapper = createHighlightWrapper(id, color);
        if (target.ruby) {
          const parent = target.ruby.parentNode;
          if (!parent) continue;
          parent.insertBefore(wrapper, target.ruby);
          wrapper.appendChild(target.ruby);
          wrappers.push(wrapper);
          continue;
        }

        const segment = target.segment;
        range.setStart(segment.node, segment.start);
        range.setEnd(segment.node, segment.end);
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
      this.clearCssHighlights();

      if (Array.isArray(highlights)) {
        for (const highlight of highlights) this.wrapHighlight(highlight, true);
      }

      if (supportsCssHighlights()) {
        for (const color of HIGHLIGHT_COLORS) this.rebuildCssHighlight(color);
      }

      notifyContentChanged();
    },

    removeHighlight(id) {
      const key = String(id);
      const cssEntry = this.cssEntries.get(key);
      if (cssEntry) {
        this.cssEntries.delete(key);
        this.rebuildCssHighlight(cssEntry.color);
        notifyContentChanged();
        return;
      }

      const wrappers = this.wrappers.get(key);
      if (!wrappers) return;

      unwrap(wrappers);
      this.wrappers.delete(key);
      notifyContentChanged();
    },
  };
})();
