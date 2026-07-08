/*
 * hoshidicts_c_api.cpp — C-compatible wrapper for hoshidicts
 * SPDX-License-Identifier: GPL-3.0-or-later
 */

#include "hoshidicts_c_api.h"

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <ctime>
#include <filesystem>
#include <fstream>
#include <memory>
#include <mutex>
#include <string>
#include <string_view>
#include <thread>
#include <vector>

#ifdef _WIN32
#include <windows.h>
#endif

#include "hoshidicts.h"

#include <xxh3.h>

/* -------------------------------------------------------------------------- */
/* Debug logging (writes to a file in the Hoshi logs directory)               */
/* -------------------------------------------------------------------------- */

namespace {

std::mutex g_log_mutex;

void native_log(const char* fmt, ...) {
  std::lock_guard<std::mutex> lock(g_log_mutex);
  char buf[4096];
  va_list args;
  va_start(args, fmt);
  int n = std::vsnprintf(buf, sizeof(buf), fmt, args);
  va_end(args);
  if (n <= 0) return;

  auto t = std::time(nullptr);
  char time_buf[64];
  std::strftime(time_buf, sizeof(time_buf), "%Y-%m-%d %H:%M:%S", std::localtime(&t));

  std::ofstream log("C:/Users/Wight/AppData/Roaming/Hoshi/Logs/native_debug.log", std::ios::app);
  if (log.is_open()) {
    log << time_buf << " " << buf << std::endl;
  }
}

/* -------------------------------------------------------------------------- */
/* SEH crash catcher for import (Windows only)                                */
/* -------------------------------------------------------------------------- */

#ifdef _WIN32

std::atomic<bool> g_import_in_progress{false};
PVOID g_import_veh_handle = nullptr;
std::mutex g_import_veh_mutex;

LONG CALLBACK import_veh_handler(PEXCEPTION_POINTERS ex_info) {
  auto code = ex_info->ExceptionRecord->ExceptionCode;
  auto addr = ex_info->ExceptionRecord->ExceptionAddress;
  auto tid = GetCurrentThreadId();

  native_log("[VEH] Exception code=0x%08X addr=%p tid=%lu in_import=%d",
             code, addr, tid, g_import_in_progress.load());

  if (g_import_in_progress.load()) {
    if (code == EXCEPTION_ACCESS_VIOLATION ||
        code == EXCEPTION_STACK_OVERFLOW ||
        code == EXCEPTION_ARRAY_BOUNDS_EXCEEDED ||
        code == EXCEPTION_DATATYPE_MISALIGNMENT ||
        code == EXCEPTION_ILLEGAL_INSTRUCTION ||
        code == EXCEPTION_IN_PAGE_ERROR) {
      native_log("[VEH] Terminating crashing thread tid=%lu to save process", tid);
      // Terminate only the crashing worker thread. The import will hang
      // (std::future::get blocks forever), but the timeout in hoshi_import
      // will return an error to the caller instead of crashing the app.
      TerminateThread(GetCurrentThread(), 0xC0000005);
    }
  }
  return EXCEPTION_CONTINUE_SEARCH;
}

#endif  // _WIN32

}  // namespace

/* -------------------------------------------------------------------------- */
/* JSON string builder (minimal, no external dependency)                      */
/* -------------------------------------------------------------------------- */

namespace {

class JsonWriter {
 public:
  void StartObject() { buf_ += '{'; }
  void EndObject() {
    if (buf_.back() == ',') buf_.pop_back();
    buf_ += '}';
  }
  void StartArray() { buf_ += '['; }
  void EndArray() {
    if (buf_.back() == ',') buf_.pop_back();
    buf_ += ']';
  }

  void Key(const char* k) {
    buf_ += '"';
    buf_ += k;
    buf_ += "\":";
  }

  void String(const char* s) { AppendString(s); }
  void String(const std::string& s) { AppendString(s); }
  void Int(int v) { buf_ += std::to_string(v); }
  void Int64(int64_t v) { buf_ += std::to_string(v); }
  void Bool(bool v) { buf_ += v ? "true" : "false"; }
  void Comma() { buf_ += ','; }

  const std::string& str() const { return buf_; }

