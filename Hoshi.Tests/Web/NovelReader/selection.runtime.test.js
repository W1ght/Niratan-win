"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");

const selectionPath = process.argv[2];
if (!selectionPath) throw new Error("selection.js path is required");
const bridgePath = process.argv[3];
if (!bridgePath) throw new Error("reader-bridge.js path is required");

const documentListeners = new Map();
const webViewListeners = new Map();
const counters = {
  treeWalkers: 0,
  nextNodes: 0,
  contentChangedEvents: 0,
};

class FakeElement {
  constructor(tagName, parentElement = null) {
    this.nodeType = 1;
    this.tagName = tagName.toUpperCase();
    this.parentElement = parentElement;
    this.childNodes = [];
    this.style = { setProperty() {} };
    this.className = "";
    this.textContent = "";
  }

  get parentNode() { return this.parentElement; }
  get firstChild() { return this.childNodes[0] || null; }

  appendChild(node) {
    if (node.isFragment) {
      while (node.firstChild) this.appendChild(node.firstChild);
      return node;
    }
    node.parentElement?.removeChild(node);
    node.parentElement = this;
    this.childNodes.push(node);
    return node;
  }

  insertBefore(node, referenceNode) {
    node.parentElement?.removeChild(node);
    node.parentElement = this;
    const index = this.childNodes.indexOf(referenceNode);
    this.childNodes.splice(index < 0 ? this.childNodes.length : index, 0, node);
    return node;
  }

  removeChild(node) {
    const index = this.childNodes.indexOf(node);
    if (index >= 0) this.childNodes.splice(index, 1);
    node.parentElement = null;
    return node;
  }

  normalize() {
    for (const child of [...this.childNodes]) {
      if (child.nodeType === 1) child.normalize();
    }
    const normalized = [];
    for (const child of this.childNodes) {
      if (child.nodeType === 3 && child.textContent.length === 0) {
        child.parentElement = null;
        continue;
      }
      const previous = normalized[normalized.length - 1];
      if (child.nodeType === 3 && previous?.nodeType === 3) {
        previous.textContent += child.textContent;
        child.parentElement = null;
        continue;
      }
      normalized.push(child);
    }
    this.childNodes = normalized;
  }

  closest(selector) {
    const names = selector.split(",").map(value => value.trim().toUpperCase());
    for (let current = this; current; current = current.parentElement) {
      if (names.includes(current.tagName)) return current;
    }
    return null;
  }

  addEventListener() {}
  getBoundingClientRect() {
    return { left: 10, right: 20, top: 10, bottom: 20, width: 10, height: 10, x: 10, y: 10 };
  }
}

class FakeText {
  constructor(textContent, parentElement = null) {
    this.nodeType = 3;
    this.textContent = textContent;
    this.parentElement = parentElement;
  }

  get parentNode() { return this.parentElement; }
}

class FakeFragment extends FakeElement {
  constructor() {
    super("#fragment");
    this.isFragment = true;
  }
}

const body = new FakeElement("body");
Object.assign(body, {
  scrollHeight: 600,
  scrollWidth: 1200,
  scrollTop: 0,
  scrollLeft: 0,
  clientWidth: 400,
  innerText: "",
});

function appendText(parent, text) {
  return parent.appendChild(new FakeText(text));
}

function collectTextNodes(root) {
  const result = [];
  function visit(node) {
    if (node.nodeType === 3) {
      result.push(node);
      return;
    }
    for (const child of node.childNodes || []) visit(child);
  }
  visit(root);
  return result;
}

function collectElements(root, predicate) {
  const result = [];
  function visit(node) {
    if (node.nodeType === 1 && predicate(node)) result.push(node);
    for (const child of node.childNodes || []) visit(child);
  }
  visit(root);
  return result;
}

class FakeRange {
  constructor() {
    this.startContainer = null;
    this.startOffset = 0;
    this.endContainer = null;
    this.endOffset = 0;
    this._insertionParent = null;
    this._insertionReference = null;
  }

  get collapsed() {
    return this.startContainer === this.endContainer && this.startOffset === this.endOffset;
  }

  setStart(node, offset) {
    this.startContainer = node;
    this.startOffset = offset;
  }

  setEnd(node, offset) {
    this.endContainer = node;
    this.endOffset = offset;
  }

  selectNodeContents(node) {
    this.setStart(node, 0);
    this.setEnd(node, node.textContent?.length || 0);
  }

