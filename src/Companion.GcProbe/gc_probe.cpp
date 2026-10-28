#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <atomic>
#include <cstdint>

// Unity Mono profiler API v3: native callbacks must never enter managed code,
// allocate, acquire a GC-sensitive lock, or retain managed object pointers.
struct Event { int64_t ticks; uint32_t thread; int32_t phase; uint32_t generation; int32_t serial; };
static constexpr uint32_t Capacity = 65536;
static Event events[Capacity];
static std::atomic<uint32_t> count{0}, writers{0};
static std::atomic<uint64_t> epoch{0};
static std::atomic<bool> enabled{false};
static void* handle = nullptr;
using GcCallback = void (*)(void*, int32_t, uint32_t, int32_t);
using Create = void* (*)(void*);
using SetGc = void (*)(void*, GcCallback);
static void on_gc(void*, int32_t phase, uint32_t generation, int32_t serial) noexcept {
    const auto session = epoch.load(std::memory_order_acquire);
    if (!enabled.load(std::memory_order_acquire)) return;
    writers.fetch_add(1, std::memory_order_acq_rel);
    if (enabled.load(std::memory_order_acquire) && epoch.load(std::memory_order_acquire) == session) {
        const auto slot = count.fetch_add(1, std::memory_order_relaxed);
        if (slot < Capacity) {
            LARGE_INTEGER now{}; QueryPerformanceCounter(&now);
            events[slot] = {now.QuadPart, GetCurrentThreadId(), phase, generation, serial};
        }
    }
    writers.fetch_sub(1, std::memory_order_release);
}
extern "C" __declspec(dllexport) int vpc_gc_init() noexcept {
    if (handle) return 1;
    HMODULE mono = GetModuleHandleW(L"mono-2.0-bdwgc.dll");
    if (!mono) return 0;
    auto create = reinterpret_cast<Create>(GetProcAddress(mono, "mono_profiler_create"));
    auto set_gc = reinterpret_cast<SetGc>(GetProcAddress(mono, "mono_profiler_set_gc_event_callback"));
    if (!create || !set_gc) return 0;
    HMODULE self{};
    // Mono can invoke a callback already in flight after it is removed. Pin the
    // small helper for process lifetime; inactive captures take only an atomic read.
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
            reinterpret_cast<LPCWSTR>(&vpc_gc_init), &self)) return 0;
    handle = create(nullptr);
    if (!handle) return 0;
    set_gc(handle, on_gc);
    return 1;
}
extern "C" __declspec(dllexport) int vpc_gc_begin() noexcept {
    if (!handle || enabled.load() || writers.load()) return 0;
    epoch.fetch_add(1, std::memory_order_acq_rel);
    count.store(0, std::memory_order_release);
    enabled.store(true, std::memory_order_release);
    return 1;
}
extern "C" __declspec(dllexport) int vpc_gc_end() noexcept {
    enabled.store(false, std::memory_order_release);
    return writers.load(std::memory_order_acquire) == 0 ? 1 : 0;
}
extern "C" __declspec(dllexport) uint32_t vpc_gc_count() noexcept { return count.load(std::memory_order_acquire); }
extern "C" __declspec(dllexport) int vpc_gc_read(uint32_t index, Event* value) noexcept {
    if (!value || enabled.load() || writers.load() || index >= count.load() || index >= Capacity) return 0;
    *value = events[index]; return 1;
}
