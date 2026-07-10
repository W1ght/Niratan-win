using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Hoshi.Enums;
using Hoshi.Models.Dictionary;
using Hoshi.Models.Settings;

namespace Hoshi.Services.Dictionary;

public sealed class PopupHtmlGenerator
{
    private readonly string _popupCss;
    private readonly string _popupJs;

    public PopupHtmlGenerator()
    {
        var webDir = Path.Combine(AppContext.BaseDirectory, "Web", "DictionaryPopup");

        _popupCss = File.Exists(Path.Combine(webDir, "popup.css"))
            ? File.ReadAllText(Path.Combine(webDir, "popup.css"))
            : "";

        _popupJs = File.Exists(Path.Combine(webDir, "popup.js"))
            ? File.ReadAllText(Path.Combine(webDir, "popup.js"))
            : "";
    }

    public string GenerateShellHtml(ThemeMode themeMode = ThemeMode.System, DictionaryDisplaySettings? settings = null, AudioSettings? audioSettings = null, AnkiSettings? ankiSettings = null, bool hidden = false)
    {
        return GenerateHtml([], new Dictionary<string, string>(), settings, themeMode, audioSettings: audioSettings, ankiSettings: ankiSettings, hidden: hidden);
    }

    public string GenerateHtml(
        List<DictionaryLookupResult> results,
        Dictionary<string, string> styles,
        DictionaryDisplaySettings? displaySettings = null,
        ThemeMode themeMode = ThemeMode.System,
        long renderGeneration = 0,
        AudioSettings? audioSettings = null,
        AnkiSettings? ankiSettings = null,
        bool hidden = false)
    {
        var settings = displaySettings ?? new DictionaryDisplaySettings();
        var entriesJson = SerializeLookupEntries(results);
        var stylesJson = SerializeStyles(styles);
        var collapsedDictionariesJson = SerializeCollapsedDictionaries(settings.CollapsedDictionariesOrDefault);
        var popupScaleDeclarations = DictionaryPopupScaleCss.BuildDeclarations(settings.PopupScale);
        var customCss = DictionaryPopupScaleCss.ScaleCustomCss(settings.CustomCSS);

        var (bgColor, textColor) = GetThemeColors(themeMode);

        return $@"<!doctype html>
<html lang=""ja"" data-hoshi-color-scheme=""{(IsThemeDark(themeMode) ? "dark" : "light")}"" style=""visibility:{(hidden ? "hidden" : "visible")}"">
<head>
<meta charset=""utf-8"" />
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no"" />
<title>Dictionary Lookup</title>
<style>{_popupCss}</style>
<style>
html, body {{
    {popupScaleDeclarations}
    --background-color: {bgColor};
    --text-color: {textColor};
    background-color: var(--background-color);
    color: var(--text-color);
}}
</style>
</head>
<body>
<div id=""popup-viewport"">
<div id=""popup-error"" style=""color:#e74c3c;padding:16px;font-size:13px;white-space:pre-wrap;display:none;border-bottom:2px solid #e74c3c;margin-bottom:8px;""></div>
<div id=""entries-container""></div>
<div class=""overlay"">
<div class=""overlay-close"" onclick=""closeOverlay()"">x</div>
<div class=""overlay-content""></div>
</div>
</div>
<script>
window.lookupEntries = {entriesJson};
window.entryCount = {results.Count};
window.dictionaryStyles = {stylesJson};
window.compactGlossaries = {BoolToJs(settings.CompactGlossaries)};
window.compactPitchAccents = {BoolToJs(settings.CompactPitchAccents)};
window.harmonicFrequency = {BoolToJs(settings.HarmonicFrequency)};
window.deduplicatePitchAccents = {BoolToJs(settings.DeduplicatePitchAccents)};
window.expandFirstDictionary = {BoolToJs(settings.ExpandFirstDictionary)};
window.collapseMode = '{settings.CollapseModeText}';
window.collapsedDictionaries = {collapsedDictionariesJson};
window.showExpressionTags = {BoolToJs(settings.ShowExpressionTags)};
window.scanNonJapaneseText = {BoolToJs(settings.ScanNonJapaneseText)};
window.maxResults = {settings.MaxResults};
window.scanLength = {settings.ScanLength};
window.popupRenderGeneration = {renderGeneration};
window.lookupTraceId = '';
window.audioSources = {SerializeAudioSources(audioSettings)};
window.audioPlaybackMode = '{PlaybackModeText(audioSettings)}';
window.audioEnableAutoplay = {BoolToJs(audioSettings?.EnableAutoplay ?? false)};
window.audioRequestEndpoint = 'https://hoshi-audio-resolver.local/resolve';
window.customCSS = {JsonSerializer.Serialize(customCss)};
window.dictionaryMediaRequestEndpoint = 'https://hoshi-dictionary-media.local/image';
window.useAnkiConnect = {BoolToJs(ankiSettings?.PopupSettings.UseAnkiConnect ?? false)};
window.embedMedia = {BoolToJs(ankiSettings?.PopupSettings.EmbedMedia ?? false)};
window.allowDupes = {BoolToJs(ankiSettings?.PopupSettings.AllowDupes ?? false)};
window.needsAudio = {BoolToJs(ankiSettings?.PopupSettings.NeedsAudio ?? false)};
window.compactGlossariesAnki = {BoolToJs(ankiSettings?.PopupSettings.CompactGlossaries ?? false)};
</script>
<script>
// Minimal hoshiSelection shim for popup.js
(function () {{
  var CJK_RANGES = [[0x4e00, 0x9fff], [0x3400, 0x4dbf], [0xf900, 0xfaff]];
  var JAPANESE_RANGES = [[0x3040, 0x309f], [0x30a0, 0x30ff], [0xff66, 0xff9f], [0x30fb, 0x30fc], [0xff61, 0xff65], [0x3000, 0x303f], [0xff10, 0xff19], [0xff21, 0xff3a], [0xff41, 0xff5a], [0xff01, 0xff0f], [0xff1a, 0xff1f], [0xff3b, 0xff3f], [0xff5b, 0xff60], [0xffe0, 0xffee]].concat(CJK_RANGES);
  function isCodePointJapanese(cp) {{
    for (var i = 0; i < JAPANESE_RANGES.length; i++) {{ if (cp >= JAPANESE_RANGES[i][0] && cp <= JAPANESE_RANGES[i][1]) return true; }}
    return false;
  }}
  function isScanBoundary(ch) {{
    if (/^[\s　]$/.test(ch)) return true;
    return '。、！？…※「」『』（）()【】〈〉《》〔〕｛｝{{}}[]・：；:;,─\n\r'.indexOf(ch) >= 0 ||
      (window.scanNonJapaneseText === false && !isCodePointJapanese(ch.codePointAt(0)));
  }}
  function isFurigana(node) {{
    var el = node.nodeType === Node.TEXT_NODE ? node.parentElement : node;
    return !!el?.closest('rt, rp');
  }}
  function createWalker(rootNode) {{
    return document.createTreeWalker(rootNode || document.body, NodeFilter.SHOW_TEXT, {{
      acceptNode: function (n) {{ return isFurigana(n) ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT; }}
    }});
  }}
  function inCharRange(range, x, y) {{
    var rects = range.getClientRects();
    if (rects.length) {{
      for (var i = 0; i < rects.length; i++) {{
        var rect = rects[i];
        if (x >= rect.left && x <= rect.right && y >= rect.top && y <= rect.bottom) return true;
      }}
      return false;
    }}
    var box = range.getBoundingClientRect();
    return x >= box.left && x <= box.right && y >= box.top && y <= box.bottom;
  }}
  var sel = null;
  window.hoshiSelection = {{
    get selection() {{ return sel; }},
    isCodePointJapanese: isCodePointJapanese,
    getCaretRange: function (x, y) {{
      if (document.caretPositionFromPoint) {{
        var pos = document.caretPositionFromPoint(x, y);
        if (!pos) return null;
        var range = document.createRange();
        range.setStart(pos.offsetNode, pos.offset);
        range.collapse(true);
        return range;
      }}

      var element = document.elementFromPoint(x, y);
      var container = element?.closest('p, div, span, ruby, a') || document.body;
      var walker = createWalker(container);
      var range = document.createRange();
      var node;
      while ((node = walker.nextNode())) {{
        for (var i = 0; i < node.textContent.length; i++) {{
          range.setStart(node, i);
          range.setEnd(node, i + 1);
          if (inCharRange(range, x, y)) {{
            range.collapse(true);
            return range;
          }}
        }}
      }}

      return document.caretRangeFromPoint?.(x, y) || null;
    }},
    getCharacterAtPoint: function (x, y) {{
      var range = this.getCaretRange(x, y);
      if (!range || range.startContainer.nodeType !== Node.TEXT_NODE) return null;
      var node = range.startContainer;
      if (isFurigana(node)) return null;
      var text = node.textContent || '';
      var caret = range.startOffset;
      var offsets = [caret, caret - 1, caret + 1];
      for (var i = 0; i < offsets.length; i++) {{
        var offset = offsets[i];
        if (offset < 0 || offset >= text.length) continue;
        var charRange = document.createRange();
        charRange.setStart(node, offset);
        charRange.setEnd(node, offset + 1);
        if (inCharRange(charRange, x, y)) {{
          if (isScanBoundary(text[offset])) return null;
          return {{ node: node, offset: offset }};
        }}
      }}
      return null;
    }},
    getSelectionRect: function (x, y) {{
      if (!sel?.ranges?.length) return null;
      var first = sel.ranges[0];
      var range = document.createRange();
      range.setStart(first.node, first.start);
      range.setEnd(first.node, Math.min(first.start + 1, first.end));
      var rects = Array.from(range.getClientRects());
      var rect = rects.find(function (r) {{ return x >= r.left && x <= r.right && y >= r.top && y <= r.bottom; }}) || range.getBoundingClientRect();
      return {{ x: rect.x, y: rect.y, width: rect.width, height: rect.height }};
    }},
    highlightSelection: function (charCount) {{
      if (!sel?.ranges?.length) return;
      var highlights = this.selectionCharacterRanges(charCount);
      CSS.highlights?.set('hoshi-selection', new Highlight(...highlights));
    }},
    selectionCharacterRanges: function (charCount) {{
      if (!sel?.ranges?.length) return [];
      var ranges = [];
      var remaining = charCount;
      for (var i = 0; i < sel.ranges.length; i++) {{
        if (remaining <= 0) break;
        var r = sel.ranges[i];
        var start = r.start;
        var end = start;
        while (end < r.end && remaining > 0) {{
          var ch = String.fromCodePoint(r.node.textContent.codePointAt(end));
          end += ch.length;
          remaining--;
        }}
        var range = document.createRange();
        range.setStart(r.node, start);
        range.setEnd(r.node, end);
        ranges.push(range);
      }}
      return ranges;
    }},
    selectText: function (x, y, maxLen) {{
      maxLen = maxLen || 16;
      var hit = this.getCharacterAtPoint(x, y);
      if (!hit) {{ sel = null; return null; }}

      var startNode = hit.node;
      var node = startNode;
      var offset = hit.offset;
      var text = '', ranges = [];
      while (text.length < maxLen && node) {{
        var content = node.textContent;
        var start = offset;
        while (offset < content.length && text.length < maxLen) {{
          if (isScanBoundary(content[offset])) break;
          text += content[offset];
          offset++;
        }}
        if (offset > start) ranges.push({{ node: node, start: start, end: offset }});
        if (offset < content.length || text.length >= maxLen) break;
        var nextWalker = createWalker(document.body);
        nextWalker.currentNode = node;
        node = nextWalker.nextNode();
        offset = 0;
      }}
      if (!text) {{ sel = null; return null; }}
      sel = {{ startNode: startNode, startOffset: hit.offset, ranges: ranges, text: text }};
      return text;
    }}
  }};
}})();
</script>
<script>{_popupJs}</script>
<script>
(function () {{
  function postDiagnostic(name, body) {{
    try {{ window.chrome?.webview?.postMessage({{ version: 1, type: 'popupMessage', payload: {{ name: name, body: body || null }} }}); }} catch (_) {{}}
  }}
  try {{
    postDiagnostic('shellReady', {{ entryCount: window.entryCount || 0 }});
    if (typeof window.renderPopup === 'function') {{
      window.renderPopup();
    }} else {{
      var errDiv = document.getElementById('popup-error');
      if (errDiv) {{ errDiv.style.display = 'block'; errDiv.textContent = 'renderPopup is not a function (type: ' + typeof window.renderPopup + ')'; }}
    }}
  }} catch (e) {{
    var errDiv = document.getElementById('popup-error');
    if (errDiv) {{ errDiv.style.display = 'block'; errDiv.textContent = 'Render error: ' + e.message + '\\n' + (e.stack || ''); }}
    postDiagnostic('popupError', e.message + '\n' + (e.stack || ''));
  }}
}})();
</script>
</body>
</html>";
    }

