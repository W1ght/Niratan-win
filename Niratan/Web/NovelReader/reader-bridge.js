(function () {
"use strict";

const VERSION = 1;

window.__niratanReaderState = {
  bridgeReady: false,
  bookTitle: "",
  statusText: "",
  sectionIndex: 0,
  sectionCount: 0,
  hasRenderedText: false,
  readerRect: null,
  contentRect: null,
  layoutMetrics: null,
  error: null,
};

let currentChapter = null;
let sasayakiHighlightGeneration = 0;

var reader = {
  pageHeight: 0,
  pageWidth: 0,
  columnGap: 0,
  lastProgress: 0,
  ttuRegexNegated: /[^0-9A-Za-z○◯々-〇〻ぁ-ゖゝ-ゞァ-ヺー０-９Ａ-Ｚａ-ｚｦ-ﾝ\p{Radical}\p{Unified_Ideograph}]+/gimu,
  ttuRegex: /[0-9A-Za-z○◯々-〇〻ぁ-ゖゝ-ゞァ-ヺー０-９Ａ-Ｚａ-ｚｦ-ﾝ\p{Radical}\p{Unified_Ideograph}]/iu,
  nodeStartOffsets: new WeakMap(),
  nodeOffsetsGeneration: 0,
  nodeOffsetsReadyGeneration: -1,
  paginationMetrics: null,

  isVertical: function () {
    return getComputedStyle(document.body).writingMode === "vertical-rl";
  },

  isFurigana: function (node) {
    var el = node.nodeType === Node.TEXT_NODE ? node.parentElement : node;
    return !!(el && el.closest("rt, rp"));
  },

  normalizeText: function (text) {
    return (text || "").replace(this.ttuRegexNegated, "");
  },

  countChars: function (text) {
    return Array.from(this.normalizeText(text)).length;
  },

  isMatchableChar: function (char) {
    return this.ttuRegex.test(char || "");
  },

  textOffsetForCharCount: function (node, targetCount) {
    var text = node.textContent || "";
    var count = 0;
    var offset = 0;
    while (offset < text.length) {
      var ch = String.fromCodePoint(text.codePointAt(offset));
      if (this.isMatchableChar(ch)) {
        if (count >= targetCount) return offset;
        count += 1;
      }
      offset += ch.length;
    }
    return text.length;
  },

  createWalker: function (rootNode) {
    var root = rootNode || document.body;
    return document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
      acceptNode: function (n) {
        return reader.isFurigana(n)
          ? NodeFilter.FILTER_REJECT
          : NodeFilter.FILTER_ACCEPT;
      },
    });
  },

  getRect: function (target) {
    var rect = target.getClientRects()[0];
    return rect || target.getBoundingClientRect();
  },

  buildNodeOffsets: function () {
    var offsets = new WeakMap();
    var walker = this.createWalker();
    var count = 0;
    var node;
    while ((node = walker.nextNode())) {
      offsets.set(node, count);
      count += this.countChars(node.textContent);
    }
    this.nodeStartOffsets = offsets;
    this.nodeOffsetsReadyGeneration = this.nodeOffsetsGeneration;
    this.paginationMetrics = null;
  },

  invalidateNodeOffsets: function () {
    this.nodeOffsetsGeneration += 1;
    this.nodeOffsetsReadyGeneration = -1;
    this.paginationMetrics = null;
  },

  ensureNodeOffsets: function () {
    if (this.nodeOffsetsReadyGeneration !== this.nodeOffsetsGeneration) {
      this.buildNodeOffsets();
    }
    return this.nodeStartOffsets;
  },

  isIgnoredWheelTarget: function (target) {
    var element = target instanceof Element ? target : target?.parentElement;
    if (!element) return false;

    return !!element.closest([
      "input",
      "textarea",
      "select",
      "button",
      "[contenteditable=\"true\"]",
      "[data-niratan-popup]",
      ".popup",
      ".dictionary-popup",
      ".popover",
      "[role=\"dialog\"]",
    ].join(","));
  },

  registerWheelNavigation: function (enabled) {
    window.niratanWheelNavigationEnabled = !!enabled;
    if (window.niratanWheelNavigationRegistered) return;

    window.niratanWheelNavigationRegistered = true;
    window.niratanLastWheelNavigationTime = 0;

    document.addEventListener("wheel", function (event) {
      if (!window.niratanWheelNavigationEnabled) return;
      if (event.ctrlKey || event.metaKey || event.altKey || event.shiftKey) return;
      if (Math.abs(event.deltaX) > Math.abs(event.deltaY) || event.deltaY === 0) return;
      if (reader.isIgnoredWheelTarget(event.target)) return;
      if (window.getSelection && window.getSelection()?.isCollapsed === false) return;

      var now = Date.now();
      if (now - window.niratanLastWheelNavigationTime < 170) {
        event.preventDefault();
        return;
      }

      var direction = event.deltaY > 0 ? "forward" : "backward";
      window.niratanLastWheelNavigationTime = now;
      event.preventDefault();
      requestPageNavigation(direction);
    }, { passive: false });
  },

  bottomOverlap: function () {
    return this.isVertical() ? 22 : 0;
  },

  currentColumnGap: function () {
    var value = parseFloat(getComputedStyle(document.body).columnGap);
    return Number.isFinite(value) ? value : 0;
  },

  currentSafeInline: function () {
    var value = parseFloat(getComputedStyle(document.body).paddingLeft);
    return Number.isFinite(value) ? value : 0;
  },

  currentSafeBlock: function () {
    var value = parseFloat(getComputedStyle(document.body).paddingTop);
    return Number.isFinite(value) ? value : 0;
  },

  currentImageMaxWidth: function (pageWidth) {
    if (!this.isVertical()) {
      var columnWidth = parseFloat(getComputedStyle(document.body).columnWidth);
      if (Number.isFinite(columnWidth) && columnWidth > 0) return columnWidth;
    }
    return Math.max(1, Math.floor(pageWidth - (this.currentSafeInline() * 2)));
  },

  pageStep: function (context) {
    return context.pageSize;
  },

  getScrollContext: function () {
    var vertical = this.isVertical();
    var scrollEl = document.body;
    var pageSize = Math.max(
      1,
      vertical
        ? this.pageHeight || window.innerHeight
        : this.pageWidth || window.innerWidth
    );
    var totalSize = vertical
      ? scrollEl.scrollHeight
      : scrollEl.scrollWidth;
    var maxScroll = Math.max(0, totalSize - pageSize);
    return {
      vertical: vertical,
      scrollEl: scrollEl,
      pageSize: pageSize,
      maxScroll: maxScroll,
    };
  },

  getPagePosition: function (context) {
    var c = context || this.getScrollContext();
    return c.vertical ? c.scrollEl.scrollTop : c.scrollEl.scrollLeft;
  },

  assignPagePosition: function (context, position) {
    if (context.vertical) {
      context.scrollEl.scrollTop = position;
    } else {
      context.scrollEl.scrollLeft = position;
    }
    this.lockRootViewport();
  },

  setPagePosition: function (context, position) {
    var clamped = Math.min(Math.max(0, position), context.maxScroll);
    window.lastPageScroll = clamped;
    this.assignPagePosition(context, clamped);
    return clamped;
  },

  lockRootViewport: function () {
    var root = document.documentElement;
    var didScroll = false;
    if (root.scrollTop !== 0) { root.scrollTop = 0; didScroll = true; }
    if (root.scrollLeft !== 0) { root.scrollLeft = 0; didScroll = true; }
    if (window.scrollX !== 0 || window.scrollY !== 0) {
      window.scrollTo(0, 0);
      didScroll = true;
    }
    return didScroll;
  },

  alignToPage: function (context, offset) {
    var c = context || this.getScrollContext();
    var step = c.pageSize;
    if (step <= 0) return 0;
    return Math.min(Math.max(0, Math.floor(Math.max(0, offset) / step) * step), c.maxScroll);
  },

  buildPaginationMetrics: function () {
    this.ensureNodeOffsets();
    var context = this.getScrollContext();
    var currentScroll = this.getPagePosition(context);
    var step = this.pageStep(context);
    var maxAlignedScroll =
      Math.floor(context.maxScroll / step) * step;
    if (context.pageSize <= 0) {
      var empty = { minScroll: 0, maxScroll: 0, totalChars: 0, progressStops: [] };
      this.paginationMetrics = empty;
      return empty;
    }

    var lastContentEdge = 0;
    var firstContentEdge = null;
    var progressStops = [];
    var exploredChars = 0;
    var totalChars = 0;
    var walker = this.createWalker();
    var node;
    while ((node = walker.nextNode())) {
      var nodeLen = this.countChars(node.textContent);
      totalChars += nodeLen;
      if (nodeLen <= 0) continue;
      var range = document.createRange();
      range.selectNodeContents(node);
      var rects = range.getClientRects();
      var progressRect = this.getRect(range);
      var nodeStartEdge =
        progressRect && progressRect.width > 0 && progressRect.height > 0
          ? (context.vertical ? progressRect.top : progressRect.left) + currentScroll
          : null;
      for (var i = 0; i < rects.length; i++) {
        var rect = rects[i];
        if (rect.width <= 0 || rect.height <= 0) continue;
        var startEdge = (context.vertical ? rect.top : rect.left) + currentScroll;
        var endEdge = (context.vertical ? rect.bottom : rect.right) + currentScroll;
        firstContentEdge =
          firstContentEdge === null
            ? startEdge
            : Math.min(firstContentEdge, startEdge);
        lastContentEdge = Math.max(lastContentEdge, endEdge);
      }
      if (nodeStartEdge !== null) {
        progressStops.push({
          scroll: nodeStartEdge,
          exploredChars: exploredChars + nodeLen,
        });
      }
      exploredChars += nodeLen;
    }

    var media = document.querySelectorAll("img, svg, image, video, canvas");
    for (var j = 0; j < media.length; j++) {
      var mediaRect = media[j].getBoundingClientRect();
      if (mediaRect.width <= 0 || mediaRect.height <= 0) continue;
      var ms = (context.vertical ? mediaRect.top : mediaRect.left) + currentScroll;
      var me = (context.vertical ? mediaRect.bottom : mediaRect.right) + currentScroll;
      firstContentEdge =
        firstContentEdge === null ? ms : Math.min(firstContentEdge, ms);
      lastContentEdge = Math.max(lastContentEdge, me);
    }

    var minScroll =
      firstContentEdge === null
        ? 0
        : Math.min(
            maxAlignedScroll,
            Math.floor(Math.max(0, firstContentEdge) / step) * step
          );
    var lastContentScroll =
      lastContentEdge <= 0
        ? 0
        : Math.floor(Math.max(0, lastContentEdge - 1) / step) *
          step;
    var maxScroll = Math.min(maxAlignedScroll, lastContentScroll);
    progressStops.sort(function (a, b) {
      return a.scroll - b.scroll;
    });

    var metrics = {
      minScroll: minScroll,
      maxScroll: maxScroll,
      totalChars: totalChars,
      progressStops: progressStops,
    };
    this.paginationMetrics = metrics;
    return metrics;
  },

  contentFirstPageScroll: function (context) {
    var c = context || this.getScrollContext();
    var metrics = this.paginationMetrics || this.buildPaginationMetrics();
    return metrics.minScroll;
  },

  contentLastPageScroll: function (context) {
    var c = context || this.getScrollContext();
    var metrics = this.paginationMetrics || this.buildPaginationMetrics();
    return metrics.maxScroll;
  },

  calculateProgress: function () {
    var context = this.getScrollContext();
    var walker = this.createWalker();
    var totalChars = 0;
    var exploredChars = 0;
    var node;
    while ((node = walker.nextNode())) {
      var nodeLen = this.countChars(node.textContent);
      totalChars += nodeLen;
      if (nodeLen > 0)
        exploredChars += this.countCharsBeforeViewport(node, context);
    }
    return totalChars > 0 ? exploredChars / totalChars : 0;
  },

  countCharsBeforeViewport: function (node, context) {
    var text = node.textContent || "";
    var totalChars = this.countChars(text);
    if (totalChars <= 0) return 0;
    var range = document.createRange();
    range.selectNodeContents(node);
    var rects = range.getClientRects();
    if (!rects.length) return 0;
    var minStart = Infinity;
    var maxEnd = -Infinity;
    for (var i = 0; i < rects.length; i++) {
      var rect = rects[i];
      if (rect.width <= 0 || rect.height <= 0) continue;
      var start = context.vertical ? rect.top : rect.left;
      var end = context.vertical ? rect.bottom : rect.right;
      minStart = Math.min(minStart, start);
      maxEnd = Math.max(maxEnd, end);
    }
    if (maxEnd <= 0) return totalChars;
    if (minStart >= 0 || minStart === Infinity) return 0;

    var offsets = [];
    var prefixCounts = [0];
    var count = 0;
    var offset = 0;
    while (offset < text.length) {
      offsets.push(offset);
      var ch = String.fromCodePoint(text.codePointAt(offset));
      offset += ch.length;
      if (this.isMatchableChar(ch)) count += 1;
      prefixCounts.push(count);
    }
    var low = 0;
    var high = offsets.length - 1;
    var firstVisible = offsets.length;
    while (low <= high) {
      var mid = Math.floor((low + high) / 2);
      if (this.isTextOffsetBeforeViewport(node, offsets[mid], text, context)) {
        low = mid + 1;
      } else {
        firstVisible = mid;
        high = mid - 1;
      }
    }
    return prefixCounts[firstVisible];
  },

  isTextOffsetBeforeViewport: function (node, offset, text, context) {
    var ch = String.fromCodePoint(text.codePointAt(offset));
    if (!ch) return false;
    var range = document.createRange();
    range.setStart(node, offset);
    range.setEnd(node, offset + ch.length);
    var rect = this.getRect(range);
    if (!rect || rect.width <= 0 || rect.height <= 0) return false;
    return (context.vertical ? rect.bottom : rect.right) <= 0;
  },

  handlePagedBodyScroll: function () {
    this.lockRootViewport();
    var context = this.getScrollContext();
    if (context.pageSize <= 0) return;
    var currentScroll = this.getPagePosition(context);
    var step = this.pageStep(context);
    var snappedScroll = Math.round(currentScroll / step) * step;
    var maxScroll = context.maxScroll;
    snappedScroll = Math.min(Math.max(0, snappedScroll), maxScroll);
    if (Math.abs(currentScroll - snappedScroll) > 1) {
      this.assignPagePosition(context, window.lastPageScroll || 0);
    } else {
      window.lastPageScroll = snappedScroll;
    }
  },

  registerSnapScroll: function (initialScroll) {
    if (window.snapScrollRegistered) return;
    window.snapScrollRegistered = true;
    window.lastPageScroll = initialScroll;
    this.lockRootViewport();

    var self = this;
    document.body.addEventListener(
      "scroll",
      function () {
        self.handlePagedBodyScroll();
      },
      { passive: true }
    );
  },

  paginate: function (direction) {
    var context = this.getScrollContext();
    if (context.pageSize <= 0) return "limit";
    var step = this.pageStep(context);
    var currentScroll = this.getPagePosition(context);
    var metrics = this.paginationMetrics || this.buildPaginationMetrics();
    var minAlignedScroll = metrics.minScroll;
    var maxAlignedScroll = metrics.maxScroll;

    if (direction === "forward") {
      var nextScroll = Math.round(currentScroll / step) * step + step;
      if (nextScroll <= maxAlignedScroll + 1) {
        this.setPagePosition(context, nextScroll);
        this.lastProgress = this.calculateProgress();
        return "scrolled";
      }
      return "limit";
    } else {
      var prevScroll = Math.round(currentScroll / step) * step - step;
      if (currentScroll > minAlignedScroll + 1) {
        prevScroll = Math.max(minAlignedScroll, prevScroll);
        this.setPagePosition(context, prevScroll);
        this.lastProgress = this.calculateProgress();
        return "scrolled";
      }
      return "limit";
    }
  },

  restoreProgress: async function (progress, navigationGeneration, restoreTarget, renderAttemptId) {
    renderAttemptId = renderAttemptId ?? currentChapter?.renderAttemptId ?? 0;
    await document.fonts.ready;
    var context = this.getScrollContext();
    var targetScroll;
    if (restoreTarget === "start") {
      targetScroll = this.contentFirstPageScroll(context);
    } else if (restoreTarget === "end") {
      targetScroll = this.contentLastPageScroll(context);
    } else if (context.pageSize <= 0 || progress <= 0) {
      targetScroll = this.contentFirstPageScroll(context);
    } else if (progress >= 0.99) {
      targetScroll = this.contentLastPageScroll(context);
    } else {
      var walker = this.createWalker();
      var totalChars = 0;
      var node;
      while ((node = walker.nextNode())) {
        totalChars += this.countChars(node.textContent);
      }
      var targetCharCount = Math.ceil(totalChars * progress);
      var runningSum = 0;
      var targetNode = null;
      var targetOffset = 0;
      walker = this.createWalker();
      while ((node = walker.nextNode())) {
        var nodeLen = this.countChars(node.textContent);
        if (runningSum + nodeLen > targetCharCount) {
          targetNode = node;
          targetOffset = this.textOffsetForCharCount(
            node,
            Math.max(0, targetCharCount - runningSum)
          );
          break;
        }
        runningSum += nodeLen;
      }
      if (targetNode) {
        var range = document.createRange();
        var targetText = targetNode.textContent || "";
        var targetChar = String.fromCodePoint(targetText.codePointAt(targetOffset));
        range.setStart(targetNode, targetOffset);
        range.setEnd(
          targetNode,
          Math.min(targetText.length, targetOffset + Math.max(1, targetChar.length))
        );
        var rect = this.getRect(range);
        var currentScroll = this.getPagePosition(context);
        var anchor =
          (context.vertical ? rect.top : rect.left) + currentScroll;
        targetScroll = this.alignToPage(context, anchor);
      } else {
        targetScroll = this.contentFirstPageScroll(context);
      }
    }

    this.setPagePosition(context, Math.max(0, targetScroll));
    await new Promise(function (resolve) {
      requestAnimationFrame(function () { requestAnimationFrame(resolve); });
    });
    context = this.getScrollContext();
    if (restoreTarget === "start")
      targetScroll = this.contentFirstPageScroll(context);
    else if (restoreTarget === "end")
      targetScroll = this.contentLastPageScroll(context);
    this.setPagePosition(context, Math.max(0, targetScroll));
    this.registerSnapScroll(targetScroll);
    this.lastProgress = this.calculateProgress();
    notifyRestoreComplete(navigationGeneration, renderAttemptId);
  },

  jumpToFragment: async function (fragment, navigationGeneration, renderAttemptId) {
    renderAttemptId = renderAttemptId ?? currentChapter?.renderAttemptId ?? 0;
    await document.fonts.ready;
    var context = this.getScrollContext();
    var rawFragment = (fragment || "").trim();
    var target =
      rawFragment &&
      (document.getElementById(rawFragment) ||
        document.getElementsByName(rawFragment)[0]);
    if (context.pageSize <= 0 || !target) {
      this.registerSnapScroll(this.getPagePosition(context));
      notifyRestoreComplete(navigationGeneration, renderAttemptId);
      return false;
    }
    var rect = this.getRect(target);
    var currentScroll = this.getPagePosition(context);
    var anchor = (context.vertical ? rect.top : rect.left) + currentScroll;
    var targetScroll = this.alignToPage(context, anchor);
    this.setPagePosition(context, targetScroll);
    requestAnimationFrame(function () {
      var ctx = reader.getScrollContext();
      reader.setPagePosition(ctx, targetScroll);
      reader.registerSnapScroll(targetScroll);
      requestAnimationFrame(function () {
        notifyRestoreComplete(navigationGeneration, renderAttemptId);
      });
    });
    return true;
  },

  scrollToRange: function (range) {
    var context = this.getScrollContext();
    if (context.pageSize <= 0) return false;

    var rect = this.getRect(range);
    if (!rect || rect.width <= 0 || rect.height <= 0) return false;

    var currentScroll = this.getPagePosition(context);
    var anchor =
      (context.vertical
        ? (rect.top + rect.bottom) / 2
        : (rect.left + rect.right) / 2) + currentScroll;
    var targetScroll = this.alignToPage(context, anchor);
    if (Math.abs(targetScroll - currentScroll) <= 1) return false;

    this.setPagePosition(context, targetScroll);
    requestAnimationFrame(function () {
      var ctx = reader.getScrollContext();
      reader.setPagePosition(ctx, targetScroll);
    });
    return true;
  },

  reflow: function (progress) {
    var overlap = this.bottomOverlap();
    var pageHeight = window.innerHeight + overlap;
    var pageWidth = Math.max(1, Math.floor(window.innerWidth));
    document.documentElement.style.setProperty("--page-height", pageHeight + "px");
    document.documentElement.style.setProperty("--page-width", pageWidth + "px");
    document.documentElement.style.setProperty(
      "--niratan-image-max-width",
      this.currentImageMaxWidth(pageWidth) + "px"
    );
    document.documentElement.style.setProperty(
      "--niratan-image-max-height",
      Math.max(1, Math.floor(pageHeight - (this.currentSafeBlock() * 2) - overlap)) + "px"
    );
    this.pageHeight = pageHeight;
    this.pageWidth = pageWidth;
    this.columnGap = this.currentColumnGap();
    this.paginationMetrics = null;
    window.snapScrollRegistered = false;
    window.lastPageScroll = 0;
    this.lastProgress = progress != null ? progress : this.lastProgress;

    var self = this;
    return new Promise(function (resolve) {
      setTimeout(function () {
        self.buildNodeOffsets();
        self.restoreProgress(progress != null ? progress : 0).then(function () {
          updateDiagnostics();
          resolve();
        });
      }, 100);
    });
  },

  setupImage: function (element, source, wrap, blurElement) {
    if (!element || !source) return;

    var blurredTarget = blurElement || element;
    var clickTarget = element;
    if (window.__niratanBlurImages === true) {
      blurredTarget.classList.add("niratan-blurred");
      if (wrap) {
        clickTarget = document.createElement("div");
        clickTarget.className = "niratan-blur-wrapper";
        blurredTarget.before(clickTarget);
        clickTarget.appendChild(blurredTarget);
      }
    }

    clickTarget.addEventListener("click", function (event) {
      event.preventDefault();
      event.stopPropagation();
      if (blurredTarget.classList.contains("niratan-blurred")) {
        blurredTarget.classList.remove("niratan-blurred");
        return;
      }

      try {
        postToHost("imageTapped", {
          src: new URL(source, document.baseURI).href,
        });
      } catch (error) {
        logDebug("image-tap-invalid-source", { source: String(source) });
      }
    });
  },

  initialize: async function (initialProgress, navigationGeneration, restoreTarget, renderAttemptId) {
    if (reader.didInitialize) return;
    reader.didInitialize = true;

    var viewport = document.querySelector('meta[name="viewport"]');
    if (viewport) viewport.remove();
    var newViewport = document.createElement("meta");
    newViewport.name = "viewport";
    newViewport.content =
      "width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no";
    document.head.appendChild(newViewport);

    var overlap = this.bottomOverlap();
    var pageHeight = window.innerHeight + overlap;
    var pageWidth = Math.max(1, Math.floor(window.innerWidth));
    document.documentElement.style.setProperty(
      "--page-height",
      pageHeight + "px"
    );
    document.documentElement.style.setProperty(
      "--page-width",
      pageWidth + "px"
    );
    document.documentElement.style.setProperty(
      "--niratan-image-max-width",
      reader.currentImageMaxWidth(pageWidth) + "px"
    );
    document.documentElement.style.setProperty(
      "--niratan-image-max-height",
      Math.max(1, Math.floor(pageHeight - (reader.currentSafeBlock() * 2) - overlap)) + "px"
    );
    reader.pageHeight = pageHeight;
    reader.pageWidth = pageWidth;
    reader.columnGap = reader.currentColumnGap();

    Array.from(document.querySelectorAll("svg")).forEach(function (svg) {
      var svgImage = svg.querySelector("image");
      if (
        svgImage &&
        svg.getAttribute("preserveAspectRatio") === "none"
      ) {
        svg.setAttribute("preserveAspectRatio", "xMidYMid meet");
      }
      if (svgImage) {
        var svgSource = svgImage.getAttribute("href")
          || svgImage.getAttribute("xlink:href")
          || (svgImage.href && svgImage.href.baseVal);
        reader.setupImage(svgImage, svgSource, false, svg);
      }
    });

    var imagePromises = Array.from(document.querySelectorAll("img")).map(function (img) {
      return new Promise(function (resolve) {
        function mark() {
          var isGaiji = img.classList.contains("gaiji")
            || img.classList.contains("gaiji-line");
          if (!isGaiji && (img.naturalWidth > 256 || img.naturalHeight > 256)) {
            img.classList.add("block-img");
            reader.setupImage(img, img.currentSrc || img.src, true, img);
          }
          resolve();
        }
        if (img.complete && img.naturalWidth > 0) {
          mark();
        } else {
          img.onload = mark;
          img.onerror = function () { resolve(); };
        }
      });
    });

    var spacer = document.createElement("div");
    spacer.style.height = this.isVertical() ? "22px" : "100%";
    spacer.style.width = this.isVertical() ? "0" : "2.5vw";
    spacer.style.display = "block";
    spacer.style.breakInside = "avoid";
    document.body.appendChild(spacer);

    var progress = initialProgress != null ? initialProgress : 0;
    var self = this;
    await Promise.all(imagePromises);
    await new Promise(function (resolve) { setTimeout(resolve, 50); });
    self.buildNodeOffsets();
    if (window.niratanHighlights) {
      window.niratanHighlights.applyHighlights(window.__niratanChapterHighlights || []);
    }
    await self.restoreProgress(
      progress,
      navigationGeneration,
      restoreTarget,
      renderAttemptId
    );
    self.lastProgress = self.calculateProgress();
    updateDiagnostics();
    notifyChapterReady(navigationGeneration, renderAttemptId);
  },
};