  surroundContents(wrapper) {
    if (this.startContainer?.forceExtractFallback) {
      throw new Error("forced surroundContents fallback");
    }
    const contents = this.extractContents();
    wrapper.appendChild(contents);
    this.insertNode(wrapper);
  }

  extractContents() {
    assert.equal(this.startContainer, this.endContainer, "fixture ranges must stay within one Text node");
    const source = this.startContainer;
    const parent = source.parentElement;
    assert.ok(parent, "range source must remain attached");
    const sourceIndex = parent.childNodes.indexOf(source);
    const before = source.textContent.slice(0, this.startOffset);
    const selected = source.textContent.slice(this.startOffset, this.endOffset);
    const after = source.textContent.slice(this.endOffset);
    parent.removeChild(source);
    const followingNode = parent.childNodes[sourceIndex] || null;

    const beforeNode = before ? new FakeText(before) : null;
    const afterNode = after ? new FakeText(after) : null;
    let insertionIndex = sourceIndex;
    if (beforeNode) {
      beforeNode.parentElement = parent;
      parent.childNodes.splice(insertionIndex, 0, beforeNode);
      insertionIndex += 1;
    }
    if (afterNode) {
      afterNode.parentElement = parent;
      parent.childNodes.splice(insertionIndex, 0, afterNode);
    }

    this._insertionParent = parent;
    this._insertionReference = afterNode || followingNode;
    const fragment = new FakeFragment();
    if (selected) fragment.appendChild(new FakeText(selected));
    return fragment;
  }

  insertNode(node) {
    assert.ok(this._insertionParent, "extractContents must run before insertNode");
    this._insertionParent.insertBefore(node, this._insertionReference);
  }

  getClientRects() {
    return [{ left: 10, right: 20, top: 10, bottom: 20, width: 10, height: 10, x: 10, y: 10 }];
  }

  getBoundingClientRect() {
    return this.getClientRects()[0];
  }
}

const head = new FakeElement("head");
const documentElement = new FakeElement("html");
documentElement.scrollTop = 0;
documentElement.scrollLeft = 0;

const document = {
  body,
  head,
  documentElement,
  fonts: { ready: Promise.resolve() },
  readyState: "complete",
  baseURI: "https://hoshi-novel-book.local/Text/chapter.xhtml",
  addEventListener(type, listener) {
    const listeners = documentListeners.get(type) || [];
    listeners.push(listener);
    documentListeners.set(type, listeners);
  },
  dispatchEvent(event) {
    if (event.type === "hoshi-reader-content-changed") {
      counters.contentChangedEvents += 1;
    }
    for (const listener of documentListeners.get(event.type) || []) listener(event);
    return true;
  },
  createTreeWalker(root, _whatToShow, filter) {
    counters.treeWalkers += 1;
    const nodes = collectTextNodes(root).filter(node =>
      filter.acceptNode(node) === 1);
    let index = -1;
    return {
      currentNode: root,
      nextNode() {
        counters.nextNodes += 1;
        index += 1;
        return nodes[index] || null;
      },
    };
  },
  createRange() { return new FakeRange(); },
  createElement(tagName) { return new FakeElement(tagName); },
  elementFromPoint() { return null; },
  querySelector() { return null; },
  querySelectorAll() { return []; },
  getElementById() { return null; },
  getElementsByName() { return []; },
};

const outbound = [];
const windowListeners = new Map();
const window = {
  document,
  innerWidth: 400,
  innerHeight: 600,
  devicePixelRatio: 1,
  scrollX: 0,
  scrollY: 0,
  location: { host: "hoshi-novel-book.local" },
  visualViewport: null,
  chrome: {
    webview: {
      addEventListener(type, listener) { webViewListeners.set(type, listener); },
      postMessage(message) { outbound.push(message); },
      dispatchHostMessage(data) {
        const listener = webViewListeners.get("message");
        return listener?.(Object.freeze({
          isTrusted: false,
          source: this,
          data,
        }));
      },
    },
  },
  addEventListener(type, listener) {
    const listeners = windowListeners.get(type) || [];
    listeners.push(listener);
    windowListeners.set(type, listeners);
  },
  getComputedStyle() { return { writingMode: "horizontal-tb" }; },
  getSelection() { return { isCollapsed: true, removeAllRanges() {} }; },
  scrollTo() {},
};
window.window = window;

class FakeEvent {
  constructor(type) {
    this.type = type;
  }
}

