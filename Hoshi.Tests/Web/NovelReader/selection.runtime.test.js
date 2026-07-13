"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");

const selectionPath = process.argv[2];
if (!selectionPath) throw new Error("selection.js path is required");

const documentListeners = new Map();
const counters = {
  treeWalkers: 0,
  nextNodes: 0,
};

class FakeElement {
  constructor(tagName, parentElement = null) {
    this.nodeType = 1;
    this.tagName = tagName.toUpperCase();
    this.parentElement = parentElement;
    this.childNodes = [];
  }

  appendChild(node) {
    node.parentElement = this;
    this.childNodes.push(node);
    return node;
  }

  insertBefore(node, referenceNode) {
    node.parentElement = this;
    const index = this.childNodes.indexOf(referenceNode);
    this.childNodes.splice(index < 0 ? this.childNodes.length : index, 0, node);
    return node;
  }

  closest(selector) {
    const names = selector.split(",").map(value => value.trim().toUpperCase());
    for (let current = this; current; current = current.parentElement) {
      if (names.includes(current.tagName)) return current;
    }
    return null;
  }
}

class FakeText {
  constructor(textContent, parentElement = null) {
    this.nodeType = 3;
    this.textContent = textContent;
    this.parentElement = parentElement;
  }
}

const body = new FakeElement("body");

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

const document = {
  body,
  addEventListener(type, listener) {
    const listeners = documentListeners.get(type) || [];
    listeners.push(listener);
    documentListeners.set(type, listeners);
  },
  dispatchEvent(event) {
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
  elementFromPoint() { return null; },
};

const outbound = [];
const window = {
  document,
  chrome: { webview: { postMessage(message) { outbound.push(message); } } },
  getComputedStyle() { return { writingMode: "horizontal-tb" }; },
  getSelection() { return { removeAllRanges() {} }; },
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
  Node: { TEXT_NODE: 3, ELEMENT_NODE: 1 },
  NodeFilter: { SHOW_TEXT: 4, FILTER_REJECT: 2, FILTER_ACCEPT: 1 },
  CSS: {},
  Event: FakeEvent,
  Highlight: class {},
  Date,
  Math,
  Number,
  Object,
  Array,
  Map,
  Set,
  WeakMap,
  String,
  performance: { now: () => 1 },
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
