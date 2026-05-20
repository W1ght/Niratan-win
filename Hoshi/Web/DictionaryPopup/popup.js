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

let selectedDictionaries = {};

function postPopupMessage(name, body) {
  window.chrome?.webview?.postMessage({
    version: 1,
    type: 'popupMessage',
    payload: { name: name, body: body },
  });
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

  var imageUrl = getDictionaryMediaUrl(dictionary, path);
  var img = document.createElement('img');
  img.classList.add('gloss-image');
  img.alt = (nodeData?.alt || title || '');
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
  return header;
}

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
  window.lookupEntries = undefined;
  window.entryCount = count;
  selectedDictionaries = {};
  document.getElementById('entries-container').innerHTML = '';
  window.renderPopup();
  requestAnimationFrame(function () {
    document.scrollingElement.scrollTop = 0;
    requestAnimationFrame(function () {
      document.scrollingElement.scrollTop = 0;
    });
  });
}

window.replacePopupResults = function (count) {
  closeOverlay();
  flushPendingHistoryRestore();
  backStack.length = 0;
  forwardStack.length = 0;
  window.lookupEntries = undefined;
  window.entryCount = count;
  selectedDictionaries = {};
  var container = document.getElementById('entries-container');
  if (container) container.innerHTML = '';
  window.hoshiPopupObserveContentReady?.();
  window.renderPopup();
  requestAnimationFrame(function () {
    document.scrollingElement.scrollTop = 0;
  });
};

// Warm-root injection: takes entry data directly instead of relying on
// a native bridge to supply entries. Used by Windows WebView2 host.
window.hoshiInjectResults = function (entriesJson, count) {
  closeOverlay();
  flushPendingHistoryRestore();
  backStack.length = 0;
  forwardStack.length = 0;
  window.lookupEntries = entriesJson;
  window.entryCount = count;
  selectedDictionaries = {};
  var container = document.getElementById('entries-container');
  if (container) container.innerHTML = '';
  window.hoshiPopupObserveContentReady?.();
  window.renderPopup();
  requestAnimationFrame(function () {
    document.scrollingElement.scrollTop = 0;
  });
};

function snapshot() {
  flushPendingHistoryRestore();
  var container = document.getElementById('entries-container');
  return {
    nodes: Array.from(container.childNodes),
    scrollTop: document.scrollingElement.scrollTop,
    lookupEntries: window.lookupEntries,
    entryCount: window.entryCount,
  };
}

function restore(snap) {
  flushPendingHistoryRestore();
  var container = document.getElementById('entries-container');
  var nodes = snap.nodes.slice();
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
  requestAnimationFrame(function () {
    document.scrollingElement.scrollTop = snap.scrollTop;
  });
}

function navigate(origin, destination) {
  if (!origin.length) return;
  destination.push(snapshot());
  restore(origin.pop());
}

window.navigateBack = function () { navigate(backStack, forwardStack); };
window.navigateForward = function () { navigate(forwardStack, backStack); };

// === Main render ===

window.renderPopup = function () {
  var container = document.getElementById('entries-container');
  if (!window.entryCount) return;

  (function () {
    var idx = 0;
    function next() {
      if (idx >= window.entryCount) {
        // Post-process: wrap ruby text nodes in spans
        var rubies = container.querySelectorAll('.glossary-content ruby');
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
        applyPostStyles();
        return;
      }

      var entry = window.lookupEntries?.[idx];
      if (!entry) { idx++; next(); return; }

      if (idx > 0) container.appendChild(document.createElement('hr'));

      var entryDiv = el('div', { className: 'entry' });
      entryDiv.appendChild(createEntryHeader(entry, idx));

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
      var dictIdx = 0;

      function nextDict() {
        if (dictIdx >= dictNames.length) { idx++; next(); return; }
        var dictName = dictNames[dictIdx];
        entryDiv.appendChild(createGlossarySection(dictName, grouped[dictName], dictIdx === 0, idx));
        dictIdx++;
        requestAnimationFrame(nextDict);
      }

      requestAnimationFrame(nextDict);
    }

    function applyPostStyles() {
      if (window.compactGlossaries && !document.getElementById('popup-compact-glossaries')) {
        var glossaryStyle = document.createElement('style');
        glossaryStyle.id = 'popup-compact-glossaries';
        glossaryStyle.textContent = 'ul[data-sc-content="glossary"],ol[data-sc-content="glossary"],.glossary-list{list-style:none;padding-left:0;margin:0}ul[data-sc-content="glossary"]>li,ol[data-sc-content="glossary"]>li,.glossary-list>li{display:inline}ul[data-sc-content="glossary"]>li:not(:last-child)::after,ol[data-sc-content="glossary"]>li:not(:last-child)::after,.glossary-list>li:not(:last-child)::after{content:" | ";opacity:0.6}';
        document.body.appendChild(glossaryStyle);
      }

      if (window.compactPitchAccents && !document.getElementById('popup-compact-pitch-accents')) {
        var pitchStyle = document.createElement('style');
        pitchStyle.id = 'popup-compact-pitch-accents';
        pitchStyle.textContent = '.pitch-entries,.pitch-entries>li{display:inline}.pitch-entries>li{white-space:nowrap}.pitch-entries>li:not(:last-child)::after{content:" | ";opacity:0.6;white-space:normal}.pitch-dict-label{margin-right:4px}';
        document.body.appendChild(pitchStyle);
      }

      if (window.customCSS && !document.getElementById('popup-custom-css')) {
        var customStyle = document.createElement('style');
        customStyle.id = 'popup-custom-css';
        customStyle.textContent = window.customCSS;
        document.body.appendChild(customStyle);
      }

      postPopupMessage('contentReady', null);
    }

    next();
  })();

  if (container.clickAttached) return;
  container.clickAttached = true;
  container.addEventListener('click', function (e) {
    var target = e.target && e.target.nodeType === Node.TEXT_NODE ? e.target.parentElement : e.target;
    if (target && target.closest('summary')) return;
    if (!target || (!target.closest('.glossary-content') && !target.closest('.expr-tag'))) {
      postPopupMessage('tapOutside', null);
      return;
    }
    var selected = window.hoshiSelection?.selectText(e.clientX, e.clientY, 16);
    if (!selected) {
      postPopupMessage('tapOutside', null);
      return;
    }
    postPopupMessage('lookupRedirect', selected);
  });
};

document.addEventListener('scroll', function () {
  postPopupMessage('popupScrolled', null);
}, { passive: true });

// Notify host that the popup shell is ready
postPopupMessage('shellReady', null);
