//
//  popup.js — Hoshi Reader Windows (ported from Hoshi Reader Android)
//  SPDX-License-Identifier: GPL-3.0-or-later
//

const KANJI_RANGE = '一-鿿㐀-䶿豈-﫿々';
const KANJI_PATTERN = new RegExp(`[${KANJI_RANGE}]`);
const KANJI_SEGMENT_PATTERN = new RegExp(`[${KANJI_RANGE}]+|[^${KANJI_RANGE}]+`, 'g');
const KANA_PATTERN = /[぀-ヿｦ-ﾟ]/;
const DEFAULT_HARMONIC_RANK = '9999999';
const SMALL_KANA_SET = new Set('ぁぃぅぇぉゃゅょゎァィゥェォャュョヮ');
const NUMERIC_TAG = /^\d+$/;
const POS_TAGS = new Set(['n', 'adj-i', 'adj-na', 'adj-no', 'v1', 'vk', 'vs', 'vs-i', 'vs-s', 'vz', 'vi', 'vt']);
const COMPACT_GLOSSARIES_ANKI = '.yomitan-glossary ul[data-sc-content="glossary"] > li:not(:first-child)::before, .yomitan-glossary .glossary-list > li:not(:first-child)::before { white-space: pre-wrap; content: " | "; display: inline; color: rgb(119, 119, 119); }\n'
  + '.yomitan-glossary ul[data-sc-content="glossary"] > li, .yomitan-glossary .glossary-list > li { display: inline; }\n'
  + '.yomitan-glossary ul[data-sc-content="glossary"], .yomitan-glossary .glossary-list { display: inline; list-style: none; padding-left: 0px; }';

let selectedDictionaries = {};
let miningRequestPending = false;
let popupScrollIndicatorTimer = 0;

function getPopupScrollElement() {
  return document.getElementById('popup-viewport') || document.scrollingElement;
}

function setPopupScrollIndicatorActive(eventTarget) {
  var root = document.documentElement;
  var body = document.body;
  var activeClass = 'popup-scroll-active';
  root.classList.add(activeClass);
  if (body) body.classList.add(activeClass);

  var scrollTarget = eventTarget && eventTarget.nodeType === Node.ELEMENT_NODE ? eventTarget : null;
  if (scrollTarget && scrollTarget.classList) scrollTarget.classList.add(activeClass);

  if (popupScrollIndicatorTimer) {
    clearTimeout(popupScrollIndicatorTimer);
  }

  popupScrollIndicatorTimer = setTimeout(function () {
    root.classList.remove(activeClass);
    if (body) body.classList.remove(activeClass);
    document.querySelectorAll('.' + activeClass).forEach(function (element) {
      element.classList.remove(activeClass);
    });
  }, 900);
}

function postPopupMessage(name, body) {
  window.chrome?.webview?.postMessage({
    version: 1,
    type: 'popupMessage',
    payload: { name: name, body: body },
  });
}

function postPopupTrace(stage, details) {
  try {
    postPopupMessage('popupDiagnostic', {
      kind: 'popupTrace',
      stage: stage,
      lookupTraceId: window.lookupTraceId || '',
      now: performance.now(),
      details: details || {}
    });
  } catch (_) { }
}

function getPopupSelectionText() {
  return window.hoshiSelection?.selection?.text || window.getSelection()?.toString() || '';
}

function el(tag, props, children) {
  props = props || {};
  children = children || [];
  var element = document.createElement(tag);
  var keys = Object.keys(props);
  for (var ki = 0; ki < keys.length; ki++) {
    var key = keys[ki];
    var value = props[key];
    if (key in element) {
      element[key] = value;
    } else {
      element.setAttribute(key, value);
    }
  }
  for (var ci = 0; ci < children.length; ci++) {
    element.append(children[ci]);
  }
  return element;
}

function toHiragana(text) {
  return text.replace(/[ァ-ヶ]/g, function (ch) { return String.fromCharCode(ch.charCodeAt(0) - 0x60); });
}

function toKebabCase(str) {
  return str.replace(/([A-Z])/g, function (_, c, i) { return (i ? '-' : '') + c.toLowerCase(); });
}

function isStringPartiallyJapanese(str) {
  if (!str) return false;
  for (var i = 0; i < str.length; i++) {
    if (window.hoshiSelection?.isCodePointJapanese(str.charCodeAt(i))) return true;
  }
  return false;
}

function isStringPartiallyChinese(text) {
  if (!text) return false;
  return KANJI_PATTERN.test(text) || /[㄀-ㄯㆠ-ㆿ]/.test(text);
}

function getLanguageFromText(text, language) {
  var partiallyJapanese = isStringPartiallyJapanese(text);
  var partiallyChinese = isStringPartiallyChinese(text);
  if (!['zh', 'yue'].includes(language ?? '')) {
    if (partiallyJapanese) return 'ja';
    if (partiallyChinese) return 'zh';
  }
  return language || null;
}

function openExternalLink(url) {
  postPopupMessage('openLink', url);
}

function showDescription(element) {
  var description = element.getAttribute('data-description');
  if (!description) return;
  var overlay = document.querySelector('.overlay');
  document.querySelector('.overlay-content').textContent = description;
  overlay.style.display = 'block';
}

function closeOverlay() {
  document.querySelector('.overlay').style.display = 'none';
}

// === Furigana segmentation ===

function createFuriganaSegment(text, reading) {
  return { text: text, reading: reading };
}

function getFuriganaKanaSegments(text, reading) {
  var textLength = text.length;
  var newSegments = [];
  var start = 0;
  var state = (reading[0] === text[0]);
  for (var i = 1; i < textLength; ++i) {
    var newState = (reading[i] === text[i]);
    if (state === newState) continue;
    newSegments.push(createFuriganaSegment(text.substring(start, i), state ? '' : reading.substring(start, i)));
    state = newState;
    start = i;
  }
  newSegments.push(createFuriganaSegment(text.substring(start, textLength), state ? '' : reading.substring(start, textLength)));
  return newSegments;
}

function segmentizeFurigana(reading, readingNormalized, groups, groupsStart) {
  var groupCount = groups.length - groupsStart;
  if (groupCount <= 0) return reading.length === 0 ? [] : null;

  var group = groups[groupsStart];
  var isKana = group.isKana;
  var text = group.text;
  var textLength = text.length;
  if (isKana) {
    var textNormalized = group.textNormalized;
    if (textNormalized !== null && readingNormalized.startsWith(textNormalized)) {
      var segments = segmentizeFurigana(
        reading.substring(textLength),
        readingNormalized.substring(textLength),
        groups, groupsStart + 1);
      if (segments !== null) {
        if (reading.startsWith(text)) {
          segments.unshift(createFuriganaSegment(text, ''));
        } else {
          var kanaSegs = getFuriganaKanaSegments(text, reading);
          for (var k = kanaSegs.length - 1; k >= 0; k--) segments.unshift(kanaSegs[k]);
        }
        return segments;
      }
    }
    return null;
  } else {
    var result = null;
    for (var ri = reading.length; ri >= textLength; --ri) {
      var segs = segmentizeFurigana(
        reading.substring(ri),
        readingNormalized.substring(ri),
        groups, groupsStart + 1);
      if (segs !== null) {
        if (result !== null) return null;
        var segmentReading = reading.substring(0, ri);
        segs.unshift(createFuriganaSegment(text, segmentReading));
        result = segs;
      }
      if (groupCount === 1) break;
    }
    return result;
  }
}

function segmentFurigana(expression, reading) {
  if (!reading || reading === expression) return [[expression, '']];

  var groups = [];
  var segmentMatches = expression.match(KANJI_SEGMENT_PATTERN) || [];
  for (var si = 0; si < segmentMatches.length; si++) {
    var stext = segmentMatches[si];
    var isKana = !KANJI_PATTERN.test(stext[0]);
    groups.push({ isKana: isKana, text: stext, textNormalized: isKana ? toHiragana(stext) : null });
  }

  var readingNormalized = toHiragana(reading);
  var segments = segmentizeFurigana(reading, readingNormalized, groups, 0);
  if (segments !== null) return segments.map(function (seg) { return [seg.text, seg.reading]; });
  return [[expression, reading]];
}

function buildFuriganaEl(parent, expression, reading) {
  var segments = segmentFurigana(expression, reading);
  for (var i = 0; i < segments.length; i++) {
    var seg = segments[i];
    var text = seg[0];
    var furigana = seg[1];
    if (furigana) {
      var ruby = el('ruby', {}, [document.createTextNode(text)]);
      ruby.appendChild(el('rt', { textContent: furigana }));
      parent.appendChild(ruby);
    } else {
      parent.appendChild(document.createTextNode(text));
    }
  }
  return segments.length === 1 && segments[0][1];
}

// === Dictionary CSS scoping ===

function constructDictCss(css, dictName) {
  if (!css) return '';
  var prefix = '.yomitan-glossary [data-dictionary="' + dictName + '"]';
  var parts = [];
  var i = 0;
  while (i < css.length) {
    while (i < css.length && /\s/.test(css[i])) { parts.push(css[i++]); }
    if (css.slice(i, i + 2) === '/*') {
      var end = css.indexOf('*/', i + 2);
      if (end === -1) break;
      parts.push(css.slice(i, end + 2));
      i = end + 2;
      continue;
    }
    var bracePos = css.indexOf('{', i);
    if (bracePos === -1) break;
    var selectorPart = css.slice(i, bracePos);
    var selectors = selectorPart.split(',').map(function (s) {
      var trimmed = s.trim();
      if (!trimmed) return '';
      if (trimmed.charAt(0) === '&') return s;
      return prefix + ' ' + trimmed;
    });
    parts.push(selectors.join(', '), ' {');
    i = bracePos + 1;
    var depth = 1;
    var blockStart = i;
    while (i < css.length && depth > 0) {
      if (css[i] === '{') depth++;
      else if (css[i] === '}') depth--;
      i++;
    }
    var blockContent = css.slice(blockStart, i - 1);
    if (blockContent.indexOf('{') !== -1) {
      var pos = 0;
      var properties = '';
      var nestedRules = '';
      while (pos < blockContent.length) {
        while (pos < blockContent.length && /\s/.test(blockContent[pos])) pos++;
        if (pos >= blockContent.length) break;
        var nextSemi = blockContent.indexOf(';', pos);
        var nextBrace = blockContent.indexOf('{', pos);
        if (nextBrace !== -1 && (nextSemi === -1 || nextBrace < nextSemi)) {
          var nestedDepth = 1;
          var nestedEnd = nextBrace + 1;
          while (nestedEnd < blockContent.length && nestedDepth > 0) {
            if (blockContent[nestedEnd] === '{') nestedDepth++;
            else if (blockContent[nestedEnd] === '}') nestedDepth--;
            nestedEnd++;
          }
          nestedRules += blockContent.slice(pos, nestedEnd);
          pos = nestedEnd;
        } else if (nextSemi !== -1) {
          properties += blockContent.slice(pos, nextSemi + 1);
          pos = nextSemi + 1;
        } else {
          properties += blockContent.slice(pos);
          break;
        }
      }
      parts.push(properties);
      if (nestedRules) parts.push(constructDictCss(nestedRules, dictName));
    } else {
      parts.push(blockContent);
    }
    parts.push('}');
  }
  return parts.join('');
}

