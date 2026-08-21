// P1 zero-behaviour-change split: included inside dll_main.cpp anonymous namespace.
// == wen ben hook (grab dialogue text) ==
// 覆盖 GDI 文本渲染 API（galgame 经典 hook 面）：GetGlyphOutlineW 逐字形渲染逐字累积成行，
// ExtTextOutW/TextOutW/DrawTextW 整串直接成行；写进共享内存文本环供 host 消费。
//
// 诚实局限：只覆盖 **GDI 渲染文本**的游戏。KiriKiriZ / RenPy / Unity 走 FreeType / DirectWrite
// / 自绘位图字体，GDI 文本 API 抓不到——那些靠 LunaTranslator 等备选覆盖，不在本组件范围。也
// 不写引擎特定 H-code（逐游戏内存 callsite/参数偏移的 DB，是 LunaHook 的活儿，超出本组件）。
//
// 并发模型：文本 hook **不是**音频回调，允许加锁 + 静态缓冲（仅音频回调是零阻塞红线）；但仍
// 不做重 IO。所有累积/去重/写环都在 g_text_cs 保护下、并以 g_capture_enabled 兜住 DETACH
// 解映射窗口（与音频回调同一总开关）。

// 文本渲染 API 原型（GetProcAddress 动态取址，不链接 gdi32/user32.lib）。
typedef DWORD(WINAPI* GetGlyphOutlineW_t)(HDC, UINT, UINT, LPGLYPHMETRICS, DWORD,
                                          LPVOID, const MAT2*);
typedef BOOL(WINAPI* ExtTextOutW_t)(HDC, int, int, UINT, const RECT*, LPCWSTR,
                                    UINT, const INT*);
typedef BOOL(WINAPI* TextOutW_t)(HDC, int, int, LPCWSTR, int);
typedef int(WINAPI* DrawTextW_t)(HDC, LPCWSTR, int, LPRECT, UINT);

GetGlyphOutlineW_t g_orig_GetGlyphOutlineW = nullptr;
ExtTextOutW_t g_orig_ExtTextOutW = nullptr;
TextOutW_t g_orig_TextOutW = nullptr;
DrawTextW_t g_orig_DrawTextW = nullptr;

// 文本累积/去重的锁与缓冲（与音频回调隔离，允许加锁；锁在全局捕获状态区声明）。
constexpr int kMaxLineChars = 500;           // 一行台词上界（UTF-16 字符数）
wchar_t g_glyph_buf[kMaxLineChars + 8];      // GetGlyphOutlineW 逐字累积缓冲
int g_glyph_len = 0;                         // 当前累积字符数
ULONGLONG g_glyph_last_tick = 0;             // 上次 GetGlyphOutlineW 时刻（判行界）
wchar_t g_last_flushed[kMaxLineChars + 8];   // 上一条已 flush 的行（去重）
int g_last_flushed_len = 0;

// 把一行文本写进共享内存文本环（调用者持 g_text_cs，仅序列化本 DLL 内部去重状态）。
// 文本环是**多写者**：本 DLL（游戏内 GDI hook）和 host 侧 injector 里的 LunaHook 写同一个环，
// 跨进程并发。故 slot 号必须用 InterlockedIncrement64 原子 fetch-add 预留——绝不与另一写者撞
// 同一槽，也绝不丢更新（旧的“读 text_write_count → 填 → plain store idx+1”是非原子 RMW，与
// 原子写者交错时会互相覆盖计数、丢行或读到半写槽）。写序：原子占号 → 填文本字节 + 字段 →
// **最后**写 seq=占到的号作完成标记（reader 校验 slot.seq==text_write_count 才取该槽；x86/x64
// store 有序 TSO，前面的数据写对 reader 先于 seq 可见）。首次 flush 置 text_hooked=1。
void WriteTextRingEntryLocked(const wchar_t* text, int char_len,
                              uint64_t thread_id, uint64_t thread_address,
                              uint64_t thread_context, uint32_t source_kind,
                              const char* hook_name,
                              const wchar_t* hook_code) {
  if (g_text_base == nullptr || g_header == nullptr || char_len <= 0) {
    return;
  }
  // LunaHook（引擎精确、干净）一旦活跃，GDI 文本 hook 让位，不再写文本环——否则游戏为粗体/描边
  // 每字重画会让 GetGlyphOutlineW 累加出「ここのの」式伪影，与 LunaHook 干净行混在一起污染卡片。
  // GDI 仅在 LunaHook 覆盖不到的引擎（luna_active==0）时作兜底文本源。音频 hook 不看此标志。
  if (source_kind == fushi_voice_hook::kTextSourceGdi &&
      g_header->luna_active != 0) {
    return;
  }
  // v13：写进本线程自己那条道（游戏内 writer 用高段下标，与 injector 里的 LunaHook 互斥）。
  // 调用方已持 g_text_cs，认领与道内占号因此在本进程内串行；跨进程隔离靠区段划分。
  fushi_voice_hook::TextLaneWrite write;
  write.thread_id = thread_id;
  write.thread_address = thread_address;
  write.thread_context = thread_context;
  write.process_id = GetCurrentProcessId();
  write.source_kind = source_kind;
  write.is_utf8 = 0;  // UTF-16LE
  write.text = text;
  write.byte_len = static_cast<uint32_t>(char_len) *
                   static_cast<uint32_t>(sizeof(wchar_t));
  write.hook_name = hook_name;
  write.hook_code = hook_code;
  fushi_voice_hook::WriteTextLaneEvent(
      g_header, fushi_voice_hook::kNativeThreadPreviewStart,
      fushi_voice_hook::kTextLaneCount, write);
}

void WriteTextRingLocked(const wchar_t* text, int char_len) {
  WriteTextRingEntryLocked(text, char_len, 1, 0, 0,
                           fushi_voice_hook::kTextSourceGdi,
                           "GDI fallback", nullptr);
}

// 过滤 + 去重 + 写环（调用者持 g_text_cs）。过滤：跳空串/纯空白/纯 ASCII 控制；只保留含至少
// 一个非 ASCII（>=0x3000，日文假名/汉字之类）或非空白字符数>=2 的串——避免把 UI 数字/单字母
// 当台词（粗过滤即可，别过度）。去重：与上一条 flush 完全相同则跳过（游戏常重绘同句）。
void FlushLineLocked(const wchar_t* text, int len) {
  if (text == nullptr || len <= 0) {
    return;
  }
  if (len > kMaxLineChars) {
    len = kMaxLineChars;  // 截断到缓冲/环槽上界
  }
  bool has_cjk = false;
  int meaningful = 0;
  for (int i = 0; i < len; i++) {
    const wchar_t c = text[i];
    // 空白/控制字符（空格 0x20、TAB 0x09、CR 0x0D、LF 0x0A、以及 <0x20 控制字符）不计。
    if (c == 0x20 || c == 0x09 || c == 0x0D || c == 0x0A || c < 0x20) {
      continue;
    }
    meaningful++;
    if (c >= 0x3000) {
      has_cjk = true;
    }
  }
  if (meaningful == 0) {
    return;  // 空串/纯空白/纯控制
  }
  if (!has_cjk && meaningful < 2) {
    return;  // 单个 ASCII 字符（UI 数字/字母噪声）
  }
  if (len == g_last_flushed_len &&
      memcmp(text, g_last_flushed,
             static_cast<size_t>(len) * sizeof(wchar_t)) == 0) {
    return;  // 与上一条完全相同（重绘同句）
  }
  memcpy(g_last_flushed, text, static_cast<size_t>(len) * sizeof(wchar_t));
  g_last_flushed_len = len;
  WriteTextRingLocked(text, len);
}

// 冲掉 GetGlyphOutlineW 逐字累积缓冲成一行（调用者持 g_text_cs）。
void FlushGlyphAccumLocked() {
  if (g_glyph_len > 0) {
    FlushLineLocked(g_glyph_buf, g_glyph_len);
    g_glyph_len = 0;
  }
}

#if defined(_M_IX86)

// Siglus 3 使用与 32 位 MSVC std::wstring 相同的 24-byte 小字符串布局。当前 Luna 的
// EmbedSiglus2 也从该函数入口的 ECX 读取此对象；在引擎逻辑层拿整句可彻底避开 GDI
// 描边/逐字重画造成的重复字与漏标点。
struct SiglusTextUnionW {
  union {
    const wchar_t* text;
    wchar_t chars[8];
  } storage;
  uint32_t size;
  uint32_t capacity;
};
static_assert(sizeof(SiglusTextUnionW) == 24,
              "Siglus TextUnionW layout changed");