function notifyRestoreComplete(navigationGeneration, renderAttemptId) {
  postToHost("restoreCompleted", {
    progress: reader.calculateProgress(),
    chapterIndex: currentChapter.index,
    renderAttemptId: renderAttemptId,
    navigationGeneration: navigationGeneration ?? null,
  });
}

function notifyChapterReady(navigationGeneration, renderAttemptId) {
  postToHost("chapterReady", {
    ...window.__niratanReaderState,
    chapterIndex: currentChapter.index,
    renderAttemptId: renderAttemptId,
    navigationGeneration: navigationGeneration ?? null,
  });
}

function postToHost(type, payload) {
  window.chrome?.webview?.postMessage({
    version: VERSION,
    type: type,
    payload: payload || {},
  });
}

function createBridgeErrorReporter() {
  var reported = false;
  return function reportBridgeError(error) {
    if (reported) return;
    reported = true;
    var msg = error instanceof Error ? error.message : String(error);
    window.__niratanReaderState.error = msg;
    logDebug("bridge-error", { error: msg });
    postToHost("error", { message: msg });
  };
}

var defaultShortcutBindings = {
  "popup.dismiss": { key: "Escape", control: false, shift: false, alt: false, windows: false },
  "dictionary.previousEntry": { key: "PageUp", control: false, shift: false, alt: true, windows: false },
  "dictionary.nextEntry": { key: "PageDown", control: false, shift: false, alt: true, windows: false },
  "reader.previousPage": { key: "LeftArrow", control: false, shift: false, alt: false, windows: false },
  "reader.nextPage": { key: "RightArrow", control: false, shift: false, alt: false, windows: false },
  "reader.close": { key: "Escape", control: false, shift: false, alt: false, windows: false },
  "reader.toggleFocusMode": { key: "f", control: false, shift: false, alt: false, windows: false },
  "reader.toggleStatistics": { key: "t", control: false, shift: false, alt: false, windows: false },
  "reader.toggleLyricsMode": { key: "l", control: false, shift: false, alt: false, windows: false },
  "sasayaki.previousCue": { key: "[", control: false, shift: false, alt: false, windows: false },
  "sasayaki.playPause": { key: "p", control: false, shift: false, alt: false, windows: false },
  "sasayaki.nextCue": { key: "]", control: false, shift: false, alt: false, windows: false },
  "sasayaki.replayCue": { key: "r", control: false, shift: false, alt: false, windows: false },
  "sasayaki.jumpCue": { key: "j", control: false, shift: false, alt: false, windows: false },
};

