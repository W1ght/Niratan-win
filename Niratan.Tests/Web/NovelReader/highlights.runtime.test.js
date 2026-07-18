"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");

const highlightsPath = process.argv[2];
if (!highlightsPath) throw new Error("highlights.js path is required");

class FakeElement {
  constructor(tagName) {
    this.nodeType = 1;
    this.tagName = tagName.toUpperCase();
    this.parentElement = null;
    this.childNodes = [];
    this.className = "";
    this.dataset = {};
    this.id = "";
    this._textContent = "";
  }

  get parentNode() { return this.parentElement; }
  get firstChild() { return this.childNodes[0] || null; }
  get textContent() {
    return this.childNodes.length
      ? this.childNodes.map(node => node.textContent).join("")
      : this._textContent;
  }
  set textContent(value) {
    this._textContent = String(value ?? "");
    this.childNodes = [];
  }

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

  remove() {
    this.parentElement?.removeChild(this);
  }

  normalize() {
    for (const child of [...this.childNodes]) {
      if (child.nodeType === 1) child.normalize();
    }
    const normalized = [];
    for (const child of this.childNodes) {
      const previous = normalized[normalized.length - 1];
      if (child.nodeType === 3 && previous?.nodeType === 3) {
        previous.textContent += child.textContent;
        child.parentElement = null;
      } else {
        normalized.push(child);
      }
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

  querySelectorAll(selector) {
    const names = selector.split(",").map(value => value.trim().toUpperCase());
    return collectElements(this, element => names.includes(element.tagName));
  }
}

class FakeText {
  constructor(textContent) {
    this.nodeType = 3;
    this.textContent = textContent;
    this.parentElement = null;
  }

  get parentNode() { return this.parentElement; }
}

class FakeFragment extends FakeElement {
  constructor() {
    super("#fragment");
    this.isFragment = true;
  }
}

function appendText(parent, text) {
  return parent.appendChild(new FakeText(text));
}

function collectTextNodes(root) {
  const result = [];
  const visit = node => {
    if (node.nodeType === 3) {
      result.push(node);
      return;
    }
    for (const child of node.childNodes) visit(child);
  };
  visit(root);
  return result;
}

function collectElements(root, predicate) {
  const result = [];
  const visit = node => {
    if (node.nodeType === 1 && predicate(node)) result.push(node);
    for (const child of node.childNodes || []) visit(child);
  };
  visit(root);
  return result;
}

class FakeRange {
  setStart(node, offset) {
    this.startContainer = node;
    this.startOffset = offset;
  }

  setEnd(node, offset) {
    this.endContainer = node;
    this.endOffset = offset;
  }

  extractContents() {
    assert.equal(this.startContainer, this.endContainer);
    const source = this.startContainer;
    const parent = source.parentElement;
    assert.ok(parent);
    const sourceIndex = parent.childNodes.indexOf(source);
    const before = source.textContent.slice(0, this.startOffset);
    const selected = source.textContent.slice(this.startOffset, this.endOffset);
    const after = source.textContent.slice(this.endOffset);
    parent.removeChild(source);

    const followingNode = parent.childNodes[sourceIndex] || null;
    let insertionIndex = sourceIndex;
    if (before) {
      const beforeNode = new FakeText(before);
      beforeNode.parentElement = parent;
      parent.childNodes.splice(insertionIndex, 0, beforeNode);
      insertionIndex += 1;
    }

    let afterNode = null;
    if (after) {
      afterNode = new FakeText(after);
      afterNode.parentElement = parent;
      parent.childNodes.splice(insertionIndex, 0, afterNode);
    }

    this.insertionParent = parent;
    this.insertionReference = afterNode || followingNode;
    const fragment = new FakeFragment();
    if (selected) fragment.appendChild(new FakeText(selected));
    return fragment;
  }

  cloneContents() {
    assert.equal(this.startContainer, this.endContainer);
    const fragment = new FakeFragment();
    const selected = this.startContainer.textContent.slice(
      this.startOffset,
      this.endOffset,
    );
    if (selected) fragment.appendChild(new FakeText(selected));
    return fragment;
  }

  insertNode(node) {
    this.insertionParent.insertBefore(node, this.insertionReference);
  }
}

const head = new FakeElement("head");
const body = new FakeElement("body");
let contentChangedEvents = 0;

const document = {
  head,
  body,
  createElement: tagName => new FakeElement(tagName),
  createRange: () => new FakeRange(),
  createTreeWalker(root, _whatToShow, filter) {
    const nodes = collectTextNodes(root).filter(node => filter.acceptNode(node) === 1);
    let index = -1;
    return { nextNode: () => nodes[++index] || null };
  },
  getElementById(id) {
    return collectElements(head, element => element.id === id)[0] || null;
  },
  dispatchEvent(event) {
    if (event.type === "niratan-reader-content-changed") contentChangedEvents += 1;
    return true;
  },
};

class FakeHighlight extends Set {
  constructor(...ranges) {
    super(ranges);
  }
}

const cssHighlights = new Map();

class FakeEvent {
  constructor(type) { this.type = type; }
}

const window = { document };
window.window = window;

const selectedRange = new FakeRange();
let selectionCleared = false;
const selection = {
  rangeCount: 1,
  isCollapsed: false,
  getRangeAt: index => {
    assert.equal(index, 0);
    return selectedRange;
  },
  removeAllRanges() {
    this.rangeCount = 0;
    this.isCollapsed = true;
    selectionCleared = true;
  },
};
window.getSelection = () => selection;

const context = vm.createContext({
  window,
  document,
  Node: { TEXT_NODE: 3 },
  NodeFilter: { SHOW_TEXT: 4, FILTER_REJECT: 2, FILTER_ACCEPT: 1 },
  CSS: { highlights: cssHighlights },
  Event: FakeEvent,
  Highlight: FakeHighlight,
  Array,
  Map,
  Math,
  Number,
  Object,
  String,
});

const paragraph = body.appendChild(new FakeElement("p"));
appendText(paragraph, "前。");
const ruby = paragraph.appendChild(new FakeElement("ruby"));
const rubyBase = appendText(ruby, "歪");
const rt = ruby.appendChild(new FakeElement("rt"));
const rubyText = appendText(rt, "ゆが");
const trailingText = appendText(paragraph, "め後");

const source = fs.readFileSync(highlightsPath, "utf8");
vm.runInContext(source, context, { filename: highlightsPath });

window.niratanHighlights.applyHighlights([{
  id: "ruby-highlight",
  color: "green",
  offset: 2,
  text: "歪め",
}]);

const wrappers = window.niratanHighlights.wrappers.get("ruby-highlight");
assert.equal(wrappers, undefined, "CSS highlights must not create DOM wrappers");
const cssHighlight = cssHighlights.get("niratan-highlight-green");
assert.equal(cssHighlight.size, 2, "ruby base and adjacent plain text should each have a range");
assert.equal(ruby.parentElement, paragraph, "CSS painting must not move the ruby element");
assert.equal(rubyBase.parentElement, ruby, "base text must remain a direct ruby child");
assert.equal(rt.parentElement, ruby, "rt must remain in the same ruby formatting context");
assert.equal(rubyText.parentElement, rt);

const visibleText = collectTextNodes(body)
  .filter(node => !node.parentElement.closest("rt, rp"))
  .map(node => node.textContent)
  .join("");
assert.equal(visibleText, "前。歪め後", "furigana must remain excluded from persistent offsets");

window.niratanHighlights.removeHighlight("ruby-highlight");
assert.equal(ruby.parentElement, paragraph, "removal must restore the original ruby position");
assert.equal(rubyBase.parentElement, ruby);
assert.equal(rt.parentElement, ruby);
assert.equal(
  collectElements(body, element => element.className.includes("niratan-highlight")).length,
  0,
  "removal must not leave highlight wrappers behind",
);
assert.equal(cssHighlight.size, 0, "removal must clear the rendered CSS ranges");

selectedRange.setStart(trailingText, 0);
selectedRange.setEnd(trailingText, 1);
const created = window.niratanHighlights.createHighlight("pink", "created-highlight");
assert.deepEqual(
  JSON.parse(JSON.stringify(created)),
  { start: 2, offset: 3, text: "め" },
  "creation must keep Niratan's normalized whole-book start separate from its raw render offset",
);
assert.equal(selectionCleared, true, "creation must clear the browser selection");
assert.equal(
  cssHighlights.get("niratan-highlight-pink").size,
  1,
  "the new highlight must render immediately",
);

window.niratanHighlights.removeHighlight("created-highlight");
assert.equal(contentChangedEvents, 4, "apply, delete, create, and delete must notify offset caches");