using SiglusExactTextFn = uintptr_t(__thiscall*)(SiglusTextUnionW* self);
SiglusExactTextFn g_orig_SiglusExactText = nullptr;
uintptr_t g_siglus_text_function_rva = 0;

void RememberSiglusLookupText(const wchar_t* text, uint32_t length);

bool IsReadableSpan(const void* pointer, size_t bytes) {
  if (pointer == nullptr || bytes == 0) return false;
  uintptr_t cursor = reinterpret_cast<uintptr_t>(pointer);
  const uintptr_t end = cursor + bytes;
  if (end < cursor) return false;
  while (cursor < end) {
    MEMORY_BASIC_INFORMATION memory = {};
    if (VirtualQuery(reinterpret_cast<const void*>(cursor), &memory,
                     sizeof(memory)) != sizeof(memory) ||
        memory.State != MEM_COMMIT ||
        (memory.Protect & (PAGE_GUARD | PAGE_NOACCESS)) != 0) {
      return false;
    }
    const uintptr_t region_end =
        reinterpret_cast<uintptr_t>(memory.BaseAddress) + memory.RegionSize;
    if (region_end <= cursor) return false;
    cursor = region_end;
  }
  return true;
}

const wchar_t* ReadSiglusText(const SiglusTextUnionW* value,
                              uint32_t* length) {
  if (length == nullptr ||
      !IsReadableSpan(value, sizeof(SiglusTextUnionW)) || value->size == 0 ||
      value->size > 1500 || value->size > value->capacity) {
    return nullptr;
  }
  const wchar_t* text = value->capacity < 8 ? value->storage.chars
                                             : value->storage.text;
  const size_t bytes =
      (static_cast<size_t>(value->size) + 1) * sizeof(wchar_t);
  if (!IsReadableSpan(text, bytes) || text[value->size] != L'\0') {
    return nullptr;
  }
  *length = value->size;
  return text;
}

uintptr_t __fastcall Detour_SiglusExactText(SiglusTextUnionW* self, void*) {
  uint32_t length = 0;
  const wchar_t* text = ReadSiglusText(self, &length);
  if (text != nullptr && g_capture_enabled && g_text_cs_ready) {
    RememberSiglusLookupText(text, length);
    const uintptr_t module =
        reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
    const uintptr_t return_address =
        reinterpret_cast<uintptr_t>(_ReturnAddress());
    const uint64_t callsite_rva =
        return_address >= module ? return_address - module : return_address;
    // Luna 以 hook 地址 + caller context 区分文本线程。沿用同一粒度，名字/正文等不同
    // caller 会出现在下拉框中，用户可像 Luna Translator 一样只选干净台词线程。
    const uint64_t thread_id =
        (static_cast<uint64_t>(g_siglus_text_function_rva) << 32) ^
        callsite_rva;
    wchar_t hook_code[kTextHookCodeChars] = {0};
    _snwprintf_s(hook_code, kTextHookCodeChars, _TRUNCATE,
                 L"EXBWX0@%llX:SiglusEngine.exe",
                 static_cast<unsigned long long>(g_siglus_text_function_rva));

    EnterCriticalSection(&g_text_cs);
    WriteTextRingEntryLocked(
        text, static_cast<int>(length), thread_id,
        g_siglus_text_function_rva, callsite_rva,
        fushi_voice_hook::kTextSourceSiglus, "SiglusEngine exact", hook_code);
    LeaveCriticalSection(&g_text_cs);
    if (g_header != nullptr) {
      g_header->luna_active = 1;  // 精确引擎文本已实锤，GDI 兜底从此让位。
      g_header->hook_diagnostics |= kDiagSiglusExactTextObserved;
    }
  }
  return g_orig_SiglusExactText(self);
}

bool TryHookSiglusExactText() {
  if (!IsSiglusEngine()) return true;
  if (g_orig_SiglusExactText != nullptr) return true;
  HMODULE module = GetModuleHandleW(nullptr);
  if (module == nullptr) return false;
  const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(module);
  if (dos->e_magic != IMAGE_DOS_SIGNATURE) return false;
  const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS*>(
      reinterpret_cast<const uint8_t*>(module) + dos->e_lfanew);
  if (nt->Signature != IMAGE_NT_SIGNATURE) return false;

  const IMAGE_SECTION_HEADER* section = IMAGE_FIRST_SECTION(nt);
  for (uint16_t i = 0; i < nt->FileHeader.NumberOfSections; ++i) {
    if ((section[i].Characteristics & IMAGE_SCN_MEM_EXECUTE) == 0) continue;
    const size_t section_bytes = section[i].Misc.VirtualSize;
    const auto* section_base = reinterpret_cast<const uint8_t*>(module) +
                               section[i].VirtualAddress;
    if (!IsReadableSpan(section_base, section_bytes)) continue;
    const size_t offset = fushi_voice_hook::siglus::FindExactTextFunctionOffset(
        section_base, section_bytes);
    if (offset == fushi_voice_hook::siglus::kInvalidTextFunctionOffset) {
      continue;
    }
    void* target = const_cast<uint8_t*>(section_base + offset);
    g_siglus_text_function_rva =
        static_cast<uintptr_t>(section[i].VirtualAddress) + offset;
    if (!HookFn(target, reinterpret_cast<void*>(&Detour_SiglusExactText),
                reinterpret_cast<void**>(&g_orig_SiglusExactText))) {
      g_siglus_text_function_rva = 0;
      return false;
    }
    if (g_header != nullptr) {
      g_header->text_hooked = 1;
      g_header->hook_diagnostics |= kDiagSiglusExactTextHookReady;
    }
    return true;
  }
  return false;
}

#else

bool TryHookSiglusExactText() { return true; }

#endif  // _M_IX86

// ── Siglus D3D9 字形几何 + 单击命中 ─────────────────────────────────────────
//
// SiglusEngine 的台词不是 WebView/TJS，也不经过 TextOutW 的屏幕坐标。精确文本 hook
// 只能告诉我们“现在要画哪一句”；真正的字形位置在 D3D9 的 transformed vertex 中。
// 这里仅认 SiglusEngine.exe，并只采集紧跟精确台词 hook 出现的、同一水平线上的小四边形。
// 不碰通用 GDI 文本链，避免把字体 atlas 的离屏四边形误当成屏幕台词。
constexpr uint32_t kSiglusLookupTextChars = 1024;
constexpr uint32_t kSiglusLookupGlyphs = 512;

struct SiglusLookupGlyph {
  uint32_t char_index = 0;
  int32_t x = 0;
  int32_t y = 0;
  int32_t width = 0;
  int32_t height = 0;
};

struct SiglusLookupSnapshot {
  bool valid = false;
  ULONGLONG text_tick = 0;
  uint32_t char_count = 0;
  wchar_t line[kSiglusLookupTextChars] = {0};
  uint32_t glyph_count = 0;
  SiglusLookupGlyph glyphs[kSiglusLookupGlyphs] = {};
  D3DVIEWPORT9 viewport = {};
};

SiglusLookupSnapshot g_siglus_lookup;
volatile LONG64 g_siglus_lookup_consumed_up = 0;

using Direct3DCreate9Fn = IDirect3D9*(WINAPI*)(UINT);
using D3D9CreateDeviceFn = HRESULT(STDMETHODCALLTYPE*)(
    IDirect3D9*, UINT, D3DDEVTYPE, HWND, DWORD, D3DPRESENT_PARAMETERS*,
    IDirect3DDevice9**);
using DrawPrimitiveUPFn = HRESULT(STDMETHODCALLTYPE*)(
    IDirect3DDevice9*, D3DPRIMITIVETYPE, UINT, const void*, UINT);
using DrawIndexedPrimitiveUPFn = HRESULT(STDMETHODCALLTYPE*)(
    IDirect3DDevice9*, D3DPRIMITIVETYPE, UINT, UINT, UINT, const void*,
    D3DFORMAT, const void*, UINT);
using DrawPrimitiveFn = HRESULT(STDMETHODCALLTYPE*)(
    IDirect3DDevice9*, D3DPRIMITIVETYPE, UINT, UINT);
using DrawIndexedPrimitiveFn = HRESULT(STDMETHODCALLTYPE*)(
    IDirect3DDevice9*, D3DPRIMITIVETYPE, INT, UINT, UINT, UINT, UINT);
using GetStreamSourceFn = HRESULT(STDMETHODCALLTYPE*)(
    IDirect3DDevice9*, UINT, IDirect3DVertexBuffer9**, UINT*, UINT*);
using GetIndicesFn = HRESULT(STDMETHODCALLTYPE*)(IDirect3DDevice9*,
                                                  IDirect3DIndexBuffer9**);
