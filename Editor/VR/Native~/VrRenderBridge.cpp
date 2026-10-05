// Windows x64 bridge for UnityRenderingEventAndData. OpenVR 1.16.8 ABI.
// The callback contains no managed code and can finish after a Unity domain reload.
#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <Unknwn.h>
#include <d3d11.h>
#include <atomic>
#include <cstdint>
#include <new>

struct Matrix34 { float m[12]; };
struct Pose { Matrix34 matrix; float velocity[3], angularVelocity[3]; int result; bool valid, connected; };
struct Texture { void* handle; int type, colorSpace; Matrix34 pose; };
struct Bounds { float uMin, vMin, uMax, vMax; };
static_assert(sizeof(void*) == 8 && sizeof(Pose) == 80 && sizeof(Texture) == 64, "OpenVR Windows x64 ABI");

struct Functions {
    int (__stdcall* wait)(Pose*, uint32_t, Pose*, uint32_t);
    int (__stdcall* submit)(int, const Texture*, const Bounds*, int);
    void (__stdcall* handoff)();
    bool (__stdcall* canRender)();
    void (__cdecl* shutdown)();
};
enum Phase { Idle, Waiting, Ready, Submitting, Submitted };
enum Event { Acquire = 1, Present = 2 };
static constexpr int NoFocus = 101;
static std::atomic<bool> active{false};

static void FlushTextureCommands(IUnknown* resource) {
    ID3D11Texture2D* texture{};
    if (!resource || FAILED(resource->QueryInterface(__uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&texture)))) return;
    ID3D11Device* device{}; ID3D11DeviceContext* graphics{};
    texture->GetDevice(&device); device->GetImmediateContext(&graphics);
    // Unity cannot present/flush its window while this callback waits for the
    // next VR frame. Issue pending GPU commands before handing off to SteamVR.
    // Flush submits work; it does not wait for GPU completion or read pixels.
    graphics->Flush();
    graphics->Release(); device->Release(); texture->Release();
}

struct Context {
    Functions functions;
    HMODULE runtime;
    std::atomic<int> references{1}, phase{Idle}, error{0};
    std::atomic<bool> closing{false};
    IUnknown* resources[2]{};
    Matrix34 pose{};
    Bounds bounds{};
    Context(const Functions& f, HMODULE module) : functions(f), runtime(module) {}
};

static void Release(Context* context) {
    if (context->references.fetch_sub(1) != 1) return;
    // A queued/executing event owns its own reference. Shutdown and texture
    // release cannot overtake an OpenVR call, even if C# has already unloaded.
    context->functions.shutdown();
    for (auto resource : context->resources) if (resource) resource->Release();
    if (context->runtime) FreeLibrary(context->runtime);
    delete context;
    active.store(false);
}

static void __stdcall RenderEvent(int event, void* data) {
    auto context = static_cast<Context*>(data);
    int error = NoFocus;
    int nextPhase = Submitted;
    if (!context->closing.load()) {
        if (event == Acquire) {
            // A dashboard may take input focus while scene rendering remains
            // enabled. Only another scene owning the compositor skips the wait.
            if (context->functions.canRender()) {
                Pose ignored{};
                error = context->functions.wait(&ignored, 1, nullptr, 0);
            }
            nextPhase = Ready;
        } else if (event == Present) {
            Texture texture{context->resources[0], 0, 0, context->pose};
            int left = context->functions.submit(0, &texture, &context->bounds, 8);
            texture.handle = context->resources[1];
            int right = context->functions.submit(1, &texture, &context->bounds, 8);
            // Losing focus for one eye must not hide a real failure for the other.
            error = left != 0 && left != NoFocus ? left : right != 0 ? right : left;
            FlushTextureCommands(context->resources[0]);
            context->functions.handoff();
            // Keep frame pacing on the render thread. Waiting for a managed
            // Editor tick to enqueue Acquire adds an avoidable gap per frame.
            // Preserve a real submission failure instead of overwriting it.
            if ((error == 0 || error == NoFocus) && !context->closing.load()) {
                context->phase.store(Waiting);
                error = NoFocus;
                if (context->functions.canRender()) {
                    Pose ignored{};
                    error = context->functions.wait(&ignored, 1, nullptr, 0);
                }
                nextPhase = Ready;
            }
        }
    }
    context->error.store(error);
    context->phase.store(nextPhase);
    Release(context);
}

#define API extern "C" __declspec(dllexport)
API int mp_version() { return 1; }
API int mp_active() { return active.load() ? 1 : 0; }
API void* mp_callback() {
    // Unity's queue can outlive an AppDomain. Keep this small bridge loaded for
    // the Editor process, without keeping OpenVR or textures alive when idle.
    HMODULE module{};
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
        reinterpret_cast<LPCWSTR>(&RenderEvent), &module)) return nullptr;
    return reinterpret_cast<void*>(&RenderEvent);
}
API Context* mp_create(const Functions* functions) {
    if (!functions || !functions->wait || !functions->submit || !functions->handoff ||
        !functions->canRender || !functions->shutdown || active.exchange(true)) return nullptr;
    HMODULE module{};
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
        reinterpret_cast<LPCWSTR>(functions->shutdown), &module)) { active.store(false); return nullptr; }
    auto context = new (std::nothrow) Context(*functions, module);
    if (!context) { FreeLibrary(module); active.store(false); }
    return context;
}
API void mp_close(Context* context) {
    if (context && !context->closing.exchange(true)) Release(context);
}
API int mp_status(Context* context, int* error) {
    int phase = context->phase.load();
    *error = context->error.load();
    return phase;
}
API int mp_targets(Context* context, IUnknown* left, IUnknown* right) {
    if (!left || !right || context->phase.load() != Idle || context->closing.load()) return 0;
    left->AddRef(); right->AddRef();
    for (auto resource : context->resources) if (resource) resource->Release();
    context->resources[0] = left; context->resources[1] = right;
    return 1;
}
API int mp_acquire(Context* context) {
    int phase = context->phase.load();
    if (context->closing.load() || (phase != Idle && phase != Submitted)) return 0;
    context->references.fetch_add(1);
    context->phase.store(Waiting);
    return 1;
}
API int mp_submit(Context* context, const Matrix34* pose, const Bounds* bounds) {
    if (context->closing.load() || context->phase.load() != Ready || !context->resources[0]) return 0;
    context->pose = *pose; context->bounds = *bounds;
    context->references.fetch_add(1);
    context->phase.store(Submitting);
    return 1;
}
API void mp_skip(Context* context) {
    if (context->phase.load() == Ready) context->phase.store(Idle);
}
// Only called if Unity throws before accepting an event into its queue.
API void mp_abort_event(Context* context, int event) {
    context->phase.store(event == Acquire ? Idle : Ready);
    Release(context);
}
