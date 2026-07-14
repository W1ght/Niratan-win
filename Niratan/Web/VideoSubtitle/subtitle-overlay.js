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

  const clickPolicy = { dismissOnEmpty: true, isHover: false };
  const hoverPolicy = { dismissOnEmpty: false, isHover: true };

  function postLookupEmpty(policy) {
    postToHost('lookupEmpty', {
      dismissOnEmpty: policy?.dismissOnEmpty === true,
      isHover: policy?.isHover === true,
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

    selectText(x, y, policy = clickPolicy) {
      const hit = this.getCharacterAtPoint(x, y);
      if (!hit) {
        if (policy.dismissOnEmpty) this.clearSelection();
        postLookupEmpty(policy);
        return null;
      }

      return this.selectHit(hit, x, y, undefined, policy);
    },

    selectTextAtOffset(request) {
      const node = textElement.firstChild;
      if (!node || node.nodeType !== Node.TEXT_NODE || !node.textContent.length) {
        if (request?.dismissOnEmpty === true) this.clearSelection();
        postLookupEmpty(request);
        return null;
      }

      const offset = Math.min(
        node.textContent.length - 1,
        Math.max(0, Number(request?.offset) || 0));
      const x = Number(request?.x) || 0;
      const y = Number(request?.y) || 0;
      const width = Math.max(1, Number(request?.width) || 1);
      const height = Math.max(1, Number(request?.height) || 1);
      return this.selectHit(
        { node, offset },
        x + width / 2,
        y + height / 2,
        { x, y, width, height },
        request);
    },

    selectHit(hit, x, y, anchorRect, policy = clickPolicy) {

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
        if (policy.dismissOnEmpty) this.clearSelection();
        postLookupEmpty(policy);
        return null;
      }

      this.clearSelection();
      state.selection = {
        startNode: hit.node,
        startOffset: hit.offset,
        ranges,
        text,
      };

      const rect = anchorRect || this.getSelectionRect(x, y);
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
      CSS.highlights?.set('niratan-selection', new Highlight(...ranges));
    },

    clearSelection() {
      CSS.highlights?.get('niratan-selection')?.clear();
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

  function applyTextShadow(radius) {
    const r = Math.min(10, Math.max(0, Number(radius) || 0));
    if (r <= 0) {
      textElement.style.textShadow = 'none';
      return;
    }

    textElement.style.textShadow = `0 1px ${r}px rgba(0, 0, 0, 0.9)`;
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
    highlightStyle.textContent = `::highlight(niratan-selection) { background-color: ${cssColor(next.lookupHighlightColor, 'rgba(62, 181, 193, 0.8)')}; color: ${cssColor(next.lookupHighlightTextColor, '#fff')}; }`;

    applyTextShadow(next.shadowRadius);
  }

  function lookupAtPoint(x, y) {
    const hit = selection.getCharacterAtPoint(x, y);
    if (!hit) {
      state.lastShiftHoverKey = '';
      postLookupEmpty(hoverPolicy);
      return;
    }

    const key = `${hit.offset}:${hit.node.textContent}`;
    if (key === state.lastShiftHoverKey) return;
    state.lastShiftHoverKey = key;
    selection.selectText(x, y, hoverPolicy);
  }

  window.niratanVideoSubtitle = {
    setState,
    highlightSelection: (charCount) => selection.highlightSelection(charCount),
    clearSelection: () => selection.clearSelection(),
    getCharacterAtPoint: (x, y) => selection.getCharacterAtPoint(x, y),
    lookupAtOffset: (request) => selection.selectTextAtOffset(request),
  };

  postToHost('ready');
})();