using BufferLockFn = HRESULT(STDMETHODCALLTYPE*)(void*, UINT, UINT, void**,
                                                 DWORD);
using BufferUnlockFn = HRESULT(STDMETHODCALLTYPE*)(void*);
using ComReleaseFn = ULONG(STDMETHODCALLTYPE*)(void*);
using DispatchMessageWFn = LRESULT(WINAPI*)(const MSG*);
using DispatchMessageAFn = LRESULT(WINAPI*)(const MSG*);

Direct3DCreate9Fn g_orig_Direct3DCreate9 = nullptr;
D3D9CreateDeviceFn g_orig_D3D9CreateDevice = nullptr;
DrawPrimitiveUPFn g_orig_DrawPrimitiveUP = nullptr;
DrawIndexedPrimitiveUPFn g_orig_DrawIndexedPrimitiveUP = nullptr;
DrawPrimitiveFn g_orig_DrawPrimitive = nullptr;
DrawIndexedPrimitiveFn g_orig_DrawIndexedPrimitive = nullptr;
DispatchMessageWFn g_orig_DispatchMessageW = nullptr;
DispatchMessageAFn g_orig_DispatchMessageA = nullptr;

// KiriKiri 的查词辅助函数只存在于它自己的 x86 适配器分支；Siglus 也需要同一套
// IPC 原子写入，但不能依赖那个引擎的实现（x64 构建中它根本不会被编译）。
uint32_t SiglusLookupEnabledNow() {
  if (g_header == nullptr) return 0;
  return static_cast<uint32_t>(InterlockedCompareExchange(
      reinterpret_cast<volatile LONG*>(&g_header->lookup_enabled), 0, 0));
}

void SiglusSetLookupDiag(uint32_t bits) {
  if (g_header == nullptr) return;
  InterlockedOr(reinterpret_cast<volatile LONG*>(&g_header->lookup_diag),
                static_cast<LONG>(bits));
}

void SiglusWriteSharedU64(volatile uint64_t* value, uint64_t next) {
  if (value == nullptr) return;
  InterlockedExchange64(reinterpret_cast<volatile LONGLONG*>(value),
                        static_cast<LONGLONG>(next));
}

uint32_t SiglusEncodeLineUtf8(const wchar_t* line, uint8_t* out,
                              uint32_t capacity) {
  if (line == nullptr || out == nullptr || capacity == 0) return 0;
  char scratch[kSiglusLookupTextChars * 4] = {};
  const int written = WideCharToMultiByte(
      CP_UTF8, 0, line, -1, scratch, static_cast<int>(sizeof(scratch)),
      nullptr, nullptr);
  if (written <= 1) return 0;
  uint32_t bytes = static_cast<uint32_t>(written - 1);
  if (bytes > capacity) {
    bytes = capacity;
    while (bytes > 0 &&
           (static_cast<uint8_t>(scratch[bytes]) & 0xC0u) == 0x80u) {
      --bytes;
    }
  }
  memcpy(out, scratch, bytes);
  return bytes;
}

bool IsSiglusLookupWhitespace(wchar_t value) {
  return value == L' ' || value == L'\t' || value == L'\r' || value == L'\n';
}

void RememberSiglusLookupText(const wchar_t* text, uint32_t length) {
  if (!IsSiglusEngine() || text == nullptr || length == 0 || !g_text_cs_ready) {
    return;
  }
  length = (std::min)(length, kSiglusLookupTextChars - 1);
  EnterCriticalSection(&g_text_cs);
  memset(&g_siglus_lookup, 0, sizeof(g_siglus_lookup));
  memcpy(g_siglus_lookup.line, text,
         static_cast<size_t>(length) * sizeof(wchar_t));
  g_siglus_lookup.line[length] = L'\0';
  g_siglus_lookup.char_count = length;
  g_siglus_lookup.text_tick = GetTickCount64();
  LeaveCriticalSection(&g_text_cs);
}

bool ReadD3DVertexXY(const uint8_t* vertex, float* x, float* y) {
  if (vertex == nullptr || x == nullptr || y == nullptr) return false;
  __try {
    memcpy(x, vertex, sizeof(float));
    memcpy(y, vertex + sizeof(float), sizeof(float));
  } __except (EXCEPTION_EXECUTE_HANDLER) {
    return false;
  }
  // Transformed D3D9 vertices are screen-space. Reject shaders/vertex buffers that
  // are plainly not a 2D text batch instead of creating a false hit region.
  return *x > -100.0f && *x < 10000.0f && *y > -100.0f && *y < 10000.0f;
}

bool AddSiglusLookupQuadLocked(float left, float top, float right,
                               float bottom, const D3DVIEWPORT9& viewport) {
  if (g_siglus_lookup.char_count == 0 ||
      g_siglus_lookup.glyph_count >= kSiglusLookupGlyphs) {
    return false;
  }
  const int32_t x = static_cast<int32_t>(left + 0.5f);
  const int32_t y = static_cast<int32_t>(top + 0.5f);
  const int32_t width = static_cast<int32_t>(right - left + 0.5f);
  const int32_t height = static_cast<int32_t>(bottom - top + 0.5f);
  if (width < 2 || height < 6 || width > 320 || height > 320) return false;

  // Siglus 可能把一个句子拆成多个 Draw* 调用。保留上一调用的字形，只接受
  // 同一水平线、尺寸相近、不会明显倒退的后续四边形；遇到另一类 UI 图元时
  // 清掉几何但保留当前文本，等待下一批真正的字形。
  if (g_siglus_lookup.glyph_count != 0) {
    const SiglusLookupGlyph& previous =
        g_siglus_lookup.glyphs[g_siglus_lookup.glyph_count - 1];
    if (abs(y - previous.y) > 12 || abs(height - previous.height) > 12 ||
        x + width < previous.x - 12) {
      g_siglus_lookup.glyph_count = 0;
      g_siglus_lookup.valid = false;
    }
  }

  // 描边、阴影和填充可能分别提交同一个字符的四边形。一个位置只认一次，
  // 否则“按绘制调用顺序映射字符”会在第二个字开始逐字漂移。
  for (uint32_t i = 0; i < g_siglus_lookup.glyph_count; ++i) {
    const SiglusLookupGlyph& existing = g_siglus_lookup.glyphs[i];
    if (abs(x - existing.x) <= 2 && abs(y - existing.y) <= 2 &&
        abs(width - existing.width) <= 3 &&
        abs(height - existing.height) <= 3) {
      return false;
    }
  }

  uint32_t index = g_siglus_lookup.glyph_count == 0
                       ? 0
                       : g_siglus_lookup.glyphs[g_siglus_lookup.glyph_count - 1]
                                 .char_index +
                             1;
  while (index < g_siglus_lookup.char_count &&
         IsSiglusLookupWhitespace(g_siglus_lookup.line[index])) {
    ++index;
  }
  if (index >= g_siglus_lookup.char_count) return false;

  SiglusLookupGlyph& glyph =
      g_siglus_lookup.glyphs[g_siglus_lookup.glyph_count++];
  glyph.char_index = index;
  glyph.x = x;
  glyph.y = y;
  glyph.width = width;
  glyph.height = height;
  g_siglus_lookup.viewport = viewport;
  g_siglus_lookup.valid = true;
  return true;
}