function applyTableStyles(html) {
  var tableStyle = 'table-layout:auto;border-collapse:collapse;';
  var cellStyle = 'border-style:solid;padding:0.25em;vertical-align:top;border-width:1px;border-color:currentColor;';
  var thStyle = 'font-weight:bold;' + cellStyle;
  return html
    .replace(/<table(?=[>\s])/g, '<table style="' + tableStyle + '"')
    .replace(/<th(?=[>\s])/g, '<th style="' + thStyle + '"')
    .replace(/<td(?=[>\s])/g, '<td style="' + cellStyle + '"');
}

// === Image handling ===

var currentDictionaryMedia = null;

function getMediaFilename(dictionary, path) {
  var key = dictionary + '\n' + path;
  if (!currentDictionaryMedia.has(key)) {
    var extension = path.split('.').pop();
    currentDictionaryMedia.set(key, {
      dictionary: dictionary,
      path: path,
      filename: 'hoshi_dict_' + currentDictionaryMedia.size + '.' + extension,
    });
  }
  return currentDictionaryMedia.get(key).filename;
}

function getDictionaryMediaUrl(dictionary, path) {
  if (window.dictionaryMediaRequestEndpoint) {
    return window.dictionaryMediaRequestEndpoint + '?dictionary=' + encodeURIComponent(dictionary) + '&path=' + encodeURIComponent(path);
  }
  return 'image://?dictionary=' + encodeURIComponent(dictionary) + '&path=' + encodeURIComponent(path);
}

function applyDictionaryImageContainerFixes(imageContainer) {
  if (window.disablePopupImageViewportMaxHeight) {
    imageContainer.style.maxHeight = 'none';
  }
}

function applyImageStyles(node, imageContainer, aspectRatioSizer, imageBackground, image, filename, appearance, useEmUnits) {
  node.style.cssText += 'display:inline-block;position:relative;line-height:1;max-width:100%;';
  imageContainer.style.cssText += 'display:inline-block;white-space:nowrap;max-width:100%;max-height:100vh;position:relative;vertical-align:top;line-height:0;overflow:hidden;font-size:' + (useEmUnits ? '1em' : '1px') + ';';
  aspectRatioSizer.style.cssText += 'display:inline-block;width:0;vertical-align:top;font-size:0;';
  image.style.cssText += 'display:inline-block;vertical-align:top;object-fit:contain;border:none;outline:none;position:absolute;left:0;top:0;width:100%;height:100%;';
  if (appearance === 'monochrome') {
    imageBackground.style.cssText += '--image:url("' + filename + '");position:absolute;left:0;top:0;width:100%;height:100%;-webkit-mask-repeat:no-repeat;-webkit-mask-position:center center;-webkit-mask-mode:alpha;-webkit-mask-size:contain;-webkit-mask-image:var(--image);mask-repeat:no-repeat;mask-position:center center;mask-mode:alpha;mask-size:contain;mask-image:var(--image);background-color:currentColor;';
    image.style.opacity = '0';
  }
}

function setStructuredContentElementStyle(element, style) {
  for (var _i = 0, _keys = Object.keys(style); _i < _keys.length; _i++) {
    var property = _keys[_i];
    var value = style[property];
    if ((property === 'marginTop' || property === 'marginLeft' || property === 'marginRight' || property === 'marginBottom') && typeof value === 'number') {
      element.style[property] = value + 'em';
    } else {
      element.style[property] = value;
    }
  }
}

function createDefinitionImage(data, dictionary, exporting) {
  exporting = exporting || false;
  var path = data.path;
  var width = data.width || 100;
  var height = data.height || 100;
  var preferredWidth = data.preferredWidth;
  var preferredHeight = data.preferredHeight;
  var title = data.title;
  var pixelated = data.pixelated;
  var imageRendering = data.imageRendering;
  var appearance = data.appearance;
  var background = data.background;
  var collapsed = data.collapsed;
  var collapsible = data.collapsible;
  var verticalAlign = data.verticalAlign;
  var border = data.border;
  var borderRadius = data.borderRadius;
  var sizeUnits = data.sizeUnits;
  var nodeData = data.data;
  var hasPreferredWidth = (typeof preferredWidth === 'number');
  var hasPreferredHeight = (typeof preferredHeight === 'number');
  var hasDimensions = (hasPreferredWidth || hasPreferredHeight || typeof data.width === 'number' || typeof data.height === 'number');
  var invAspectRatio = (hasPreferredWidth && hasPreferredHeight ? preferredHeight / preferredWidth : height / width);
  var usedWidth = (hasPreferredWidth ? preferredWidth : (hasPreferredHeight ? preferredHeight / invAspectRatio : width));

  var node = document.createElement('a');
  node.classList.add('gloss-image-link');
  node.target = '_blank';
  node.rel = 'noreferrer noopener';

  var imageContainer = document.createElement('span');
  imageContainer.classList.add('gloss-image-container');
  node.appendChild(imageContainer);

  var aspectRatioSizer = document.createElement('span');
  aspectRatioSizer.classList.add('gloss-image-sizer');
  imageContainer.appendChild(aspectRatioSizer);

  var imageBackground = document.createElement('span');
  imageBackground.classList.add('gloss-image-background');
  imageContainer.appendChild(imageBackground);

  node.dataset.path = path;
  node.dataset.dictionary = dictionary;
  node.dataset.hasAspectRatio = 'true';
  node.dataset.imageRendering = typeof imageRendering === 'string' ? imageRendering : (pixelated ? 'pixelated' : 'auto');
  node.dataset.appearance = typeof appearance === 'string' ? appearance : 'auto';
  node.dataset.background = typeof background === 'boolean' ? String(background) : 'true';
  node.dataset.collapsed = typeof collapsed === 'boolean' ? String(collapsed) : 'false';
  node.dataset.collapsible = typeof collapsible === 'boolean' ? String(collapsible) : 'true';
  if (typeof verticalAlign === 'string') node.dataset.verticalAlign = verticalAlign;
  if (typeof sizeUnits === 'string') node.dataset.sizeUnits = sizeUnits;

  aspectRatioSizer.style.paddingTop = (invAspectRatio * 100) + '%';
  if (typeof border === 'string') imageContainer.style.border = border;
  if (typeof borderRadius === 'string') imageContainer.style.borderRadius = borderRadius;
  imageContainer.style.width = usedWidth + 'em';
  applyDictionaryImageContainerFixes(imageContainer);
  if (typeof title === 'string') imageContainer.title = title;

  var alt = (nodeData?.alt || title || '');
  if (!exporting) {
    var imageUrl = getDictionaryMediaUrl(dictionary, path);
    var img = document.createElement('img');
    img.classList.add('gloss-image');
    img.alt = alt;
    if (!hasDimensions) {
      img.addEventListener('load', function () {
        var imageWidth = Math.min(img.naturalWidth, window.innerWidth - 20);
        imageContainer.style.width = imageWidth + 'px';
        aspectRatioSizer.style.paddingTop = ((img.naturalHeight / img.naturalWidth) * 100) + '%';
        applyDictionaryImageContainerFixes(imageContainer);
      }, { once: true });
    } else if (!hasPreferredWidth && !hasPreferredHeight && sizeUnits === 'em') {
      img.addEventListener('load', function () {
        var aspectRatio = img.naturalHeight / img.naturalWidth;
        var widthEm = typeof data.width === 'number' ? data.width : data.height / aspectRatio;
        imageContainer.style.width = widthEm + 'em';
        aspectRatioSizer.style.paddingTop = (aspectRatio * 100) + '%';
        applyDictionaryImageContainerFixes(imageContainer);
      }, { once: true });
    }
    img.src = imageUrl;
    imageContainer.appendChild(img);
  } else {
    var filename = (window.useAnkiConnect || window.embedMedia) ? getMediaFilename(dictionary, path) : null;
    var image = document.createElement(filename ? 'img' : 'span');
    image.classList.add('gloss-image');
    if (filename) {
      image.alt = alt;
      image.src = filename;
      if (sizeUnits === 'em') {
        var emSize = 14;
        var scaleFactor = 2 * (window.devicePixelRatio || 1);
        image.width = usedWidth * emSize * scaleFactor;
      } else {
        image.width = usedWidth;
      }
      image.height = image.width * invAspectRatio;
      applyImageStyles(node, imageContainer, aspectRatioSizer, imageBackground, image, filename, appearance, sizeUnits === 'em');
    } else {
      image.textContent = alt;
    }
    imageContainer.appendChild(image);
  }

  return node;
}

// === Structured content rendering ===

