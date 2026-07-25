(function () {
"use strict";

var config = window.__niratanVisualNovelSettings || {};
var MATCHABLE_CHARACTER = /[0-9A-Za-z○◯々-〇〻ぁ-ゖゝ-ゞァ-ヺー０-９Ａ-Ｚａ-ｚｦ-ﾝ\p{Radical}\p{Unified_Ideograph}]/u;
var BLOCK_TAGS = new Set([
  "address", "aside", "blockquote", "dd", "details", "dialog", "dl", "dt",
  "figcaption", "figure", "footer", "h1", "h2", "h3", "h4", "h5", "h6",
  "header", "hr", "li", "main", "nav", "ol", "p", "pre", "table", "ul",
]);
var CONTAINER_TAGS = new Set(["article", "body", "div", "section"]);
var MEDIA_TAGS = new Set(["audio", "canvas", "img", "picture", "svg", "video"]);

function clamp(value, minimum, maximum, fallback) {
  var parsed = Number(value);
  if (!Number.isFinite(parsed)) return fallback;
  return Math.min(maximum, Math.max(minimum, parsed));
}

function countRaw(text) {
  return Array.from(text || "").length;
}

function countMatchable(text) {
  var count = 0;
  Array.from(text || "").forEach(function (character) {
    if (MATCHABLE_CHARACTER.test(character)) count += 1;
  });
  return count;
}

function unionIds(descriptors) {
  var ids = new Set();
  descriptors.forEach(function (descriptor) {
    descriptor.ids.forEach(function (id) { ids.add(id); });
  });
  return ids;
}

var visualNovel = {
  enabled: config.enabled === true,
  revealSpeed: clamp(config.revealSpeed, 0, 120, 45),
  screenMode: String(config.screenMode || "block").toLowerCase(),
  sentencesPerScreen: Math.floor(clamp(config.sentencesPerScreen, 1, 12, 1)),
  preserveDialogue: config.preserveDialogue === true,
  clickAdvance: config.clickAdvance === true,
  sourceRoot: null,
  stage: null,
  screen: null,
  screens: [],
  currentScreenIndex: 0,
  totalChars: 0,
  totalRawChars: 0,
  nodeCharOffsets: new WeakMap(),
  nodeRawOffsets: new WeakMap(),
  nodeCharAnchors: new WeakMap(),
  nodeRawAnchors: new WeakMap(),
  adapter: null,
  revealComplete: true,
  revealTimer: null,
  revealSegments: [],
  highlights: [],
  localHighlightOperation: false,

  initialize: async function (adapter) {
    if (!this.enabled) return;
    this.adapter = adapter || {};
    this.captureSource();
    this.buildSourceIndex();
    this.ensureStage();
    await document.fonts.ready;
    this.buildScreens();
    this.highlights = Array.isArray(window.__niratanChapterHighlights)
      ? window.__niratanChapterHighlights.slice()
      : [];
    this.patchHighlights();
  },

  captureSource: function () {
    if (this.sourceRoot) return;
    this.sourceRoot = document.createDocumentFragment();
    while (document.body.firstChild) {
      this.sourceRoot.appendChild(document.body.firstChild);
    }
  },

  ensureStage: function () {
    if (this.stage && this.screen) return;
    this.stage = document.createElement("div");
    this.stage.className = "niratan-vn-stage";
    this.screen = document.createElement("div");
    this.screen.className = "niratan-vn-screen";
    this.stage.appendChild(this.screen);
    document.body.appendChild(this.stage);
  },

  isIgnoredText: function (node) {
    var element = node && node.nodeType === Node.TEXT_NODE ? node.parentElement : node;
    return !!(element && element.closest && element.closest(
      "rt, rp, script, style, template, [hidden], [aria-hidden=\"true\"]"
    ));
  },

  readableTextNodes: function (root) {
    if (!root) return [];
    var result = [];
    var walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
      acceptNode: function (node) {
        return visualNovel.isIgnoredText(node)
          ? NodeFilter.FILTER_REJECT
          : NodeFilter.FILTER_ACCEPT;
      },
    });
    var node;
    while ((node = walker.nextNode())) result.push(node);
    return result;
  },

  buildSourceIndex: function () {
    var charOffset = 0;
    var rawOffset = 0;
    var self = this;
    function visit(node) {
      self.nodeCharAnchors.set(node, charOffset);
      self.nodeRawAnchors.set(node, rawOffset);
      if (node.nodeType === Node.TEXT_NODE) {
        if (!self.isIgnoredText(node)) {
          self.nodeCharOffsets.set(node, charOffset);
          self.nodeRawOffsets.set(node, rawOffset);
          charOffset += countMatchable(node.textContent);
          rawOffset += countRaw(node.textContent);
        }
        return;
      }
      Array.from(node.childNodes || []).forEach(visit);
    }
    visit(this.sourceRoot);
    this.totalChars = charOffset;
    this.totalRawChars = rawOffset;
  },

  idsForNode: function (root) {
    var ids = new Set();
    if (!root) return ids;
    if (root.nodeType === Node.ELEMENT_NODE && root.id) ids.add(root.id);
    if (root.querySelectorAll) {
      Array.from(root.querySelectorAll("[id], a[name]")).forEach(function (element) {
        if (element.id) ids.add(element.id);
        var name = element.getAttribute && element.getAttribute("name");
        if (name) ids.add(name);
      });
    }
    return ids;
  },

  descriptorForNode: function (node) {
    var textNodes = this.readableTextNodes(node);
    var startChar = this.nodeCharAnchors.get(node) || 0;
    var startRaw = this.nodeRawAnchors.get(node) || 0;
    var endChar = startChar;
    var endRaw = startRaw;
    textNodes.forEach(function (textNode) {
      var charStart = visualNovel.nodeCharOffsets.get(textNode) || startChar;
      var rawStart = visualNovel.nodeRawOffsets.get(textNode) || startRaw;
      endChar = Math.max(endChar, charStart + countMatchable(textNode.textContent));
      endRaw = Math.max(endRaw, rawStart + countRaw(textNode.textContent));
    });
    var text = textNodes.map(function (textNode) { return textNode.textContent || ""; }).join("");
    return {
      node: node,
      startChar: startChar,
      endChar: endChar,
      startRaw: startRaw,
      endRaw: endRaw,
      text: text,
      ids: this.idsForNode(node),
      mediaOnly: text.trim().length === 0 && this.containsMedia(node),
      render: function () {
        var fragment = document.createDocumentFragment();
        fragment.appendChild(node.cloneNode(true));
        return fragment;
      },
    };
  },

  containsMedia: function (node) {
    if (!node) return false;
    if (node.nodeType === Node.ELEMENT_NODE && MEDIA_TAGS.has(node.tagName.toLowerCase())) {
      return true;
    }
    return !!(node.querySelector && node.querySelector("img, svg, picture, video, audio, canvas"));
  },

  hasDirectBlockChildren: function (element) {
    return Array.from(element.children || []).some(function (child) {
      var tag = child.tagName.toLowerCase();
      return BLOCK_TAGS.has(tag) || CONTAINER_TAGS.has(tag) || MEDIA_TAGS.has(tag);
    });
  },

  collectBlockDescriptors: function (root, output) {
    var self = this;
    Array.from(root.childNodes || []).forEach(function (node) {
      if (node.nodeType === Node.TEXT_NODE) {
        if ((node.textContent || "").trim()) output.push(self.descriptorForNode(node));
        return;
      }
      if (node.nodeType !== Node.ELEMENT_NODE) return;
      var tag = node.tagName.toLowerCase();
      if (tag === "script" || tag === "style" || tag === "template" || node.hidden) return;
      if (CONTAINER_TAGS.has(tag) && self.hasDirectBlockChildren(node)) {
        self.collectBlockDescriptors(node, output);
        return;
      }
      output.push(self.descriptorForNode(node));
    });
  },

  sentenceSegments: function (text) {
    if (!text) return [];
    if (typeof Intl !== "undefined" && typeof Intl.Segmenter === "function") {
      var segmenter = new Intl.Segmenter("ja", { granularity: "sentence" });
      return Array.from(segmenter.segment(text)).map(function (entry) {
        return { start: entry.index, end: entry.index + entry.segment.length };
      }).filter(function (entry) { return entry.end > entry.start; });
    }

    var result = [];
    var start = 0;
    var closing = /[」』）】”’]/u;
    for (var index = 0; index < text.length;) {
      var character = String.fromCodePoint(text.codePointAt(index));
      index += character.length;
      if (!/[。！？!?]/u.test(character)) continue;
      while (index < text.length) {
        var next = String.fromCodePoint(text.codePointAt(index));
        if (!closing.test(next)) break;
        index += next.length;
      }
      result.push({ start: start, end: index });
      start = index;
    }
    if (start < text.length) result.push({ start: start, end: text.length });
    return result;
  },

  sentenceDescriptorsForNode: function (root) {
    var textNodes = this.readableTextNodes(root);
    if (!textNodes.length) return [];
    var entries = [];
    var fullText = "";
    textNodes.forEach(function (node) {
      var start = fullText.length;
      fullText += node.textContent || "";
      entries.push({ node: node, start: start, end: fullText.length });
    });
    var segments = this.sentenceSegments(fullText);
    var self = this;

    function boundaryAt(index, endBoundary) {
      if (index <= 0) return { node: entries[0].node, offset: 0 };
      for (var i = 0; i < entries.length; i++) {
        var entry = entries[i];
        if (index < entry.end || (endBoundary && index === entry.end)) {
          return {
            node: entry.node,
            offset: Math.max(0, Math.min(index - entry.start, (entry.node.textContent || "").length)),
          };
        }
      }
      var last = entries[entries.length - 1];
      return { node: last.node, offset: (last.node.textContent || "").length };
    }

    return segments.map(function (segment) {
      var startBoundary = boundaryAt(segment.start, false);
      var endBoundary = boundaryAt(segment.end, true);
      var prefix = fullText.slice(0, segment.start);
      var value = fullText.slice(segment.start, segment.end);
      var rootStartChar = self.nodeCharAnchors.get(root) || 0;
      var rootStartRaw = self.nodeRawAnchors.get(root) || 0;
      return {
        node: root,
        startChar: rootStartChar + countMatchable(prefix),
        endChar: rootStartChar + countMatchable(prefix + value),
        startRaw: rootStartRaw + countRaw(prefix),
        endRaw: rootStartRaw + countRaw(prefix + value),
        text: value,
        ids: self.idsForNode(root),
        mediaOnly: false,
        render: function () {
          var range = document.createRange();
          range.setStart(startBoundary.node, startBoundary.offset);
          range.setEnd(endBoundary.node, endBoundary.offset);
          var fragment = document.createDocumentFragment();
          if (root.nodeType === Node.ELEMENT_NODE) {
            var wrapper = root.cloneNode(false);
            wrapper.appendChild(range.cloneContents());
            fragment.appendChild(wrapper);
          } else {
            fragment.appendChild(range.cloneContents());
          }
          return fragment;
        },
      };
    }).filter(function (descriptor) { return descriptor.text.trim().length > 0; });
  },

  combineDescriptors: function (items) {
    if (items.length === 1) return items[0];
    return {
      node: null,
      parts: items.slice(),
      startChar: items[0].startChar,
      endChar: items[items.length - 1].endChar,
      startRaw: items[0].startRaw,
      endRaw: items[items.length - 1].endRaw,
      text: items.map(function (item) { return item.text; }).join(""),
      ids: unionIds(items),
      mediaOnly: items.every(function (item) { return item.mediaOnly; }),
      render: function () {
        var fragment = document.createDocumentFragment();
        items.forEach(function (item) { fragment.appendChild(item.render()); });
        return fragment;
      },
    };
  },

  dialogueDepthAfter: function (text, initialDepth) {
    var depth = initialDepth || 0;
    Array.from(text || "").forEach(function (character) {
      if (character === "「" || character === "『") depth += 1;
      if (character === "」" || character === "』") depth = Math.max(0, depth - 1);
    });
    return depth;
  },

  buildSentenceScreens: function (blocks) {
    var units = [];
    blocks.forEach(function (block) {
      if (block.mediaOnly) {
        units.push(block);
        return;
      }
      var sentences = visualNovel.sentenceDescriptorsForNode(block.node);
      if (sentences.length) units.push.apply(units, sentences);
      else units.push(block);
    });

    var screens = [];
    var pending = [];
    var dialogueDepth = 0;
    function flush() {
      if (!pending.length) return;
      screens.push(visualNovel.combineDescriptors(pending));
      pending = [];
      dialogueDepth = 0;
    }
    units.forEach(function (unit) {
      if (unit.mediaOnly) {
        flush();
        screens.push(unit);
        return;
      }
      pending.push(unit);
      dialogueDepth = visualNovel.dialogueDepthAfter(unit.text, dialogueDepth);
      var reachedLimit = pending.length >= visualNovel.sentencesPerScreen;
      if (reachedLimit && (!visualNovel.preserveDialogue || dialogueDepth === 0)) flush();
    });
    flush();
    return screens;
  },

  descriptorFits: function (descriptor) {
    if (!this.screen || !descriptor) return true;
    this.replaceScreenContent(descriptor);
    var content = this.screen.firstElementChild;
    if (!content) return true;
    var screenRect = this.screen.getBoundingClientRect();
    var contentRect = content.getBoundingClientRect();
    var tolerance = 1;
    return content.scrollWidth <= this.screen.clientWidth + tolerance
      && content.scrollHeight <= this.screen.clientHeight + tolerance
      && contentRect.left >= screenRect.left - tolerance
      && contentRect.top >= screenRect.top - tolerance
      && contentRect.right <= screenRect.right + tolerance
      && contentRect.bottom <= screenRect.bottom + tolerance;
  },

  fitScreensToViewport: function (screens) {
    var fitted = [];
    screens.forEach(function (screen) {
      if (visualNovel.descriptorFits(screen)) {
        fitted.push(screen);
        return;
      }
      var parts = screen.parts && screen.parts.length
        ? screen.parts
        : (screen.node ? visualNovel.sentenceDescriptorsForNode(screen.node) : []);
      if (parts.length <= 1) {
        fitted.push(screen);
        return;
      }
      var group = [];
      parts.forEach(function (part) {
        var candidate = visualNovel.combineDescriptors(group.concat([part]));
        if (group.length && !visualNovel.descriptorFits(candidate)) {
          fitted.push(visualNovel.combineDescriptors(group));
          group = [part];
        } else {
          group.push(part);
        }
      });
      if (group.length) fitted.push(visualNovel.combineDescriptors(group));
    });
    while (this.screen.firstChild) this.screen.removeChild(this.screen.firstChild);
    return fitted;
  },

  buildScreens: function () {
    var blocks = [];
    this.collectBlockDescriptors(this.sourceRoot, blocks);
    var screens = this.screenMode === "sentences"
      ? this.buildSentenceScreens(blocks)
      : blocks;
    if (!screens.length) {
      screens.push({
        node: null,
        startChar: 0,
        endChar: 0,
        startRaw: 0,
        endRaw: 0,
        text: "",
        ids: new Set(),
        mediaOnly: false,
        render: function () { return document.createDocumentFragment(); },
      });
    }
    this.screens = this.fitScreensToViewport(screens);
    this.currentScreenIndex = 0;
  },

  replaceScreenContent: function (descriptor) {
    while (this.screen.firstChild) this.screen.removeChild(this.screen.firstChild);
    var content = document.createElement("div");
    content.className = "niratan-vn-content";
    content.appendChild(descriptor.render());
    this.screen.appendChild(content);
  },

  renderScreen: function (index, fullyRevealed) {
    if (!this.screens.length) return;
    var safeIndex = Math.min(this.screens.length - 1, Math.max(0, Math.floor(index)));
    this.clearRevealTimer();
    this.currentScreenIndex = safeIndex;
    var descriptor = this.screens[safeIndex];
    this.replaceScreenContent(descriptor);
    window.__niratanTextMatchableOffsetBase = descriptor.startChar;
    window.__niratanTextRawOffsetBase = descriptor.startRaw;

    if (this.adapter && typeof this.adapter.setupImages === "function") {
      this.adapter.setupImages(this.screen);
    }
    if (fullyRevealed || this.revealSpeed <= 0 || descriptor.mediaOnly) {
      this.revealComplete = true;
      this.applyCurrentHighlights();
    } else {
      this.hideCurrentScreenForReveal();
    }
    document.dispatchEvent(new Event("niratan-reader-content-changed"));
  },

  currentDescriptor: function () {
    return this.screens[this.currentScreenIndex] || null;
  },

  currentRange: function () {
    var descriptor = this.currentDescriptor();
    return descriptor ? {
      startChar: descriptor.startChar,
      endChar: descriptor.endChar,
      startRaw: descriptor.startRaw,
      endRaw: descriptor.endRaw,
    } : { startChar: 0, endChar: 0, startRaw: 0, endRaw: 0 };
  },

  setRevealSpeed: function (speed) {
    this.revealSpeed = clamp(speed, 0, 120, 0);
    if (!this.revealComplete) {
      this.clearRevealTimer();
      this.scheduleRevealTick();
    }
  },

  hideCurrentScreenForReveal: function () {
    this.revealSegments = [];
    var textNodes = this.readableTextNodes(this.screen);
    var self = this;
    textNodes.forEach(function (node) { self.prepareTextNodeForReveal(node); });
    if (!this.revealSegments.length) {
      this.revealComplete = true;
      this.applyCurrentHighlights();
      return;
    }
    this.revealComplete = false;
    this.scheduleRevealTick();
  },

  prepareTextNodeForReveal: function (node) {
    var parent = node.parentNode;
    var text = node.textContent || "";
    if (!parent || !text) return;
    var visible = document.createTextNode("");
    var hidden = document.createElement("span");
    hidden.setAttribute("data-niratan-vn-unrevealed", "");
    hidden.setAttribute("aria-hidden", "true");
    var hiddenText = document.createTextNode(text);
    hidden.appendChild(hiddenText);
    parent.insertBefore(visible, node);
    parent.insertBefore(hidden, node);
    parent.removeChild(node);
    this.revealSegments.push({
      visible: visible,
      hidden: hidden,
      hiddenText: hiddenText,
      characters: Array.from(text),
      revealed: 0,
    });
  },

  scheduleRevealTick: function () {
    if (this.revealComplete) return;
    if (this.revealSpeed <= 0) {
      this.completeReveal();
      return;
    }
    var self = this;
    this.revealTimer = setTimeout(function () {
      self.revealTimer = null;
      self.revealNextCharacter();
    }, Math.max(1, 1000 / this.revealSpeed));
  },

  revealNextCharacter: function () {
    for (var i = 0; i < this.revealSegments.length; i++) {
      var segment = this.revealSegments[i];
      if (segment.revealed >= segment.characters.length) continue;
      segment.revealed += 1;
      segment.visible.textContent = segment.characters.slice(0, segment.revealed).join("");
      segment.hiddenText.textContent = segment.characters.slice(segment.revealed).join("");
      this.scheduleRevealTick();
      return;
    }
    this.completeReveal();
  },

  clearRevealTimer: function () {
    if (this.revealTimer !== null) clearTimeout(this.revealTimer);
    this.revealTimer = null;
  },

  completeReveal: function () {
    this.clearRevealTimer();
    this.revealSegments.forEach(function (segment) {
      segment.visible.textContent = segment.characters.join("");
      if (segment.hidden.parentNode) segment.hidden.parentNode.removeChild(segment.hidden);
    });
    this.revealSegments = [];
    this.revealComplete = true;
    this.applyCurrentHighlights();
    document.dispatchEvent(new Event("niratan-reader-content-changed"));
  },

  paginate: function (direction) {
    var selection = window.getSelection && window.getSelection();
    if (selection && selection.isCollapsed === false) return "limit";
    if (direction === "forward") {
      if (!this.revealComplete) {
        this.completeReveal();
        return "scrolled";
      }
      if (this.currentScreenIndex >= this.screens.length - 1) return "limit";
      this.renderScreen(this.currentScreenIndex + 1, false);
      return "scrolled";
    }
    if (this.currentScreenIndex <= 0) return "limit";
    this.renderScreen(this.currentScreenIndex - 1, true);
    return "scrolled";
  },

  calculateProgress: function () {
    if (!this.screens.length) return 0;
    var descriptor = this.currentDescriptor();
    if (this.totalChars > 0 && descriptor.endChar > 0) {
      return Math.min(1, Math.max(0, descriptor.endChar / this.totalChars));
    }
    return this.screens.length <= 1 ? 0 : this.currentScreenIndex / (this.screens.length - 1);
  },

  screenIndexForProgress: function (progress) {
    if (!this.screens.length || progress <= 0) return 0;
    if (progress >= 0.99) return this.screens.length - 1;
    if (this.totalChars <= 0) {
      return Math.min(this.screens.length - 1, Math.floor(progress * this.screens.length));
    }
    var target = progress * this.totalChars;
    for (var i = 0; i < this.screens.length; i++) {
      if (this.screens[i].endChar >= target) return i;
    }
    return this.screens.length - 1;
  },

  restoreProgress: function (progress, restoreTarget) {
    var index = restoreTarget === "start"
      ? 0
      : restoreTarget === "end"
        ? this.screens.length - 1
        : this.screenIndexForProgress(progress);
    var revealInitial = restoreTarget === "start" || (!restoreTarget && progress <= 0);
    this.renderScreen(index, !revealInitial);
  },

  jumpToFragment: function (fragment) {
    var target = String(fragment || "").trim();
    if (!target) return false;
    for (var i = 0; i < this.screens.length; i++) {
      if (this.screens[i].ids.has(target)) {
        this.renderScreen(i, true);
        return true;
      }
    }
    return false;
  },

  screenIndexForCharOffset: function (offset) {
    var target = Math.max(0, Number(offset) || 0);
    for (var i = 0; i < this.screens.length; i++) {
      var screen = this.screens[i];
      if (target >= screen.startChar && target < Math.max(screen.startChar + 1, screen.endChar)) {
        return i;
      }
    }
    return this.screens.length - 1;
  },

  showCharOffset: function (offset) {
    var index = this.screenIndexForCharOffset(offset);
    if (index < 0 || index === this.currentScreenIndex) return false;
    this.renderScreen(index, true);
    return true;
  },

  reflow: function (progress) {
    this.buildScreens();
    this.renderScreen(this.screenIndexForProgress(progress), true);
  },

  metrics: function () {
    return {
      pageIndex: this.currentScreenIndex,
      pageCount: this.screens.length,
      totalChars: this.totalChars,
      progress: this.calculateProgress(),
    };
  },

  patchHighlights: function () {
    var highlights = window.niratanHighlights;
    if (!highlights || highlights.niratanVisualNovelPatched) return;
    var self = this;
    var originalCollectSegments = highlights.collectSegments.bind(highlights);
    var originalCreateHighlight = highlights.createHighlight.bind(highlights);
    var originalRemoveHighlight = highlights.removeHighlight.bind(highlights);

    highlights.collectSegments = function (offset, length) {
      if (self.localHighlightOperation) return originalCollectSegments(offset, length);
      var range = self.currentRange();
      var requestedStart = Math.max(0, Number(offset) || 0);
      var requestedEnd = requestedStart + Math.max(0, Number(length) || 0);
      var visibleStart = Math.max(requestedStart, range.startRaw);
      var visibleEnd = Math.min(requestedEnd, range.endRaw);
      if (visibleEnd <= visibleStart) return [];
      return originalCollectSegments(visibleStart - range.startRaw, visibleEnd - visibleStart);
    };

    highlights.createHighlight = function (color, id) {
      self.localHighlightOperation = true;
      var result;
      try {
        result = originalCreateHighlight(color, id);
      } finally {
        self.localHighlightOperation = false;
      }
      if (!result) return result;
      var range = self.currentRange();
      var adjusted = {
        start: result.start + range.startChar,
        offset: result.offset + range.startRaw,
        text: result.text,
      };
      self.highlights = self.highlights.filter(function (highlight) {
        return String(highlight.id) !== String(id);
      });
      self.highlights.push({ id: String(id), color: color, offset: adjusted.offset, text: adjusted.text });
      return adjusted;
    };

    highlights.removeHighlight = function (id) {
      self.highlights = self.highlights.filter(function (highlight) {
        return String(highlight.id) !== String(id);
      });
      return originalRemoveHighlight(id);
    };
    highlights.niratanVisualNovelPatched = true;
  },

  applyCurrentHighlights: function () {
    if (!this.revealComplete || !window.niratanHighlights) return;
    window.niratanHighlights.applyHighlights(this.highlights);
  },
};

window.niratanVisualNovel = visualNovel;
})();