void CaptureSiglusD3DPrimitiveUP(IDirect3DDevice9* device,
                                 D3DPRIMITIVETYPE type, UINT primitive_count,
                                 const void* vertex_data, UINT stride) {
  if (!IsSiglusEngine() || !g_capture_enabled || !g_text_cs_ready ||
      vertex_data == nullptr || stride < sizeof(float) * 2 ||
      primitive_count == 0 || g_header == nullptr ||
      SiglusLookupEnabledNow() == 0) {
    return;
  }
  EnterCriticalSection(&g_text_cs);
  const ULONGLONG now = GetTickCount64();
  if (!g_siglus_lookup.valid &&
      (g_siglus_lookup.char_count == 0 ||
       now - g_siglus_lookup.text_tick > 1500)) {
    LeaveCriticalSection(&g_text_cs);
    return;
  }

  D3DVIEWPORT9 viewport = {};
  using GetViewportFn = HRESULT(STDMETHODCALLTYPE*)(IDirect3DDevice9*,
                                                     D3DVIEWPORT9*);
  const auto get_viewport = reinterpret_cast<GetViewportFn>(
      VtableSlot(device, 48));
  if (get_viewport == nullptr || FAILED(get_viewport(device, &viewport)) ||
      viewport.Width == 0 || viewport.Height == 0) {
    LeaveCriticalSection(&g_text_cs);
    return;
  }

  const UINT vertices_per_quad = type == D3DPT_TRIANGLELIST ? 6u :
                                 type == D3DPT_TRIANGLESTRIP ? 4u : 0u;
  if (vertices_per_quad == 0) {
    LeaveCriticalSection(&g_text_cs);
    return;
  }
  const UINT quad_count = primitive_count / 2;
  uint32_t captured = 0;
  for (UINT quad = 0; quad < quad_count; ++quad) {
    float left = 100000.0f;
    float top = 100000.0f;
    float right = -100000.0f;
    float bottom = -100000.0f;
    bool valid = true;
    const auto* bytes = static_cast<const uint8_t*>(vertex_data) +
                        static_cast<size_t>(quad) * vertices_per_quad * stride;
    for (UINT vertex = 0; vertex < vertices_per_quad; ++vertex) {
      float x = 0;
      float y = 0;
      if (!ReadD3DVertexXY(bytes + static_cast<size_t>(vertex) * stride, &x,
                           &y)) {
        valid = false;
        break;
      }
      left = (std::min)(left, x);
      top = (std::min)(top, y);
      right = (std::max)(right, x);
      bottom = (std::max)(bottom, y);
    }
    if (!valid || !AddSiglusLookupQuadLocked(left, top, right, bottom,
                                             viewport)) {
      continue;
    }
    ++captured;
    if (g_siglus_lookup.glyph_count >= g_siglus_lookup.char_count) break;
  }

  // Keep a single quad as a valid one-character line. The next Draw* call may
  // contain the following glyph when Siglus renders one character per call.
  if (captured >= 2 || g_siglus_lookup.glyph_count == 1) {
    SiglusSetLookupDiag(fushi_voice_hook::kLookupDiagSensorInstalled |
                        fushi_voice_hook::kLookupDiagGeometryObserved);
  }
  LeaveCriticalSection(&g_text_cs);
}

bool ReadSiglusLookupIndex(const void* index_data, D3DFORMAT format,
                           UINT index, uint32_t* value) {
  if (index_data == nullptr || value == nullptr) return false;
  __try {
    if (format == D3DFMT_INDEX16) {
      *value = static_cast<const uint16_t*>(index_data)[index];
      return true;
    }
    if (format == D3DFMT_INDEX32) {
      *value = static_cast<const uint32_t*>(index_data)[index];
      return true;
    }
  } __except (EXCEPTION_EXECUTE_HANDLER) {
  }
  return false;
}

void CaptureSiglusD3DIndexedPrimitiveUP(
    IDirect3DDevice9* device, D3DPRIMITIVETYPE type, UINT min_vertex_index,
    UINT num_vertices, UINT primitive_count, const void* index_data,
    D3DFORMAT index_format, const void* vertex_data, UINT stride) {
  if (!IsSiglusEngine() || !g_capture_enabled || !g_text_cs_ready ||
      index_data == nullptr || vertex_data == nullptr || stride < sizeof(float) * 2 ||
      primitive_count == 0 || g_header == nullptr ||
      SiglusLookupEnabledNow() == 0) {
    return;
  }
  const UINT indices_per_quad = type == D3DPT_TRIANGLELIST ? 6u :
                                type == D3DPT_TRIANGLESTRIP ? 4u : 0u;
  if (indices_per_quad == 0) return;

  EnterCriticalSection(&g_text_cs);
  const ULONGLONG now = GetTickCount64();
  if (!g_siglus_lookup.valid &&
      (g_siglus_lookup.char_count == 0 ||
       now - g_siglus_lookup.text_tick > 1500)) {
    LeaveCriticalSection(&g_text_cs);
    return;
  }
  D3DVIEWPORT9 viewport = {};
  using GetViewportFn = HRESULT(STDMETHODCALLTYPE*)(IDirect3DDevice9*,
                                                     D3DVIEWPORT9*);
  const auto get_viewport = reinterpret_cast<GetViewportFn>(
      VtableSlot(device, 48));
  if (get_viewport == nullptr || FAILED(get_viewport(device, &viewport)) ||
      viewport.Width == 0 || viewport.Height == 0) {
    LeaveCriticalSection(&g_text_cs);
    return;
  }

  const UINT quad_count = primitive_count / 2;
  uint32_t captured = 0;
  for (UINT quad = 0; quad < quad_count; ++quad) {
    float left = 100000.0f;
    float top = 100000.0f;
    float right = -100000.0f;
    float bottom = -100000.0f;
    bool valid = true;
    for (UINT corner = 0; corner < indices_per_quad; ++corner) {
      uint32_t vertex_index = 0;
      const UINT index = quad * indices_per_quad + corner;
      if (!ReadSiglusLookupIndex(index_data, index_format, index,
                                 &vertex_index) ||
          vertex_index < min_vertex_index ||
          vertex_index - min_vertex_index >= num_vertices) {
        valid = false;
        break;
      }
      float x = 0;
      float y = 0;
      const auto* vertex = static_cast<const uint8_t*>(vertex_data) +
                           static_cast<size_t>(vertex_index - min_vertex_index) *
                               stride;
      if (!ReadD3DVertexXY(vertex, &x, &y)) {
        valid = false;
        break;
      }
      left = (std::min)(left, x);
      top = (std::min)(top, y);
      right = (std::max)(right, x);
      bottom = (std::max)(bottom, y);
    }
    if (valid && AddSiglusLookupQuadLocked(left, top, right, bottom, viewport)) {
      ++captured;
      if (g_siglus_lookup.glyph_count >= g_siglus_lookup.char_count) break;
    }
  }
  if (captured != 0) {
    SiglusSetLookupDiag(fushi_voice_hook::kLookupDiagSensorInstalled |
                        fushi_voice_hook::kLookupDiagGeometryObserved);
  }
  LeaveCriticalSection(&g_text_cs);
}

UINT SiglusLookupVerticesPerPrimitive(D3DPRIMITIVETYPE type) {
  switch (type) {
    case D3DPT_TRIANGLELIST:
      return 3;
    case D3DPT_TRIANGLESTRIP:
    case D3DPT_TRIANGLEFAN:
      return 1;
    default:
      return 0;
  }
}

UINT SiglusLookupVertexSpan(D3DPRIMITIVETYPE type, UINT primitive_count) {
  if (primitive_count == 0) return 0;
  switch (type) {
    case D3DPT_TRIANGLELIST:
      return primitive_count > (UINT_MAX / 3u) ? 0 : primitive_count * 3u;
    case D3DPT_TRIANGLESTRIP:
    case D3DPT_TRIANGLEFAN:
      return primitive_count > UINT_MAX - 2u ? 0 : primitive_count + 2u;
    default:
      return 0;
  }
}

bool SiglusLookupWindowActiveLocked() {
  const ULONGLONG now = GetTickCount64();
  return g_siglus_lookup.char_count != 0 &&
         (g_siglus_lookup.valid || now - g_siglus_lookup.text_tick <= 1500);
}

bool ReadSiglusLookupVertexBuffer(IDirect3DDevice9* device, UINT start_vertex,
                                  UINT vertex_count, UINT stride,
                                  const void** vertex_data,
                                  void** locked_buffer,
                                  UINT* locked_offset) {
  if (vertex_data == nullptr || locked_buffer == nullptr ||
      locked_offset == nullptr || device == nullptr || vertex_count == 0 ||
      stride < sizeof(float) * 2 || stride > 1024) {
    return false;
  }
  const auto get_stream = reinterpret_cast<GetStreamSourceFn>(
      VtableSlot(device, 101));
  if (get_stream == nullptr) return false;

  IDirect3DVertexBuffer9* buffer = nullptr;
  UINT stream_offset = 0;
  UINT stream_stride = 0;
  if (FAILED(get_stream(device, 0, &buffer, &stream_offset, &stream_stride)) ||
      buffer == nullptr || stream_stride != stride) {
    if (buffer != nullptr) {
      const auto release = reinterpret_cast<ComReleaseFn>(
          VtableSlot(buffer, 2));
      if (release != nullptr) release(buffer);
    }
    return false;
  }

  const uint64_t byte_offset = static_cast<uint64_t>(stream_offset) +
                               static_cast<uint64_t>(start_vertex) * stride;
  const uint64_t byte_size = static_cast<uint64_t>(vertex_count) * stride;
  if (byte_offset > UINT_MAX || byte_size == 0 || byte_size > 1024u * 1024u ||
      byte_offset + byte_size > UINT_MAX) {
    const auto release = reinterpret_cast<ComReleaseFn>(VtableSlot(buffer, 2));
    if (release != nullptr) release(buffer);
    return false;
  }

  void* locked = nullptr;
  const auto lock = reinterpret_cast<BufferLockFn>(VtableSlot(buffer, 11));
  if (lock == nullptr ||
      FAILED(lock(buffer, static_cast<UINT>(byte_offset),
                 static_cast<UINT>(byte_size), &locked, D3DLOCK_READONLY)) ||
      locked == nullptr) {
    const auto release = reinterpret_cast<ComReleaseFn>(VtableSlot(buffer, 2));
    if (release != nullptr) release(buffer);
    return false;
  }
  *vertex_data = locked;
  *locked_buffer = buffer;
  *locked_offset = static_cast<UINT>(byte_offset);
  return true;
}

