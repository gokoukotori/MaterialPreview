// Test-only OpenVR substitute. Also checks D3D11 texture contents on Unity's
// actual render thread. Never linked into the shipped bridge.
#include "VrRenderBridge.cpp"
#include <d3d11.h>
struct Snapshot {
    int waits, left, right, handoffs, shutdowns, failures;
    DWORD mainThread, renderThread;
    Matrix34 pose;
};
static Snapshot snapshot{};
static bool visible = true;
static void CheckThread() {
    DWORD current = GetCurrentThreadId();
    if (snapshot.renderThread && snapshot.renderThread != current) ++snapshot.failures;
    snapshot.renderThread = current;
}
static int __stdcall Wait(Pose*, uint32_t, Pose*, uint32_t) { CheckThread(); ++snapshot.waits; return 0; }
static int __stdcall Submit(int eye, const Texture* texture, const Bounds* bounds, int flags) {
    CheckThread(); if (eye == 0) ++snapshot.left; else ++snapshot.right;
    snapshot.pose = texture->pose;
    if (flags != 8 || bounds->vMin != 1 || bounds->vMax != 0) ++snapshot.failures;
    ID3D11Texture2D* source{}; ID3D11Texture2D* staging{};
    ID3D11Device* device{}; ID3D11DeviceContext* context{};
    auto resource = static_cast<IUnknown*>(texture->handle);
    if (FAILED(resource->QueryInterface(__uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&source)))) { ++snapshot.failures; return 102; }
    source->GetDevice(&device); device->GetImmediateContext(&context);
    D3D11_TEXTURE2D_DESC desc{}; source->GetDesc(&desc);
    if (desc.SampleDesc.Count != 1) ++snapshot.failures;
    desc.Usage = D3D11_USAGE_STAGING; desc.BindFlags = desc.MiscFlags = 0; desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    if (FAILED(device->CreateTexture2D(&desc, nullptr, &staging))) ++snapshot.failures;
    else {
        context->CopyResource(staging, source);
        D3D11_MAPPED_SUBRESOURCE mapped{};
        if (FAILED(context->Map(staging, 0, D3D11_MAP_READ, 0, &mapped))) ++snapshot.failures;
        else {
            uint32_t pixel = *static_cast<uint32_t*>(mapped.pData);
            if (pixel != (eye == 0 ? 0xffffffffu : 0xff000000u)) ++snapshot.failures;
            context->Unmap(staging, 0);
        }
        staging->Release();
    }
    context->Release(); device->Release(); source->Release();
    return 0;
}
static void __stdcall Handoff() { CheckThread(); ++snapshot.handoffs; }
static bool __stdcall CanRender() { CheckThread(); return visible; }
static void __cdecl Shutdown() { ++snapshot.shutdowns; }
API void fixture_functions(Functions* functions) {
    snapshot = {}; snapshot.mainThread = GetCurrentThreadId(); visible = true;
    *functions = {Wait, Submit, Handoff, CanRender, Shutdown};
}
API void fixture_snapshot(Snapshot* value) { *value = snapshot; }
API void fixture_visible(int value) { visible = value != 0; }
