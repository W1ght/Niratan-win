"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");

const bridgePath = process.argv[2];
if (!bridgePath) throw new Error("reader-bridge.js path is required");

const outbound = [];
const documentListeners = new Map();
const webViewListeners = new Map();

class FakeElement {
  closest() { return null; }
}

const body = new FakeElement();
Object.assign(body, {
  scrollHeight: 600,
  scrollWidth: 1200,
  scrollTop: 0,
  scrollLeft: 0,
  clientWidth: 400,
  innerText: "",
  appendChild() {},
  addEventListener() {},
});

const style = { setProperty() {} };
const document = {
  body,
  head: { appendChild() {} },
  documentElement: { style, scrollTop: 0, scrollLeft: 0 },
  fonts: { ready: Promise.resolve() },
  readyState: "complete",
  baseURI: "https://hoshi-novel-book.local/Text/chapter.xhtml",
  addEventListener(type, listener) {
    const listeners = documentListeners.get(type) || [];
    listeners.push(listener);
    documentListeners.set(type, listeners);
  },
  querySelector() { return null; },
  querySelectorAll() { return []; },
  getElementById() { return null; },
  getElementsByName() { return []; },
  createElement() {
    return Object.assign(new FakeElement(), {
      style: {},
      classList: { add() {} },
      appendChild() {},
      remove() {},
    });
  },
  createTreeWalker() {
    return { currentNode: null, nextNode() { return null; } };
  },
  createRange() {
    return {
      collapsed: false,
      selectNodeContents() {},
      setStart() {},
      setEnd() {},
      getClientRects() { return []; },
      getBoundingClientRect() {
        return { left: 0, right: 0, top: 0, bottom: 0, width: 0, height: 0 };
      },
    };
  },
};

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
      addEventListener(type, listener) {
        webViewListeners.set(type, listener);
      },
      postMessage(message) {
        outbound.push(message);
      },
      dispatchRendererMessage(data) {
        const listener = webViewListeners.get("message");
        return listener?.(Object.freeze({ isTrusted: false, data }));
      },
      dispatchHostMessage(data) {
        const listener = webViewListeners.get("message");
        return listener?.(Object.freeze({ isTrusted: true, data }));
      },
    },
  },
  addEventListener(type, listener) {
    const listeners = windowListeners.get(type) || [];
    listeners.push(listener);
    windowListeners.set(type, listeners);
  },
  getSelection() {
    return { isCollapsed: true, removeAllRanges() {} };
  },
  scrollTo() {},
};
window.window = window;

const context = vm.createContext({
  window,
  document,
  Element: FakeElement,
  Node: { TEXT_NODE: 3, ELEMENT_NODE: 1 },
  NodeFilter: { SHOW_TEXT: 4, FILTER_REJECT: 2, FILTER_ACCEPT: 1 },
  CSS: {},
  URL,
  Date,
  Error,
  JSON,
  Math,
  Number,
  Object,
  Promise,
  String,
  Array,
  Map,
  WeakMap,
  RegExp,
  console,
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
});

const bridgeSource = fs.readFileSync(bridgePath, "utf8");
vm.runInContext(bridgeSource, context, { filename: bridgePath });

assert.equal(typeof context.handleNavigate, "undefined");
assert.equal(typeof context.handleMessage, "undefined");
assert.equal(typeof window.handleNavigate, "undefined");
assert.equal(typeof window.handleMessage, "undefined");
assert.equal(typeof context.reader, "undefined");
assert.equal(typeof window.reader, "undefined");
assert.equal(typeof window.hoshiReader, "undefined");
assert.equal(typeof context.sasayaki, "undefined");
assert.equal(typeof window.hoshiSasayaki, "undefined");

function messages(type) {
  return outbound.filter(message => message.type === type);
}

function fireWheel() {
  const event = {
    target: new FakeElement(),
    deltaX: 0,
    deltaY: 100,
    ctrlKey: false,
    metaKey: false,
    altKey: false,
    shiftKey: false,
    preventDefault() {},
  };
  for (const listener of documentListeners.get("wheel") || []) listener(event);
}

async function main() {
  fireWheel();
  assert.equal(messages("pageNavigationRequest").length, 0);

  await window.chrome.webview.dispatchRendererMessage({
    version: 1,
    type: "setWheelNavigation",
    payload: { enabled: true },
  });
  fireWheel();
  assert.equal(messages("pageNavigationRequest").length, 0);

  await window.chrome.webview.dispatchHostMessage({
    version: 1,
    type: "setWheelNavigation",
    payload: { enabled: true },
  });
  fireWheel();
  assert.equal(messages("pageNavigationRequest").length, 1);

  await window.chrome.webview.dispatchRendererMessage({
    version: 1,
    type: "navigatePage",
    payload: { direction: "forward" },
  });
  assert.equal(messages("pageChanged").length, 0);

  await window.chrome.webview.dispatchHostMessage({
    version: 1,
    type: "navigatePage",
    payload: { direction: "forward" },
  });
  assert.equal(messages("pageChanged").length, 1);

  const errorsBefore = messages("error").length;
  await window.chrome.webview.dispatchHostMessage({
    version: 1,
    type: "setWheelNavigation",
    payload: { enabled: "true" },
  });
  assert.equal(messages("error").length, errorsBefore + 1);

  await window.chrome.webview.dispatchHostMessage({
    version: 2,
    type: "navigatePage",
    payload: { direction: "forward" },
  });
  await window.chrome.webview.dispatchHostMessage({
    version: 1,
    type: "navigatePage",
    payload: [],
  });
  assert.equal(messages("error").length, errorsBefore + 3);
  assert.equal(messages("pageChanged").length, 1);
}

main().catch(error => {
  console.error(error);
  process.exitCode = 1;
});
