const VERSION = 1;

window.__hoshiReaderState = {
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

window.hoshiReader = {
  pageHeight: 0,
  pageWidth: 0,
  columnGap: 0,
  lastProgress: 0,
  ttuRegexNegated: /[^0-9A-Za-z○◯々-〇〻ぁ-ゖゝ-ゞァ-ヺー０-９Ａ-Ｚａ-ｚｦ-ﾝ\p{Radical}\p{Unified_Ideograph}]+/gimu,
  ttuRegex: /[0-9A-Za-z○◯々-〇〻ぁ-ゖゝ-ゞァ-ヺー０-９Ａ-Ｚａ-ｚｦ-ﾝ\p{Radical}\p{Unified_Ideograph}]/iu,
  nodeStartOffsets: new WeakMap(),
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
    var fallbackOffset = 0;
    while (offset < text.length) {
      var ch = String.fromCodePoint(text.codePointAt(offset));
      if (this.isMatchableChar(ch)) {
        if (count >= targetCount) return offset;
        fallbackOffset = offset;
        count += 1;
      }
      offset += ch.length;
    }
    return fallbackOffset;
  },

  createWalker: function (rootNode) {
    var root = rootNode || document.body;
    return document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
      acceptNode: function (n) {
        return window.hoshiReader.isFurigana(n)
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
    this.paginationMetrics = null;
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
      "[data-hoshi-popup]",
      ".popup",
      ".dictionary-popup",
      ".popover",
      "[role=\"dialog\"]",
    ].join(","));
  },

  registerWheelNavigation: function (enabled) {
    window.hoshiWheelNavigationEnabled = !!enabled;
    if (window.hoshiWheelNavigationRegistered) return;

    window.hoshiWheelNavigationRegistered = true;
    window.hoshiLastWheelNavigationTime = 0;

    document.addEventListener("wheel", function (event) {
      if (!window.hoshiWheelNavigationEnabled) return;
      if (event.ctrlKey || event.metaKey || event.altKey || event.shiftKey) return;
      if (Math.abs(event.deltaX) > Math.abs(event.deltaY) || event.deltaY === 0) return;
      if (window.hoshiReader.isIgnoredWheelTarget(event.target)) return;
      if (window.getSelection && window.getSelection()?.isCollapsed === false) return;

      var now = Date.now();
      if (now - window.hoshiLastWheelNavigationTime < 170) {
        event.preventDefault();
        return;
      }

      var direction = event.deltaY > 0 ? "forward" : "backward";
      window.hoshiLastWheelNavigationTime = now;
      event.preventDefault();
      handleNavigate(direction);
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

  restoreProgress: async function (progress) {
    await document.fonts.ready;
    var context = this.getScrollContext();
    if (context.pageSize <= 0 || progress <= 0) {
      var firstPage = this.contentFirstPageScroll(context);
      this.setPagePosition(context, firstPage);
      this.registerSnapScroll(firstPage);
      this.lastProgress = 0;
      notifyRestoreComplete();
      return;
    }
    if (progress >= 0.99) {
      var lastPage = this.contentLastPageScroll(context);
      this.setPagePosition(context, Math.max(0, lastPage));
      requestAnimationFrame(function () {
        var ctx = window.hoshiReader.getScrollContext();
        var lp = window.hoshiReader.contentLastPageScroll(ctx);
        window.hoshiReader.setPagePosition(ctx, Math.max(0, lp));
        window.hoshiReader.registerSnapScroll(lp);
        window.hoshiReader.lastProgress = 1;
        requestAnimationFrame(function () { notifyRestoreComplete(); });
      });
      return;
    }
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
      var targetScroll = this.alignToPage(context, anchor);
      this.setPagePosition(context, targetScroll);
      requestAnimationFrame(function () {
        var ctx = window.hoshiReader.getScrollContext();
        window.hoshiReader.setPagePosition(ctx, targetScroll);
        window.hoshiReader.registerSnapScroll(targetScroll);
        window.hoshiReader.lastProgress = window.hoshiReader.calculateProgress();
      });
    } else {
      this.setPagePosition(context, 0);
      this.registerSnapScroll(0);
      this.lastProgress = 0;
    }
    requestAnimationFrame(function () {
      requestAnimationFrame(function () { notifyRestoreComplete(); });
    });
  },

  jumpToFragment: async function (fragment) {
    await document.fonts.ready;
    var context = this.getScrollContext();
    var rawFragment = (fragment || "").trim();
    var target =
      rawFragment &&
      (document.getElementById(rawFragment) ||
        document.getElementsByName(rawFragment)[0]);
    if (context.pageSize <= 0 || !target) {
      this.registerSnapScroll(this.getPagePosition(context));
      notifyRestoreComplete();
      return false;
    }
    var rect = this.getRect(target);
    var currentScroll = this.getPagePosition(context);
    var anchor = (context.vertical ? rect.top : rect.left) + currentScroll;
    var targetScroll = this.alignToPage(context, anchor);
    this.setPagePosition(context, targetScroll);
    requestAnimationFrame(function () {
      var ctx = window.hoshiReader.getScrollContext();
      window.hoshiReader.setPagePosition(ctx, targetScroll);
      window.hoshiReader.registerSnapScroll(targetScroll);
      requestAnimationFrame(function () { notifyRestoreComplete(); });
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
      var ctx = window.hoshiReader.getScrollContext();
      window.hoshiReader.setPagePosition(ctx, targetScroll);
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
      "--hoshi-image-max-width",
      Math.max(1, Math.floor(pageWidth - (this.currentSafeInline() * 2))) + "px"
    );
    document.documentElement.style.setProperty(
      "--hoshi-image-max-height",
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

  initialize: function (initialProgress) {
    if (window.hoshiReader.didInitialize) return;
    window.hoshiReader.didInitialize = true;

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
      "--hoshi-image-max-width",
      Math.max(1, Math.floor(pageWidth - (window.hoshiReader.currentSafeInline() * 2))) + "px"
    );
    document.documentElement.style.setProperty(
      "--hoshi-image-max-height",
      Math.max(1, Math.floor(pageHeight - (window.hoshiReader.currentSafeBlock() * 2) - overlap)) + "px"
    );
    window.hoshiReader.pageHeight = pageHeight;
    window.hoshiReader.pageWidth = pageWidth;
    window.hoshiReader.columnGap = window.hoshiReader.currentColumnGap();

    Array.from(document.querySelectorAll("svg")).forEach(function (svg) {
      if (
        svg.querySelector("image") &&
        svg.getAttribute("preserveAspectRatio") === "none"
      ) {
        svg.setAttribute("preserveAspectRatio", "xMidYMid meet");
      }
    });

    var imagePromises = Array.from(document.querySelectorAll("img")).map(function (img) {
      return new Promise(function (resolve) {
        function mark() {
          if (img.naturalWidth > 256 || img.naturalHeight > 256) {
            img.classList.add("block-img");
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
    Promise.all(imagePromises)
      .then(function () {
        return new Promise(function (resolve) { setTimeout(resolve, 50); });
      })
      .then(function () {
        self.buildNodeOffsets();
        if (window.hoshiHighlights) {
          window.hoshiHighlights.applyHighlights(window.__hoshiChapterHighlights || []);
        }
        self.restoreProgress(progress).then(function () {
          self.lastProgress = self.calculateProgress();
          updateDiagnostics();
          postToHost("chapterReady", window.__hoshiReaderState);
        });
      });
  },
};

function notifyRestoreComplete() {
  postToHost("restoreCompleted", {
    progress: window.hoshiReader.calculateProgress(),
  });
}

function postToHost(type, payload) {
  window.chrome?.webview?.postMessage({
    version: VERSION,
    type: type,
    payload: payload || {},
  });
}

function updateDiagnostics() {
  var context = window.hoshiReader.getScrollContext();
  var metrics =
    window.hoshiReader.paginationMetrics ||
    window.hoshiReader.buildPaginationMetrics();
  var step = window.hoshiReader.pageStep(context);
  var pageCount = context.pageSize > 0
    ? Math.max(1, Math.floor(context.maxScroll / step) + 1)
    : 0;
  var currentPage = context.pageSize > 0
    ? Math.floor(window.hoshiReader.getPagePosition(context) / step)
    : 0;
  var progress = window.hoshiReader.calculateProgress();
  window.hoshiReader.lastProgress = progress;
  var renderedText = document.body?.innerText?.trim()?.length ?? 0;

  window.__hoshiReaderState = {
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
      pageWidth: window.hoshiReader.pageWidth,
      pageHeight: window.hoshiReader.pageHeight,
      currentPage: currentPage,
      pageIndex: currentPage,
      pageCount: pageCount,
      totalChars: metrics.totalChars,
      progress: progress,
      minScroll: metrics.minScroll,
      maxScroll: metrics.maxScroll,
      scrollPosition: window.hoshiReader.getPagePosition(context),
      columnGap: window.hoshiReader.currentColumnGap(),
      safeInline: window.hoshiReader.currentSafeInline(),
      safeBlock: window.hoshiReader.currentSafeBlock(),
      pageStep: step,
      bodyTextLength: renderedText,
    },
    error: null,
  };
}

window.hoshiGetDiagnostics = function () {
  updateDiagnostics();
  return JSON.stringify(window.__hoshiReaderState, null, 2);
};

async function handleNavigate(direction) {
  var result = window.hoshiReader.paginate(direction);
  updateDiagnostics();
  postToHost("pageChanged", {
    direction: direction,
    result: result,
    progress: window.hoshiReader.calculateProgress(),
    state: window.__hoshiReaderState,
  });
}

async function handleMessage(event) {
  try {
    var message =
      typeof event.data === "string" ? JSON.parse(event.data) : event.data;
    logDebug("rx-message", { type: message?.type });
    if (message?.version !== VERSION) {
      throw new Error(
        "Unsupported bridge message version: " + message?.version
      );
    }

    switch (message.type) {
      case "setChapter":
        currentChapter = {
          index: message.payload?.index ?? 0,
          totalChapters: message.payload?.totalChapters ?? 0,
        };
        var progress = message.payload?.progress ?? 0;
        logDebug("setChapter-received", { chapter: currentChapter, progress: progress });
        window.hoshiReader.initialize(progress);
        break;
      case "restoreProgress":
        logDebug("restoreProgress-start", {
          progress: message.payload?.progress ?? 0,
          pageHeight: window.hoshiReader.pageHeight,
          pageWidth: window.hoshiReader.pageWidth,
          bodyScrollWidth: document.body.scrollWidth,
          bodyClientWidth: document.body.clientWidth,
        });
        await window.hoshiReader.restoreProgress(
          message.payload?.progress ?? 0
        );
        updateDiagnostics();
        logDebug("restoreProgress-done", window.__hoshiReaderState);
        postToHost("chapterReady", window.__hoshiReaderState);
        break;
      default:
        throw new Error("Unsupported bridge message type: " + message.type);
    }
  } catch (error) {
    var msg = error instanceof Error ? error.message : String(error);
    window.__hoshiReaderState.error = msg;
    logDebug("handleMessage-error", { error: msg });
    postToHost("error", { message: msg });
  }
}

window.chrome?.webview?.addEventListener("message", handleMessage);
window.hoshiReaderNavigate = handleNavigate;

document.addEventListener("keydown", function (event) {
  if (event.key === "ArrowLeft") {
    event.preventDefault();
    handleNavigate("backward");
  } else if (event.key === "ArrowRight") {
    event.preventDefault();
    handleNavigate("forward");
  }
});

window.__hoshiReaderState.bridgeReady = true;

var resizeDebounce = null;
window.addEventListener("resize", function () {
  clearTimeout(resizeDebounce);
  resizeDebounce = setTimeout(function () {
    var progress = window.hoshiReader.lastProgress || window.hoshiReader.calculateProgress();
    logDebug("resize", { progress: progress, innerWidth: window.innerWidth, innerHeight: window.innerHeight });
    window.hoshiReader.reflow(progress);
  }, 250);
});

function logDebug(msg, data) {
  postToHost("debugLog", { message: msg, data: data, timestamp: Date.now() });
}

window.onerror = function (message, source, lineno, colno, error) {
  var msg = error instanceof Error ? error.message : String(message);
  var stack = error instanceof Error ? error.stack : undefined;
  postToHost("error", {
    message: "Uncaught: " + msg + " at " + source + ":" + lineno + ":" + colno,
    stack: stack,
  });
};

window.addEventListener("unhandledrejection", function (event) {
  var msg = event.reason instanceof Error ? event.reason.message : String(event.reason);
  postToHost("error", { message: "Unhandled rejection: " + msg });
});

// ── Sasayaki highlighting ──────────────────────────────────────────
window.hoshiSasayaki = {
  _currentHighlightNodes: [],
  _highlightStyle: null,
  _textColor: null,
  _backgroundColor: null,

  setColors: function (textColor, backgroundColor) {
    this._textColor = textColor || null;
    this._backgroundColor = backgroundColor || null;
    document.documentElement.style.setProperty(
      "--hoshi-sasayaki-text-color",
      this._textColor || "inherit"
    );
    document.documentElement.style.setProperty(
      "--hoshi-sasayaki-background-color",
      this._backgroundColor || "rgba(255,235,59,0.45)"
    );
  },

  _ensureStyle: function () {
    if (this._highlightStyle) return;
    this._highlightStyle = document.createElement("style");
    this._highlightStyle.textContent =
      ".hoshi-sasayaki-highlight { color: var(--hoshi-sasayaki-text-color, inherit); background-color: var(--hoshi-sasayaki-background-color, rgba(255,235,59,0.45)); border-radius: 2px; transition: background-color 0.15s; }" +
      ".hoshi-sasayaki-highlight-active { outline: 2px solid rgba(255,152,0,0.5); outline-offset: 1px; }";
    document.head.appendChild(this._highlightStyle);
  },

  clearHighlight: function () {
    this._currentHighlightNodes.forEach(function (span) {
      var parent = span.parentNode;
      if (parent) {
        while (span.firstChild) parent.insertBefore(span.firstChild, span);
        parent.removeChild(span);
        parent.normalize();
      }
    });
    this._currentHighlightNodes = [];
  },

  highlightCue: function (startCodePoint, length, autoScroll) {
    this.clearHighlight();
    this._ensureStyle();
    var beforeProgress = window.hoshiReader.calculateProgress();

    var walker = window.hoshiReader.createWalker();
    var runningCount = 0;
    var targetStartNode = null;
    var targetStartOffset = 0;
    var node;

    while ((node = walker.nextNode())) {
      var nodeLen = window.hoshiReader.countChars(node.textContent);
      if (runningCount + nodeLen > startCodePoint) {
        targetStartNode = node;
        targetStartOffset = window.hoshiReader.textOffsetForCharCount(
          node,
          Math.max(0, startCodePoint - runningCount)
        );
        break;
      }
      runningCount += nodeLen;
    }

    if (!targetStartNode) return;

    var endOffset = startCodePoint + length;
    var targetEndNode = null;
    var targetEndOffset = 0;
    runningCount = 0;

    walker = window.hoshiReader.createWalker();
    while ((node = walker.nextNode())) {
      var nodeLen = window.hoshiReader.countChars(node.textContent);
      if (runningCount + nodeLen > endOffset) {
        targetEndNode = node;
        targetEndOffset = window.hoshiReader.textOffsetForCharCount(
          node,
          Math.max(0, endOffset - runningCount)
        );
        break;
      }
      runningCount += nodeLen;
    }

    if (!targetEndNode) {
      targetEndNode = targetStartNode;
      targetEndOffset = targetStartNode.textContent.length;
    }

    var didScroll = this._highlightRange(
      targetStartNode,
      targetStartOffset,
      targetEndNode,
      targetEndOffset,
      autoScroll !== false
    );
    if (didScroll) {
      var afterProgress = window.hoshiReader.calculateProgress();
      window.hoshiReader.lastProgress = afterProgress;
      return Math.abs(afterProgress - beforeProgress) > 0.0001 ? afterProgress : null;
    }
    return null;
  },

  _highlightRange: function (startNode, startOffset, endNode, endOffset, autoScroll) {
    var range = document.createRange();
    range.setStart(startNode, Math.min(startOffset, startNode.textContent.length));
    range.setEnd(endNode, Math.min(endOffset, endNode.textContent.length));

    // Walk through text nodes in the range and wrap each with a highlight span
    var self = this;
    var treeWalker = document.createTreeWalker(
      range.commonAncestorContainer,
      NodeFilter.SHOW_TEXT,
      {
        acceptNode: function (n) {
          return range.intersectsNode(n)
            ? NodeFilter.FILTER_ACCEPT
            : NodeFilter.FILTER_REJECT;
        },
      }
    );

    var spans = [];
    var node;
    while ((node = treeWalker.nextNode())) {
      if (!node.textContent || node.textContent.trim() === "") continue;
      if (window.hoshiReader.isFurigana(node)) continue;

      var textRange = document.createRange();
      textRange.selectNodeContents(node);

      var nodeStart = node === startNode ? startOffset : 0;
      var nodeEnd = node === endNode ? endOffset : node.textContent.length;
      if (nodeStart >= nodeEnd) continue;

      var span = document.createElement("span");
      span.className = "hoshi-sasayaki-highlight";
      textRange.setStart(node, nodeStart);
      textRange.setEnd(node, nodeEnd);
      try {
        textRange.surroundContents(span);
        spans.push(span);
      } catch (e) {
        // surroundContents fails if partial selection crosses elements — skip
      }
    }

    // Mark the first span as active (for scrolling into view)
    var didScroll = false;
    if (spans.length > 0) {
      spans[0].classList.add("hoshi-sasayaki-highlight-active");
      if (autoScroll) {
        var scrollRange = document.createRange();
        scrollRange.selectNodeContents(spans[0]);
        didScroll = window.hoshiReader.scrollToRange(scrollRange);
      }
    }

    this._currentHighlightNodes = spans;
    return didScroll;
  },
};

logDebug("bridge-loaded", { readyState: document.readyState });
if (window.__hoshiChapterInfo) {
  currentChapter = {
    index: window.__hoshiChapterInfo.index ?? 0,
    totalChapters: window.__hoshiChapterInfo.totalChapters ?? 0,
  };
  var initProgress = window.__hoshiChapterInfo.progress ?? 0;
  logDebug("auto-initialize", { chapter: currentChapter, progress: initProgress });
  window.hoshiReader.initialize(initProgress);
} else {
  postToHost("readerReady", {});
}
