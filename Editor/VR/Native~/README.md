# VR rendering bridge

Windows x64 / D3D11 / OpenVR 1.16.8. `VrRenderBridge.cpp` implements the
UnityRenderingEventAndData callback without entering managed code. Frame wait,
both eye submissions, and handoff execute on Unity's rendering thread. The
managed side obtains fresh predicted tracking poses immediately before drawing.

The shipped `../material_preview_vr_render.dll.bytes` is loaded only on VR opt-in
and copied to a content-addressed directory under `Library/MaterialPreview/VR`.
Its static C++ runtime does not require users to install build tools. OpenVR
itself remains the optional, version-pinned download controlled by the window.
The dependency installer and its installed-state cache are unchanged.

The callback DLL is pinned for the Editor process because queued Unity events
can outlive a managed domain. A reference-counted native context independently
retains the OpenVR module and D3D11 textures until the last event finishes.
Closing cancels pending work; OpenVR shutdown never overtakes an executing call.
The next session is rejected until that context has finished closing.

To rebuild with installed Visual Studio C++ x64 tools, run `Build.ps1` with
`-OutputDirectory` pointing inside the project's Library. It builds and runs
the standalone native tests, and produces a test-only fixture DLL. Copy
`material_preview_vr_render.dll` to `../material_preview_vr_render.dll.bytes`
after successful validation. No files need to be created under Assets.

For the Unity EditMode tests, set `MATERIAL_PREVIEW_VR_FIXTURE` to the absolute
path of the generated `material_preview_vr_fixture.dll`. The fixture verifies
actual Unity rendering-thread execution and D3D11 texture pixels without
starting SteamVR. HMD latency, reprojection, and dashboard behavior still
require hardware validation.