function renderStructuredContent(parent, node, language, dictName, exporting) {
  language = language || null;
  dictName = dictName || null;
  exporting = exporting || false;

  if (typeof node === 'string') {
    node.split(/\r?\n/).forEach(function (line, i) {
      if (i > 0) parent.appendChild(document.createElement('br'));
      if (line) {
        if (!language && !parent.hasAttribute('lang')) {
          var detected = getLanguageFromText(line, language);
          if (detected) parent.setAttribute('lang', detected);
        }
        parent.appendChild(document.createTextNode(line));
      }
    });
    return;
  }

  if (Array.isArray(node)) {
    var isStringArray = node.every(function (item) { return typeof item === 'string'; });
    var insideSpan = parent.tagName === 'SPAN';
    if (isStringArray && node.length > 1 && !insideSpan) {
      var ul = document.createElement('ul');
      ul.classList.add('glossary-list');
      node.forEach(function (child) {
        var li = document.createElement('li');
        li.appendChild(document.createTextNode(child));
        ul.appendChild(li);
      });
      parent.appendChild(ul);
      return;
    }

    var items = node.map(function (item) {
      return item && item.type === 'structured-content' ? item.content : item;
    });
    var isLinkArray = items.every(function (item) { return item && item.tag === 'a'; });
    if (isLinkArray && node.length > 1) {
      var ul2 = document.createElement('ul');
      ul2.classList.add('glossary-list');
      node.forEach(function (child) {
        var li = document.createElement('li');
        renderStructuredContent(li, child, language, dictName, exporting);
        ul2.appendChild(li);
      });
      parent.appendChild(ul2);
      return;
    }

    node.forEach(function (child) { renderStructuredContent(parent, child, language, dictName, exporting); });
    return;
  }

  if (!node || typeof node !== 'object') return;

  if (node.type === 'structured-content') {
    var container = document.createElement('span');
    container.classList.add('structured-content');
    parent.appendChild(container);
    renderStructuredContent(container, node.content, language, dictName, exporting);
    return;
  }

  if (node.tag === 'img') {
    parent.appendChild(createDefinitionImage(node, dictName, exporting));
    return;
  }

  var tagName = node.tag || 'span';
  var element = document.createElement(tagName);
  element.classList.add('gloss-sc-' + tagName);
  var nextLanguage = language;

  if (node.href) {
    element.setAttribute('href', node.href);
    element.onclick = function (e) {
      e.preventDefault();
      e.stopPropagation();
      if (/^https?:\/\//i.test(node.href)) {
        openExternalLink(node.href);
      } else {
        var qi = node.href.indexOf('?');
        var query = qi < 0 ? null : new URLSearchParams(node.href.slice(qi + 1)).get('query');
        if (query) {
          postPopupMessage('lookupRedirect', query);
        }
      }
    };
  }

  if (node.title) element.setAttribute('title', node.title);
  if (node.lang) { element.setAttribute('lang', node.lang); nextLanguage = node.lang; }

  if (node.data) {
    for (var _k = 0, _dk = Object.keys(node.data); _k < _dk.length; _k++) {
      var k = _dk[_k];
      var v = node.data[k];
      var isCJK = /^[　-鿿豈-﫿]/.test(k);
      element.setAttribute('data-sc' + (isCJK ? '' : '-') + toKebabCase(k), v);
    }
  }

  if (node.style) setStructuredContentElementStyle(element, node.style);
  if (node.content) renderStructuredContent(element, node.content, nextLanguage, dictName, exporting);
  if (node.colSpan) element.setAttribute('colspan', node.colSpan);
  if (node.rowSpan) element.setAttribute('rowspan', node.rowSpan);

  if (tagName === 'table') {
    var tableContainer = document.createElement('div');
    tableContainer.classList.add('gloss-sc-table-container');
    tableContainer.appendChild(element);
    parent.appendChild(tableContainer);
    return;
  }

  parent.appendChild(element);
}

// === Tags ===

function isPartOfSpeech(tag) {
  return POS_TAGS.has(tag) || tag.startsWith('v5');
}

function parseTags(raw) {
  return (raw || '').split(' ').filter(Boolean);
}

function createGlossaryTags(tags, className) {
  className = className || 'glossary-tags';
  if (!tags || !tags.length) return null;
  return el('div', { className: className }, tags.map(function (tag) {
    return el('span', { className: 'glossary-tag', textContent: tag });
  }));
}

function createDeinflectionTag(tag) {
  return el('span', {
    className: 'deinflection-tag',
    textContent: tag.name,
    'data-description': tag.description,
    onclick: function () { showDescription(this); }
  });
}

function createFrequencyGroup(freqGroup) {
  var values = freqGroup.frequencies.map(function (f) { return f.displayValue || f.value; }).join(', ');
  return el('span', { className: 'frequency-group', 'data-details': freqGroup.dictionary }, [
    el('span', { className: 'frequency-dict-label', textContent: freqGroup.dictionary }),
    el('span', { className: 'frequency-values', textContent: values })
  ]);
}

function getFrequencyHarmonicRank(frequencies) {
  if (!frequencies || frequencies.length === 0) return DEFAULT_HARMONIC_RANK;
  var values = [];
  var seenDictionaries = new Set();
  frequencies.forEach(function (freqGroup) {
    var dictionary = freqGroup?.dictionary;
    if (dictionary && seenDictionaries.has(dictionary)) return;
    if (dictionary) seenDictionaries.add(dictionary);
    var firstFreq = freqGroup?.frequencies?.[0];
    if (!firstFreq) return;
    var displayValue = firstFreq.displayValue;
    if (displayValue != null) {
      var match = String(displayValue).match(/^\d+/);
      if (match) {
        var parsed = Number.parseInt(match[0], 10);
        if (parsed > 0) { values.push(parsed); return; }
      }
    }
    var val = firstFreq.value;
    if (val && val > 0) values.push(val);
  });
  if (values.length === 0) return DEFAULT_HARMONIC_RANK;
  var sumOfReciprocals = values.reduce(function (sum, val) { return sum + (1 / val); }, 0);
  return String(Math.floor(values.length / sumOfReciprocals));
}

function createHarmonicFrequencyTag(frequencies) {
  var rank = getFrequencyHarmonicRank(frequencies);
  return el('span', { className: 'frequency-group harmonic-frequency' }, [
    el('span', { className: 'frequency-dict-label', textContent: 'Average' }),
    el('span', { className: 'frequency-values', textContent: rank })
  ]);
}

// === Pitch accent ===

function isMoraPitchHigh(moraIndex, pitchAccentValue) {
  switch (pitchAccentValue) {
    case 0: return (moraIndex > 0);
    case 1: return (moraIndex < 1);
    default: return (moraIndex > 0 && moraIndex < pitchAccentValue);
  }
}

function getKanaMorae(text) {
  var morae = [];
  for (var ci = 0; ci < text.length; ci++) {
    var c = text[ci];
    if (SMALL_KANA_SET.has(c) && morae.length > 0) {
      morae[morae.length - 1] += c;
    } else {
      morae.push(c);
    }
  }
  return morae;
}

function isVerbOrAdjective(rules) {
  return (rules || []).some(function (tag) { return tag.startsWith('v') || tag.startsWith('adj-i'); });
}

function getPitchCategory(reading, pitchAccentValue, verbOrAdjective) {
  verbOrAdjective = verbOrAdjective || false;
  if (pitchAccentValue === 0) return 'heiban';
  if (verbOrAdjective) return pitchAccentValue > 0 ? 'kifuku' : null;
  if (pitchAccentValue === 1) return 'atamadaka';
  if (pitchAccentValue > 1) {
    var moraCount = getKanaMorae(reading).length;
    return pitchAccentValue >= moraCount ? 'odaka' : 'nakadaka';
  }
  return null;
}

function createPitchHtml(reading, pitchValue) {
  var morae = getKanaMorae(reading);
  var container = el('span', { className: 'pronunciation-text' });
  for (var mi = 0; mi < morae.length; mi++) {
    var mora = morae[mi];
    var isHigh = isMoraPitchHigh(mi, pitchValue);
    var isHighNext = isMoraPitchHigh(mi + 1, pitchValue);
    var moraSpan = el('span', {
      className: 'pronunciation-mora',
      'data-pitch': isHigh ? 'high' : 'low',
      'data-pitch-next': isHighNext ? 'high' : 'low',
      textContent: mora
    });
    moraSpan.appendChild(el('span', { className: 'pronunciation-mora-line' }));
    container.appendChild(moraSpan);
  }
  return container;
}

function createPitchGroup(pitchData, reading) {
  var container = el('div', { className: 'pitch-group', 'data-details': pitchData.dictionary });
  container.appendChild(el('span', { className: 'pitch-dict-label', textContent: pitchData.dictionary }));
  var list = el('ul', { className: 'pitch-entries' });
  pitchData.pitchPositions.forEach(function (pitch) {
    var li = el('li');
    li.appendChild(createPitchHtml(reading, pitch));
    li.appendChild(document.createTextNode(' [' + pitch + ']'));
    list.appendChild(li);
  });
  container.appendChild(list);
  return container;
}

function createTags(entry) {
  var deinflectionTrace = entry.deinflectionTrace;
  var frequencies = entry.frequencies;
  var pitches = entry.pitches;
  var reading = entry.reading;
  var expression = entry.expression;
  var hasDeinflection = deinflectionTrace && deinflectionTrace.length;
  var hasFrequencies = frequencies && frequencies.length;
  var hasPitches = pitches && pitches.length;

  if (!hasDeinflection && !hasFrequencies && !hasPitches && !window.showExpressionTags) return null;

  var container = el('div', { className: 'entry-tags' });

  if (window.showExpressionTags) {
    var exprRow = el('div', { className: 'tag-row expr-tag-row' });
    exprRow.appendChild(el('span', { className: 'expr-tag', textContent: expression }));
    if (reading && reading !== expression) {
      exprRow.appendChild(el('span', { className: 'expr-tag', textContent: reading }));
    }
    container.appendChild(exprRow);
  }

  if (hasDeinflection) {
    var deinflectionDiv = el('div', { className: 'tag-row' });
    deinflectionTrace.forEach(function (tag) { deinflectionDiv.appendChild(createDeinflectionTag(tag)); });
    container.appendChild(deinflectionDiv);
  }

  if (hasFrequencies) {
    if (window.harmonicFrequency) {
      var normalRow = el('div', { className: 'tag-row', style: 'display:none' });
      frequencies.forEach(function (freq) { normalRow.appendChild(createFrequencyGroup(freq)); });
      var harmonicRow = el('div', { className: 'tag-row' });
      harmonicRow.appendChild(createHarmonicFrequencyTag(frequencies));
      var toggle = function () {
        var swap = harmonicRow.style.display !== 'none';
        harmonicRow.style.display = swap ? 'none' : '';
        normalRow.style.display = swap ? '' : 'none';
      };
      normalRow.addEventListener('click', toggle);
      harmonicRow.addEventListener('click', toggle);
      container.appendChild(harmonicRow);
      container.appendChild(normalRow);
    } else {
      var freqContainer = el('div', { className: 'tag-row' });
      frequencies.forEach(function (freq) { freqContainer.appendChild(createFrequencyGroup(freq)); });
      container.appendChild(freqContainer);
    }
  }

  if (hasPitches) {
    var pitchContainer = el('div', { className: 'pitch-list' });
    if (window.deduplicatePitchAccents) {
      var seen = new Set();
      pitches.forEach(function (pitch) {
        var unique = pitch.pitchPositions.filter(function (pos) { return !seen.has(pos); });
        if (unique.length > 0) {
          unique.forEach(function (pos) { seen.add(pos); });
          pitchContainer.appendChild(createPitchGroup({ dictionary: pitch.dictionary, pitchPositions: unique }, reading));
        }
      });
    } else {
      pitches.forEach(function (pitch) { pitchContainer.appendChild(createPitchGroup(pitch, reading)); });
    }
    container.appendChild(pitchContainer);
  }

  return container;
}

