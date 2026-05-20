using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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

    public string GenerateShellHtml(DictionaryDisplaySettings? settings = null)
    {
        return GenerateHtml([], new Dictionary<string, string>(), settings);
    }

    public string GenerateHtml(
        List<DictionaryLookupResult> results,
        Dictionary<string, string> styles,
        DictionaryDisplaySettings? displaySettings = null)
    {
        var settings = displaySettings ?? new DictionaryDisplaySettings();
        var entriesJson = SerializeLookupEntries(results);
        var stylesJson = SerializeStyles(styles);
        var collapsedDictionariesJson = SerializeCollapsedDictionaries(settings.CollapsedDictionariesOrDefault);

        return $@"<!doctype html>
<html lang=""ja"">
<head>
<meta charset=""utf-8"" />
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no"" />
<title>Dictionary Lookup</title>
<style>{_popupCss}</style>
<style>
html, body {{
    --background-color: #fff;
    --text-color: #000;
    background-color: var(--background-color);
    color: var(--text-color);
}}
@media (prefers-color-scheme: dark) {{
    html, body {{
        --background-color: #1e1e1e;
        --text-color: #e0e0e0;
    }}
}}
</style>
</head>
<body>
<div id=""popup-error"" style=""color:#e74c3c;padding:16px;font-size:13px;white-space:pre-wrap;display:none;border-bottom:2px solid #e74c3c;margin-bottom:8px;""></div>
<div id=""entries-container""></div>
<div class=""overlay"">
<div class=""overlay-close"" onclick=""closeOverlay()"">x</div>
<div class=""overlay-content""></div>
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
window.audioSources = [];
window.audioPlaybackMode = 'interrupt';
window.audioEnableAutoplay = false;
window.customCSS = {JsonSerializer.Serialize(settings.CustomCSS)};
window.dictionaryMediaRequestEndpoint = '';
window.useAnkiConnect = false;
window.embedMedia = false;
window.allowDupes = false;
window.needsAudio = false;
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
    return '。、！？…※「」『』（）()【】〈〉《》〔〕｛｝{{}}[]・：；:;,─\n\r'.indexOf(ch) >= 0 || !isCodePointJapanese(ch.codePointAt(0));
  }}
  var sel = null;
  window.hoshiSelection = {{
    get selection() {{ return sel; }},
    isCodePointJapanese: isCodePointJapanese,
    selectText: function (x, y, maxLen) {{
      maxLen = maxLen || 16;
      if (document.caretPositionFromPoint) {{
        var pos = document.caretPositionFromPoint(x, y);
        if (!pos) {{ sel = null; return null; }}
        var node = pos.offsetNode;
        if (node.nodeType !== Node.TEXT_NODE) {{ sel = null; return null; }}
        var offset = pos.offset, text = '', ranges = [];
        while (text.length < maxLen && node) {{
          var content = node.textContent;
          for (var i = offset; i < content.length && text.length < maxLen; i++) {{
            if (isScanBoundary(content[i])) break;
            text += content[i];
          }}
          if (text.length >= maxLen || offset + text.length < content.length) break;
          node = node.parentElement?.nextSibling?.firstChild || null;
          offset = 0;
        }}
        if (!text) {{ sel = null; return null; }}
        sel = {{ startNode: pos.offsetNode, startOffset: pos.offset, ranges: ranges, text: text }};
        return text;
      }}
      sel = null; return null;
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
  function observeContentReady() {{
    var container = document.getElementById('entries-container');
    var posted = false;
    var observer = null;
    function snapshot() {{
      return {{
        entryCount: window.entryCount || 0,
        renderedEntries: container ? container.querySelectorAll('.entry').length : 0,
        renderedGlossaries: container ? container.querySelectorAll('.glossary-content').length : 0,
        textLength: container ? container.innerText.length : 0
      }};
    }}
    function postReady() {{
      if (posted) return;
      posted = true;
      if (observer) observer.disconnect();
      var state = snapshot();
      postDiagnostic('contentReady', state);
      postDiagnostic('popupDiagnostic', state);
    }}
    window.hoshiPopupObserveContentReady = function () {{
      posted = false;
      if (observer) {{
        observer.disconnect();
        observer = null;
      }}
      if (!container || !window.entryCount) {{
        postReady();
        return;
      }}
      if (container.querySelector('.entry .glossary-content')) {{
        postReady();
        return;
      }}
      observer = new MutationObserver(function () {{
        if (container.querySelector('.entry .glossary-content')) postReady();
      }});
      observer.observe(container, {{ childList: true, subtree: true }});
      window.setTimeout(function () {{
        if (!posted) {{
          postDiagnostic('popupDiagnostic', snapshot());
        }}
      }}, 1200);
    }};
  }}
  try {{
    observeContentReady();
    postDiagnostic('shellReady', {{ entryCount: window.entryCount || 0 }});
    window.hoshiPopupObserveContentReady();
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
        DictionaryDisplaySettings? displaySettings = null)
    {
        var settings = displaySettings ?? new DictionaryDisplaySettings();
        var entriesJson = SerializeLookupEntries(results);
        var stylesJson = SerializeStyles(styles);
        var collapsedDictionariesJson = SerializeCollapsedDictionaries(settings.CollapsedDictionariesOrDefault);

        return $@"
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
window.customCSS = {JsonSerializer.Serialize(settings.CustomCSS)};
if (typeof window.hoshiInjectResults === 'function') {{
    window.hoshiInjectResults({entriesJson}, {results.Count});
}} else {{
    window.lookupEntries = {entriesJson};
    window.entryCount = {results.Count};
    window.renderPopup();
}}";
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
}
