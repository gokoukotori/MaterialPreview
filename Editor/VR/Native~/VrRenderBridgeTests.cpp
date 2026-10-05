// Runs the production bridge against fake OpenVR functions, without SteamVR.
#include "VrRenderBridge.cpp"
#include <cstdio>
#include <cstdlib>
#include <thread>
#include <vector>
#define CHECK(value) do { if (!(value)) { std::fprintf(stderr, "Failed: %s:%d: %s\n", __FILE__, __LINE__, #value); std::exit(1); } } while (false)

static std::vector<int> calls;
static DWORD renderThread;
static int shutdowns, waits;
static int leftError, rightError;
static bool sceneVisible = true;
static ID3D11DeviceContext* testGraphics;
static ID3D11Query* testCompletion;
static int flushes;
static void (__stdcall* originalFlush)(ID3D11DeviceContext*);
static void __stdcall ObserveFlush(ID3D11DeviceContext* graphics) {
    CHECK(GetCurrentThreadId() == renderThread); ++flushes; originalFlush(graphics);
}
static HANDLE waitEntered = CreateEventW(nullptr, FALSE, FALSE, nullptr);
static HANDLE waitResume = CreateEventW(nullptr, TRUE, TRUE, nullptr);
static int __stdcall Wait(Pose*, uint32_t count, Pose*, uint32_t games) {
    CHECK(GetCurrentThreadId() == renderThread && count == 1 && games == 0);
    ++waits; calls.push_back(1); SetEvent(waitEntered);
    CHECK(WaitForSingleObject(waitResume, 5000) == WAIT_OBJECT_0);
    return 0;
}
static int __stdcall Submit(int eye, const Texture* texture, const Bounds* bounds, int flags) {
    CHECK(GetCurrentThreadId() == renderThread && texture->handle && texture->type == 0 && flags == 8);
    CHECK(texture->pose.m[3] == 42 && bounds->vMin == 1 && bounds->vMax == 0);
    if (eye == 1 && testGraphics) testGraphics->End(testCompletion);
    calls.push_back(2 + eye); return eye == 0 ? leftError : rightError;
}
static void __stdcall Handoff() {
    CHECK(GetCurrentThreadId() == renderThread);
    if (testGraphics) {
        CHECK(flushes == 1);
        auto deadline = GetTickCount64() + 1000;
        HRESULT result;
        // DONOTFLUSH proves the bridge issued the commands before handoff.
        do { result = testGraphics->GetData(testCompletion, nullptr, 0, D3D11_ASYNC_GETDATA_DONOTFLUSH); }
        while (result == S_FALSE && GetTickCount64() < deadline);
        CHECK(result == S_OK);
    }
    calls.push_back(4);
}
static bool __stdcall CanRender() { CHECK(GetCurrentThreadId() == renderThread); return sceneVisible; }
static void __cdecl Shutdown() { ++shutdowns; }
static Functions functions{Wait, Submit, Handoff, CanRender, Shutdown};