 private:
  void AppendString(const char* s) { AppendString(std::string_view(s)); }
  void AppendString(const std::string& s) { AppendString(std::string_view(s)); }
  void AppendString(std::string_view s) {
    buf_ += '"';
    for (char c : s) {
      switch (c) {
        case '"':  buf_ += "\\\""; break;
        case '\\': buf_ += "\\\\"; break;
        case '\n': buf_ += "\\n";  break;
        case '\r': buf_ += "\\r";  break;
        case '\t': buf_ += "\\t";  break;
        default:   buf_ += c;      break;
      }
    }
    buf_ += '"';
  }

  std::string buf_;
};

/* -------------------------------------------------------------------------- */
}  // namespace

/* -------------------------------------------------------------------------- */
/* Session object (mirrors Android LookupObject)                              */
/* -------------------------------------------------------------------------- */

struct hoshi_session_t {
  std::unique_ptr<DictionaryQuery> query;
  const LanguageProcessor* language;
  std::unique_ptr<Lookup> lookup;

  hoshi_session_t()
      : query(std::make_unique<DictionaryQuery>()),
        language(&language::get("ja")),
        lookup(std::make_unique<Lookup>(*query, *language)) {}
};

/* -------------------------------------------------------------------------- */
/* JSON serialization helpers                                                 */
/* -------------------------------------------------------------------------- */

namespace {

void WriteFrequency(JsonWriter& w, const Frequency& f) {
  w.StartObject();
  w.Key("value"); w.Int(f.value);
  w.Comma();
  w.Key("displayValue"); w.String(f.display_value);
  w.EndObject();
}

void WriteFrequencyEntry(JsonWriter& w, const FrequencyEntry& fe) {
  w.StartObject();
  w.Key("dictName"); w.String(fe.dict_name);
  w.Comma();
  w.Key("frequencies");
  w.StartArray();
  for (size_t i = 0; i < fe.frequencies.size(); ++i) {
    if (i > 0) w.Comma();
    WriteFrequency(w, fe.frequencies[i]);
  }
  w.EndArray();
  w.EndObject();
}

void WritePitchEntry(JsonWriter& w, const PitchEntry& pe) {
  w.StartObject();
  w.Key("dictName"); w.String(pe.dict_name);
  w.Comma();
  w.Key("pitchPositions");
  w.StartArray();
  for (size_t i = 0; i < pe.pitch_positions.size(); ++i) {
    if (i > 0) w.Comma();
    w.Int(pe.pitch_positions[i]);
  }
  w.EndArray();
  w.Comma();
  w.Key("transcriptions");
  w.StartArray();
  for (size_t i = 0; i < pe.transcriptions.size(); ++i) {
    if (i > 0) w.Comma();
    w.String(pe.transcriptions[i]);
  }
  w.EndArray();
  w.EndObject();
}

void WriteGlossaryEntry(JsonWriter& w, const GlossaryEntry& ge) {
  w.StartObject();
  w.Key("dictName"); w.String(ge.dict_name);
  w.Comma();
  w.Key("glossary"); w.String(ge.glossary);
  w.Comma();
  w.Key("definitionTags"); w.String(ge.definition_tags);
  w.Comma();
  w.Key("termTags"); w.String(ge.term_tags);
  w.EndObject();
}

void WriteTransformGroup(JsonWriter& w, const TransformGroup& tg) {
  w.StartObject();
  w.Key("name"); w.String(tg.name);
  w.Comma();
  w.Key("description"); w.String(tg.description);
  w.EndObject();
}

void WriteTermResult(JsonWriter& w, const TermResult& tr) {
  w.StartObject();
  w.Key("expression"); w.String(tr.expression);
  w.Comma();
  w.Key("reading"); w.String(tr.reading);
  w.Comma();
  w.Key("rules"); w.String(tr.rules);
  w.Comma();

  w.Key("glossaries");
  w.StartArray();
  for (size_t i = 0; i < tr.glossaries.size(); ++i) {
    if (i > 0) w.Comma();
    WriteGlossaryEntry(w, tr.glossaries[i]);
  }
  w.EndArray();
  w.Comma();

  w.Key("frequencies");
  w.StartArray();
  for (size_t i = 0; i < tr.frequencies.size(); ++i) {
    if (i > 0) w.Comma();
    WriteFrequencyEntry(w, tr.frequencies[i]);
  }
  w.EndArray();
  w.Comma();

  w.Key("pitches");
  w.StartArray();
  for (size_t i = 0; i < tr.pitches.size(); ++i) {
    if (i > 0) w.Comma();
    WritePitchEntry(w, tr.pitches[i]);
  }
  w.EndArray();

  w.EndObject();
}

void WriteLookupResult(JsonWriter& w, const LookupResult& lr) {
  const TraceCandidate* candidate = lr.trace_candidates.empty() ? nullptr : &lr.trace_candidates.front();

  w.StartObject();
  w.Key("matched"); w.String(lr.matched);
  w.Comma();
  w.Key("deinflected"); w.String(candidate ? candidate->deinflected : lr.term.expression);
  w.Comma();
  w.Key("preprocessorSteps"); w.Int(candidate ? candidate->preprocessor_steps : 0);
  w.Comma();

  w.Key("trace");
  w.StartArray();
  if (candidate) {
    for (size_t i = 0; i < candidate->trace.size(); ++i) {
      if (i > 0) w.Comma();
      WriteTransformGroup(w, candidate->trace[i]);
    }
  }
  w.EndArray();
  w.Comma();

  w.Key("term");
  WriteTermResult(w, lr.term);

  w.EndObject();
}

char* AllocString(const std::string& s) {
  auto* p = new char[s.size() + 1];
  std::memcpy(p, s.data(), s.size());
  p[s.size()] = '\0';
  return p;
}

uint8_t* AllocBuffer(const void* data, size_t size) {
  auto* p = new uint8_t[size];
  std::memcpy(p, data, size);
  return p;
}

}  // namespace