// === Button slots (audio, mine) ===

function createButtonSlot(kind, entryIndex, enabled) {
  if (enabled === undefined) enabled = true;
  return el('span', {
    className: 'button-slot',
    'data-kind': kind,
    'data-entry-index': entryIndex,
    'data-enabled': String(enabled)
  });
}

function getButtonSlot(kind, entryIndex) {
  return document.querySelector('.button-slot[data-kind="' + kind + '"][data-entry-index="' + entryIndex + '"]');
}

function updateButtonSlot(slot, changes) {
  if (!slot || !slot.isConnected) return;
  if ('state' in changes) slot.dataset.state = changes.state;
  if ('enabled' in changes) slot.dataset.enabled = String(changes.enabled);
}

// === Audio ===

var audioUrls = {};
var audioTraceSequence = 0;

function nextAudioTraceId() {
  audioTraceSequence += 1;
  return 'popup-audio-' + Date.now().toString(36) + '-' + audioTraceSequence;
}

function postAudioTrace(audioTraceId, stage, details) {
  postPopupMessage('popupDiagnostic', {
    kind: 'audioTrace',
    stage: stage,
    lookupTraceId: window.lookupTraceId || '',
    audioTraceId: audioTraceId || '',
    now: performance.now(),
    details: details || {}
  });
}

function escapeUrlParam(s) {
  return encodeURIComponent(s).replace(/%20/g, '+');
}

function expandAudioTemplate(template, term, reading, audioTraceId) {
  var escapedTerm = escapeUrlParam(term);
  var escapedReading = escapeUrlParam(reading);
  var result = template.replace('{term}', escapedTerm).replace('{reading}', escapedReading);
  postAudioTrace(audioTraceId, 'template-expanded', { template: template, term: term, reading: reading, url: result });
  console.log('[Audio] expandAudioTemplate: template=' + template + ' term=' + term + ' reading=' + reading + ' -> ' + result);
  return result;
}

async function fetchAudioUrl(expression, reading, audioTraceId) {
  try {
    var sources = window.audioSources || [];
    postAudioTrace(audioTraceId, 'fetch-url-start', { expression: expression, reading: reading, sourceCount: sources.length, endpoint: window.audioRequestEndpoint || '' });
    console.log('[Audio] fetchAudioUrl: expression=' + expression + ' reading=' + reading + ' sources=' + JSON.stringify(sources) + ' endpoint=' + (window.audioRequestEndpoint || '(direct)'));
    for (var i = 0; i < sources.length; i++) {
      var expandedUrl = expandAudioTemplate(sources[i], expression, reading, audioTraceId);
      var endpoint = window.audioRequestEndpoint || '';

      if (!endpoint) {
        // Direct mode: pass the URL to the native host for resolution.
        // The native side has no CORS restrictions and can resolve audioSourceList JSON,
        // download, and play the audio. Avoids a wasted fetch() that fails on CORS
        // in WebView2 (about:blank origin).
        postAudioTrace(audioTraceId, 'fetch-url-direct', { sourceIndex: i, url: expandedUrl });
        console.log('[Audio] fetchAudioUrl: direct mode, passing URL to native host ' + expandedUrl);
        return expandedUrl;
      }

      // Proxy mode: ask the audio endpoint to resolve the source URL
      var requestUrl = endpoint + '?url=' + encodeURIComponent(expandedUrl);
      if (window.lookupTraceId) requestUrl += '&lookupTraceId=' + encodeURIComponent(window.lookupTraceId);
      if (audioTraceId) requestUrl += '&audioTraceId=' + encodeURIComponent(audioTraceId);
      var proxyStart = performance.now();
      postAudioTrace(audioTraceId, 'fetch-url-proxy-start', { sourceIndex: i, url: requestUrl });
      console.log('[Audio] fetchAudioUrl: proxy mode, fetching ' + requestUrl);
      try {
        var response = await fetch(requestUrl);
        if (!response.ok) {
          postAudioTrace(audioTraceId, 'fetch-url-proxy-status', { sourceIndex: i, status: response.status, elapsedMs: performance.now() - proxyStart });
          console.log('[Audio] fetchAudioUrl: proxy returned ' + response.status);
          continue;
        }
        var data = await response.json();
        if (data.type === 'audioSourceList' && data.audioSources && data.audioSources[0] && data.audioSources[0].url) {
          var resolved = data.audioSources[0].url.replace(/\\/g, '/');
          postAudioTrace(audioTraceId, 'fetch-url-proxy-resolved', { sourceIndex: i, url: resolved, elapsedMs: performance.now() - proxyStart });
          console.log('[Audio] fetchAudioUrl: proxy resolved to ' + resolved);
          return resolved;
        }
      } catch (e) {
        postAudioTrace(audioTraceId, 'fetch-url-proxy-error', { sourceIndex: i, message: e.message, elapsedMs: performance.now() - proxyStart });
        console.log('[Audio] fetchAudioUrl: proxy error ' + e.message);
        continue;
      }
    }
    postAudioTrace(audioTraceId, 'fetch-url-missing', { expression: expression, reading: reading });
    console.log('[Audio] fetchAudioUrl: no URL found');
    return null;
  } catch (e) {
    postAudioTrace(audioTraceId, 'fetch-url-crash', { message: e.message, stack: e.stack || '' });
    console.error('[Audio] fetchAudioUrl CRASH: ' + e.message + ' stack=' + (e.stack || ''));
    return null;
  }
}

function playWordAudio(audioUrl, audioTraceId) {
  try {
    if (!audioUrl) { console.log('[Audio] playWordAudio: empty URL'); return false; }
    postAudioTrace(audioTraceId, 'native-message-send', { url: audioUrl, mode: window.audioPlaybackMode || 'interrupt' });
    console.log('[Audio] playWordAudio: sending playWordAudio message with url=' + audioUrl + ' mode=' + (window.audioPlaybackMode || 'interrupt'));
    postPopupMessage('playWordAudio', {
      url: audioUrl,
      mode: window.audioPlaybackMode || 'interrupt',
      lookupTraceId: window.lookupTraceId || '',
      audioTraceId: audioTraceId || ''
    });
    return true;
  } catch (e) {
    postAudioTrace(audioTraceId, 'native-message-crash', { message: e.message, stack: e.stack || '' });
    console.error('[Audio] playWordAudio CRASH: ' + e.message + ' stack=' + (e.stack || ''));
    return false;
  }
}

async function playEntryAudio(entryIndex, audioTraceId, options) {
  try {
    audioTraceId = audioTraceId || nextAudioTraceId();
    options = options || {};
    var entry = window.lookupEntries && window.lookupEntries[entryIndex];
    if (!entry) { console.log('[Audio] playEntryAudio: no entry at index ' + entryIndex); return; }
    postAudioTrace(audioTraceId, 'entry-start', { entryIndex: entryIndex, expression: entry.expression, reading: entry.reading });
    console.log('[Audio] playEntryAudio: index=' + entryIndex + ' expression=' + entry.expression + ' reading=' + entry.reading);
    var audioSlot = getButtonSlot('audio', entryIndex);

    if (!audioUrls[entryIndex]) {
      if (options.deferResolutionToNative && (window.audioSources || []).length) {
        audioUrls[entryIndex] = expandAudioTemplate((window.audioSources || [])[0], entry.expression, entry.reading || entry.expression, audioTraceId);
        postAudioTrace(audioTraceId, 'fetch-url-deferred-to-native', { entryIndex: entryIndex, url: audioUrls[entryIndex] });
      } else {
        audioUrls[entryIndex] = await fetchAudioUrl(entry.expression, entry.reading || entry.expression, audioTraceId);
      }
    }
    if (!audioUrls[entryIndex] || !playWordAudio(audioUrls[entryIndex], audioTraceId)) {
      postAudioTrace(audioTraceId, 'entry-failed', { entryIndex: entryIndex, url: audioUrls[entryIndex] || '' });
      console.log('[Audio] playEntryAudio: FAILED audioUrls[' + entryIndex + ']=' + audioUrls[entryIndex]);
      updateButtonSlot(audioSlot, { state: 'error' });
      setTimeout(function () { updateButtonSlot(audioSlot, { state: 'default' }); }, 1500);
    }
  } catch (e) {
    postAudioTrace(audioTraceId, 'entry-crash', { message: e.message, stack: e.stack || '' });
    console.error('[Audio] playEntryAudio CRASH: ' + e.message + ' stack=' + (e.stack || ''));
  }
}

// === Anki mining ===

function constructFuriganaPlain(expression, reading) {
  if (!reading || reading === expression) return expression;
  var segments = segmentFurigana(expression, reading);
  var result = '';
  for (var i = 0; i < segments.length; i++) {
    var seg = segments[i];
    if (seg[1]) {
      result += ' ' + seg[0] + '[' + seg[1] + ']';
    } else {
      result += seg[0];
    }
  }
  return result.trim();
}