void UnlockSiglusLookupBuffer(void* buffer) {
  if (buffer == nullptr) return;
  const auto unlock = reinterpret_cast<BufferUnlockFn>(VtableSlot(buffer, 12));
  if (unlock != nullptr) unlock(buffer);
  const auto release = reinterpret_cast<ComReleaseFn>(VtableSlot(buffer, 2));
  if (release != nullptr) release(buffer);
}

void CaptureSiglusD3DPrimitive(IDirect3DDevice9* device,
                               D3DPRIMITIVETYPE type, UINT start_vertex,
                               UINT primitive_count) {
  if (!IsSiglusEngine() || !g_capture_enabled || !g_text_cs_ready ||
      device == nullptr || primitive_count == 0 || g_header == nullptr ||
      SiglusLookupEnabledNow() == 0) {
    return;
  }
  const UINT vertices_per_primitive =
      SiglusLookupVerticesPerPrimitive(type);
  const UINT vertex_span = SiglusLookupVertexSpan(type, primitive_count);
  if (vertices_per_primitive == 0 || vertex_span == 0 ||
      primitive_count < 2 || (primitive_count % 2u) != 0) {
    return;
  }

  EnterCriticalSection(&g_text_cs);
  if (!SiglusLookupWindowActiveLocked()) {
    LeaveCriticalSection(&g_text_cs);
    return;
  }

  D3DVIEWPORT9 viewport = {};
  using GetViewportFn = HRESULT(STDMETHODCALLTYPE*)(IDirect3DDevice9*,
                                                     D3DVIEWPORT9*);
  const auto get_viewport = reinterpret_cast<GetViewportFn>(
      VtableSlot(device, 48));
  if (get_viewport == nullptr || FAILED(get_viewport(device, &viewport)) ||
      viewport.Width == 0 || viewport.Height == 0) {
    LeaveCriticalSection(&g_text_cs);
    return;
  }

  UINT stride = 0;
  const auto get_stream = reinterpret_cast<GetStreamSourceFn>(
      VtableSlot(device, 101));
  IDirect3DVertexBuffer9* stream_probe = nullptr;
  UINT stream_offset = 0;
  if (get_stream == nullptr ||
      FAILED(get_stream(device, 0, &stream_probe, &stream_offset, &stride)) ||
      stream_probe == nullptr) {
    if (stream_probe != nullptr) {
      const auto release = reinterpret_cast<ComReleaseFn>(
          VtableSlot(stream_probe, 2));
      if (release != nullptr) release(stream_probe);
    }
    LeaveCriticalSection(&g_text_cs);
    return;
  }
  const auto release_probe = reinterpret_cast<ComReleaseFn>(
      VtableSlot(stream_probe, 2));
  if (release_probe != nullptr) release_probe(stream_probe);

  const void* vertex_data = nullptr;
  void* locked_buffer = nullptr;
  UINT locked_offset = 0;
  if (!ReadSiglusLookupVertexBuffer(device, start_vertex, vertex_span, stride,
                                    &vertex_data, &locked_buffer,
                                    &locked_offset)) {
    LeaveCriticalSection(&g_text_cs);
    return;
  }

  const UINT quad_count = primitive_count / 2u;
  uint32_t captured = 0;
  for (UINT quad = 0; quad < quad_count; ++quad) {
    float left = 100000.0f;
    float top = 100000.0f;
    float right = -100000.0f;
    float bottom = -100000.0f;
    bool valid = true;
    const UINT first = type == D3DPT_TRIANGLELIST
                           ? quad * 6u
                           : quad * vertices_per_primitive * 2u;
    const UINT count = type == D3DPT_TRIANGLELIST ? 6u : 4u;
    for (UINT vertex = 0; vertex < count; ++vertex) {
      float x = 0;
      float y = 0;
      const auto* bytes = static_cast<const uint8_t*>(vertex_data) +
                          static_cast<size_t>(first + vertex) * stride;
      if (!ReadD3DVertexXY(bytes, &x, &y)) {
        valid = false;
        break;
      }
      left = (std::min)(left, x);
      top = (std::min)(top, y);
      right = (std::max)(right, x);
      bottom = (std::max)(bottom, y);
    }
    if (valid && AddSiglusLookupQuadLocked(left, top, right, bottom,
                                           viewport)) {
      ++captured;
      if (g_siglus_lookup.glyph_count >= g_siglus_lookup.char_count) break;
    }
  }
  UnlockSiglusLookupBuffer(locked_buffer);
  if (captured != 0) {
    SiglusSetLookupDiag(fushi_voice_hook::kLookupDiagSensorInstalled |
                        fushi_voice_hook::kLookupDiagGeometryObserved);
  }
  LeaveCriticalSection(&g_text_cs);
}

