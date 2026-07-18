//
//  selection.js — Niratan Windows reader selection bridge
//  SPDX-License-Identifier: GPL-3.0-or-later
//

(function () {
  const CJK_UNIFIED_IDEOGRAPHS_RANGE = [0x4e00, 0x9fff];
  const CJK_EXT_A = [0x3400, 0x4dbf];
  const CJK_COMPAT = [0xf900, 0xfaff];
  const CJK_RANGES = [CJK_UNIFIED_IDEOGRAPHS_RANGE, CJK_EXT_A, CJK_COMPAT];

  const FULLWIDTH_RANGES = [
    [0xff10, 0xff19], [0xff21, 0xff3a], [0xff41, 0xff5a],
    [0xff01, 0xff0f], [0xff1a, 0xff1f], [0xff3b, 0xff3f],
    [0xff5b, 0xff60], [0xffe0, 0xffee],
  ];

  const JAPANESE_RANGES = [
    [0x3040, 0x309f], [0x30a0, 0x30ff],
    ...CJK_RANGES,
    [0xff66, 0xff9f], [0x30fb, 0x30fc],
    [0xff61, 0xff65], [0x3000, 0x303f],
    ...FULLWIDTH_RANGES,
  ];

  const TTU_MATCHABLE_CHARACTER = /[0-9A-Za-z○◯々-〇〻ぁ-ゖゝ-ゞァ-ヺー０-９Ａ-Ｚａ-ｚｦ-ﾝ\p{Radical}\p{Unified_Ideograph}]/iu;

  let normalizedOffsetGeneration = 0;
  let normalizedOffsetReadyGeneration = -1;
  let normalizedNodeStartOffsets = new WeakMap();

  function countNormalizedCharacters(text, limit) {
    let count = 0;
    const end = Math.min(Math.max(0, limit), text.length);
    for (let i = 0; i < end; ) {
      const char = String.fromCodePoint(text.codePointAt(i));
      if (TTU_MATCHABLE_CHARACTER.test(char)) count++;
      i += char.length;
    }
    return count;
  }

  function invalidateNormalizedOffsetIndex() {
    normalizedOffsetGeneration += 1;
    normalizedOffsetReadyGeneration = -1;
  }

  function isCodePointJapanese(codePoint) {
    return JAPANESE_RANGES.some(([start, end]) => codePoint >= start && codePoint <= end);
  }

  function postToHost(type, payload) {
    window.chrome?.webview?.postMessage({
      version: 1,
      type: type,
      payload: payload || {},
    });
  }

  function isInteractiveClickTarget(target) {
    return target instanceof Element && !!target.closest(
      'a, button, input, textarea, select, option, label, summary, [contenteditable]:not([contenteditable="false"]), [role="button"]',
    );
  }

  let lookupTraceSequence = 0;
  function nextLookupTraceId() {
    lookupTraceSequence += 1;
    return 'reader-lookup-' + Date.now().toString(36) + '-' + lookupTraceSequence;
  }

  function getScanLength() {
    const configured = Number(window.__niratanLookupSettings?.scanLength);
    if (!Number.isFinite(configured)) return 16;
    return Math.min(64, Math.max(1, configured));
  }

  function scanNonJapaneseTextEnabled() {
    if (window.scanNonJapaneseText === false) {
      return false;
    }

    if (window.scanNonJapaneseText === true) {
      return true;
    }

    const configured = window.__niratanLookupSettings?.scanNonJapaneseText;
    return typeof configured === 'boolean' ? configured : true;
  }

  const niratanSelection = {
    selection: null,
    miningContextCache: new WeakMap(),
    scanDelimiters: '。、！？…※「」『』（）()【】〈〉《》〔〕｛｝{}[]・：；:;,　─\n\r',
    sentenceDelimiters: '。！？.!?\n\r',
    trailingSentenceChars: '。、！？…※」』）)]】〉》〕｝}］]',
    brackets: {
      '「': '」', '『': '』',
      '（': '）', '(': ')',
      '【': '】', '〈': '〉',
      '《': '》', '〔': '〕',
      '｛': '｝', '{': '}',
      '［': '］', '[': ']',
    },

    isVertical() {
      return window.getComputedStyle(document.body).writingMode === 'vertical-rl';
    },

    isScanBoundary(char) {
      return /^[\s　]$/.test(char) ||
        this.scanDelimiters.includes(char) ||
        (!scanNonJapaneseTextEnabled() && !this.isCodePointJapanese(char.codePointAt(0)));
    },

    isFurigana(node) {
      const el = node.nodeType === Node.TEXT_NODE ? node.parentElement : node;
      return !!el?.closest('rt, rp');
    },

    findParagraph(node) {
      let el = node.nodeType === Node.TEXT_NODE ? node.parentElement : node;
      return el?.closest('p') || null;
    },

    createWalker(rootNode) {
      const root = rootNode || document.body;
      return document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
        acceptNode: (n) =>
          this.isFurigana(n) ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT,
      });
    },

    miningContextScope(node) {
      const element = node?.nodeType === Node.TEXT_NODE ? node.parentElement : node;
      return element?.closest('.glossary-content') || document.body;
    },

    miningContextBlock(node, scope) {
      const element = node.parentElement;
      return element?.closest('p, li, blockquote, h1, h2, h3, h4, h5, h6, figcaption, pre') || scope;
    },

    isMiningContextTextNode(node) {
      const element = node.parentElement;
      return !!element && !element.closest('rt, rp, script, style, noscript, svg, button');
    },

    miningContextSentences(text) {
      const ranges = [];
      let start = 0;
      const append = (end) => {
        let trimmedStart = start;
        let trimmedEnd = end;
        while (trimmedStart < trimmedEnd && /\s/.test(text[trimmedStart])) trimmedStart++;
        while (trimmedEnd > trimmedStart && /\s/.test(text[trimmedEnd - 1])) trimmedEnd--;
        if (trimmedStart < trimmedEnd) {
          ranges.push({
            start: trimmedStart,
            end: trimmedEnd,
            text: text.slice(trimmedStart, trimmedEnd),
          });
        }
        start = end;
      };

      for (let i = 0; i < text.length; i++) {
        if (!this.sentenceDelimiters.includes(text[i])) continue;
        let end = i + 1;
        while (end < text.length && this.trailingSentenceChars.includes(text[end])) end++;
        append(end);
        i = end - 1;
      }
      append(text.length);
      return ranges;
    },

    miningContextData(scope) {
      const cached = this.miningContextCache.get(scope);
      if (cached) return cached;

      const walker = this.createWalker(scope);
      let text = '';
      let previousBlock = null;
      const nodeOffsets = new WeakMap();
      let node;
      while ((node = walker.nextNode())) {
        if (!this.isMiningContextTextNode(node)) continue;
        const block = this.miningContextBlock(node, scope);
        if (text && previousBlock && block !== previousBlock) text += '\n';
        nodeOffsets.set(node, text.length);
        text += node.textContent;
        previousBlock = block;
      }

      const data = { nodeOffsets, sentenceRanges: this.miningContextSentences(text) };
      this.miningContextCache.set(scope, data);
      return data;
    },

    miningContextForSelection(targetNode, targetOffset) {
      const scope = this.miningContextScope(targetNode);
      let data = this.miningContextData(scope);
      if (!data.nodeOffsets.has(targetNode)) {
        this.miningContextCache.delete(scope);
        data = this.miningContextData(scope);
      }

      const nodeOffset = data.nodeOffsets.get(targetNode);
      if (nodeOffset === undefined) return null;
      const targetTextOffset = nodeOffset + targetOffset;
      const currentIndex = data.sentenceRanges.findIndex(({ start, end }) =>
        targetTextOffset >= start && targetTextOffset <= end);
      if (currentIndex < 0) return null;

      return {
        currentIndex,
        sentences: data.sentenceRanges.map(({ start, text }, index) => ({
          id: String(index),
          text,
          ...(index === currentIndex
            ? { targetLocation: Math.max(0, targetTextOffset - start) }
            : {}),
        })),
      };
    },

    inCharRange(charRange, x, y) {
      const rects = charRange.getClientRects();
      if (rects.length) {
        for (const rect of rects) {
          if (x >= rect.left && x <= rect.right && y >= rect.top && y <= rect.bottom)
            return true;
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
      const container = element.closest('p, div, span, ruby, a') || document.body;
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
      if (node.nodeType !== Node.TEXT_NODE) return null;
      if (this.isFurigana(node)) return null;

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

    getSentenceContext(startNode, startOffset) {
      const container = this.findParagraph(startNode) || document.body;
      const walker = this.createWalker(container);

      walker.currentNode = startNode;
      const partsBefore = [];
      let node = startNode;
      let limit = startOffset;

      while (node) {
        const text = node.textContent;
        let foundStart = false;
        for (let i = limit - 1; i >= 0; i--) {
          if (this.sentenceDelimiters.includes(text[i])) {
            partsBefore.push(text.slice(i + 1, limit));
            foundStart = true;
            break;
          }
        }
        if (foundStart) break;
        partsBefore.push(text.slice(0, limit));
        node = walker.previousNode();
        if (node) limit = node.textContent.length;
      }

      walker.currentNode = startNode;
      const partsAfter = [];
      node = startNode;
      let start = startOffset;

      while (node) {
        const text = node.textContent;
        let foundEnd = false;
        for (let i = start; i < text.length; i++) {
          if (this.sentenceDelimiters.includes(text[i])) {
            let end = i + 1;
            while (end < text.length) {
              if (!this.trailingSentenceChars.includes(text[end])) break;
              end += 1;
            }
            partsAfter.push(text.slice(start, end));
            foundEnd = true;
            break;
          }
        }
        if (foundEnd) break;
        partsAfter.push(text.slice(start));
        node = walker.nextNode();
        start = 0;
      }

      const beforeText = partsBefore.reverse().join('');
      const rawSentence = beforeText + partsAfter.join('');
      const leadingTrim = rawSentence.length - rawSentence.trimStart().length;
      let selectedOffset = Math.max(0, beforeText.length - leadingTrim);
      let sentence = rawSentence.trim();

      const closeBrackets = new Set(Object.values(this.brackets));
      const openBrackets = new Set(Object.keys(this.brackets));
      let stack = [];
      let unmatchedClose = [];

      for (let i = 0; i < sentence.length; i++) {
        const ch = sentence[i];
        if (openBrackets.has(ch)) {
          stack.push(ch);
        } else if (closeBrackets.has(ch)) {
          if (stack.length > 0 && this.brackets[stack[stack.length - 1]] === ch) {
            stack.pop();
          } else {
            unmatchedClose.push(ch);
          }
        }
      }

      let startSlice = 0;
      while (stack.length > 0 && startSlice < sentence.length - 1) {
        if (stack[0] === sentence[startSlice]) stack.shift();
        else break;
        startSlice++;
      }

      let endSlice = sentence.length - 1;
      let endIdx = sentence.length - 1;
      while (unmatchedClose.length > 0 && endIdx > startSlice) {
        if (unmatchedClose[unmatchedClose.length - 1] === sentence[endIdx]) {
          unmatchedClose.pop();
          endSlice = endIdx - 1;
        } else if (!this.sentenceDelimiters.includes(sentence[endIdx])) break;
        endIdx--;
      }

      const sliced = sentence.slice(startSlice, endSlice + 1);
      const slicedLeadingTrim = sliced.length - sliced.trimStart().length;
      selectedOffset = Math.max(0, selectedOffset - startSlice - slicedLeadingTrim);
      return {
        sentence: sliced.trim(),
        sentenceOffset: selectedOffset,
      };
    },

    getSelectionRect(x, y) {
      if (!this.selection?.ranges.length) return null;
      const first = this.selection.ranges[0];
      const range = document.createRange();
      range.setStart(first.node, first.start);
      range.setEnd(first.node, first.start + 1);
      const rects = Array.from(range.getClientRects());
      const rect = rects.find(
        (r) => x >= r.left && x <= r.right && y >= r.top && y <= r.bottom,
      ) ?? range.getBoundingClientRect();
      return { x: rect.x, y: rect.y, width: rect.width, height: rect.height };
    },

    highlightSelection(charCount) {
      if (!this.selection?.ranges.length) return;

      const highlights = this.selectionCharacterRanges(charCount);
      CSS.highlights?.set('niratan-selection', new Highlight(...highlights));
    },

    selectionCharacterRanges(charCount) {
      if (!this.selection?.ranges.length) return [];

      const ranges = [];
      let remaining = charCount;

      for (const r of this.selection.ranges) {
        if (remaining <= 0) break;

        let start = r.start;
        let end = start;
        while (end < r.end && remaining > 0) {
          const char = String.fromCodePoint(r.node.textContent.codePointAt(end));
          end += char.length;
          remaining--;
        }

        const range = document.createRange();
        range.setStart(r.node, start);
        range.setEnd(r.node, end);
        ranges.push(range);
      }

      return ranges;
    },

    selectText(x, y, maxLength) {
      if (document.elementFromPoint(x, y)?.closest('a')) {
        this.clearSelection();
        return null;
      }

      const hit = this.getCharacterAtPoint(x, y);
      if (!hit) {
        this.clearSelection();
        return null;
      }

      if (
        this.selection &&
        hit.node === this.selection.startNode &&
        hit.offset === this.selection.startOffset
      ) {
        this.clearSelection();
        return null;
      }

      this.clearSelection();

      const container = this.findParagraph(hit.node) || document.body;
      const walker = this.createWalker(container);

      let text = '';
      let node = hit.node;
      let offset = hit.offset;
      let ranges = [];

      walker.currentNode = node;
      while (text.length < maxLength && node) {
        const content = node.textContent;
        const start = offset;

        while (offset < content.length && text.length < maxLength) {
          const char = content[offset];
          if (this.isScanBoundary(char)) break;
          text += char;
          offset++;
        }

        if (offset > start) {
          ranges.push({ node, start, end: offset });
        }

        if (offset < content.length || text.length >= maxLength) break;

        node = walker.nextNode();
        offset = 0;
      }

      if (!text) return null;

      this.selection = {
        startNode: hit.node,
        startOffset: hit.offset,
        ranges,
        text,
      };

      const sentenceContext = this.getSentenceContext(hit.node, hit.offset);
      const rect = this.getSelectionRect(x, y);
      const normalizedOffset = this.getNormalizedOffset(hit.node, hit.offset);
      const traceId = nextLookupTraceId();

      postToHost('lookupRequest', {
        traceId,
        clientNow: performance.now(),
        text,
        sentence: sentenceContext.sentence,
        x: rect.x,
        y: rect.y,
        width: rect.width,
        height: rect.height,
        normalizedOffset,
        sentenceOffset: sentenceContext.sentenceOffset,
        pointX: x,
        pointY: y,
        miningContext: this.miningContextForSelection(hit.node, hit.offset),
      });

      return text;
    },

    getNormalizedOffset(targetNode, offset) {
      if (normalizedOffsetReadyGeneration !== normalizedOffsetGeneration) {
        const offsets = new WeakMap();
        const walker = this.createWalker();
        let count = 0;
        let node;
        while ((node = walker.nextNode())) {
          offsets.set(node, count);
          const text = node.textContent || '';
          count += countNormalizedCharacters(text, text.length);
        }
        normalizedNodeStartOffsets = offsets;
        normalizedOffsetReadyGeneration = normalizedOffsetGeneration;
      }

      const text = targetNode.textContent || '';
      const startOffset = normalizedNodeStartOffsets.get(targetNode) || 0;
      return startOffset + countNormalizedCharacters(text, offset);
    },

    clearSelection() {
      window.getSelection()?.removeAllRanges();
      CSS.highlights?.get('niratan-selection')?.clear();
      this.selection = null;
    },
  };

  window.niratanSelection = niratanSelection;

  document.addEventListener(
    'niratan-reader-content-changed',
    invalidateNormalizedOffsetIndex,
  );

  // Click handler for instant lookup
  document.addEventListener('click', (e) => {
    if (isInteractiveClickTarget(e.target)) return;

    const browserSelection = window.getSelection();
    if (browserSelection && !browserSelection.isCollapsed) return;

    const selectedText = niratanSelection.selectText(
      e.clientX,
      e.clientY,
      getScanLength(),
    );
    if (window.__niratanLookupPopupActive === true && !selectedText) {
      niratanSelection.clearSelection();
      postToHost('lookupDismiss', {});
    }
  });

  // Hover + Shift handler
  let lastHoverPoint = null;
  let lastShiftHoverKey = '';
  let shiftHoverTimer = 0;
  function lookupAtPoint(x, y) {
    const hit = niratanSelection.getCharacterAtPoint(x, y);
    if (!hit) {
      lastShiftHoverKey = '';
      return;
    }

    const key = `${x}:${y}:${hit.offset}:${hit.node.textContent}`;
    if (key === lastShiftHoverKey) return;
    lastShiftHoverKey = key;
    niratanSelection.selectText(x, y, getScanLength());
  }

  function scheduleLookupAtPoint(x, y) {
    if (shiftHoverTimer) clearTimeout(shiftHoverTimer);
    const configured = Number(window.__niratanLookupSettings?.hoverDelayMs);
    const delay = Number.isFinite(configured)
      ? Math.min(250, Math.max(0, configured))
      : 45;
    shiftHoverTimer = setTimeout(() => {
      shiftHoverTimer = 0;
      lookupAtPoint(x, y);
    }, delay);
  }

  document.addEventListener('mousemove', (e) => {
    lastHoverPoint = { x: e.clientX, y: e.clientY };
    if (!e.shiftKey) {
      if (shiftHoverTimer) clearTimeout(shiftHoverTimer);
      shiftHoverTimer = 0;
      lastShiftHoverKey = '';
      return;
    }
    scheduleLookupAtPoint(e.clientX, e.clientY);
  });

  document.addEventListener('keydown', (e) => {
    if (e.key !== 'Shift' || !lastHoverPoint) return;
    scheduleLookupAtPoint(lastHoverPoint.x, lastHoverPoint.y);
  });
})();