function keyboardEventToShortcutKey(event) {
  switch (event.key) {
    case "ArrowLeft": return "LeftArrow";
    case "ArrowRight": return "RightArrow";
    case "ArrowUp": return "UpArrow";
    case "ArrowDown": return "DownArrow";
    case "PageUp": return "PageUp";
    case "PageDown": return "PageDown";
    case "Escape": return "Escape";
    case " ": return "Space";
  }

  switch (event.code) {
    case "BracketLeft": return "[";
    case "BracketRight": return "]";
  }

  return event.key && event.key.length === 1
    ? event.key.toLowerCase()
    : event.key;
}

function modifiersMatch(event, binding) {
  return !!event.ctrlKey === !!binding.control
    && !!event.shiftKey === !!binding.shift
    && !!event.altKey === !!binding.alt
    && !!event.metaKey === !!binding.windows;
}

function shortcutActionForKeyboardEvent(event) {
  var bindings = window.__niratanReaderShortcutBindings || defaultShortcutBindings;
  var key = keyboardEventToShortcutKey(event);
  if (!key) return null;

  for (var actionId in bindings) {
    if (!Object.prototype.hasOwnProperty.call(bindings, actionId)) continue;
    var binding = bindings[actionId];
    if (binding && binding.key === key && modifiersMatch(event, binding)) {
      return actionId;
    }
  }

  return null;
}