const LanguageProcessor& ResolveLanguage(const char* language_id) {
  try {
    if (!language_id || std::strlen(language_id) == 0) {
      return language::get("ja");
    }
    return language::get(std::string_view(language_id));
  } catch (const std::exception& e) {
    native_log("[REBUILD] unsupported language '%s': %s; falling back to ja",
               language_id ? language_id : "(null)", e.what());
    return language::get("ja");
  }
}

/* -------------------------------------------------------------------------- */
/* Public C API                                                               */
/* -------------------------------------------------------------------------- */

extern "C" {

HOSHI_API hoshi_session_t* hoshi_session_create(void) {
  return new hoshi_session_t();
}

HOSHI_API void hoshi_session_destroy(hoshi_session_t* session) {
  delete session;
}

HOSHI_API void hoshi_session_rebuild(
    hoshi_session_t* session,
    const char* const* term_paths, int term_count,
    const char* const* freq_paths, int freq_count,
    const char* const* pitch_paths, int pitch_count) {
  hoshi_session_rebuild_with_language(
      session,
      term_paths, term_count,
      freq_paths, freq_count,
      pitch_paths, pitch_count,
      "ja");
}

HOSHI_API void hoshi_session_rebuild_with_language(
    hoshi_session_t* session,
    const char* const* term_paths, int term_count,
    const char* const* freq_paths, int freq_count,
    const char* const* pitch_paths, int pitch_count,
    const char* language_id) {
  if (!session) {
    native_log("[REBUILD] session is NULL");
    return;
  }

  native_log("[REBUILD] Starting: %d term, %d freq, %d pitch dicts, language=%s",
             term_count, freq_count, pitch_count, language_id ? language_id : "(null)");

  auto query = std::make_unique<DictionaryQuery>();

  for (int i = 0; i < term_count; ++i) {
    if (!term_paths[i]) continue;
    auto dict_path = std::filesystem::path(std::string(term_paths[i]));
    bool ok_v1 = std::filesystem::is_regular_file(dict_path / ".hoshidicts_1");
    bool ok_v2 = std::filesystem::is_regular_file(dict_path / ".hoshidicts_2");
    native_log("[REBUILD] Term[%d] marker found v1=%d v2=%d", i, ok_v1, ok_v2);
    query->add_term_dict(std::string(term_paths[i]));
  }
  for (int i = 0; i < freq_count; ++i) {
    if (!freq_paths[i]) continue;
    query->add_freq_dict(std::string(freq_paths[i]));
  }
  for (int i = 0; i < pitch_count; ++i) {
    if (!pitch_paths[i]) continue;
    query->add_pitch_dict(std::string(pitch_paths[i]));
  }

  session->lookup.reset();
  session->query = std::move(query);
  session->language = &ResolveLanguage(language_id);
  session->lookup = std::make_unique<Lookup>(*session->query, *session->language);
  native_log("[REBUILD] Complete, lookup recreated with %d term dicts passed", term_count);
}

HOSHI_API char* hoshi_import(const char* zip_path, const char* output_dir) {
  native_log("[IMPORT] zip_path=%s", zip_path ? zip_path : "(null)");
  native_log("[IMPORT] output_dir=%s", output_dir ? output_dir : "(null)");

  if (!zip_path || !output_dir) {
    native_log("[IMPORT] NULL parameter, returning error");
    JsonWriter w;
    w.StartObject();
    w.Key("success"); w.Bool(false);
    w.Comma();
    w.Key("title"); w.String("");
    w.Comma();
    w.Key("errors");
    w.StartArray();
    w.String("NULL parameter");
    w.EndArray();
    w.EndObject();
    return AllocString(w.str());
  }

#ifdef _WIN32
  // Install vectored exception handler once
  {
    std::lock_guard<std::mutex> lock(g_import_veh_mutex);
    if (!g_import_veh_handle) {
      g_import_veh_handle = AddVectoredExceptionHandler(1, import_veh_handler);
    }
  }
#endif

  auto zip_path_str = std::string(zip_path);
  auto output_dir_str = std::string(output_dir);

  ImportResult result;
  bool crashed = false;

#ifdef _WIN32
  // Run import in a dedicated thread. If a worker thread crashes, the VEH
  // handler terminates it, but std::future::get() blocks forever. The timeout
  // here prevents the entire app from hanging.
  {
    std::mutex done_mutex;
    std::condition_variable done_cv;
    bool done = false;

    g_import_in_progress = true;
    native_log("[IMPORT] Starting import in dedicated thread...");

    std::thread import_thread([&]() {
      native_log("[IMPORT-THREAD] Calling dictionary_importer::import...");
      try {
        result = dictionary_importer::import(zip_path_str, output_dir_str, /*low_ram=*/true);
        native_log("[IMPORT-THREAD] Import returned: success=%d title=%s errors=%zu",
                   result.success, result.title.c_str(), result.errors.size());
      } catch (const std::exception& e) {
        native_log("[IMPORT-THREAD] C++ exception: %s", e.what());
        result.success = false;
        result.errors.push_back(std::string("C++ exception: ") + e.what());
      } catch (...) {
        native_log("[IMPORT-THREAD] Unknown C++ exception");
        result.success = false;
        result.errors.push_back("Unknown C++ exception during import");
      }
      {
        std::lock_guard<std::mutex> lk(done_mutex);
        done = true;
      }
      done_cv.notify_one();
    });

    // Wait up to 3 minutes for import to complete
    {
      std::unique_lock<std::mutex> lk(done_mutex);
      if (!done_cv.wait_for(lk, std::chrono::minutes(3), [&] { return done; })) {
        crashed = true;
        native_log("[IMPORT] *** Timeout after 3 minutes — worker thread likely crashed ***");
        import_thread.detach();
      } else {
        import_thread.join();
      }
    }

    g_import_in_progress = false;
  }
#else
  native_log("[IMPORT] Calling dictionary_importer::import...");
  try {
    result = dictionary_importer::import(zip_path_str, output_dir_str, /*low_ram=*/true);
    native_log("[IMPORT] Import returned: success=%d title=%s errors=%zu",
               result.success, result.title.c_str(), result.errors.size());
  } catch (const std::exception& e) {
    native_log("[IMPORT] C++ exception in import: %s", e.what());
    result.success = false;
    result.errors.push_back(std::string("C++ exception: ") + e.what());
  } catch (...) {
    native_log("[IMPORT] Unknown C++ exception in import");
    result.success = false;
    result.errors.push_back("Unknown C++ exception during import");
  }
#endif

  JsonWriter w;
  w.StartObject();
  if (crashed) {
    w.Key("success"); w.Bool(false);
    w.Comma();
    w.Key("title"); w.String("");
    w.Comma();
    w.Key("termCount"); w.Int64(0);
    w.Comma();
    w.Key("metaCount"); w.Int64(0);
    w.Comma();
    w.Key("freqCount"); w.Int64(0);
    w.Comma();
    w.Key("pitchCount"); w.Int64(0);
    w.Comma();
    w.Key("mediaCount"); w.Int64(0);
    w.Comma();
    w.Key("timedOut"); w.Bool(true);
    w.Comma();
    w.Key("errors");
    w.StartArray();
    w.String("Dictionary import timed out (likely an importer crash for this format). This dictionary may not be compatible. Try a different dictionary source.");
    w.EndArray();
  } else {
    w.Key("success"); w.Bool(result.success);
    w.Comma();
    w.Key("title"); w.String(result.title);
    w.Comma();
    w.Key("termCount"); w.Int64(static_cast<int64_t>(result.term_count));
    w.Comma();
    w.Key("metaCount"); w.Int64(static_cast<int64_t>(result.meta_count));
    w.Comma();
    w.Key("freqCount"); w.Int64(static_cast<int64_t>(result.freq_count));
    w.Comma();
    w.Key("pitchCount"); w.Int64(static_cast<int64_t>(result.pitch_count));
    w.Comma();
    w.Key("mediaCount"); w.Int64(static_cast<int64_t>(result.media_count));
    w.Comma();
    w.Key("timedOut"); w.Bool(false);
    w.Comma();
    w.Key("errors");
    w.StartArray();
    for (size_t i = 0; i < result.errors.size(); ++i) {
      if (i > 0) w.Comma();
      w.String(result.errors[i]);
    }
    w.EndArray();
  }
  w.EndObject();

  return AllocString(w.str());
}

HOSHI_API char* hoshi_lookup(hoshi_session_t* session, const char* text,
                             int max_results, int scan_length) {
  if (!session || !text) {
    native_log("[LOOKUP] session=%p text=%s -> NULL session/text, returning []", (void*)session, text ? text : "(null)");
    return AllocString("[]");
  }

  native_log("[LOOKUP] text='%s' max=%d scan=%d", text, max_results, scan_length);

  auto results = session->lookup->lookup(
      std::string(text),
      max_results > 0 ? max_results : 16,
      static_cast<size_t>(scan_length > 0 ? scan_length : 16));

  native_log("[LOOKUP] result_count=%zu", results.size());
  if (!results.empty()) {
    native_log("[LOOKUP] first='%s' reading='%s'", results[0].term.expression.c_str(), results[0].term.reading.c_str());
  }

  JsonWriter w;
  w.StartArray();
  for (size_t i = 0; i < results.size(); ++i) {
    if (i > 0) w.Comma();
    WriteLookupResult(w, results[i]);
  }
  w.EndArray();

  return AllocString(w.str());
}

HOSHI_API char* hoshi_get_styles(hoshi_session_t* session) {
  if (!session) {
    return AllocString("[]");
  }

  auto styles = session->query->get_styles();

  JsonWriter w;
  w.StartArray();
  for (size_t i = 0; i < styles.size(); ++i) {
    if (i > 0) w.Comma();
    w.StartObject();
    w.Key("dictName"); w.String(styles[i].dict_name);
    w.Comma();
    w.Key("styles"); w.String(styles[i].styles);
    w.EndObject();
  }
  w.EndArray();

  return AllocString(w.str());
}

HOSHI_API uint8_t* hoshi_get_media_file(hoshi_session_t* session,
                                        const char* dict_name,
                                        const char* media_path,
                                        int* out_size) {
  if (!session || !dict_name || !media_path || !out_size) {
    return nullptr;
  }

  auto data = session->query->get_media_file(
      std::string(dict_name), std::string(media_path));

  if (data.empty()) {
    *out_size = 0;
    return nullptr;
  }

  *out_size = static_cast<int>(data.size());
  return AllocBuffer(data.data(), data.size());
}

HOSHI_API void hoshi_string_free(char* str) {
  delete[] str;
}

HOSHI_API void hoshi_buffer_free(uint8_t* buffer) {
  delete[] buffer;
}

HOSHI_API char* hoshi_debug_hash(const char* text) {
  if (!text) {
    return AllocString("");
  }
  auto h = XXH3_64bits(text, std::strlen(text));
  char buf[32];
  std::snprintf(buf, sizeof(buf), "%016llX", static_cast<unsigned long long>(h));
  return AllocString(buf);
}

}  // extern "C"