void CaptureSiglusD3DIndexedPrimitive(
    IDirect3DDevice9* device, D3DPRIMITIVETYPE type, INT base_vertex_index,
    UINT min_vertex_index, UINT num_vertices, UINT start_index,
    UINT primitive_count) {
  if (!IsSiglusEngine() || !g_capture_enabled || !g_text_cs_ready ||
      device == nullptr || primitive_count == 0 || g_header == nullptr ||
      SiglusLookupEnabledNow() == 0) {
    return;
  }
  const UINT indices_per_primitive =
      SiglusLookupVerticesPerPrimitive(type);
  const UINT index_count =
      primitive_count > UINT_MAX / 3u ? 0 : primitive_count * 3u;
  if (indices_per_primitive == 0 || index_count == 0 || primitive_count < 2 ||
      (primitive_count % 2u) != 0 || num_vertices == 0 ||
      num_vertices > 65536u) {
    return;
  }

  EnterCriticalSection(&g_text_cs);
  if (!SiglusLookupWindowActiveLocked()) {
    LeaveCriticalSection(&g_text_cs);
    return;
  }

  D3DVIEWPORT9 viewport = {};
  using GetViewportFn = HRESULT(STDMETHODCALLTYPE*)(IDirect3DDevice9*,
                                                     D3DVIEWPORT9*);
  const auto get_viewport = reinterpret_cast<GetViewportFn>(
      VtableSlot(device, 48));
  if (get_viewport == nullptr || FAILED(get_viewport(device, &viewport)) ||
      viewport.Width == 0 || viewport.Height == 0) {
    LeaveCriticalSection(&g_text_cs);
    return;
  }

  UINT stride = 0;
  const auto get_stream = reinterpret_cast<GetStreamSourceFn>(
      VtableSlot(device, 101));
  IDirect3DVertexBuffer9* stream_probe = nullptr;
  UINT stream_offset = 0;
  if (get_stream == nullptr ||
      FAILED(get_stream(device, 0, &stream_probe, &stream_offset, &stride)) ||
      stream_probe == nullptr) {
    if (stream_probe != nullptr) {
      const auto release = reinterpret_cast<ComReleaseFn>(
          VtableSlot(stream_probe, 2));
      if (release != nullptr) release(stream_probe);
    }
    LeaveCriticalSection(&g_text_cs);
    return;
  }
  const auto release_probe = reinterpret_cast<ComReleaseFn>(
      VtableSlot(stream_probe, 2));
  if (release_probe != nullptr) release_probe(stream_probe);

  const auto get_indices = reinterpret_cast<GetIndicesFn>(
      VtableSlot(device, 105));
  IDirect3DIndexBuffer9* index_buffer = nullptr;
  if (get_indices == nullptr ||
      FAILED(get_indices(device, &index_buffer)) || index_buffer == nullptr) {
    if (index_buffer != nullptr) {
      const auto release = reinterpret_cast<ComReleaseFn>(
          VtableSlot(index_buffer, 2));
      if (release != nullptr) release(index_buffer);
    }
    LeaveCriticalSection(&g_text_cs);
    return;
  }

  DWORD index_format = D3DFMT_INDEX16;
  using GetDescFn = HRESULT(STDMETHODCALLTYPE*)(void*, D3DINDEXBUFFER_DESC*);
  const auto get_desc = reinterpret_cast<GetDescFn>(VtableSlot(index_buffer, 13));
  D3DINDEXBUFFER_DESC index_desc = {};
  if (get_desc == nullptr || FAILED(get_desc(index_buffer, &index_desc)) ||
      (index_desc.Format != D3DFMT_INDEX16 &&
       index_desc.Format != D3DFMT_INDEX32)) {
    const auto release = reinterpret_cast<ComReleaseFn>(VtableSlot(index_buffer, 2));
    if (release != nullptr) release(index_buffer);
    LeaveCriticalSection(&g_text_cs);
    return;
  }
  index_format = index_desc.Format;
  const UINT index_size = index_format == D3DFMT_INDEX32 ? 4u : 2u;
  const uint64_t index_offset = static_cast<uint64_t>(start_index) * index_size;
  const uint64_t index_bytes = static_cast<uint64_t>(index_count) * index_size;
  if (index_offset > UINT_MAX || index_bytes == 0 || index_bytes > 512u * 1024u ||
      index_offset + index_bytes > UINT_MAX) {
    const auto release = reinterpret_cast<ComReleaseFn>(VtableSlot(index_buffer, 2));
    if (release != nullptr) release(index_buffer);
    LeaveCriticalSection(&g_text_cs);
    return;
  }

  void* index_data = nullptr;
  const auto lock_index = reinterpret_cast<BufferLockFn>(
      VtableSlot(index_buffer, 11));
  if (lock_index == nullptr ||
      FAILED(lock_index(index_buffer, static_cast<UINT>(index_offset),
                        static_cast<UINT>(index_bytes), &index_data,
                        D3DLOCK_READONLY)) ||
      index_data == nullptr) {
    const auto release = reinterpret_cast<ComReleaseFn>(VtableSlot(index_buffer, 2));
    if (release != nullptr) release(index_buffer);
    LeaveCriticalSection(&g_text_cs);
    return;
  }

  const int64_t first_vertex = static_cast<int64_t>(base_vertex_index) +
                               static_cast<int64_t>(min_vertex_index);
  const int64_t last_vertex = first_vertex +
                              static_cast<int64_t>(num_vertices - 1u);
  if (first_vertex < 0 || last_vertex < first_vertex ||
      last_vertex > UINT_MAX) {
    const auto unlock = reinterpret_cast<BufferUnlockFn>(
        VtableSlot(index_buffer, 12));
    if (unlock != nullptr) unlock(index_buffer);
    const auto release = reinterpret_cast<ComReleaseFn>(VtableSlot(index_buffer, 2));
    if (release != nullptr) release(index_buffer);
    LeaveCriticalSection(&g_text_cs);
    return;
  }

  const UINT vertex_start = static_cast<UINT>(first_vertex);
  const void* vertex_data = nullptr;
  void* vertex_buffer = nullptr;
  UINT locked_offset = 0;
  if (!ReadSiglusLookupVertexBuffer(device, vertex_start, num_vertices, stride,
                                    &vertex_data, &vertex_buffer,
                                    &locked_offset)) {
    const auto unlock = reinterpret_cast<BufferUnlockFn>(
        VtableSlot(index_buffer, 12));
    if (unlock != nullptr) unlock(index_buffer);
    const auto release = reinterpret_cast<ComReleaseFn>(VtableSlot(index_buffer, 2));
    if (release != nullptr) release(index_buffer);
    LeaveCriticalSection(&g_text_cs);
    return;
  }

  const UINT quad_count = primitive_count / 2u;
  const UINT index_per_quad = type == D3DPT_TRIANGLELIST ? 6u : 4u;
  uint32_t captured = 0;
  for (UINT quad = 0; quad < quad_count; ++quad) {
    float left = 100000.0f;
    float top = 100000.0f;
    float right = -100000.0f;
    float bottom = -100000.0f;
    bool valid = true;
    for (UINT corner = 0; corner < index_per_quad; ++corner) {
      uint32_t index_value = 0;
      const UINT index = quad * index_per_quad + corner;
      if (!ReadSiglusLookupIndex(index_data, static_cast<D3DFORMAT>(index_format),
                                 index,
                                 &index_value)) {
        valid = false;
        break;
      }
      const int64_t absolute_vertex =
          static_cast<int64_t>(base_vertex_index) + index_value;
      if (absolute_vertex < first_vertex || absolute_vertex > last_vertex) {
        valid = false;
        break;
      }
      float x = 0;
      float y = 0;
      const auto* bytes = static_cast<const uint8_t*>(vertex_data) +
                          static_cast<size_t>(absolute_vertex - first_vertex) *
                              stride;
      if (!ReadD3DVertexXY(bytes, &x, &y)) {
        valid = false;
        break;
      }
      left = (std::min)(left, x);
      top = (std::min)(top, y);
      right = (std::max)(right, x);
      bottom = (std::max)(bottom, y);
    }
    if (valid && AddSiglusLookupQuadLocked(left, top, right, bottom,
                                           viewport)) {
      ++captured;
      if (g_siglus_lookup.glyph_count >= g_siglus_lookup.char_count) break;
    }
  }
  UnlockSiglusLookupBuffer(vertex_buffer);
  const auto unlock_index = reinterpret_cast<BufferUnlockFn>(
      VtableSlot(index_buffer, 12));
  if (unlock_index != nullptr) unlock_index(index_buffer);
  const auto release_index = reinterpret_cast<ComReleaseFn>(
      VtableSlot(index_buffer, 2));
  if (release_index != nullptr) release_index(index_buffer);
  if (captured != 0) {
    SiglusSetLookupDiag(fushi_voice_hook::kLookupDiagSensorInstalled |
                        fushi_voice_hook::kLookupDiagGeometryObserved);
  }
  LeaveCriticalSection(&g_text_cs);
}

// 个别 Siglus 版本把字体顶点缓冲放在不可读的 D3DPOOL_DEFAULT 中；这时
// Draw* hook 能看到绘制调用，但 Lock(READONLY) 拿不到字形矩形。仍然不能让
// 点击穿透到“下一句”：只在已有精确台词、且点击落在底部台词带时，用行宽估算
// 一个字符命中框。真实 D3D 几何一旦可用，SubmitSiglusLookupAt 优先走上面的
// 精确矩形，这个兜底不会参与普通命中。
bool BuildSiglusFallbackGlyphLocked(int32_t client_x, int32_t client_y,
                                    int32_t client_width,
                                    int32_t client_height,
                                    SiglusLookupGlyph* glyph,
                                    int32_t* view_width,
                                    int32_t* view_height) {
  if (glyph == nullptr || view_width == nullptr || view_height == nullptr ||
      client_width < 240 || client_height < 180 ||
      g_siglus_lookup.char_count == 0 ||
      g_siglus_lookup.glyph_count != 0 || g_siglus_lookup.valid) {
    return false;
  }

  // Siglus 的对白区位于客户区下半部。上半部留给标题/场景点击，避免兜底
  // 把菜单或背景点击也吃掉。
  if (client_y < client_height * 62 / 100 ||
      client_y > client_height * 96 / 100 || client_x < client_width * 8 / 100 ||
      client_x > client_width * 92 / 100) {
    return false;
  }

  uint32_t visible_count = 0;
  uint32_t first_visible = g_siglus_lookup.char_count;
  for (uint32_t i = 0; i < g_siglus_lookup.char_count; ++i) {
    if (!IsSiglusLookupWhitespace(g_siglus_lookup.line[i])) {
      if (first_visible == g_siglus_lookup.char_count) first_visible = i;
      ++visible_count;
    }
  }
  if (visible_count == 0 || first_visible == g_siglus_lookup.char_count) {
    return false;
  }

  const int32_t available_width = client_width * 84 / 100;
  const int32_t estimated_width =
      (std::min)(available_width,
                 (std::max)(240, static_cast<int32_t>(visible_count) * 48));
  const int32_t line_left = (client_width - estimated_width) / 2;
  if (client_x < line_left || client_x >= line_left + estimated_width) {
    return false;
  }
  const int32_t glyph_width =
      (std::max)(16, estimated_width / static_cast<int32_t>(visible_count));
  uint32_t visible_index = static_cast<uint32_t>(
      (client_x - line_left) / glyph_width);
  if (visible_index >= visible_count) visible_index = visible_count - 1;
  const uint32_t chosen_visible_index = visible_index;

  uint32_t char_index = first_visible;
  for (uint32_t i = first_visible; i < g_siglus_lookup.char_count; ++i) {
    if (IsSiglusLookupWhitespace(g_siglus_lookup.line[i])) continue;
    if (visible_index == 0) {
      char_index = i;
      break;
    }
    --visible_index;
  }

  *view_width = client_width;
  *view_height = client_height;
  glyph->char_index = char_index;
  glyph->x = line_left +
             static_cast<int32_t>(chosen_visible_index * glyph_width);
  glyph->y = client_height * 70 / 100;
  glyph->width = glyph_width;
  glyph->height = (std::max)(32, client_height * 18 / 100);
  return true;
}