function updateDiagnostics() {
  var context = reader.getScrollContext();
  var metrics =
    reader.paginationMetrics ||
    reader.buildPaginationMetrics();
  var step = reader.pageStep(context);
  var pageCount = context.pageSize > 0
    ? Math.max(1, Math.floor(context.maxScroll / step) + 1)
    : 0;
  var currentPage = context.pageSize > 0
    ? Math.floor(reader.getPagePosition(context) / step)
    : 0;
  var progress = reader.calculateProgress();
  reader.lastProgress = progress;
  var renderedText = document.body?.innerText?.trim()?.length ?? 0;

  window.__niratanReaderState = {
    bridgeReady: true,
    statusText: "Chapter loaded",
    sectionIndex: currentChapter?.index ?? 0,
    sectionCount: currentChapter?.totalChapters ?? 0,
    hasRenderedText: renderedText > 0,
    readerRect: {
      width: window.innerWidth,
      height: window.innerHeight,
    },
    contentRect: {
      width: document.body.scrollWidth,
      height: document.body.scrollHeight,
    },
    layoutMetrics: {
      viewportWidth: window.innerWidth,
      viewportHeight: window.innerHeight,
      devicePixelRatio: window.devicePixelRatio || 1,
      visualViewportWidth: window.visualViewport?.width || window.innerWidth,
      visualViewportHeight: window.visualViewport?.height || window.innerHeight,
      pageWidth: reader.pageWidth,
      pageHeight: reader.pageHeight,
      currentPage: currentPage,
      pageIndex: currentPage,
      pageCount: pageCount,
      totalChars: metrics.totalChars,
      progress: progress,
      minScroll: metrics.minScroll,
      maxScroll: metrics.maxScroll,
      scrollPosition: reader.getPagePosition(context),
      columnGap: reader.currentColumnGap(),
      safeInline: reader.currentSafeInline(),
      safeBlock: reader.currentSafeBlock(),
      pageStep: step,
      bodyTextLength: renderedText,
    },
    error: null,
  };
}