    public string GenerateInjectionScript(
        List<DictionaryLookupResult> results,
        Dictionary<string, string> styles,
        DictionaryDisplaySettings? displaySettings = null,
        ThemeMode themeMode = ThemeMode.System,
        long renderGeneration = 0,
        AudioSettings? audioSettings = null,
        AnkiSettings? ankiSettings = null,
        string? traceId = null,
        int? totalResultCount = null) =>
        GenerateResultsInjectionScript(
            results,
            styles,
            displaySettings,
            themeMode,
            renderGeneration,
            audioSettings,
            ankiSettings,
            traceId,
            totalResultCount,
            "hoshiInjectResults");

    public string GenerateRedirectInjectionScript(
        List<DictionaryLookupResult> results,
        Dictionary<string, string> styles,
        DictionaryDisplaySettings displaySettings,
        ThemeMode themeMode,
        long renderGeneration,
        AudioSettings? audioSettings = null,
        AnkiSettings? ankiSettings = null,
        string? traceId = null) =>
        GenerateResultsInjectionScript(
            results,
            styles,
            displaySettings,
            themeMode,
            renderGeneration,
            audioSettings,
            ankiSettings,
            traceId,
            results.Count,
            "hoshiRedirectResults");

    private string GenerateResultsInjectionScript(
        List<DictionaryLookupResult> results,
        Dictionary<string, string> styles,
        DictionaryDisplaySettings? displaySettings,
        ThemeMode themeMode,
        long renderGeneration,
        AudioSettings? audioSettings,
        AnkiSettings? ankiSettings,
        string? traceId,
        int? totalResultCount,
        string injectionFunction)
    {
        var settings = displaySettings ?? new DictionaryDisplaySettings();
        var entriesJson = SerializeLookupEntries(results);
        var finalResultCount = totalResultCount ?? results.Count;
        var stylesJson = SerializeStyles(styles);
        var collapsedDictionariesJson = SerializeCollapsedDictionaries(settings.CollapsedDictionariesOrDefault);
        var popupScaleDeclarations = DictionaryPopupScaleCss.BuildDeclarations(settings.PopupScale);
        var customCss = DictionaryPopupScaleCss.ScaleCustomCss(settings.CustomCSS);
        var (bgColor, textColor) = GetThemeColors(themeMode);

        return $@"
document.documentElement.setAttribute('data-hoshi-color-scheme', '{(IsThemeDark(themeMode) ? "dark" : "light")}');
document.documentElement.style.setProperty('--background-color', '{bgColor}');
document.documentElement.style.setProperty('--text-color', '{textColor}');
document.documentElement.style.cssText += {JsonSerializer.Serialize(popupScaleDeclarations)};
if (document.body) document.body.style.cssText += {JsonSerializer.Serialize(popupScaleDeclarations)};
window.dictionaryStyles = {stylesJson};
window.compactGlossaries = {BoolToJs(settings.CompactGlossaries)};
window.compactPitchAccents = {BoolToJs(settings.CompactPitchAccents)};
window.harmonicFrequency = {BoolToJs(settings.HarmonicFrequency)};
window.deduplicatePitchAccents = {BoolToJs(settings.DeduplicatePitchAccents)};
window.expandFirstDictionary = {BoolToJs(settings.ExpandFirstDictionary)};
window.collapseMode = '{settings.CollapseModeText}';
window.collapsedDictionaries = {collapsedDictionariesJson};
window.showExpressionTags = {BoolToJs(settings.ShowExpressionTags)};
window.scanNonJapaneseText = {BoolToJs(settings.ScanNonJapaneseText)};
window.maxResults = {settings.MaxResults};
window.scanLength = {settings.ScanLength};
window.customCSS = {JsonSerializer.Serialize(customCss)};
window.popupRenderGeneration = {renderGeneration};
window.lookupTraceId = {JsonSerializer.Serialize(traceId ?? "")};
window.audioSources = {SerializeAudioSources(audioSettings)};
window.audioPlaybackMode = '{PlaybackModeText(audioSettings)}';
window.audioEnableAutoplay = {BoolToJs(audioSettings?.EnableAutoplay ?? false)};
window.audioRequestEndpoint = 'https://hoshi-audio-resolver.local/resolve';
window.useAnkiConnect = {BoolToJs(ankiSettings?.PopupSettings.UseAnkiConnect ?? false)};
window.embedMedia = {BoolToJs(ankiSettings?.PopupSettings.EmbedMedia ?? false)};
window.allowDupes = {BoolToJs(ankiSettings?.PopupSettings.AllowDupes ?? false)};
window.needsAudio = {BoolToJs(ankiSettings?.PopupSettings.NeedsAudio ?? false)};
window.compactGlossariesAnki = {BoolToJs(ankiSettings?.PopupSettings.CompactGlossaries ?? false)};
if (typeof window.{injectionFunction} === 'function') {{
    window.{injectionFunction}({entriesJson}, {finalResultCount});
}} else {{
    window.lookupEntries = {entriesJson};
    window.entryCount = {finalResultCount};
    window.renderPopup();
}}";
    }