const context = vm.createContext({
  window,
  document,
  Element: FakeElement,
  Node: { TEXT_NODE: 3, ELEMENT_NODE: 1 },
  NodeFilter: { SHOW_TEXT: 4, FILTER_REJECT: 2, FILTER_ACCEPT: 1 },
  CSS: {},
  Event: FakeEvent,
  Highlight: class {},
  URL,
  Date,
  Error,
  JSON,
  Math,
  Number,
  Object,
  Promise,
  Array,
  Map,
  Set,
  WeakMap,
  String,
  performance: { now: () => 1 },
  getComputedStyle: () => ({
    writingMode: "horizontal-tb",
    columnGap: "0",
    paddingLeft: "0",
    paddingTop: "0",
  }),
  requestAnimationFrame: callback => callback(),
  setTimeout,
  clearTimeout,
  console,
});

vm.runInContext(`
  globalThis.__ttuRegexTests = 0;
  const originalRegExpTest = RegExp.prototype.test;
  RegExp.prototype.test = function(value) {
    if (this.source.includes("Unified_Ideograph")) {
      globalThis.__ttuRegexTests += 1;
    }
    return originalRegExpTest.call(this, value);
  };
`, context);

const paragraph = new FakeElement("p");
body.appendChild(paragraph);
for (let index = 0; index < 2000; index += 1) {
  appendText(paragraph, `節${index}。`);
}

const ruby = paragraph.appendChild(new FakeElement("ruby"));
appendText(ruby, "本");
const rt = ruby.appendChild(new FakeElement("rt"));
appendText(rt, "ほん");
const rp = ruby.appendChild(new FakeElement("rp"));
appendText(rp, "（）");
const target = appendText(paragraph, "終端A😀語。後");
const firstCueNode = appendText(paragraph, "甲乙");
const secondCueNode = appendText(paragraph, "丙丁後");
secondCueNode.forceExtractFallback = true;

const selectionSource = fs.readFileSync(selectionPath, "utf8");
vm.runInContext(selectionSource, context, { filename: selectionPath });

assert.equal(typeof context.normalizedNodeStartOffsets, "undefined");
assert.equal(typeof context.normalizedOffsetGeneration, "undefined");
assert.equal(typeof window.normalizedNodeStartOffsets, "undefined");
assert.equal(typeof window.normalizedOffsetGeneration, "undefined");

const matchable = /[0-9A-Za-z○◯々-〇〻ぁ-ゖゝ-ゞァ-ヺー０-９Ａ-Ｚａ-ｚｦ-ﾝ\p{Radical}\p{Unified_Ideograph}]/iu;
function countMatchable(text) {
  return Array.from(text).filter(char => matchable.test(char)).length;
}

function expectedBefore(targetNode) {
  let count = 0;
  for (const node of collectTextNodes(body)) {
    if (node === targetNode) return count;
    if (!node.parentElement.closest("rt, rp")) count += countMatchable(node.textContent);
  }
  throw new Error("target node was not found");
}

function indexedCodePointCount() {
  return collectTextNodes(body)
    .filter(node => !node.parentElement.closest("rt, rp"))
    .reduce((total, node) => total + Array.from(node.textContent).length, 0);
}

const targetLocalOffset = "終端A😀語".length;
const expectedInitial = expectedBefore(target) + countMatchable(target.textContent.slice(0, targetLocalOffset));
const first = window.hoshiSelection.getNormalizedOffset(target, targetLocalOffset);
assert.equal(first, expectedInitial, "late-chapter offset must preserve ruby and matchable-character semantics");
assert.equal(counters.treeWalkers, 1, "first lookup should build one chapter index");

const firstNextNodes = counters.nextNodes;
const firstRegexTests = context.__ttuRegexTests;
const second = window.hoshiSelection.getNormalizedOffset(target, targetLocalOffset);
assert.equal(second, expectedInitial);
assert.equal(counters.treeWalkers, 1, "same-chapter lookup must reuse the chapter index");
assert.equal(counters.nextNodes, firstNextNodes, "same-chapter lookup must not walk the full tree again");
assert.equal(
  context.__ttuRegexTests - firstRegexTests,
  Array.from(target.textContent.slice(0, targetLocalOffset)).length,
  "same-chapter lookup may scan only the target Text node prefix",
);

const inserted = new FakeText("追加Z。", paragraph);
paragraph.insertBefore(inserted, paragraph.childNodes[0]);
document.dispatchEvent(new FakeEvent("hoshi-reader-content-changed"));