window.niratanGetDiagnostics = function () {
  updateDiagnostics();
  return JSON.stringify(window.__niratanReaderState, null, 2);
};

async function handleNavigate(direction) {
  var result = reader.paginate(direction);
  updateDiagnostics();
  postToHost("pageChanged", {
    direction: direction,
    result: result,
    progress: reader.calculateProgress(),
    state: window.__niratanReaderState,
  });
}

function requireObject(value, name) {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    throw new Error(name + " must be an object");
  }
  return value;
}

function requireInteger(value, name, minimum) {
  if (!Number.isSafeInteger(value) || value < minimum) {
    throw new Error(name + " must be an integer >= " + minimum);
  }
  return value;
}

function requireProgress(value, name) {
  if (typeof value !== "number" || !Number.isFinite(value) || value < 0 || value > 1) {
    throw new Error(name + " must be a finite number between 0 and 1");
  }
  return value;
}

function optionalGeneration(value, name) {
  if (value === undefined || value === null) return null;
  return requireInteger(value, name, 0);
}

async function handleMessage(event) {
  if (!event || event.source !== window.chrome?.webview) return;

  var reportOperationError = createBridgeErrorReporter();
  try {
    var message =
      typeof event.data === "string" ? JSON.parse(event.data) : event.data;
    requireObject(message, "message");
    if (typeof message.type !== "string" || !message.type) {
      throw new Error("message.type must be a non-empty string");
    }
    var payload = requireObject(message.payload, "message.payload");
    logDebug("rx-message", { type: message?.type });
    if (message?.version !== VERSION) {
      throw new Error(
        "Unsupported bridge message version: " + message?.version
      );
    }

    switch (message.type) {
      case "setChapter":
        var chapterIndex = requireInteger(payload.index, "payload.index", 0);
        var totalChapters = requireInteger(
          payload.totalChapters,
          "payload.totalChapters",
          1
        );
        var chapterAttemptId = requireInteger(
          payload.renderAttemptId,
          "payload.renderAttemptId",
          0
        );
        var chapterGeneration = optionalGeneration(
          payload.navigationGeneration,
          "payload.navigationGeneration"
        );
        var restoreTarget = payload.restoreTarget ?? null;
        if (restoreTarget !== null && restoreTarget !== "start" && restoreTarget !== "end") {
          throw new Error("payload.restoreTarget must be start, end, or null");
        }
        var progress = payload.progress === undefined
          ? 0
          : requireProgress(payload.progress, "payload.progress");
        currentChapter = {
          index: chapterIndex,
          totalChapters: totalChapters,
          renderAttemptId: chapterAttemptId,
        };
        logDebug("setChapter-received", { chapter: currentChapter, progress: progress });
        await reader.initialize(
          progress,
          chapterGeneration,
          restoreTarget,
          currentChapter.renderAttemptId
        );
        break;
      case "restoreProgress":
        if (!currentChapter) throw new Error("restoreProgress requires an active chapter");
        var restoreProgress = requireProgress(payload.progress, "payload.progress");
        var restoreGeneration = optionalGeneration(
          payload.navigationGeneration,
          "payload.navigationGeneration"
        );
        var restoreAttemptId = requireInteger(
          payload.renderAttemptId,
          "payload.renderAttemptId",
          0
        );
        currentChapter.renderAttemptId = restoreAttemptId;
        logDebug("restoreProgress-start", {
          progress: restoreProgress,
          pageHeight: reader.pageHeight,
          pageWidth: reader.pageWidth,
          bodyScrollWidth: document.body.scrollWidth,
          bodyClientWidth: document.body.clientWidth,
        });
        await reader.restoreProgress(
          restoreProgress,
          restoreGeneration,
          null,
          restoreAttemptId
        );
        updateDiagnostics();
        logDebug("restoreProgress-done", window.__niratanReaderState);
        notifyChapterReady(restoreGeneration, restoreAttemptId);
        break;
      case "jumpToFragment":
        if (!currentChapter) throw new Error("jumpToFragment requires an active chapter");
        if (typeof payload.fragment !== "string") {
          throw new Error("payload.fragment must be a string");
        }
        var fragmentGeneration = requireInteger(
          payload.navigationGeneration,
          "payload.navigationGeneration",
          0
        );
        var fragmentAttemptId = requireInteger(
          payload.renderAttemptId,
          "payload.renderAttemptId",
          0
        );
        currentChapter.renderAttemptId = fragmentAttemptId;
        await reader.jumpToFragment(
          payload.fragment,
          fragmentGeneration,
          fragmentAttemptId
        );
        updateDiagnostics();
        notifyChapterReady(fragmentGeneration, fragmentAttemptId);
        break;
      case "navigatePage":
        var authorizedDirection = payload.direction;
        if (authorizedDirection !== "forward" && authorizedDirection !== "backward") {
          throw new Error("Invalid authorized page direction");
        }
        await handleNavigate(authorizedDirection);
        break;
      case "setWheelNavigation":
        if (typeof payload.enabled !== "boolean") {
          throw new Error("payload.enabled must be a boolean");
        }
        reader.registerWheelNavigation(payload.enabled);
        break;
      case "highlightSasayakiCue":
        var sasayakiGeneration = requireInteger(
          payload.generation,
          "payload.generation",
          0
        );
        var startCodePoint = requireInteger(
          payload.startCodePoint,
          "payload.startCodePoint",
          0
        );
        var cueLength = requireInteger(payload.length, "payload.length", 0);
        if (typeof payload.autoScroll !== "boolean"
          || typeof payload.textColor !== "string"
          || typeof payload.backgroundColor !== "string") {
          throw new Error("Invalid highlightSasayakiCue payload");
        }
        var currentGeneration = sasayakiHighlightGeneration;
        if (currentGeneration > sasayakiGeneration) break;
        sasayakiHighlightGeneration = sasayakiGeneration;
        sasayaki.setColors(payload.textColor, payload.backgroundColor);
        var sasayakiProgress = sasayaki.highlightCue(
          startCodePoint,
          cueLength,
          payload.autoScroll
        );
        postToHost("sasayakiHighlightCompleted", {
          generation: sasayakiGeneration,
          progress: typeof sasayakiProgress === "number" && Number.isFinite(sasayakiProgress)
            ? sasayakiProgress
            : null,
        });
        break;
      case "clearSasayakiHighlight":
        sasayaki.clearHighlight();
        break;
      default:
        throw new Error("Unsupported bridge message type: " + message.type);
    }
  } catch (error) {
    reportOperationError(error);
  }
}

