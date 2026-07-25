"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");

const visualNovelPath = process.argv[2];
if (!visualNovelPath) throw new Error("reader-visual-novel.js path is required");

const window = {
  __niratanVisualNovelSettings: {
    enabled: true,
    revealSpeed: 45,
    screenMode: "block",
    sentencesPerScreen: 1,
    preserveDialogue: false,
    clickAdvance: true,
  },
  getSelection() { return { isCollapsed: true }; },
};
window.window = window;

const context = vm.createContext({
  window,
  Intl,
  Set,
  WeakMap,
  Number,
  Math,
  Array,
  String,
  RegExp,
  Node: { TEXT_NODE: 3, ELEMENT_NODE: 1 },
  NodeFilter: { SHOW_TEXT: 4, FILTER_REJECT: 2, FILTER_ACCEPT: 1 },
  document: {},
  setTimeout,
  clearTimeout,
});
vm.runInContext(fs.readFileSync(visualNovelPath, "utf8"), context, {
  filename: visualNovelPath,
});

const reader = window.niratanVisualNovel;
assert.equal(reader.enabled, true);
assert.equal(reader.clickAdvance, true);

reader.screens = [
  { startChar: 0, endChar: 5, startRaw: 0, endRaw: 5 },
  { startChar: 5, endChar: 10, startRaw: 5, endRaw: 10 },
];
reader.totalChars = 10;
reader.currentScreenIndex = 0;
reader.revealComplete = false;
reader.completeReveal = function () { this.revealComplete = true; };
reader.renderScreen = function (index, fullyRevealed) {
  this.currentScreenIndex = index;
  this.revealComplete = fullyRevealed;
};

assert.equal(reader.paginate("forward"), "scrolled");
assert.equal(reader.currentScreenIndex, 0, "first advance completes the active reveal");
assert.equal(reader.revealComplete, true);
assert.equal(reader.paginate("forward"), "scrolled");
assert.equal(reader.currentScreenIndex, 1, "second advance moves to the next screen");
assert.equal(reader.calculateProgress(), 1);
assert.equal(reader.paginate("forward"), "scrolled");
assert.equal(reader.currentScreenIndex, 1, "the final screen reveal completes before chapter limit");
assert.equal(reader.paginate("forward"), "limit");
assert.equal(reader.paginate("backward"), "scrolled");
assert.equal(reader.currentScreenIndex, 0);
assert.equal(reader.revealComplete, true, "backward navigation shows prior content immediately");

reader.setRevealSpeed(999);
assert.equal(reader.revealSpeed, 120);
reader.setRevealSpeed(-3);
assert.equal(reader.revealSpeed, 0);