const afterChangeExpected = expectedBefore(target) + countMatchable(target.textContent.slice(0, targetLocalOffset));
const beforeRebuildRegexTests = context.__ttuRegexTests;
const afterChange = window.hoshiSelection.getNormalizedOffset(target, targetLocalOffset);
assert.equal(afterChange, afterChangeExpected, "content changes must update the normalized start offset");
assert.equal(counters.treeWalkers, 2, "the first lookup after invalidation must rebuild exactly once");
assert.equal(
  context.__ttuRegexTests - beforeRebuildRegexTests,
  indexedCodePointCount() + Array.from(target.textContent.slice(0, targetLocalOffset)).length,
  "invalidation must cause exactly one full index build plus the target-local scan",
);

const rebuiltNextNodes = counters.nextNodes;
const rebuiltRegexTests = context.__ttuRegexTests;
window.hoshiSelection.getNormalizedOffset(target, targetLocalOffset);
assert.equal(counters.treeWalkers, 2, "the rebuilt chapter index must be reused");
assert.equal(counters.nextNodes, rebuiltNextNodes, "post-rebuild lookup must remain local to the target node");
assert.equal(
  context.__ttuRegexTests - rebuiltRegexTests,
  Array.from(target.textContent.slice(0, targetLocalOffset)).length,
  "post-rebuild lookup may scan only the target Text node prefix",
);