function requestPageNavigation(direction) {
  if (direction !== "forward" && direction !== "backward") return;
  postToHost("pageNavigationRequest", { direction: direction });
}

window.chrome?.webview?.addEventListener("message", handleMessage);

document.addEventListener("niratan-reader-content-changed", function () {
  reader.invalidateNodeOffsets();
});

document.addEventListener("click", function (event) {
  var anchor = event.target?.closest?.("a[href]");
  if (!anchor) return;

  event.preventDefault();
  event.stopPropagation();
  try {
    var url = new URL(anchor.getAttribute("href"), document.baseURI);
    if (url.protocol === "https:" && url.host === window.location.host) {
      postToHost("internalLink", { href: url.href });
    }
  } catch (_) {
    // Malformed and external EPUB links are intentionally not navigated.
  }
}, true);

document.addEventListener("keydown", function (event) {
  // Niratan ignores NSEvent.isARepeat for shortcuts. WebView2 must do the
  // equivalent so one physical key press dispatches at most once.
  if (event.defaultPrevented || event.repeat) return;

  var actionId = shortcutActionForKeyboardEvent(event);
  if (!actionId) return;

  event.preventDefault();
  postToHost("shortcut", {
    key: keyboardEventToShortcutKey(event),
    control: !!event.ctrlKey,
    shift: !!event.shiftKey,
    alt: !!event.altKey,
    windows: !!event.metaKey,
  });
});

