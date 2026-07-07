(function () {
  const JAPANESE_RANGES = [
    [0x3040, 0x309f],
    [0x30a0, 0x30ff],
    [0x3400, 0x4dbf],
    [0x4e00, 0x9fff],
    [0xf900, 0xfaff],
    [0xff66, 0xff9f],
    [0x30fb, 0x30fc],
    [0xff61, 0xff65],
    [0x3000, 0x303f],
    [0xff10, 0xff19],
    [0xff21, 0xff3a],
    [0xff41, 0xff5a],
    [0xff01, 0xff0f],
    [0xff1a, 0xff1f],
    [0xff3b, 0xff3f],
    [0xff5b, 0xff60],
    [0xffe0, 0xffee],
  ];

  const state = {
    text: '',
    scanLength: 64,
    scanNonJapaneseText: true,
    selection: null,
    lastShiftHoverKey: '',
    lastHoverPoint: null,
  };

  const root = document.getElementById('subtitle-root');
  const textElement = document.getElementById('subtitle-text');
  const highlightStyle = document.createElement('style');
  document.head.appendChild(highlightStyle);

  function postToHost(type, payload) {
    window.chrome?.webview?.postMessage({
      version: 1,
      type,
      payload: payload || {},
    });
  }

  function isCodePointJapanese(codePoint) {
    return JAPANESE_RANGES.some(([start, end]) => codePoint >= start && codePoint <= end);
  }

  const selection = {
    scanDelimiters: '。、！？…‥「」『』（）()【】〈〉《》〔〕｛｝{}［］[]・：；:;，,.─\n\r"\'“”‘’',
    sentenceDelimiters: '。！？.!?\n\r',
    trailingSentenceChars: '。、！？…‥」』）)】〉》〕｝}］]',

    isScanBoundary(char) {
      return /^[\s　]$/.test(char) ||
        this.scanDelimiters.includes(char) ||
        (state.scanNonJapaneseText === false && !isCodePointJapanese(char.codePointAt(0)));
    },

    findContainer(node) {
      const element = node.nodeType === Node.TEXT_NODE ? node.parentElement : node;
      return element?.closest('#subtitle-text') || textElement;
    },

    createWalker(rootNode) {
      return document.createTreeWalker(rootNode || textElement, NodeFilter.SHOW_TEXT);
    },

    inCharRange(charRange, x, y) {
      const rects = charRange.getClientRects();
      if (rects.length) {
        for (const rect of rects) {
          if (x >= rect.left && x <= rect.right && y >= rect.top && y <= rect.bottom) {
            return true;
          }
        }
        return false;
      }

      const rect = charRange.getBoundingClientRect();
      return x >= rect.left && x <= rect.right && y >= rect.top && y <= rect.bottom;
    },

    getCaretRange(x, y) {
      if (document.caretPositionFromPoint) {
        const pos = document.caretPositionFromPoint(x, y);
        if (!pos) return null;
        const range = document.createRange();
        range.setStart(pos.offsetNode, pos.offset);
        range.collapse(true);
        return range;
      }

      const element = document.elementFromPoint(x, y);
      if (!element) return null;
      const container = element.closest('#subtitle-text') || textElement;
      const walker = this.createWalker(container);
      const range = document.createRange();
      let node;
      while ((node = walker.nextNode())) {
        for (let i = 0; i < node.textContent.length; i++) {
          range.setStart(node, i);
          range.setEnd(node, i + 1);
          if (this.inCharRange(range, x, y)) {
            range.collapse(true);
            return range;
          }
        }
      }

      return document.caretRangeFromPoint?.(x, y) || null;
    },

    getCharacterAtPoint(x, y) {
      const range = this.getCaretRange(x, y);
      if (!range) return null;

      const node = range.startContainer;
      if (node.nodeType !== Node.TEXT_NODE || !textElement.contains(node)) return null;

      const text = node.textContent;
      const caret = range.startOffset;
      for (const offset of [caret, caret - 1, caret + 1]) {
        if (offset < 0 || offset >= text.length) continue;

        const charRange = document.createRange();
        charRange.setStart(node, offset);
        charRange.setEnd(node, offset + 1);
        if (this.inCharRange(charRange, x, y)) {
          if (this.isScanBoundary(text[offset])) return null;
          return { node, offset };
        }
      }

      return null;
    },

    normalizedOffset(targetNode, offset) {
      let count = 0;
      const walker = this.createWalker(textElement);
      let node;
      while ((node = walker.nextNode())) {
        if (node === targetNode) {
          return count + offset;
        }
        count += node.textContent.length;
      }
      return offset;
    },

    getSelectionRect(x, y) {
      if (!state.selection?.ranges.length) return null;
      const first = state.selection.ranges[0];
      const range = document.createRange();
      range.setStart(first.node, first.start);
      range.setEnd(first.node, first.start + 1);
      const rects = Array.from(range.getClientRects());
      const rect = rects.find((r) => x >= r.left && x <= r.right && y >= r.top && y <= r.bottom) ||
        range.getBoundingClientRect();
      return { x: rect.x, y: rect.y, width: rect.width, height: rect.height };
    },

    selectText(x, y) {
      const hit = this.getCharacterAtPoint(x, y);
      if (!hit) {
        this.clearSelection();
        postToHost('lookupEmpty');
        return null;
      }

      this.clearSelection();

      const content = hit.node.textContent;
      if (hit.offset < content.length && !isCodePointJapanese(content.codePointAt(hit.offset))) {
        while (hit.offset > 0 && !this.isScanBoundary(content[hit.offset - 1])) {
          hit.offset--;
        }
      }

      const container = this.findContainer(hit.node);
      const walker = this.createWalker(container);
      let text = '';
      let node = hit.node;
      let offset = hit.offset;
      const ranges = [];

      walker.currentNode = node;
      while (text.length < state.scanLength && node) {
        const nodeText = node.textContent;
        const start = offset;
        while (offset < nodeText.length && text.length < state.scanLength) {
          const char = nodeText[offset];
          if (this.isScanBoundary(char)) break;
          text += char;
          offset++;
        }
        if (offset > start) {
          ranges.push({ node, start, end: offset });
        }
        if (offset < nodeText.length || text.length >= state.scanLength) break;
        node = walker.nextNode();
        offset = 0;
      }

      if (!text) {
        this.clearSelection();
        postToHost('lookupEmpty');
        return null;
      }

      state.selection = {
        startNode: hit.node,
        startOffset: hit.offset,
        ranges,
        text,
      };

      const rect = this.getSelectionRect(x, y);
      const sourceOffset = this.normalizedOffset(hit.node, hit.offset);
      postToHost('lookupRequest', {
        text,
        sentence: state.text,
        offset: sourceOffset,
        x: rect?.x || x,
        y: rect?.y || y,
        width: rect?.width || 1,
        height: rect?.height || 1,
        pointX: x,
        pointY: y,
      });
      return text;
    },

    selectionCharacterRanges(charCount) {
      if (!state.selection?.ranges.length) return [];
      const ranges = [];
      let remaining = charCount;

      for (const selectedRange of state.selection.ranges) {
        if (remaining <= 0) break;
        let end = selectedRange.start;
        while (end < selectedRange.end && remaining > 0) {
          const char = String.fromCodePoint(selectedRange.node.textContent.codePointAt(end));
          end += char.length;
          remaining--;
        }

        const range = document.createRange();
        range.setStart(selectedRange.node, selectedRange.start);
        range.setEnd(selectedRange.node, end);
        ranges.push(range);
      }

      return ranges;
    },

    highlightSelection(charCount) {
      const ranges = this.selectionCharacterRanges(charCount);
      if (!ranges.length) return;
      CSS.highlights?.set('hoshi-selection', new Highlight(...ranges));
    },

    clearSelection() {
      CSS.highlights?.get('hoshi-selection')?.clear();
      state.selection = null;
    },
  };

  function cssColor(value, fallback) {
    if (typeof value !== 'string' || !value.trim()) return fallback;
    const hex = value.trim();
    if (/^#[0-9a-fA-F]{6}$/.test(hex)) return hex;
    if (/^#[0-9a-fA-F]{8}$/.test(hex)) {
      const alpha = parseInt(hex.slice(1, 3), 16) / 255;
      const red = parseInt(hex.slice(3, 5), 16);
      const green = parseInt(hex.slice(5, 7), 16);
      const blue = parseInt(hex.slice(7, 9), 16);
      return `rgba(${red}, ${green}, ${blue}, ${alpha.toFixed(3)})`;
    }
    return fallback;
  }

  function applyTextShadow(radius, opacity) {
    const r = Math.max(0, Number(radius) || 0);
    if (r <= 0 || opacity <= 0) {
      textElement.style.textShadow = 'none';
      return;
    }

    const alpha = Math.min(0.95, Math.max(0, 0.9 * opacity));
    const stroke = Math.max(1, Math.min(3, r));
    textElement.style.textShadow = [
      `0 0 ${r}px rgba(0, 0, 0, ${alpha})`,
      `${stroke}px 0 0 rgba(0, 0, 0, ${alpha})`,
      `-${stroke}px 0 0 rgba(0, 0, 0, ${alpha})`,
      `0 ${stroke}px 0 rgba(0, 0, 0, ${alpha})`,
      `0 -${stroke}px 0 rgba(0, 0, 0, ${alpha})`,
    ].join(', ');
  }

  function setState(next) {
    const previousText = state.text;
    state.text = typeof next.text === 'string' ? next.text : '';
    state.scanLength = Math.min(64, Math.max(1, Number(next.scanLength) || 64));
    state.scanNonJapaneseText = next.scanNonJapaneseText !== false;

    if (previousText !== state.text) {
      selection.clearSelection();
      textElement.textContent = state.text;
      state.lastShiftHoverKey = '';
    }

    const fontFamily = typeof next.fontFamily === 'string' && next.fontFamily.trim()
      ? next.fontFamily.trim()
      : '"Segoe UI", "Yu Gothic UI", "Meiryo", sans-serif';
    const fontSize = Math.min(72, Math.max(12, Number(next.fontSize) || 36));
    const fontWeight = Math.min(900, Math.max(100, Math.round((Number(next.fontWeight) || 700) / 100) * 100));
    const textOpacity = Math.min(1, Math.max(0, Number(next.textOpacity)));
    const blurRadius = Math.min(20, Math.max(0, Number(next.blurRadius) || 0));

    textElement.style.setProperty('--subtitle-font-family', fontFamily);
    textElement.style.setProperty('--subtitle-font-size', `${fontSize}px`);
    textElement.style.setProperty('--subtitle-font-weight', `${fontWeight}`);
    textElement.style.setProperty('--subtitle-color', cssColor(next.subtitleColor, '#fff'));
    textElement.style.setProperty('--subtitle-text-opacity', `${Number.isFinite(textOpacity) ? textOpacity : 1}`);
    textElement.style.setProperty('--subtitle-filter-blur', `${blurRadius}px`);
    textElement.style.color = cssColor(next.subtitleColor, '#fff');
    highlightStyle.textContent = `::highlight(hoshi-selection) { background-color: ${cssColor(next.lookupHighlightColor, 'rgba(62, 181, 193, 0.8)')}; color: ${cssColor(next.lookupHighlightTextColor, '#fff')}; }`;

    applyTextShadow(next.shadowRadius, Number.isFinite(textOpacity) ? textOpacity : 1);
  }

  function lookupAtPoint(x, y) {
    const hit = selection.getCharacterAtPoint(x, y);
    if (!hit) {
      state.lastShiftHoverKey = '';
      return;
    }

    const key = `${hit.offset}:${hit.node.textContent}`;
    if (key === state.lastShiftHoverKey) return;
    state.lastShiftHoverKey = key;
    selection.selectText(x, y);
  }

  window.hoshiVideoSubtitle = {
    setState,
    highlightSelection: (charCount) => selection.highlightSelection(charCount),
    clearSelection: () => selection.clearSelection(),
    getCharacterAtPoint: (x, y) => selection.getCharacterAtPoint(x, y),
  };

  document.addEventListener('click', (event) => {
    selection.selectText(event.clientX, event.clientY);
  });

  document.addEventListener('mousemove', (event) => {
    state.lastHoverPoint = { x: event.clientX, y: event.clientY };
    if (!event.shiftKey) {
      state.lastShiftHoverKey = '';
      return;
    }
    lookupAtPoint(event.clientX, event.clientY);
  });

  document.addEventListener('keydown', (event) => {
    if (event.key !== 'Shift' || !state.lastHoverPoint) return;
    lookupAtPoint(state.lastHoverPoint.x, state.lastHoverPoint.y);
  });

  root.addEventListener('mouseenter', () => postToHost('hoverChanged', { isHovering: true }));
  root.addEventListener('mouseleave', () => {
    state.lastShiftHoverKey = '';
    postToHost('hoverChanged', { isHovering: false });
  });

  postToHost('ready');
})();