async function verifySasayakiFallbackInvalidation() {
  const bridgeSource = fs.readFileSync(bridgePath, "utf8");
  vm.runInContext(bridgeSource, context, { filename: bridgePath });

  assert.equal(typeof context.reader, "undefined");
  assert.equal(typeof context.sasayaki, "undefined");
  assert.equal(typeof window.hoshiReader, "undefined");
  assert.equal(typeof window.hoshiSasayaki, "undefined");
  assert.equal(typeof window.nodeStartOffsets, "undefined");
  assert.equal(typeof window.nodeOffsetsGeneration, "undefined");

  const cueStart = expectedBefore(firstCueNode);
  const initialCueOffset = expectedBefore(secondCueNode) + countMatchable("丙丁");
  const beforeJointInvalidationWalkers = counters.treeWalkers;
  document.dispatchEvent(new FakeEvent("hoshi-reader-content-changed"));
  assert.equal(
    counters.treeWalkers,
    beforeJointInvalidationWalkers,
    "both production scripts must invalidate their private indexes without eagerly walking",
  );
  const beforeInitialLookupWalkers = counters.treeWalkers;
  assert.equal(window.hoshiSelection.getNormalizedOffset(secondCueNode, "丙丁".length), initialCueOffset);
  assert.equal(
    counters.treeWalkers,
    beforeInitialLookupWalkers + 1,
    "lookup must build the selection cache after both production scripts are loaded",
  );

  const beforeHighlightEvents = counters.contentChangedEvents;
  const beforeHighlightWalkers = counters.treeWalkers;
  await window.chrome.webview.dispatchHostMessage({
    version: 1,
    type: "highlightSasayakiCue",
    payload: {
      generation: 1,
      startCodePoint: cueStart,
      length: 4,
      autoScroll: false,
      textColor: "#111111",
      backgroundColor: "#eeee00",
    },
  });

  assert.equal(
    counters.contentChangedEvents,
    beforeHighlightEvents + 1,
    "a successful DOM wrapper fallback must dispatch exactly one content-changed event",
  );
  assert.equal(
    counters.treeWalkers - beforeHighlightWalkers,
    4,
    "Sasayaki boundary/range discovery must not eagerly rebuild either offset index",
  );

  const wrappers = collectElements(
    paragraph,
    element => element.className === "hoshi-sasayaki-highlight",
  );
  assert.equal(wrappers.length, 2, "both surroundContents and extractContents fallback must wrap");
  const wrappedTarget = collectTextNodes(wrappers[1])[0];
  assert.equal(wrappedTarget.textContent, "丙丁");

  const beforeWrappedLookupWalkers = counters.treeWalkers;
  const wrappedOffset = expectedBefore(wrappedTarget) + countMatchable(wrappedTarget.textContent);
  assert.equal(
    window.hoshiSelection.getNormalizedOffset(wrappedTarget, wrappedTarget.textContent.length),
    wrappedOffset,
    "selection must index the replacement Text node created by fallback wrapping",
  );
  assert.equal(
    counters.treeWalkers,
    beforeWrappedLookupWalkers + 1,
    "the first lookup after wrapping must lazily rebuild selection offsets exactly once",
  );
  window.hoshiSelection.getNormalizedOffset(wrappedTarget, wrappedTarget.textContent.length);
  assert.equal(
    counters.treeWalkers,
    beforeWrappedLookupWalkers + 1,
    "continuous lookup after wrapping must reuse the rebuilt selection index",
  );

  const beforeLazyBridgeRebuildWalkers = counters.treeWalkers;
  await window.chrome.webview.dispatchHostMessage({
    version: 1,
    type: "navigatePage",
    payload: { direction: "forward" },
  });
  assert.equal(
    counters.treeWalkers - beforeLazyBridgeRebuildWalkers,
    4,
    "the first pagination consumer must lazily rebuild the bridge index and metrics once",
  );
  const beforeReadyBridgeNavigationWalkers = counters.treeWalkers;
  await window.chrome.webview.dispatchHostMessage({
    version: 1,
    type: "navigatePage",
    payload: { direction: "forward" },
  });
  assert.equal(
    counters.treeWalkers - beforeReadyBridgeNavigationWalkers,
    2,
    "a ready bridge index and metrics must not be rebuilt on continuous navigation",
  );

  const beforeClearEvents = counters.contentChangedEvents;
  const beforeClearWalkers = counters.treeWalkers;
  await window.chrome.webview.dispatchHostMessage({
    version: 1,
    type: "clearSasayakiHighlight",
    payload: {},
  });
  assert.equal(
    counters.contentChangedEvents,
    beforeClearEvents + 1,
    "actual unwrap and normalize must dispatch exactly one content-changed event",
  );
  assert.equal(
    counters.treeWalkers,
    beforeClearWalkers,
    "clearing wrappers must invalidate offsets without eagerly rebuilding them",
  );

  const normalizedTarget = collectTextNodes(paragraph)
    .find(node => node.textContent.includes("甲乙丙丁後"));
  assert.ok(normalizedTarget, "clear must normalize the unwrapped Text nodes");
  const markerEnd = normalizedTarget.textContent.indexOf("甲乙丙丁") + "甲乙丙丁".length;
  const normalizedExpected = expectedBefore(normalizedTarget)
    + countMatchable(normalizedTarget.textContent.slice(0, markerEnd));
  const beforeNormalizedLookupWalkers = counters.treeWalkers;
  assert.equal(
    window.hoshiSelection.getNormalizedOffset(normalizedTarget, markerEnd),
    normalizedExpected,
    "selection must index the merged Text node created by clear/normalize",
  );
  assert.equal(counters.treeWalkers, beforeNormalizedLookupWalkers + 1);
  window.hoshiSelection.getNormalizedOffset(normalizedTarget, markerEnd);
  assert.equal(counters.treeWalkers, beforeNormalizedLookupWalkers + 1);

  const beforeNoopClearEvents = counters.contentChangedEvents;
  const beforeNoopClearWalkers = counters.treeWalkers;
  await window.chrome.webview.dispatchHostMessage({
    version: 1,
    type: "clearSasayakiHighlight",
    payload: {},
  });
  assert.equal(counters.contentChangedEvents, beforeNoopClearEvents, "no-op clear must not notify");
  assert.equal(counters.treeWalkers, beforeNoopClearWalkers, "no-op clear must not rebuild offsets");

  context.CSS.highlights = {
    set() {},
    delete() {},
    get() { return null; },
  };
  const beforeCssHighlightEvents = counters.contentChangedEvents;
  await window.chrome.webview.dispatchHostMessage({
    version: 1,
    type: "highlightSasayakiCue",
    payload: {
      generation: 2,
      startCodePoint: cueStart,
      length: 4,
      autoScroll: false,
      textColor: "#111111",
      backgroundColor: "#eeee00",
    },
  });
  assert.equal(
    counters.contentChangedEvents,
    beforeCssHighlightEvents,
    "CSS Highlight rendering must not report a DOM mutation",
  );
  const beforeCssLookupWalkers = counters.treeWalkers;
  assert.equal(window.hoshiSelection.getNormalizedOffset(normalizedTarget, markerEnd), normalizedExpected);
  assert.equal(counters.treeWalkers, beforeCssLookupWalkers, "CSS-only highlighting must keep selection offsets ready");
}

verifySasayakiFallbackInvalidation().catch(error => {
  console.error(error);
  process.exitCode = 1;
});