window.__niratanReaderState.bridgeReady = true;

var resizeDebounce = null;
window.addEventListener("resize", function () {
  clearTimeout(resizeDebounce);
  resizeDebounce = setTimeout(function () {
    var reportResizeError = createBridgeErrorReporter();
    var progress = reader.lastProgress || reader.calculateProgress();
    logDebug("resize", { progress: progress, innerWidth: window.innerWidth, innerHeight: window.innerHeight });
    reader.reflow(progress).catch(reportResizeError);
  }, 250);
});

function logDebug(msg, data) {
  postToHost("debugLog", { message: msg, data: data, timestamp: Date.now() });
}

window.onerror = function (message, source, lineno, colno, error) {
  var msg = error instanceof Error ? error.message : String(message);
  createBridgeErrorReporter()(
    new Error("Uncaught: " + msg + " at " + source + ":" + lineno + ":" + colno)
  );
};

window.addEventListener("unhandledrejection", function (event) {
  var msg = event.reason instanceof Error ? event.reason.message : String(event.reason);
  createBridgeErrorReporter()(new Error("Unhandled rejection: " + msg));
});

// ── Sasayaki highlighting ──────────────────────────────────────────
function notifyReaderContentChanged() {
  document.dispatchEvent(new Event("niratan-reader-content-changed"));
}

