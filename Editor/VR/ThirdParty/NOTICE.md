# OpenVR C# bindings

Source: ValveSoftware/openvr, v1.16.8, commit
`4c85abcb7f7f1f02adaf3812018c99fc593bc341`, `headers/openvr_api.cs`.
License: BSD 3-Clause, reproduced in LICENSE.txt.

Local modifications: namespace `Valve.VR` is renamed to
`GokouKotori.MaterialPreview.OpenVRApi`; the P/Invoke library name is
`material_preview_openvr_api` to keep this optional editor integration separate
from other OpenVR bindings. The matching Windows x64 native library is downloaded
only through the dependency installation button and verified before use.
The outer compilation guard is UNITY_EDITOR so an unrelated OPENVR_XR_API define
does not disable these private bindings.

Native C++ bool return values explicitly use UnmanagedType.I1, including
PollNextEvent, IsInputAvailable, GetTimeSinceLastVsync, and CanRenderScene.