    public string GenerateAppendResultsScript(
        List<DictionaryLookupResult> results,
        int totalResultCount,
        long renderGeneration)
    {
        var entriesJson = SerializeLookupEntries(results);
        return $$"""
(() => {
    if (typeof window.hoshiAppendResults !== 'function') return 'bridge-missing';
    return window.hoshiAppendResults({{entriesJson}}, {{totalResultCount}}, {{renderGeneration}})
        ? 'appended'
        : 'stale';
})()
""";
    }

    private static string BoolToJs(bool value) => value ? "true" : "false";

    private static string SerializeCollapsedDictionaries(HashSet<string> collapsed)
    {
        if (collapsed.Count == 0)
            return "[]";
        return JsonSerializer.Serialize(collapsed.OrderBy(x => x));
    }

    private static string SerializeLookupEntries(List<DictionaryLookupResult> results)
    {
        if (results.Count == 0)
            return "[]";

        var entries = results.Select(r => new
        {
            expression = r.Term.Expression,
            reading = r.Term.Reading,
            matched = r.Matched,
            deinflectionTrace = r.Trace.Select(t => new { name = t.Name, description = t.Description }),
            glossaries = r.Term.Glossaries.Select(g => new
            {
                dictionary = g.DictName,
                content = g.Glossary,
                definitionTags = g.DefinitionTags,
                termTags = g.TermTags
            }),
            frequencies = r.Term.Frequencies.Select(f => new
            {
                dictionary = f.DictName,
                frequencies = f.Frequencies.Select(fr => new
                {
                    value = fr.Value,
                    displayValue = fr.DisplayValue
                })
            }),
            pitches = r.Term.Pitches.Select(p => new
            {
                dictionary = p.DictName,
                pitchPositions = p.PitchPositions
            }),
            rules = r.Term.Rules
        });

        return JsonSerializer.Serialize(entries);
    }

    private static string SerializeStyles(Dictionary<string, string> styles)
    {
        if (styles.Count == 0)
            return "{}";

        return JsonSerializer.Serialize(styles);
    }

    private static (string bgVar, string textColor) GetThemeColors(ThemeMode themeMode)
    {
        if (themeMode == ThemeMode.Dark)
            return ("#000000", "#fff");
        if (themeMode == ThemeMode.Light)
            return ("#FFFFFF", "#000");
        return ("#FFFFFF", "#000");
    }

    private static bool IsThemeDark(ThemeMode themeMode) => themeMode switch
    {
        ThemeMode.Dark => true,
        ThemeMode.Light => false,
        _ => false,
    };

    private static string SerializeAudioSources(AudioSettings? settings)
    {
        if (settings == null)
            return "[]";

        var urls = settings.EnabledAudioSourceUrls;
        return JsonSerializer.Serialize(urls);
    }

    private static string PlaybackModeText(AudioSettings? settings)
    {
        return settings?.PlaybackModeText ?? "interrupt";
    }
}