bool SubmitSiglusLookupAt(HWND window, int32_t client_x, int32_t client_y) {
  if (!IsSiglusEngine() || g_header == nullptr || SiglusLookupEnabledNow() == 0 ||
      !g_text_cs_ready) {
    return false;
  }
  RECT client = {};
  if (window == nullptr || !GetClientRect(window, &client)) return false;
  const int32_t client_width = client.right - client.left;
  const int32_t client_height = client.bottom - client.top;
  if (client_width <= 0 || client_height <= 0) return false;

  EnterCriticalSection(&g_text_cs);
  bool submitted = false;
  if (g_siglus_lookup.char_count > 0 &&
      GetTickCount64() - g_siglus_lookup.text_tick <= 10000) {
    const D3DVIEWPORT9 viewport = g_siglus_lookup.viewport;
    int32_t view_width = viewport.Width != 0 ? viewport.Width : client_width;
    int32_t view_height = viewport.Height != 0 ? viewport.Height : client_height;
    const int32_t view_x = viewport.Width != 0
                               ? client_x * view_width / client_width
                               : client_x;
    const int32_t view_y = viewport.Height != 0
                               ? client_y * view_height / client_height
                               : client_y;
    const SiglusLookupGlyph* hit = nullptr;
    SiglusLookupGlyph fallback;
    for (uint32_t i = 0; i < g_siglus_lookup.glyph_count; ++i) {
      const SiglusLookupGlyph& glyph = g_siglus_lookup.glyphs[i];
      if (view_x >= glyph.x && view_x < glyph.x + glyph.width &&
          view_y >= glyph.y && view_y < glyph.y + glyph.height) {
        hit = &glyph;
        break;
      }
    }
    if (hit == nullptr &&
        BuildSiglusFallbackGlyphLocked(client_x, client_y, client_width,
                                       client_height, &fallback, &view_width,
                                       &view_height)) {
      hit = &fallback;
    }
    if (hit != nullptr) {
      static uint8_t encoded_line[fushi_voice_hook::kLookupLineBytes];
      const uint32_t line_bytes = SiglusEncodeLineUtf8(
          g_siglus_lookup.line, encoded_line,
          fushi_voice_hook::kLookupLineBytes);
      fushi_voice_hook::LookupHitSlot* slot =
          fushi_voice_hook::LookupHitOf(g_header);
      if (slot != nullptr && line_bytes > 0) {
        const uint64_t sequence = static_cast<uint64_t>(
            InterlockedIncrement64(reinterpret_cast<volatile LONGLONG*>(
                &g_header->lookup_hit_count)));
        SiglusWriteSharedU64(&slot->seq, 0);
        slot->char_index = hit->char_index;
        slot->char_count = g_siglus_lookup.char_count;
        slot->glyph_x = hit->x;
        slot->glyph_y = hit->y;
        slot->glyph_w = hit->width;
        slot->glyph_h = hit->height;
        slot->view_w = view_width;
        slot->view_h = view_height;
        slot->flags = fushi_voice_hook::kLookupHitFlagSubmit;
        slot->line_bytes = line_bytes;
        memcpy(slot->line_utf8, encoded_line, line_bytes);
        SiglusWriteSharedU64(&slot->seq, sequence);
        SiglusSetLookupDiag(fushi_voice_hook::kLookupDiagHitSubmitted);
        submitted = true;
      }
    }
  }
  LeaveCriticalSection(&g_text_cs);
  return submitted;
}

LRESULT WINAPI Detour_DispatchMessageW(const MSG* message) {
  if (message != nullptr &&
      message->message == WM_LBUTTONDOWN &&
      SubmitSiglusLookupAt(message->hwnd, GET_X_LPARAM(message->lParam),
                            GET_Y_LPARAM(message->lParam))) {
    // The lookup click belongs to the dictionary surface; do not also advance
    // the game line underneath it.
    InterlockedExchange64(&g_siglus_lookup_consumed_up, 1);
    return 0;
  }
  if (message != nullptr && message->message == WM_LBUTTONUP &&
      InterlockedExchange64(&g_siglus_lookup_consumed_up, 0) != 0) {
    return 0;
  }
  return g_orig_DispatchMessageW(message);
}

LRESULT WINAPI Detour_DispatchMessageA(const MSG* message) {
  if (message != nullptr &&
      message->message == WM_LBUTTONDOWN &&
      SubmitSiglusLookupAt(message->hwnd, GET_X_LPARAM(message->lParam),
                            GET_Y_LPARAM(message->lParam))) {
    InterlockedExchange64(&g_siglus_lookup_consumed_up, 1);
    return 0;
  }
  if (message != nullptr && message->message == WM_LBUTTONUP &&
      InterlockedExchange64(&g_siglus_lookup_consumed_up, 0) != 0) {
    return 0;
  }
  return g_orig_DispatchMessageA(message);
}

HRESULT STDMETHODCALLTYPE Detour_DrawPrimitiveUP(IDirect3DDevice9* device,
                                                 D3DPRIMITIVETYPE type,
                                                 UINT primitive_count,
                                                 const void* vertex_data,
                                                 UINT stride) {
  CaptureSiglusD3DPrimitiveUP(device, type, primitive_count, vertex_data,
                              stride);
  return g_orig_DrawPrimitiveUP(device, type, primitive_count, vertex_data,
                                stride);
}

HRESULT STDMETHODCALLTYPE Detour_DrawIndexedPrimitiveUP(
    IDirect3DDevice9* device, D3DPRIMITIVETYPE type, UINT min_vertex_index,
    UINT num_vertices, UINT primitive_count, const void* index_data,
    D3DFORMAT index_format, const void* vertex_data, UINT stride) {
  CaptureSiglusD3DIndexedPrimitiveUP(
      device, type, min_vertex_index, num_vertices, primitive_count,
      index_data, index_format, vertex_data, stride);
  return g_orig_DrawIndexedPrimitiveUP(
      device, type, min_vertex_index, num_vertices, primitive_count, index_data,
      index_format, vertex_data, stride);
}

HRESULT STDMETHODCALLTYPE Detour_DrawPrimitive(IDirect3DDevice9* device,
                                               D3DPRIMITIVETYPE type,
                                               UINT start_vertex,
                                               UINT primitive_count) {
  CaptureSiglusD3DPrimitive(device, type, start_vertex, primitive_count);
  return g_orig_DrawPrimitive(device, type, start_vertex, primitive_count);
}

HRESULT STDMETHODCALLTYPE Detour_DrawIndexedPrimitive(
    IDirect3DDevice9* device, D3DPRIMITIVETYPE type, INT base_vertex_index,
    UINT min_vertex_index, UINT num_vertices, UINT start_index,
    UINT primitive_count) {
  CaptureSiglusD3DIndexedPrimitive(
      device, type, base_vertex_index, min_vertex_index, num_vertices,
      start_index, primitive_count);
  return g_orig_DrawIndexedPrimitive(
      device, type, base_vertex_index, min_vertex_index, num_vertices,
      start_index, primitive_count);
}

void InstallSiglusD3DDevice(IDirect3DDevice9* device) {
  if (device == nullptr || !IsSiglusEngine()) return;
  HookFn(VtableSlot(device, 81),
         reinterpret_cast<void*>(&Detour_DrawPrimitive),
         reinterpret_cast<void**>(&g_orig_DrawPrimitive));
  HookFn(VtableSlot(device, 82),
         reinterpret_cast<void*>(&Detour_DrawIndexedPrimitive),
         reinterpret_cast<void**>(&g_orig_DrawIndexedPrimitive));
  HookFn(VtableSlot(device, 83),
         reinterpret_cast<void*>(&Detour_DrawPrimitiveUP),
         reinterpret_cast<void**>(&g_orig_DrawPrimitiveUP));
  HookFn(VtableSlot(device, 84),
         reinterpret_cast<void*>(&Detour_DrawIndexedPrimitiveUP),
         reinterpret_cast<void**>(&g_orig_DrawIndexedPrimitiveUP));
  if ((g_orig_DrawPrimitive != nullptr ||
       g_orig_DrawIndexedPrimitive != nullptr ||
       g_orig_DrawPrimitiveUP != nullptr ||
       g_orig_DrawIndexedPrimitiveUP != nullptr) &&
      g_header != nullptr) {
    SiglusSetLookupDiag(fushi_voice_hook::kLookupDiagSensorInstalled);
  }
}