struct Resource : IUnknown {
    std::atomic<ULONG> references{1};
    HRESULT __stdcall QueryInterface(REFIID, void**) override { return E_NOINTERFACE; }
    ULONG __stdcall AddRef() override { return ++references; }
    ULONG __stdcall Release() override { return --references; }
};
struct Renderer {
    HANDLE wake = CreateEventW(nullptr, FALSE, FALSE, nullptr), done = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    std::atomic<int> event{0}; Context* context{};
    std::thread worker;
    Renderer() : worker([this] {
        renderThread = GetCurrentThreadId();
        for (;;) {
            CHECK(WaitForSingleObject(wake, 5000) == WAIT_OBJECT_0);
            int next = event.load(); if (next < 0) return;
            RenderEvent(next, context); SetEvent(done);
        }
    }) {}
    void Queue(int next, Context* value) { context = value; event.store(next); SetEvent(wake); }
    void JoinEvent() { CHECK(WaitForSingleObject(done, 5000) == WAIT_OBJECT_0); }
    void Execute(int next, Context* value) { Queue(next, value); JoinEvent(); }
    ~Renderer() { event.store(-1); SetEvent(wake); worker.join(); CloseHandle(wake); CloseHandle(done); }
};
int main() {
    Renderer renderer; Resource left, right;
    auto context = mp_create(&functions);
    CHECK(context && mp_active() && !mp_create(&functions));
    CHECK(mp_targets(context, &left, &right) == 1 && left.references == 2 && right.references == 2);
    CHECK(mp_acquire(context) && !mp_acquire(context) && waits == 0);
    int error = -1; CHECK(mp_status(context, &error) == Waiting);
    renderer.Execute(Acquire, context);
    CHECK(mp_status(context, &error) == Ready && error == 0 && waits == 1);
    Matrix34 pose{}; pose.m[3] = 42; Bounds bounds{0, 1, 1, 0};
    CHECK(mp_submit(context, &pose, &bounds) && !mp_acquire(context));
    renderer.Execute(Present, context);
    CHECK(mp_status(context, &error) == Ready && error == 0 && waits == 2);
    CHECK(!mp_acquire(context));
    CHECK(calls == std::vector<int>({1, 2, 3, 4, 1}));
    leftError = NoFocus; rightError = 102;
    CHECK(mp_submit(context, &pose, &bounds)); renderer.Execute(Present, context);
    CHECK(mp_status(context, &error) == Submitted && error == 102);
    CHECK(waits == 2); // A submission failure must not be hidden by another wait.
    leftError = rightError = 0;
    sceneVisible = false;
    CHECK(mp_acquire(context)); renderer.Execute(Acquire, context);
    CHECK(mp_status(context, &error) == Ready && error == NoFocus && waits == 2);
    mp_skip(context); CHECK(mp_status(context, &error) == Idle);
    sceneVisible = true;
    CHECK(mp_acquire(context)); renderer.Execute(Acquire, context);
    mp_skip(context); CHECK(mp_status(context, &error) == Idle);
    mp_close(context);
    CHECK(shutdowns == 1 && !mp_active() && left.references == 1 && right.references == 1);
    std::puts("PASS: render-thread ordering, pose/bounds, scene visibility, skipped frames");

    context = mp_create(&functions); CHECK(context && mp_acquire(context));
    mp_close(context); CHECK(shutdowns == 1 && mp_active());
    renderer.Execute(Acquire, context);
    CHECK(shutdowns == 2 && !mp_active());
    std::puts("PASS: close before queued callback retains the native context");

    context = mp_create(&functions); CHECK(context && mp_targets(context, &left, &right));
    ResetEvent(waitEntered); ResetEvent(waitResume);
    CHECK(mp_acquire(context)); renderer.Queue(Acquire, context);
    CHECK(WaitForSingleObject(waitEntered, 5000) == WAIT_OBJECT_0);
    mp_close(context); CHECK(shutdowns == 2 && mp_active() && left.references == 2);
    SetEvent(waitResume); renderer.JoinEvent();
    CHECK(shutdowns == 3 && !mp_active() && left.references == 1);
    std::puts("PASS: close during WaitGetPoses never unloads an executing runtime");

    context = mp_create(&functions); CHECK(context && mp_targets(context, &left, &right));
    CHECK(mp_acquire(context)); renderer.Execute(Acquire, context);
    ResetEvent(waitEntered); ResetEvent(waitResume);
    CHECK(mp_submit(context, &pose, &bounds)); renderer.Queue(Present, context);
    CHECK(WaitForSingleObject(waitEntered, 5000) == WAIT_OBJECT_0);
    CHECK(mp_status(context, &error) == Waiting && !mp_acquire(context));
    mp_close(context); CHECK(shutdowns == 3 && mp_active() && left.references == 2);
    SetEvent(waitResume); renderer.JoinEvent();
    CHECK(shutdowns == 4 && !mp_active() && left.references == 1);
    std::puts("PASS: next-frame wait starts in the submission callback and survives owner disposal");

    context = mp_create(&functions); CHECK(context && mp_targets(context, &left, &right));
    CHECK(mp_acquire(context)); renderer.Execute(Acquire, context);
    CHECK(mp_submit(context, &pose, &bounds));
    mp_close(context); CHECK(shutdowns == 4 && left.references == 2);
    renderer.Execute(Present, context);
    CHECK(shutdowns == 5 && !mp_active() && left.references == 1);
    std::puts("PASS: queued submissions keep textures alive across owner disposal");

    context = mp_create(&functions); CHECK(context && mp_acquire(context));
    mp_abort_event(context, Acquire); CHECK(mp_status(context, &error) == Idle);
    mp_close(context); CHECK(shutdowns == 6 && !mp_active());

    // A real D3D11 context catches a missing flush before a blocking handoff.
    // WARP needs neither a headset nor a physical GPU / SteamVR session.
    ID3D11Device* device{};
    CHECK(SUCCEEDED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, 0, nullptr, 0,
        D3D11_SDK_VERSION, &device, nullptr, &testGraphics)));
    ID3D11Texture2D* textures[2]{};
    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = desc.Height = desc.MipLevels = desc.ArraySize = desc.SampleDesc.Count = 1;
    desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM; desc.BindFlags = D3D11_BIND_RENDER_TARGET;
    CHECK(SUCCEEDED(device->CreateTexture2D(&desc, nullptr, &textures[0])));
    CHECK(SUCCEEDED(device->CreateTexture2D(&desc, nullptr, &textures[1])));
    D3D11_QUERY_DESC query{D3D11_QUERY_EVENT, 0};
    CHECK(SUCCEEDED(device->CreateQuery(&query, &testCompletion)));
    // The Windows D3D11 COM ABI has 115 ID3D11DeviceContext entries, with
    // Flush at index 111. Observe only our test-owned WARP context's call;
    // still invoke its real Flush and restore the vtable before releasing it.
    auto originalVtable = *reinterpret_cast<void***>(testGraphics);
    void* observedVtable[115];
    for (int i = 0; i < 115; ++i) observedVtable[i] = originalVtable[i];
    originalFlush = reinterpret_cast<decltype(originalFlush)>(originalVtable[111]);
    observedVtable[111] = reinterpret_cast<void*>(&ObserveFlush);
    *reinterpret_cast<void***>(testGraphics) = observedVtable;
    context = mp_create(&functions); CHECK(context && mp_targets(context, textures[0], textures[1]));
    CHECK(mp_acquire(context)); renderer.Execute(Acquire, context);
    CHECK(mp_submit(context, &pose, &bounds)); renderer.Execute(Present, context);
    CHECK(mp_status(context, &error) == Ready && error == 0);
    mp_close(context); CHECK(shutdowns == 7 && !mp_active());
    *reinterpret_cast<void***>(testGraphics) = originalVtable;
    for (auto texture : textures) texture->Release();
    testCompletion->Release(); testCompletion = nullptr;
    testGraphics->Release(); testGraphics = nullptr; device->Release();
    std::puts("PASS: D3D11 GPU commands are flushed before handoff and the next frame wait");
    CloseHandle(waitEntered); CloseHandle(waitResume);
    std::puts("PASS: enqueue failure releases the pending event reference");
    return 0;
}