function escapeHtml(value) {
  return String(value ?? '')
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

function formatAnkiDictionaryCss(dictName) {
  var css = window.dictionaryStyles?.[dictName] || '';
  if (!css) return '';

  var scopedCss = constructDictCss(css, dictName);
  var formatted = scopedCss
    .replace(/\s+/g, ' ')
    .replace(/\s*\{\s*/g, ' { ')
    .replace(/\s*\}\s*/g, ' }\n')
    .replace(/;\s*/g, '; ')
    .trim();
  return '<style>' + formatted + '</style>';
}

function definitionTagsForAnki(rawTags, prevTags) {
  var parsedTags = parseTags(rawTags || '').filter(function (tag) { return !NUMERIC_TAG.test(tag); });
  var posTags = [];
  var seenPos = {};
  for (var pi = 0; pi < parsedTags.length; pi++) {
    if (isPartOfSpeech(parsedTags[pi]) && !seenPos[parsedTags[pi]]) {
      seenPos[parsedTags[pi]] = true;
      posTags.push(parsedTags[pi]);
    }
  }
  posTags.sort();
  var currentTags = JSON.stringify(posTags);
  var filteredTags = parsedTags.filter(function (tag) {
    return !isPartOfSpeech(tag) || !(prevTags !== null && prevTags === currentTags);
  });
  return {
    currentTags: currentTags,
    label: filteredTags.length > 0 ? filteredTags.join(', ') : '',
  };
}

function renderGlossaryContentForAnki(glossary, dictName) {
  var tempDiv = document.createElement('div');
  try {
    renderStructuredContent(tempDiv, JSON.parse(glossary.content), null, dictName, true);
  } catch (e) {
    renderStructuredContent(tempDiv, glossary.content, null, dictName, true);
  }
  return applyTableStyles(tempDiv.innerHTML);
}

function constructGlossaryHtml(entryIndex) {
  var entry = window.lookupEntries && window.lookupEntries[entryIndex];
  if (!entry) return '';

  var glossaryItems = '';
  var styles = {};
  var lastDict = '';
  var prevTags = null;
  var index = 0;

  entry.glossaries.forEach(function (g) {
    var dictName = g.dictionary;
    index++;

    var tagInfo = definitionTagsForAnki(g.definitionTags, prevTags);
    var label = '';
    if (dictName !== lastDict) {
      index = 1;
      lastDict = dictName;
      label = tagInfo.label ? '(' + index + ', ' + tagInfo.label + ', ' + dictName + ')' : '(' + index + ', ' + dictName + ')';
    } else {
      label = tagInfo.label ? '(' + index + ', ' + tagInfo.label + ')' : '(' + index + ')';
    }

    glossaryItems += '<li data-dictionary="' + escapeHtml(dictName) + '"><i>' + escapeHtml(label) + '</i> <span>' + renderGlossaryContentForAnki(g, dictName) + '</span></li>';
    prevTags = tagInfo.currentTags;

    var css = window.dictionaryStyles?.[dictName];
    if (css && !styles[dictName]) styles[dictName] = css;
  });

  var stylesHtml = '';
  for (var si = 0, styleDictNames = Object.keys(styles); si < styleDictNames.length; si++) {
    stylesHtml += formatAnkiDictionaryCss(styleDictNames[si]);
  }
  if (window.compactGlossariesAnki) {
    stylesHtml += '<style>' + COMPACT_GLOSSARIES_ANKI + '</style>';
  }

  return '<div style="text-align: left;" class="yomitan-glossary"><ol>' + glossaryItems + '</ol>' + stylesHtml + '</div>';
}

function constructSingleGlossaryHtml(entryIndex) {
  var entry = window.lookupEntries && window.lookupEntries[entryIndex];
  if (!entry) return {};

  var result = {};
  var lastDict = null;
  var currentGlossary = '';
  var prevTags = null;

  function flush() {
    if (!lastDict) return;

    var html = '<div style="text-align: left;" class="yomitan-glossary"><ol>' + currentGlossary + '</ol>';
    html += formatAnkiDictionaryCss(lastDict);
    if (window.compactGlossariesAnki) {
      html += '<style>' + COMPACT_GLOSSARIES_ANKI + '</style>';
    }
    html += '</div>';
    result[lastDict] = html;
    currentGlossary = '';
  }

  entry.glossaries.forEach(function (g) {
    var dictName = g.dictionary;
    var dictChanged = lastDict !== dictName;
    if (dictChanged) {
      flush();
      lastDict = dictName;
      prevTags = null;
    }

    var tagInfo = definitionTagsForAnki(g.definitionTags, prevTags);
    var label = '';
    if (dictChanged) {
      label = tagInfo.label ? '(' + tagInfo.label + ', ' + dictName + ')' : '(' + dictName + ')';
    } else {
      label = tagInfo.label ? '(' + tagInfo.label + ')' : '';
    }

    currentGlossary += '<li data-dictionary="' + escapeHtml(dictName) + '"><i>' + escapeHtml(label) + '</i> <span>' + renderGlossaryContentForAnki(g, dictName) + '</span></li>';
    prevTags = tagInfo.currentTags;
  });

  flush();
  return result;
}

function constructPitchPositionHtml(entryIndex) {
  var entry = window.lookupEntries && window.lookupEntries[entryIndex];
  if (!entry || !entry.pitches || !entry.pitches.length) return '';
  var parts = [];
  for (var pi = 0; pi < entry.pitches.length; pi++) {
    var pitch = entry.pitches[pi];
    for (var ppi = 0; ppi < pitch.pitchPositions.length; ppi++) {
      parts.push(pitch.dictionary + ': ' + pitch.pitchPositions[ppi]);
    }
  }
  return parts.join(', ');
}

function constructPitchCategories(entryIndex) {
  var entry = window.lookupEntries && window.lookupEntries[entryIndex];
  if (!entry || !entry.pitches || !entry.pitches.length) return '';
  var reading = entry.reading || '';
  var verbOrAdjective = isVerbOrAdjective(entry.rules || []);
  var parts = [];
  for (var pi = 0; pi < entry.pitches.length; pi++) {
    var pitch = entry.pitches[pi];
    for (var ppi = 0; ppi < pitch.pitchPositions.length; ppi++) {
      var category = getPitchCategory(reading, pitch.pitchPositions[ppi], verbOrAdjective);
      if (category) parts.push(pitch.dictionary + ': ' + category);
    }
  }
  return parts.join(', ');
}

function constructFrequencyHtml(entryIndex) {
  var entry = window.lookupEntries && window.lookupEntries[entryIndex];
  if (!entry || !entry.frequencies || !entry.frequencies.length) return '';
  var container = document.createElement('div');
  for (var fi = 0; fi < entry.frequencies.length; fi++) {
    container.appendChild(createFrequencyGroup(entry.frequencies[fi]));
  }
  return container.innerHTML;
}

async function mineEntryAtIndex(entryIndex) {
  var entry = window.lookupEntries && window.lookupEntries[entryIndex];
  if (!entry) return;
  if (miningRequestPending) return;

  var expression = entry.expression || '';
  var reading = entry.reading || '';
  var matched = entry.matched || expression;
  var mineSlot = getButtonSlot('mine', entryIndex);
  if (mineSlot && mineSlot.dataset.state === 'pending') return;
  miningRequestPending = true;
  var submitted = false;
  if (mineSlot) updateButtonSlot(mineSlot, { state: 'pending' });

  try {
    currentDictionaryMedia = new Map();
    var glossaryHtml = constructGlossaryHtml(entryIndex);
    var singleGlossaries = constructSingleGlossaryHtml(entryIndex);
    var glossaryFirstHtml = Object.values(singleGlossaries)[0] || '';
    var pitchPositions = constructPitchPositionHtml(entryIndex);
    var pitchCategories = constructPitchCategories(entryIndex);
    var frequenciesHtml = constructFrequencyHtml(entryIndex);
    var freqHarmonicRank = getFrequencyHarmonicRank(entry.frequencies || []);
    var dictionaryMedia = currentDictionaryMedia;
    currentDictionaryMedia = null;

    if (!audioUrls[entryIndex] && (window.audioSources || []).length && window.needsAudio) {
      var audioTraceId = nextAudioTraceId();
      postAudioTrace(audioTraceId, 'mine-fetch-audio-start', { entryIndex: entryIndex, expression: expression, reading: reading || expression });
      audioUrls[entryIndex] = await fetchAudioUrl(expression, reading || expression, audioTraceId);
      postAudioTrace(audioTraceId, 'mine-fetch-audio-finished', { entryIndex: entryIndex, url: audioUrls[entryIndex] || '' });
    }

    var audio = audioUrls[entryIndex] || '';
    var selectedDict = selectedDictionaries[entryIndex] ? selectedDictionaries[entryIndex].name : '';

    var dictMedia = [];
    if (dictionaryMedia && dictionaryMedia.size) {
      dictionaryMedia.forEach(function (value) { dictMedia.push(value); });
    }

    var payload = {
      expression: expression,
      reading: reading,
      matched: matched,
      furiganaPlain: constructFuriganaPlain(expression, reading),
      frequenciesHtml: frequenciesHtml,
      freqHarmonicRank: freqHarmonicRank,
      glossary: glossaryHtml,
      glossaryFirst: glossaryFirstHtml,
      singleGlossaries: JSON.stringify(singleGlossaries),
      pitchPositions: pitchPositions,
      pitchCategories: pitchCategories,
      popupSelectionText: getPopupSelectionText(),
      audio: audio,
      selectedDictionary: selectedDict,
      dictionaryMedia: JSON.stringify(dictMedia),
    };

    postPopupMessage('mineEntry', payload);
    submitted = true;
  } catch (e) {
    console.error('[Anki] mineEntry failed before native submit: ' + e.message);
    postPopupMessage('popupError', 'mineEntry failed before native submit: ' + (e.message || e));
    if (mineSlot) {
      updateButtonSlot(mineSlot, { state: 'error' });
      setTimeout(function () { updateButtonSlot(mineSlot, { state: 'default' }); }, 2000);
    }
  } finally {
    currentDictionaryMedia = null;
    if (!submitted) miningRequestPending = false;
  }
}

window.onMineComplete = function (success) {
  miningRequestPending = false;
  // Update button states for all pending mine slots
  var slots = document.querySelectorAll('.button-slot[data-kind="mine"][data-state="pending"]');
  for (var i = 0; i < slots.length; i++) {
    updateButtonSlot(slots[i], { state: success ? 'default' : 'error' });
    if (!success) {
      setTimeout((function (s) { return function () { updateButtonSlot(s, { state: 'default' }); }; })(slots[i]), 2000);
    }
  }
};

window.onDuplicateCheck = function (isDuplicate) {
  // Could update UI based on duplicate status
};

// === Entry rendering ===

function createEntryHeader(entry, idx) {
  var expression = entry.expression;
  var reading = entry.reading;
  var header = el('div', { className: 'entry-header' });
  var expressionSpan = el('span', { className: 'expression' });
  var needsScroll = false;
  if (reading && reading !== expression) {
    needsScroll = buildFuriganaEl(expressionSpan, expression, reading);
  } else {
    expressionSpan.textContent = expression;
  }
  if (needsScroll) {
    var expressionScroll = el('div', { className: 'expression-scroll' });
    expressionScroll.appendChild(expressionSpan);
    header.appendChild(expressionScroll);
  } else {
    header.appendChild(expressionSpan);
  }

  var buttonsContainer = el('div', { className: 'header-buttons' });

  if ((window.audioSources || []).length) {
    buttonsContainer.appendChild(createButtonSlot('audio', idx));
  }

  if (window.useAnkiConnect) {
    buttonsContainer.appendChild(createButtonSlot('mine', idx));
  }

  header.appendChild(buttonsContainer);
  return header;
}

// === Button click handler ===

(function () {
  document.addEventListener('click', function (e) {
    var slot = e.target.closest('.button-slot');
    if (!slot) return;
    e.stopPropagation();
    if (slot.dataset.state === 'pending' || slot.dataset.enabled === 'false') return;
    var kind = slot.dataset.kind;
    if (kind === 'mine' && miningRequestPending) return;
    var entryIndex = Number(slot.dataset.entryIndex);
    if (kind === 'audio') {
      playEntryAudio(entryIndex);
    } else if (kind === 'mine') {
      mineEntryAtIndex(entryIndex);
    }
  });
})();

function createGlossarySection(dictName, contents, isFirst, entryIdx) {
  var details = el('details', { className: 'glossary-group' });
  var collapsed = window.collapseMode === 'Collapse All'
    || (window.collapseMode === 'Custom' && window.collapsedDictionaries.includes(dictName));
  details.open = !collapsed || (window.expandFirstDictionary && isFirst);

  var summary = el('summary', { className: 'dict-label' });
  summary.appendChild(el('span', { className: 'dict-name', textContent: dictName }));
  var timer = null;
  var longPressed = false;
  var toggleSelection = function () {
    longPressed = true;
    var selected = selectedDictionaries[entryIdx];
    if (selected && selected.label) selected.label.classList.remove('selected');
    if (selected && selected.name === dictName) {
      delete selectedDictionaries[entryIdx];
    } else {
      selectedDictionaries[entryIdx] = { name: dictName, label: summary };
      summary.classList.add('selected');
    }
  };
  summary.addEventListener('pointerdown', function () {
    longPressed = false;
    timer = setTimeout(toggleSelection, 400);
  });
  var cancel = function () { clearTimeout(timer); };
  summary.addEventListener('pointerup', cancel);
  summary.addEventListener('pointercancel', cancel);
  summary.addEventListener('click', function (e) {
    e.preventDefault();
    if (longPressed) return;
    details.open = !details.open;
  });
  details.appendChild(summary);

  var dictWrapper = document.createElement('div');
  dictWrapper.setAttribute('data-dictionary', dictName);

  var dictStyle = window.dictionaryStyles?.[dictName] || '';
  dictWrapper.appendChild(el('style', {
    textContent: ('[data-dictionary="' + dictName + '"] {\n' + dictStyle + '\ncolor: var(--text-color) !important;\n}').trim()
  }));

  var termTags = [];
  var rawTags = parseTags((contents[0] || {}).termTags || '');
  var seen = {};
  for (var ti = 0; ti < rawTags.length; ti++) {
    if (!seen[rawTags[ti]]) { seen[rawTags[ti]] = true; termTags.push(rawTags[ti]); }
  }

  var renderContent = function (parent, content) {
    try {
      renderStructuredContent(parent, JSON.parse(content), null, dictName);
    } catch (e) {
      renderStructuredContent(parent, content, null, dictName);
    }
  };

  var termTagsRow = createGlossaryTags(termTags);
  if (termTagsRow) dictWrapper.appendChild(termTagsRow);

  if (contents.length > 1) {
    var ol = el('ol');
    var prevTags = null;
    contents.forEach(function (item) {
      var li = el('li');
      var parsedTags = parseTags(item.definitionTags).filter(function (tag) { return !NUMERIC_TAG.test(tag); });
      var posTags = [];
      var seenPos = {};
      for (var pi = 0; pi < parsedTags.length; pi++) {
        if (isPartOfSpeech(parsedTags[pi]) && !seenPos[parsedTags[pi]]) {
          seenPos[parsedTags[pi]] = true; posTags.push(parsedTags[pi]);
        }
      }
      posTags.sort();
      var currentTags = JSON.stringify(posTags);
      var filteredTags = parsedTags.filter(function (tag) { return !isPartOfSpeech(tag) || !(prevTags !== null && prevTags === currentTags); });
      var tags = createGlossaryTags(filteredTags);
      if (tags) li.appendChild(tags);
      var content = el('div', { className: 'glossary-content' });
      renderContent(content, item.content);
      li.appendChild(content);
      ol.appendChild(li);
      prevTags = currentTags;
    });
    dictWrapper.appendChild(ol);
  } else {
    contents.forEach(function (item) {
      var wrapper = el('div');
      var tags = createGlossaryTags(parseTags(item.definitionTags || '').filter(function (tag) { return !NUMERIC_TAG.test(tag); }));
      if (tags) wrapper.appendChild(tags);
      var content = el('div', { className: 'glossary-content' });
      renderContent(content, item.content);
      wrapper.appendChild(content);
      dictWrapper.appendChild(wrapper);
    });
  }

  details.appendChild(dictWrapper);
  return details;
}

// === Navigation stack ===

var backStack = [];
var forwardStack = [];
var pendingHistoryRestore = null;

function appendPendingHistoryRestore(flush) {
  var pending = pendingHistoryRestore;
  if (!pending) return;
  var count = flush ? pending.nodes.length : Math.min(2, pending.nodes.length);
  var chunk = pending.nodes.splice(0, count);
  if (chunk.length) pending.container.append.apply(pending.container, chunk);
  if (!pending.nodes.length) { pendingHistoryRestore = null; return; }
  if (!flush) setTimeout(function () { appendPendingHistoryRestore(); }, 16);
}

function flushPendingHistoryRestore() {
  appendPendingHistoryRestore(true);
}

function redirect(count) {
  flushPendingHistoryRestore();
  backStack.push(snapshot());
  forwardStack.length = 0;
  document.documentElement.style.visibility = 'hidden';
  window.lookupEntries = undefined;
  window.entryCount = count;
  selectedDictionaries = {};
  audioUrls = {};
  disconnectDictionaryColumns();
  document.getElementById('entries-container').innerHTML = '';
  window.renderPopup();
  requestAnimationFrame(function () {
    getPopupScrollElement().scrollTop = 0;
    requestAnimationFrame(function () {
      getPopupScrollElement().scrollTop = 0;
    });
  });
}

window.replacePopupResults = function (count) {
  postPopupTrace('replace-results-start', { count: count });
  closeOverlay();
  flushPendingHistoryRestore();
  backStack.length = 0;
  forwardStack.length = 0;
  document.documentElement.style.visibility = 'hidden';
  window.lookupEntries = undefined;
  window.entryCount = count;
  selectedDictionaries = {};
  audioUrls = {};
  var container = document.getElementById('entries-container');
  disconnectDictionaryColumns();
  if (container) container.innerHTML = '';
  window.renderPopup();
  requestAnimationFrame(function () {
    getPopupScrollElement().scrollTop = 0;
  });
};

function capturePopupRuntime() {
  return {
    colorScheme: document.documentElement.getAttribute('data-hoshi-color-scheme') || 'light',
    backgroundColor: document.documentElement.style.getPropertyValue('--background-color'),
    textColor: document.documentElement.style.getPropertyValue('--text-color'),
    dictionaryStyles: window.dictionaryStyles,
    compactGlossaries: window.compactGlossaries,
    compactPitchAccents: window.compactPitchAccents,
    harmonicFrequency: window.harmonicFrequency,
    deduplicatePitchAccents: window.deduplicatePitchAccents,
    expandFirstDictionary: window.expandFirstDictionary,
    collapseMode: window.collapseMode,
    collapsedDictionaries: window.collapsedDictionaries,
    showExpressionTags: window.showExpressionTags,
    scanNonJapaneseText: window.scanNonJapaneseText,
    maxResults: window.maxResults,
    scanLength: window.scanLength,
    customCSS: window.customCSS,
    lookupTraceId: window.lookupTraceId,
    audioSources: window.audioSources,
    audioPlaybackMode: window.audioPlaybackMode,
    audioEnableAutoplay: window.audioEnableAutoplay,
    useAnkiConnect: window.useAnkiConnect,
    embedMedia: window.embedMedia,
    allowDupes: window.allowDupes,
    needsAudio: window.needsAudio,
    compactGlossariesAnki: window.compactGlossariesAnki
  };
}

function applyPopupRuntime(runtime) {
  window.dictionaryStyles = runtime.dictionaryStyles;
  window.compactGlossaries = runtime.compactGlossaries;
  window.compactPitchAccents = runtime.compactPitchAccents;
  window.harmonicFrequency = runtime.harmonicFrequency;
  window.deduplicatePitchAccents = runtime.deduplicatePitchAccents;
  window.expandFirstDictionary = runtime.expandFirstDictionary;
  window.collapseMode = runtime.collapseMode;
  window.collapsedDictionaries = runtime.collapsedDictionaries;
  window.showExpressionTags = runtime.showExpressionTags;
  window.scanNonJapaneseText = runtime.scanNonJapaneseText;
  window.maxResults = runtime.maxResults;
  window.scanLength = runtime.scanLength;
  window.customCSS = runtime.customCSS;
  window.lookupTraceId = runtime.lookupTraceId;
  window.audioSources = runtime.audioSources;
  window.audioPlaybackMode = runtime.audioPlaybackMode;
  window.audioEnableAutoplay = runtime.audioEnableAutoplay;
  window.useAnkiConnect = runtime.useAnkiConnect;
  window.embedMedia = runtime.embedMedia;
  window.allowDupes = runtime.allowDupes;
  window.needsAudio = runtime.needsAudio;
  window.compactGlossariesAnki = runtime.compactGlossariesAnki;
}

function applyPopupDocumentStyle(runtime) {
  document.documentElement.setAttribute('data-hoshi-color-scheme', runtime.colorScheme || 'light');
  document.documentElement.style.setProperty('--background-color', runtime.backgroundColor || '');
  document.documentElement.style.setProperty('--text-color', runtime.textColor || '');
}

window.hoshiStagePopupRender = function (pendingPayload) {
  if (!pendingPayload
      || pendingPayload.documentEpoch !== window.hoshiPopupDocumentEpoch
      || !Number.isSafeInteger(pendingPayload.generation)
      || !Array.isArray(pendingPayload.entries)
      || !Number.isSafeInteger(pendingPayload.entryCount)
      || pendingPayload.entryCount < pendingPayload.entries.length
      || !pendingPayload.runtime
      || typeof pendingPayload.runtime !== 'object') return false;

  var committedRuntime = capturePopupRuntime();
  var committedEntries = window.lookupEntries;
  var committedEntryCount = window.entryCount;
  var committedGeneration = window.popupRenderGeneration;
  try {
    applyPopupRuntime(pendingPayload.runtime);
    window.lookupEntries = pendingPayload.entries;
    window.entryCount = pendingPayload.entryCount;
    window.popupRenderGeneration = pendingPayload.generation;
    window.renderPopup(pendingPayload);
    return true;
  } finally {
    applyPopupRuntime(committedRuntime);
    window.lookupEntries = committedEntries;
    window.entryCount = committedEntryCount;
    window.popupRenderGeneration = committedGeneration;
  }
};

// Legacy bridge kept for callers which already populated the committed runtime.
window.hoshiInjectResults = function (entriesJson, count) {
  return window.hoshiStagePopupRender({
    documentEpoch: window.hoshiPopupDocumentEpoch,
    generation: window.popupRenderGeneration || 0,
    entries: entriesJson,
    entryCount: count,
    runtime: capturePopupRuntime()
  });
};

window.hoshiCommitPopupRender = function (expectedEpoch, expectedGeneration) {
  if (expectedEpoch !== window.hoshiPopupDocumentEpoch) return false;
  var pending = window.hoshiPendingPopupRender;
  if (!pending || pending.generation !== expectedGeneration) return false;
  window.hoshiPendingPopupRender = null;
  pending.commit();
  return true;
};

window.hoshiCancelPopupRender = function (expectedEpoch, expectedGeneration) {
  if (expectedEpoch !== window.hoshiPopupDocumentEpoch) return false;
  var pending = window.hoshiPendingPopupRender;
  if (!pending || pending.generation !== expectedGeneration) return false;
  window.hoshiPendingPopupRender = null;
  return true;
};

window.hoshiGetCommittedPopupGeneration = function (expectedEpoch) {
  if (expectedEpoch !== window.hoshiPopupDocumentEpoch) return null;
  var generation = window.popupRenderGeneration;
  return Number.isSafeInteger(generation) ? generation : null;
};

window.hoshiDiscardPopupRender = function (expectedEpoch, expectedGeneration) {
  if (expectedEpoch !== window.hoshiPopupDocumentEpoch) return false;
  var pending = window.hoshiPendingPopupRender;
  if (!pending || pending.generation !== expectedGeneration) return false;
  window.hoshiPendingPopupRender = null;
  return true;
};

window.hoshiAppendResults = function (entries, finalCount, expectedEpoch, expectedGeneration) {
  if (expectedEpoch !== window.hoshiPopupDocumentEpoch) return false;
  var pending = window.hoshiPendingPopupRender;
  if (pending && pending.generation === expectedGeneration)
    return pending.append(entries, finalCount);
  return typeof window.hoshiCommittedPopupAppend === 'function'
    && window.hoshiCommittedPopupAppend(entries, finalCount, expectedGeneration);
};

function snapshot() {
  flushPendingHistoryRestore();
  var container = document.getElementById('entries-container');
  return {
    nodes: Array.from(container.childNodes),
    scrollTop: getPopupScrollElement().scrollTop,
    lookupEntries: window.lookupEntries,
    entryCount: window.entryCount,
  };
}

function restore(snap) {
  flushPendingHistoryRestore();
  var container = document.getElementById('entries-container');
  var nodes = snap.nodes.slice();
  disconnectDictionaryColumns();
  var shouldDeferOffscreenNodes = snap.scrollTop === 0 && nodes.length > 6;
  if (shouldDeferOffscreenNodes) {
    container.replaceChildren.apply(container, nodes.splice(0, 4));
    pendingHistoryRestore = { container: container, nodes: nodes };
    setTimeout(function () { appendPendingHistoryRestore(); }, 50);
  } else {
    container.replaceChildren.apply(container, nodes);
  }
  window.lookupEntries = snap.lookupEntries;
  window.entryCount = snap.entryCount;
  selectedDictionaries = {};
  audioUrls = {};
  observeAllDictionarySections();
  scheduleDictionaryColumns();
  requestAnimationFrame(function () {
    getPopupScrollElement().scrollTop = snap.scrollTop;
  });
}

function navigate(origin, destination) {
  if (!origin.length) return;
  destination.push(snapshot());
  restore(origin.pop());
}

window.navigateBack = function () { navigate(backStack, forwardStack); };
window.navigateForward = function () { navigate(forwardStack, backStack); };

var dictionaryColumnsRaf = 0;
var dictionaryColumnsObserver = null;

function dictionaryColumnsGap() {
  var value = parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--popup-dictionary-card-gap'));
  return Number.isFinite(value) ? value : 8;
}

function shouldUseDictionaryColumns(section) {
  return section
    && !section.classList.contains('single-section')
    && section.clientWidth >= 480;
}

function resetDictionarySectionLayout(section) {
  if (!section) return;
  section.style.position = '';
  section.style.height = '';
  Array.from(section.children).forEach(function (item) {
    item.style.position = '';
    item.style.left = '';
    item.style.top = '';
    item.style.width = '';
    item.style.transform = '';
    item.style.visibility = 'visible';
    item.style.marginTop = '';
  });
}

function disconnectDictionaryColumns() {
  if (dictionaryColumnsRaf) {
    cancelAnimationFrame(dictionaryColumnsRaf);
    dictionaryColumnsRaf = 0;
  }
  if (dictionaryColumnsObserver) {
    dictionaryColumnsObserver.disconnect();
    dictionaryColumnsObserver = null;
  }
}

function layoutDictionaryColumns() {
  var sections = document.querySelectorAll('#entries-container .glossary-sections');
  sections.forEach(function (section) {
    if (!shouldUseDictionaryColumns(section)) {
      resetDictionarySectionLayout(section);
      return;
    }

    var gap = dictionaryColumnsGap();
    var columnWidth = (section.clientWidth - gap) / 2;
    var columnHeights = [0, 0];

    section.style.position = 'relative';
    Array.from(section.children).forEach(function (item) {
      var column = columnHeights[0] <= columnHeights[1] ? 0 : 1;
      var x = column * (columnWidth + gap);
      var y = columnHeights[column];

      item.style.position = 'absolute';
      item.style.left = '0';
      item.style.top = '0';
      item.style.width = columnWidth + 'px';
      item.style.transform = 'translate(' + x + 'px, ' + y + 'px)';
      item.style.visibility = 'visible';
      item.style.marginTop = '0';

      columnHeights[column] += item.offsetHeight + gap;
    });

    section.style.height = Math.max(0, Math.max(columnHeights[0], columnHeights[1]) - gap) + 'px';
  });
}

function scheduleDictionaryColumns() {
  if (dictionaryColumnsRaf) return;
  dictionaryColumnsRaf = requestAnimationFrame(function () {
    dictionaryColumnsRaf = 0;
    layoutDictionaryColumns();
  });
}

function observeDictionaryColumns(section) {
  if (!section || typeof ResizeObserver === 'undefined') return;
  dictionaryColumnsObserver = dictionaryColumnsObserver || new ResizeObserver(scheduleDictionaryColumns);
  dictionaryColumnsObserver.observe(section);
  Array.from(section.children).forEach(function (item) {
    dictionaryColumnsObserver.observe(item);
  });
}

function observeAllDictionarySections() {
  document.querySelectorAll('#entries-container .glossary-sections').forEach(observeDictionaryColumns);
}

window.addEventListener('resize', scheduleDictionaryColumns);

document.addEventListener('toggle', function () {
  scheduleDictionaryColumns();
}, true);

// === Main render ===

window.renderPopup = function (pendingPayload) {
  var liveContainer = document.getElementById('entries-container');
  if (!liveContainer || !window.entryCount) return;
  var stagingContainer = document.createElement('div');
  var container = stagingContainer;
  var lookupEntries = window.lookupEntries;
  var entryCount = window.entryCount;
  var generation = window.popupRenderGeneration || 0;
  var renderStart = performance.now();
  var firstFrameCommitted = false;
  var pendingAutoplay = null;
  var lastShiftLookupKey = '';
  var lastShiftLookupQuery = '';
  var lastShiftLookupAt = 0;

  function isRenderCurrent() {
    return !firstFrameCommitted || generation === (window.popupRenderGeneration || 0);
  }

  function wrapRubyTextNodes(root) {
    var rubies = root.querySelectorAll('.glossary-content ruby');
    for (var ri = 0; ri < rubies.length; ri++) {
      var ruby = rubies[ri];
      for (var ci = 0; ci < ruby.childNodes.length; ci++) {
        var node = ruby.childNodes[ci];
        if (node.nodeType === Node.TEXT_NODE && node.textContent.trim()) {
          var span = document.createElement('span');
          span.textContent = node.textContent;
          node.replaceWith(span);
        }
      }
    }
  }

  function updateConfiguredStyle(id, enabled, css) {
    var style = document.getElementById(id);
    if (!enabled) {
      style?.remove();
      return;
    }
    if (!style) {
      style = document.createElement('style');
      style.id = id;
      document.body.appendChild(style);
    }
    style.textContent = css;
  }

  function applyConfiguredStyles() {
    if (!isRenderCurrent()) return;
    updateConfiguredStyle(
      'popup-compact-glossaries',
      !!window.compactGlossaries,
      'ul[data-sc-content="glossary"],ol[data-sc-content="glossary"],.glossary-list{list-style:none;padding-left:0;margin:0}ul[data-sc-content="glossary"]>li,ol[data-sc-content="glossary"]>li,.glossary-list>li{display:inline}ul[data-sc-content="glossary"]>li:not(:last-child)::after,ol[data-sc-content="glossary"]>li:not(:last-child)::after,.glossary-list>li:not(:last-child)::after{content:" | ";opacity:0.6}');
    updateConfiguredStyle(
      'popup-compact-pitch-accents',
      !!window.compactPitchAccents,
      '.pitch-entries,.pitch-entries>li{display:inline}.pitch-entries>li{white-space:nowrap}.pitch-entries>li:not(:last-child)::after{content:" | ";opacity:0.6;white-space:normal}.pitch-dict-label{margin-right:4px}');
    updateConfiguredStyle('popup-custom-css', !!window.customCSS, window.customCSS || '');
  }

  function renderEntry(idx, generation, onComplete) {
    if (!isRenderCurrent()) return;
    var entry = lookupEntries?.[idx];
    if (!entry) {
      onComplete(null);
      return;
    }

    if (idx > 0) container.appendChild(document.createElement('hr'));

    var entryDiv = el('div', { className: 'entry' });
    entryDiv.appendChild(createEntryHeader(entry, idx));

    if (window.audioEnableAutoplay && (window.audioSources || []).length && idx === 0) {
      var autoplayEntryIndex = idx;
      var autoplayAudioTraceId = nextAudioTraceId();
      pendingAutoplay = function () {
        postAudioTrace(autoplayAudioTraceId, 'autoplay-scheduled', { entryIndex: autoplayEntryIndex, delayMs: 70 });
        setTimeout(function () {
          if (generation !== (window.popupRenderGeneration || 0)) return;
          postAudioTrace(autoplayAudioTraceId, 'autoplay-fired', { entryIndex: autoplayEntryIndex });
          playEntryAudio(autoplayEntryIndex, autoplayAudioTraceId, { deferResolutionToNative: true });
        }, 70);
      };
    }

    var tags = createTags(entry);
    if (tags) entryDiv.appendChild(tags);
    container.appendChild(entryDiv);

    var grouped = {};
    entry.glossaries.forEach(function (g) {
      (grouped[g.dictionary] || (grouped[g.dictionary] = [])).push({
        content: g.content,
        definitionTags: g.definitionTags,
        termTags: g.termTags
      });
    });

    var dictNames = Object.keys(grouped);
    var glossarySections = el('div', { className: 'glossary-sections' });
    if (dictNames.length === 1) glossarySections.classList.add('single-section');
    entryDiv.appendChild(glossarySections);

    postPopupTrace('render-entry', {
      generation: generation,
      entryIndex: idx,
      dictionaryCount: dictNames.length,
      glossaryCount: entry.glossaries.length
    });

    var dictIdx = 0;
    function nextDict() {
      if (!isRenderCurrent()) return;
      if (dictIdx >= dictNames.length) {
        if (firstFrameCommitted) {
          observeDictionaryColumns(glossarySections);
          layoutDictionaryColumns();
        }
        onComplete(entryDiv);
        return;
      }

      var dictName = dictNames[dictIdx];
      glossarySections.appendChild(createGlossarySection(
        dictName, grouped[dictName], dictIdx === 0, idx));
      dictIdx++;
      if (firstFrameCommitted) scheduleDictionaryColumns();
      if (idx === 0) {
        nextDict();
      } else {
        requestAnimationFrame(nextDict);
      }
    }

    nextDict();
  }

  function commitFirstFrame(generation, entryDiv) {
    if (firstFrameCommitted
      || !entryDiv
      || generation !== (pendingPayload?.generation ?? (window.popupRenderGeneration || 0))) return false;

    wrapRubyTextNodes(entryDiv);

    if (pendingPayload) {
      window.hoshiPendingPopupRender = {
        generation: generation,
        payload: pendingPayload,
        append: appendResults,
        commit: function () {
          return promoteFirstFrame();
        }
      };
      postPopupMessage('contentPrepared', {
        epoch: window.hoshiPopupDocumentEpoch,
        generation: generation
      });
      return true;
    }

    return promoteFirstFrame();

    function promoteFirstFrame() {
      if (firstFrameCommitted) return false;
      if (pendingPayload) {
        applyPopupRuntime(pendingPayload.runtime);
        applyPopupDocumentStyle(pendingPayload.runtime);
      }
      window.popupRenderGeneration = generation;
      window.lookupEntries = lookupEntries;
      window.entryCount = entryCount;
      closeOverlay();
      flushPendingHistoryRestore();
      backStack.length = 0;
      forwardStack.length = 0;
      selectedDictionaries = {};
      audioUrls = {};
      disconnectDictionaryColumns();
      liveContainer.replaceChildren.apply(liveContainer,
        Array.from(stagingContainer.childNodes));
      container = liveContainer;
      firstFrameCommitted = true;
      applyConfiguredStyles();
      observeAllDictionarySections();
      layoutDictionaryColumns();
      document.documentElement.style.visibility = 'visible';
      window.hoshiCommittedPopupAppend = appendResults;
      attachPopupInteractionHandlers();
      postPopupTrace('first-frame-ready', {
        generation: generation,
        renderedEntries: 1,
        renderedGlossaries: entryDiv.querySelectorAll('.glossary-content').length,
        textLength: entryDiv.innerText.length,
        elapsedMs: performance.now() - renderStart
      });
      postPopupMessage('contentReady', {
        epoch: window.hoshiPopupDocumentEpoch,
        generation: generation
      });
      requestAnimationFrame(function () {
        getPopupScrollElement().scrollTop = 0;
      });
      pendingAutoplay?.();
      renderAvailableEntries();
      return true;
    }
  }

  var nextRenderIndex = 1;
  var renderPumpActive = false;

  function renderAvailableEntries() {
    if (renderPumpActive || generation !== (window.popupRenderGeneration || 0)) return;
    renderPumpActive = true;

    function next() {
      if (generation !== (window.popupRenderGeneration || 0)) {
        renderPumpActive = false;
        return;
      }
      if (nextRenderIndex >= lookupEntries.length) {
        renderPumpActive = false;
        if (nextRenderIndex >= entryCount) finishRender();
        return;
      }

      renderEntry(nextRenderIndex, generation, function () {
        nextRenderIndex++;
        requestAnimationFrame(next);
      });
    }

    requestAnimationFrame(next);
  }

  function appendResults(entries, finalCount, expectedGeneration) {
    if ((expectedGeneration !== undefined && expectedGeneration !== generation)
        || !Array.isArray(entries)
        || !Number.isSafeInteger(finalCount)
        || finalCount < lookupEntries.length + entries.length) return false;
    Array.prototype.push.apply(lookupEntries, entries);
    entryCount = finalCount;
    if (firstFrameCommitted) {
      window.lookupEntries = lookupEntries;
      window.entryCount = entryCount;
      renderAvailableEntries();
    }
    return true;
  }

  function finishRender() {
    if (generation !== (window.popupRenderGeneration || 0)) return;
    wrapRubyTextNodes(container);
    applyConfiguredStyles();
    layoutDictionaryColumns();
    postPopupTrace('render-finished', {
      generation: generation,
      entryCount: entryCount || 0,
      renderedEntries: container.querySelectorAll('.entry').length,
      renderedGlossaries: container.querySelectorAll('.glossary-content').length,
      textLength: container.innerText.length,
      elapsedMs: performance.now() - renderStart
    });
  }

  postPopupTrace('render-start', {
    generation: generation,
    entryCount: entryCount || 0,
    existingChildren: container ? container.childNodes.length : 0
  });
  renderEntry(0, generation, function (firstEntryDiv) {
    commitFirstFrame(generation, firstEntryDiv);
  });

  function popupScanLength() {
    var configuredScanLength = Number(window.scanLength);
    return Number.isFinite(configuredScanLength)
      ? Math.min(64, Math.max(1, configuredScanLength))
      : 16;
  }

  function lookupAtPopupPoint(x, y, dismissOnMiss, source) {
    var start = performance.now();
    var selected = window.hoshiSelection?.selectText(x, y, popupScanLength());
    var selectMs = performance.now() - start;
    if (!selected) {
      postPopupTrace('lookup-click-miss', {
        source: source || 'click',
        x: x,
        y: y,
        selectMs: selectMs,
        dismissOnMiss: !!dismissOnMiss
      });
      if (dismissOnMiss) postPopupMessage('tapOutside', null);
      return false;
    }
    if (source === 'shift') {
      var now = Date.now();
      if (selected === lastShiftLookupQuery) {
        lastShiftLookupAt = now;
        return true;
      }
      lastShiftLookupQuery = selected;
      lastShiftLookupAt = now;
    }
    var rectStart = performance.now();
    var rect = window.hoshiSelection?.getSelectionRect?.(x, y) || null;
    var rectMs = performance.now() - rectStart;
    postPopupTrace('lookup-click-hit', {
      source: source || 'click',
      query: selected,
      selectedLength: selected.length,
      x: x,
      y: y,
      selectMs: selectMs,
      rectMs: rectMs
    });
    postPopupMessage('lookupRedirect', {
      query: selected,
      rect: rect,
      source: source || 'click',
      selectedLength: selected.length,
      selectMs: selectMs,
      rectMs: rectMs,
      clientNow: performance.now()
    });
    return true;
  }

  function attachPopupInteractionHandlers() {
    if (!liveContainer.clickAttached) {
      liveContainer.clickAttached = true;
      liveContainer.addEventListener('click', function (e) {
        var target = e.target && e.target.nodeType === Node.TEXT_NODE ? e.target.parentElement : e.target;
        if (target && target.closest('summary')) return;
        // Don't close popup when clicking audio/mine buttons
        if (target && target.closest('.button-slot')) return;
        if (!target || (!target.closest('.glossary-content') && !target.closest('.expr-tag'))) {
          postPopupMessage('tapOutside', null);
          return;
        }
        lookupAtPopupPoint(e.clientX, e.clientY, true);
      });
    }

    if (!window.hoshiPopupShiftLookupAttached) {
      window.hoshiPopupShiftLookupAttached = true;
      document.addEventListener('mousemove', function (e) {
        if (!e.shiftKey) return;
        var target = e.target && e.target.nodeType === Node.TEXT_NODE ? e.target.parentElement : e.target;
        if (!target || (!target.closest('.glossary-content') && !target.closest('.expr-tag'))) return;
        if (target.closest('.button-slot')) return;
        var key = Math.round(e.clientX) + ':' + Math.round(e.clientY);
        if (key === lastShiftLookupKey) return;
        lastShiftLookupKey = key;
        lookupAtPopupPoint(e.clientX, e.clientY, false, 'shift');
      }, { passive: true });
      document.addEventListener('keyup', function (e) {
        if (e.key === 'Shift') {
          lastShiftLookupKey = '';
          lastShiftLookupQuery = '';
          lastShiftLookupAt = 0;
        }
      });
    }
  }
};

document.addEventListener('scroll', function (event) {
  setPopupScrollIndicatorActive(event.target);
  postPopupMessage('popupScrolled', null);
}, { passive: true, capture: true });

// Notify host that the popup shell is ready
postPopupMessage('shellReady', { epoch: window.hoshiPopupDocumentEpoch });