HRESULT STDMETHODCALLTYPE Detour_D3D9CreateDevice(
    IDirect3D9* self, UINT adapter, D3DDEVTYPE type, HWND focus_window,
    DWORD behavior_flags, D3DPRESENT_PARAMETERS* parameters,
    IDirect3DDevice9** device) {
  const HRESULT result = g_orig_D3D9CreateDevice(
      self, adapter, type, focus_window, behavior_flags, parameters, device);
  if (SUCCEEDED(result) && device != nullptr && *device != nullptr) {
    InstallSiglusD3DDevice(*device);
  }
  return result;
}

IDirect3D9* WINAPI Detour_Direct3DCreate9(UINT sdk_version) {
  IDirect3D9* result = g_orig_Direct3DCreate9(sdk_version);
  if (result != nullptr) {
    HookFn(VtableSlot(result, 16),
           reinterpret_cast<void*>(&Detour_D3D9CreateDevice),
           reinterpret_cast<void**>(&g_orig_D3D9CreateDevice));
  }
  return result;
}

void TryHookSiglusD3D9() {
  if (!IsSiglusEngine()) return;
  HMODULE d3d = GetModuleHandleW(L"d3d9.dll");
  if (d3d == nullptr) d3d = LoadLibraryW(L"d3d9.dll");
  if (d3d == nullptr) return;
  HookFn(reinterpret_cast<void*>(GetProcAddress(d3d, "Direct3DCreate9")),
         reinterpret_cast<void*>(&Detour_Direct3DCreate9),
         reinterpret_cast<void**>(&g_orig_Direct3DCreate9));
  if (g_orig_Direct3DCreate9 != nullptr && g_header != nullptr) {
    SiglusSetLookupDiag(fushi_voice_hook::kLookupDiagSensorInstalled);
  }
}

// -- detour: GetGlyphOutlineW（gdi32，galgame 最经典逐字形文本 hook）--
// 多数 GDI VN 逐字渲染字形经此。uChar 是当前渲染字符：逐字累积成行，用**时间空隙**判行界
// （距上次 >120ms 视为新行——先 flush 已累积再重开累积），缓冲将满时强制 flush。
// GGO_GLYPH_INDEX 时 uChar 是字形索引而非字符，跳过累积（否则累出乱码 id）。
DWORD WINAPI Detour_GetGlyphOutlineW(HDC hdc, UINT uChar, UINT uFormat,
                                     LPGLYPHMETRICS lpgm, DWORD cbBuffer,
                                     LPVOID lpvBuffer, const MAT2* lpmat2) {
  if (g_capture_enabled && g_text_cs_ready &&
      (uFormat & GGO_GLYPH_INDEX) == 0) {
    EnterCriticalSection(&g_text_cs);
    const ULONGLONG now = GetTickCount64();
    if (g_glyph_len > 0 && (now - g_glyph_last_tick) > 120) {
      FlushGlyphAccumLocked();  // 时间空隙 -> 行界
    }
    if (uChar != 0 && uChar <= 0xFFFFu) {
      if (g_glyph_len >= kMaxLineChars) {
        FlushGlyphAccumLocked();  // 缓冲将满 -> 强制 flush
      }
      g_glyph_buf[g_glyph_len++] = static_cast<wchar_t>(uChar);
    }
    g_glyph_last_tick = now;
    LeaveCriticalSection(&g_text_cs);
  }
  return g_orig_GetGlyphOutlineW(hdc, uChar, uFormat, lpgm, cbBuffer, lpvBuffer,
                                 lpmat2);
}

// -- detour: ExtTextOutW / TextOutW（gdi32）+ DrawTextW（user32）--
// lpString + 字符数即整串台词，直接成一行（补 GetGlyphOutline 抓不到的整串渲染）。先冲掉逐字
// 累积再写整串，避免行被拆。ETO_GLYPH_INDEX（字形索引非字符）/ DT_CALCRECT（只测量不渲染）等
// 非真实文本内容跳过。
BOOL WINAPI Detour_ExtTextOutW(HDC hdc, int x, int y, UINT options,
                               const RECT* lprect, LPCWSTR lpString, UINT c,
                               const INT* lpDx) {
  if (g_capture_enabled && g_text_cs_ready && lpString != nullptr && c > 0 &&
      (options & ETO_GLYPH_INDEX) == 0) {
    EnterCriticalSection(&g_text_cs);
    FlushGlyphAccumLocked();
    FlushLineLocked(lpString, static_cast<int>(c));
    LeaveCriticalSection(&g_text_cs);
  }
  return g_orig_ExtTextOutW(hdc, x, y, options, lprect, lpString, c, lpDx);
}

BOOL WINAPI Detour_TextOutW(HDC hdc, int x, int y, LPCWSTR lpString, int c) {
  if (g_capture_enabled && g_text_cs_ready && lpString != nullptr && c > 0) {
    EnterCriticalSection(&g_text_cs);
    FlushGlyphAccumLocked();
    FlushLineLocked(lpString, c);
    LeaveCriticalSection(&g_text_cs);
  }
  return g_orig_TextOutW(hdc, x, y, lpString, c);
}

int WINAPI Detour_DrawTextW(HDC hdc, LPCWSTR lpchText, int cchText, LPRECT lprc,
                            UINT format) {
  if (g_capture_enabled && g_text_cs_ready && lpchText != nullptr &&
      (format & DT_CALCRECT) == 0) {
    int len = cchText;
    if (len < 0) {
      // cchText<0：以 NUL 结尾；手数长度（避免依赖 wcsnlen）。
      len = 0;
      while (len < kMaxLineChars && lpchText[len] != 0) {
        len++;
      }
    }
    if (len > 0) {
      EnterCriticalSection(&g_text_cs);
      FlushGlyphAccumLocked();
      FlushLineLocked(lpchText, len);
      LeaveCriticalSection(&g_text_cs);
    }
  }
  return g_orig_DrawTextW(hdc, lpchText, cchText, lprc, format);
}

// 装文本渲染 hook：gdi32 的 GetGlyphOutlineW/ExtTextOutW/TextOutW + user32 的 DrawTextW
// （GetModuleHandle 取已加载模块，未加载则 LoadLibrary 强制解析导出）。装在 XAudio2/
// DirectSound hook 之后（见 HookWorker）。
void TryHookTextRender() {
  TryHookSiglusD3D9();
  HMODULE gdi = GetModuleHandleW(L"gdi32.dll");
  if (gdi == nullptr) {
    gdi = LoadLibraryW(L"gdi32.dll");
  }
  if (gdi != nullptr) {
    HookFn(reinterpret_cast<void*>(GetProcAddress(gdi, "GetGlyphOutlineW")),
           reinterpret_cast<void*>(&Detour_GetGlyphOutlineW),
           reinterpret_cast<void**>(&g_orig_GetGlyphOutlineW));
    HookFn(reinterpret_cast<void*>(GetProcAddress(gdi, "ExtTextOutW")),
           reinterpret_cast<void*>(&Detour_ExtTextOutW),
           reinterpret_cast<void**>(&g_orig_ExtTextOutW));
    HookFn(reinterpret_cast<void*>(GetProcAddress(gdi, "TextOutW")),
           reinterpret_cast<void*>(&Detour_TextOutW),
           reinterpret_cast<void**>(&g_orig_TextOutW));
  }
  HMODULE usr = GetModuleHandleW(L"user32.dll");
  if (usr == nullptr) {
    usr = LoadLibraryW(L"user32.dll");
  }
  if (usr != nullptr) {
    HookFn(reinterpret_cast<void*>(GetProcAddress(usr, "DrawTextW")),
           reinterpret_cast<void*>(&Detour_DrawTextW),
           reinterpret_cast<void**>(&g_orig_DrawTextW));
    HookFn(reinterpret_cast<void*>(GetProcAddress(usr, "DispatchMessageW")),
           reinterpret_cast<void*>(&Detour_DispatchMessageW),
           reinterpret_cast<void**>(&g_orig_DispatchMessageW));
    HookFn(reinterpret_cast<void*>(GetProcAddress(usr, "DispatchMessageA")),
           reinterpret_cast<void*>(&Detour_DispatchMessageA),
           reinterpret_cast<void**>(&g_orig_DispatchMessageA));
  }
}
