/*
 * hoshidicts_c_api.h — C-compatible wrapper for hoshidicts
 * SPDX-License-Identifier: GPL-3.0-or-later
 *
 * All complex return values are JSON strings to enable simple P/Invoke from C#.
 * Memory ownership: functions returning char* or uint8_t* transfer ownership;
 * the caller must free them with hoshi_string_free / hoshi_buffer_free.
 */

#pragma once

#ifdef __cplusplus
extern "C" {
#endif

#ifdef _WIN32
#ifdef HOSHIDICTS_C_API_EXPORTS
#define HOSHI_API __declspec(dllexport)
#else
#define HOSHI_API __declspec(dllimport)
#endif
#else
#define HOSHI_API __attribute__((visibility("default")))
#endif

#include <stdint.h>

/* Opaque session: holds DictionaryQuery + LanguageProcessor + Lookup */
#ifdef __cplusplus
struct hoshi_session_t;
#else
typedef struct hoshi_session_t hoshi_session_t;
#endif

HOSHI_API hoshi_session_t* hoshi_session_create(void);
HOSHI_API void hoshi_session_destroy(hoshi_session_t* session);

/*
 * Rebuild the lookup query with the given enabled dictionaries.
 * paths arrays may be NULL if count is 0.
 */
HOSHI_API void hoshi_session_rebuild(
    hoshi_session_t* session,
    const char* const* term_paths, int term_count,
    const char* const* freq_paths, int freq_count,
    const char* const* pitch_paths, int pitch_count);

HOSHI_API void hoshi_session_rebuild_with_language(
    hoshi_session_t* session,
    const char* const* term_paths, int term_count,
    const char* const* freq_paths, int freq_count,
    const char* const* pitch_paths, int pitch_count,
    const char* language_id);

/*
 * Import a Yomitan-format ZIP dictionary.
 * Returns JSON: { "success": bool, "title": "...", "termCount": N, "metaCount": N,
 *                  "freqCount": N, "pitchCount": N, "mediaCount": N, "errors": [...] }
 * Caller frees with hoshi_string_free.
 */
HOSHI_API char* hoshi_import(const char* zip_path, const char* output_dir);

/*
 * Lookup text. Returns JSON array of results:
 * [{ "matched": "...", "deinflected": "...", "preprocessorSteps": N,
 *    "trace": [{ "name": "...", "description": "..." }, ...],
 *    "term": {
 *      "expression": "...", "reading": "...", "rules": "...",
 *      "glossaries": [{ "dictName": "...", "glossary": "...",
 *                       "definitionTags": "...", "termTags": "..." }, ...],
 *      "frequencies": [{ "dictName": "...",
 *                        "frequencies": [{ "value": N, "displayValue": "..." }, ...] }, ...],
 *      "pitches": [{ "dictName": "...", "pitchPositions": [N, ...],
 *                    "transcriptions": ["...", ...] }, ...]
 *    }
 * }, ...]
 * Caller frees with hoshi_string_free.
 */
HOSHI_API char* hoshi_lookup(hoshi_session_t* session, const char* text,
                             int max_results, int scan_length);

/*
 * Get CSS styles from loaded dictionaries.
 * Returns JSON: [{ "dictName": "...", "styles": "..." }, ...]
 * Caller frees with hoshi_string_free.
 */
HOSHI_API char* hoshi_get_styles(hoshi_session_t* session);

/*
 * Get a media file (image/audio) from a dictionary.
 * Returns buffer and sets out_size. Returns NULL if not found.
 * Caller frees with hoshi_buffer_free.
 */
HOSHI_API uint8_t* hoshi_get_media_file(hoshi_session_t* session,
                                        const char* dict_name,
                                        const char* media_path,
                                        int* out_size);

HOSHI_API char* hoshi_debug_hash(const char* text);

HOSHI_API void hoshi_string_free(char* str);
HOSHI_API void hoshi_buffer_free(uint8_t* buffer);

#ifdef __cplusplus
}
#endif