var sasayaki = {
  _currentHighlightNodes: [],
  _highlightStyle: null,
  _textColor: null,
  _backgroundColor: null,
  _highlightName: "niratan-sasayaki",

  setColors: function (textColor, backgroundColor) {
    this._textColor = textColor || null;
    this._backgroundColor = backgroundColor || null;
    document.documentElement.style.setProperty(
      "--niratan-sasayaki-text-color",
      this._textColor || "inherit"
    );
    document.documentElement.style.setProperty(
      "--niratan-sasayaki-background-color",
      this._backgroundColor || "rgba(255,235,59,0.45)"
    );
  },

  _ensureStyle: function () {
    if (this._highlightStyle) return;
    this._highlightStyle = document.createElement("style");
    this._highlightStyle.textContent =
      "::highlight(niratan-sasayaki) { color: var(--niratan-sasayaki-text-color, inherit); background-color: var(--niratan-sasayaki-background-color, rgba(255,235,59,0.45)); }" +
      ".niratan-sasayaki-highlight { color: var(--niratan-sasayaki-text-color, inherit); background-color: var(--niratan-sasayaki-background-color, rgba(255,235,59,0.45)); box-decoration-break: slice; -webkit-box-decoration-break: slice; transition: background-color 0.15s; }";
    document.head.appendChild(this._highlightStyle);
  },

  clearHighlight: function () {
    if (typeof CSS !== "undefined" && CSS.highlights) {
      CSS.highlights.delete(this._highlightName);
    }
    var didMutateDom = false;
    this._currentHighlightNodes.forEach(function (span) {
      var parent = span.parentNode;
      if (parent) {
        while (span.firstChild) parent.insertBefore(span.firstChild, span);
        parent.removeChild(span);
        parent.normalize();
        didMutateDom = true;
      }
    });
    this._currentHighlightNodes = [];
    if (didMutateDom) notifyReaderContentChanged();
  },

  _clearReaderSelection: function () {
    try {
      window.niratanSelection?.clearSelection?.();
    } catch (e) {
      // Selection cleanup is cosmetic; keep audio/read-along moving if WebView
      // reports a transient selection state.
    }

    try {
      window.getSelection()?.removeAllRanges?.();
    } catch (e) {
      // Ignore stale native selections.
    }
  },

  highlightCue: function (startCodePoint, length, autoScroll) {
    this.clearHighlight();
    this._clearReaderSelection();
    this._ensureStyle();
    if (length <= 0) return null;
    var beforeProgress = reader.calculateProgress();

    var endOffset = startCodePoint + length;
    var startBoundary = this._findBoundary(startCodePoint, false);
    var endBoundary = this._findBoundary(endOffset, true);

    if (!startBoundary || !endBoundary) return null;

    var didScroll = this._highlightRange(
      startBoundary.node,
      startBoundary.offset,
      endBoundary.node,
      endBoundary.offset,
      autoScroll !== false
    );
    if (didScroll) {
      var afterProgress = reader.calculateProgress();
      reader.lastProgress = afterProgress;
      return Math.abs(afterProgress - beforeProgress) > 0.0001 ? afterProgress : null;
    }
    return null;
  },

  _findBoundary: function (targetCodePoint, isEndBoundary) {
    var walker = reader.createWalker();
    var runningCount = 0;
    var node;

    while ((node = walker.nextNode())) {
      var nodeLen = reader.countChars(node.textContent);
      var containsBoundary = isEndBoundary
        ? runningCount + nodeLen >= targetCodePoint
        : runningCount + nodeLen > targetCodePoint;

      if (containsBoundary) {
        return {
          node: node,
          offset: reader.textOffsetForCharCount(
            node,
            Math.max(0, targetCodePoint - runningCount)
          ),
        };
      }

      runningCount += nodeLen;
    }

    return null;
  },

  _collectHighlightNodes: function (startNode, endNode) {
    var nodes = [];
    var walker = reader.createWalker();
    var started = false;
    var node;

    while ((node = walker.nextNode())) {
      if (node === startNode) started = true;
      if (started) nodes.push(node);
      if (node === endNode) break;
    }

    return nodes;
  },

  _highlightRange: function (startNode, startOffset, endNode, endOffset, autoScroll) {
    var ranges = this._createHighlightRanges(startNode, startOffset, endNode, endOffset);
    var didScroll = false;
    if (ranges.length <= 0) return false;

    if (autoScroll) {
      didScroll = reader.scrollToRange(ranges[0]);
    }

    if (typeof CSS !== "undefined" && CSS.highlights && typeof Highlight !== "undefined") {
      CSS.highlights.set("niratan-sasayaki", new Highlight(...ranges));
      return didScroll;
    }

    this._currentHighlightNodes = this._wrapHighlightRanges(ranges);
    if (this._currentHighlightNodes.length > 0) {
      notifyReaderContentChanged();
    }
    return didScroll;
  },

  _createHighlightRanges: function (startNode, startOffset, endNode, endOffset) {
    var nodesToHighlight = this._collectHighlightNodes(startNode, endNode);
    var ranges = [];
    for (var i = 0; i < nodesToHighlight.length; i++) {
      var node = nodesToHighlight[i];
      if (!node.textContent || node.textContent.trim() === "") continue;
      if (reader.isFurigana(node)) continue;

      var nodeStart = Math.max(0, Math.min(node === startNode ? startOffset : 0, node.textContent.length));
      var nodeEnd = Math.max(nodeStart, Math.min(node === endNode ? endOffset : node.textContent.length, node.textContent.length));
      if (nodeStart >= nodeEnd) continue;

      var range = document.createRange();
      range.setStart(node, nodeStart);
      range.setEnd(node, nodeEnd);
      ranges.push(range);
    }

    return ranges;
  },

  _wrapHighlightRanges: function (ranges) {
    var wrappers = [];
    for (var i = ranges.length - 1; i >= 0; i--) {
      var range = ranges[i];
      if (range.collapsed) continue;

      var wrapper = document.createElement("span");
      wrapper.className = "niratan-sasayaki-highlight";
      try {
        range.surroundContents(wrapper);
        wrappers.unshift(wrapper);
      } catch (e) {
        try {
          wrapper.appendChild(range.extractContents());
          range.insertNode(wrapper);
          wrappers.unshift(wrapper);
        } catch (inner) {
          // Skip stale ranges if content changed underneath an async highlight.
        }
      }
    }

    return wrappers;
  },
};

logDebug("bridge-loaded", { readyState: document.readyState });
if (window.__niratanChapterInfo) {
  currentChapter = {
    index: window.__niratanChapterInfo.index ?? 0,
    totalChapters: window.__niratanChapterInfo.totalChapters ?? 0,
    renderAttemptId: window.__niratanChapterInfo.renderAttemptId ?? 0,
  };
  var initProgress = window.__niratanChapterInfo.progress ?? 0;
  var reportAutoInitializeError = createBridgeErrorReporter();
  logDebug("auto-initialize", { chapter: currentChapter, progress: initProgress });
  reader.initialize(
    initProgress,
    window.__niratanChapterInfo.navigationGeneration ?? null,
    window.__niratanChapterInfo.restoreTarget ?? null,
    currentChapter.renderAttemptId
  ).catch(reportAutoInitializeError);
} else {
  postToHost("readerReady", {});
}
})();
