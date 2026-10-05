using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;
using GokouKotori.MaterialPreview.OpenVRApi;

namespace GokouKotori.MaterialPreview
{
    internal struct VrFrame
    {
        internal bool Tracking, Focus, SceneHidden;
        internal Pose Head;
        internal VrHandInput Left, Right;
    }
    internal interface IVrRuntime : IDisposable
    {
        int Width { get; }
        int Height { get; }
        Pose Eye(int eye);
        Matrix4x4 Projection(int eye);
        void BindTargets(RenderTexture left, RenderTexture right);
        bool ReadyForFrame { get; }
        void RequestFrame();
        bool TryRead(out VrFrame frame);
        void Submit();
        void FinishFrame();
    }

    internal sealed class OpenVrRuntime : IVrRuntime
    {
        sealed class LibraryHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            internal LibraryHandle(IntPtr value) : base(true) { SetHandle(value); }
            protected override bool ReleaseHandle() => FreeLibrary(handle);
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        static extern IntPtr LoadLibraryExW(string file, IntPtr reserved, uint flags);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] static extern bool FreeLibrary(IntPtr module);

        LibraryHandle library;
        CVRSystem system;
        CVRCompositor compositor;
        CVRInput input;
        static volatile bool active;
        bool initialized, disposed, ownsSession;
        VrRenderBridge bridge;
        bool frameSubmitted;
        HmdMatrix34_t renderPose;
        readonly Dictionary<ulong, bool> pickOrigins = new Dictionary<ulong, bool>();
        readonly TrackedDevicePose_t[] poses = new TrackedDevicePose_t[1];
        float displayFrequency, vsyncToPhotons;
        readonly VRActiveActionSet_t[] sets = new VRActiveActionSet_t[1];
        readonly ulong[,] actions = new ulong[2, 5];
        readonly Pose[] eyes = new Pose[2];
        readonly Matrix4x4[] projections = new Matrix4x4[2];
        public int Width { get; private set; }
        public int Height { get; private set; }
        public Pose Eye(int eye) => eyes[eye];
        public Matrix4x4 Projection(int eye) => projections[eye];

        static LibraryHandle LoadLibrary()
        {
            if (!VrDependencies.Supported) throw new NotSupportedException("Windows x64 Editorが必要です。");
            if (!VrDependencies.VerifyFile(VrDependencies.DllPath))
                throw new InvalidOperationException("対応版OpenVR DLLが未導入または破損しています。依存導入を実行してください。");
            var handle = new LibraryHandle(LoadLibraryExW(VrDependencies.DllPath, IntPtr.Zero, 0x100 | 0x1000));
            if (!handle.IsInvalid) return handle;
            var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error);
        }
        // Read-only native smoke test; never calls Init or starts SteamVR.
        internal static bool IsSteamVrInstalled()
        {
            using (LoadLibrary()) return OpenVR.IsRuntimeInstalled();
        }

        internal OpenVrRuntime()
        {
            try
            {
                if (!VrDependencies.Supported) throw new NotSupportedException("Windows x64 Editorが必要です。");
                if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11)
                    throw new NotSupportedException("VRプレビューはDirect3D 11で動作します。Unityを -force-d3d11 で起動してください。");
                var displays = new List<XRDisplaySubsystem>(); SubsystemManager.GetInstances(displays);
                if (displays.Exists(display => display.running))
                    throw new InvalidOperationException("別のUnity XRセッションが動作中です。終了してからVRプレビューを開始してください。");
                if (active || VrRenderBridge.Active) throw new InvalidOperationException("別のMaterial PreviewウィンドウでVR表示中、または前の描画処理が終了待ちです。");
                active = true; ownsSession = true;
                library = LoadLibrary();
                if (!OpenVR.IsRuntimeInstalled()) throw new InvalidOperationException("SteamVRをインストールして起動してください。");
                var error = EVRInitError.None;
                system = OpenVR.Init(ref error, EVRApplicationType.VRApplication_Scene);
                initialized = error == EVRInitError.None;
                if (!initialized || system == null) throw new InvalidOperationException("SteamVR開始失敗: " + error);
                compositor = OpenVR.Compositor; input = OpenVR.Input;
                if (compositor == null || input == null) throw new InvalidOperationException("SteamVRのCompositor/Inputを取得できません。");
                compositor.SetTrackingSpace(ETrackingUniverseOrigin.TrackingUniverseStanding);
                Check(input.SetActionManifestPath(VrDependencies.ActionManifestPath));
                Check(input.GetActionSetHandle("/actions/materialpreview", ref sets[0].ulActionSet));
                for (var hand = 0; hand < 2; hand++)
                {
                    var names = new[] { "pose", "stick", "menu", "select", "grip" };
                    for (var action = 0; action < names.Length; action++)
                        Check(input.GetActionHandle("/actions/materialpreview/in/" + names[action] + (hand == 0 ? "_left" : "_right"), ref actions[hand, action]));
                }
                RefreshOptics();
                uint width = 0, height = 0; system.GetRecommendedRenderTargetSize(ref width, ref height);
                // Preserve the recommended aspect ratio while bounding the initial GPU allocation.
                var scale = Mathf.Min(1, 2048f / Mathf.Max(width, height));
                Width = Mathf.Max(256, Mathf.RoundToInt(width * scale));
                Height = Mathf.Max(256, Mathf.RoundToInt(height * scale));
                var table = OpenVR.GetGenericInterface("FnTable:" + OpenVR.IVRCompositor_Version, ref error);
                if (error != EVRInitError.None || table == IntPtr.Zero) throw new InvalidOperationException("OpenVR Compositor ABI: " + error);
                bridge = new VrRenderBridge(table, VrRenderBridge.GetProcAddress(library.DangerousGetHandle(), "VR_ShutdownInternal"));
                // Ownership of Shutdown transfers only after native creation succeeds.
                initialized = false;
            }
            catch { Dispose(); throw; }
        }
        internal static Pose PoseFrom(HmdMatrix34_t matrix) => new Pose(matrix.GetPosition(), matrix.GetRotation());
        internal static Matrix4x4 UnityProjection(HmdMatrix44_t matrix)
        {
            var projection = new Matrix4x4(
                new Vector4(matrix.m0, matrix.m4, matrix.m8, matrix.m12), new Vector4(matrix.m1, matrix.m5, matrix.m9, matrix.m13),
                new Vector4(matrix.m2, matrix.m6, matrix.m10, matrix.m14), new Vector4(matrix.m3, matrix.m7, matrix.m11, matrix.m15));
            // OpenVR depth [0,1] -> Unity Camera's OpenGL depth [-1,1].
            // Unity performs the subsequent graphics-API / reversed-Z conversion.
            projection.SetRow(2, 2 * projection.GetRow(2) - projection.GetRow(3));
            return projection;
        }
        void RefreshOptics()
        {
            for (var eye = 0; eye < 2; eye++)
            {
                eyes[eye] = PoseFrom(system.GetEyeToHeadTransform((EVREye)eye));
                projections[eye] = UnityProjection(system.GetProjectionMatrix((EVREye)eye, .03f, 150));
            }
            var error = ETrackedPropertyError.TrackedProp_Success;
            displayFrequency = system.GetFloatTrackedDeviceProperty(0, ETrackedDeviceProperty.Prop_DisplayFrequency_Float, ref error);
            if (error != ETrackedPropertyError.TrackedProp_Success) displayFrequency = 0;
            vsyncToPhotons = system.GetFloatTrackedDeviceProperty(0, ETrackedDeviceProperty.Prop_SecondsFromVsyncToPhotons_Float, ref error);
            if (error != ETrackedPropertyError.TrackedProp_Success) vsyncToPhotons = 0;
        }
        internal static bool ChangesOptics(VREvent_t ev) => ev.eventType == (uint)EVREventType.VREvent_IpdChanged
            || ev.eventType == (uint)EVREventType.VREvent_LensDistortionChanged
            || ev.trackedDeviceIndex == 0 && (ev.eventType == (uint)EVREventType.VREvent_TrackedDeviceUpdated
                || ev.eventType == (uint)EVREventType.VREvent_PropertyChanged);
        internal static float PredictionSeconds(float frequency, float sinceVsync, float toPhotons)
        {
            if (!(frequency > 0) || float.IsInfinity(frequency) || float.IsNaN(sinceVsync) || float.IsInfinity(sinceVsync)
                || float.IsNaN(toPhotons) || float.IsInfinity(toPhotons)) return 0;
            return Mathf.Clamp(1 / frequency - sinceVsync + toPhotons, 0, .1f);
        }
        static void Check(EVRInputError error)
        {
            if (error != EVRInputError.None) throw new InvalidOperationException("SteamVR Input: " + error);
        }
        static void CheckCompositor(EVRCompositorError error)
        {
            if (error != EVRCompositorError.None && error != EVRCompositorError.DoNotHaveFocus)
                throw new InvalidOperationException("SteamVR Compositor: " + error);
        }
        public void BindTargets(RenderTexture left, RenderTexture right) => bridge.Bind(left, right);
        public bool ReadyForFrame
        {
            get
            {
                var phase = bridge.Status(out var error);
                if (phase == VrRenderBridge.Phase.Submitted) CheckCompositor(error);
                return phase == VrRenderBridge.Phase.Idle || phase == VrRenderBridge.Phase.Submitted;
            }
        }
        public void RequestFrame() => bridge.Acquire();
        public bool TryRead(out VrFrame frame)
        {
            frame = default;
            var ev = new VREvent_t();
            var opticsChanged = false;
            while (system.PollNextEvent(ref ev, (uint)Marshal.SizeOf<VREvent_t>()))
            {
                if (ev.eventType == (uint)EVREventType.VREvent_Quit)
                { system.AcknowledgeQuit_Exiting(); throw new InvalidOperationException("SteamVRから終了が要求されました。"); }
                opticsChanged |= ChangesOptics(ev);
            }
            if (opticsChanged) RefreshOptics();
            if (bridge.Status(out var error) != VrRenderBridge.Phase.Ready) return false;
            CheckCompositor(error);
            frameSubmitted = false;
            // WaitGetPoses on Unity's render thread controls frame pacing. Do not
            // reuse its pose after an Editor stall: predict again immediately
            // before drawing, and use the same horizon for both hands.
            float elapsed = 0; ulong frameCounter = 0;
            var prediction = system.GetTimeSinceLastVsync(ref elapsed, ref frameCounter)
                ? PredictionSeconds(displayFrequency, elapsed, vsyncToPhotons) : 0;
            system.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseStanding, prediction, poses);
            renderPose = poses[0].mDeviceToAbsoluteTracking;
            frame = new VrFrame { Tracking = poses[0].bPoseIsValid && poses[0].bDeviceIsConnected,
                SceneHidden = error == EVRCompositorError.DoNotHaveFocus,
                Focus = error == EVRCompositorError.None && system.IsInputAvailable(), Head = PoseFrom(renderPose) };
            if (frame.Tracking && frame.Focus)
            {
                Check(input.UpdateActionState(sets, (uint)Marshal.SizeOf<VRActiveActionSet_t>()));
                frame.Left = ReadHand(0, prediction); frame.Right = ReadHand(1, prediction);
            }
            return true;
        }
        VrHandInput ReadHand(int hand, float prediction)
        {
            var pose = new InputPoseActionData_t();
            Check(input.GetPoseActionDataRelativeToNow(actions[hand, 0], ETrackingUniverseOrigin.TrackingUniverseStanding, prediction,
                ref pose, (uint)Marshal.SizeOf<InputPoseActionData_t>(), OpenVR.k_ulInvalidInputValueHandle));
            if (!pose.bActive || !pose.pose.bPoseIsValid || !pose.pose.bDeviceIsConnected) return default;
            var stick = new InputAnalogActionData_t();
            Check(input.GetAnalogActionData(actions[hand, 1], ref stick, (uint)Marshal.SizeOf<InputAnalogActionData_t>(), OpenVR.k_ulInvalidInputValueHandle));
            return new VrHandInput { Active = true, PreferPick = PrefersPick(pose.activeOrigin), Pose = PoseFrom(pose.pose.mDeviceToAbsoluteTracking),
                Stick = stick.bActive ? new Vector2(stick.x, stick.y) : Vector2.zero,
                Menu = Digital(actions[hand, 2]), Select = Digital(actions[hand, 3]), Grip = Digital(actions[hand, 4]) };
        }
        internal static bool DefaultPick(string controllerType) => controllerType == "vive_controller";
        bool PrefersPick(ulong origin)
        {
            if (pickOrigins.TryGetValue(origin, out var pick)) return pick;
            var info = new InputOriginInfo_t();
            if (input.GetOriginTrackedDeviceInfo(origin, ref info, (uint)Marshal.SizeOf<InputOriginInfo_t>()) != EVRInputError.None)
                return false;
            var type = new System.Text.StringBuilder(64);
            var error = ETrackedPropertyError.TrackedProp_Success;
            system.GetStringTrackedDeviceProperty(info.trackedDeviceIndex, ETrackedDeviceProperty.Prop_ControllerType_String,
                type, (uint)type.Capacity, ref error);
            if (error != ETrackedPropertyError.TrackedProp_Success) return false;
            return pickOrigins[origin] = DefaultPick(type.ToString());
        }
        bool Digital(ulong action)
        {
            var data = new InputDigitalActionData_t();
            Check(input.GetDigitalActionData(action, ref data, (uint)Marshal.SizeOf<InputDigitalActionData_t>(), OpenVR.k_ulInvalidInputValueHandle));
            return data.bActive && data.bState;
        }
        public void Submit()
        {
            bridge.Submit(renderPose, SubmitBounds(SystemInfo.graphicsUVStartsAtTop));
            frameSubmitted = true;
        }
        // Successful submissions already start the next wait on the render thread.
        // Only a frame skipped by the managed renderer returns to Idle.
        public void FinishFrame()
        {
            // In direct graphics mode Submit can already have completed its next
            // WaitGetPoses. Never discard that newly ready frame as an old skip.
            if (!frameSubmitted) bridge.Skip();
        }
        internal static VRTextureBounds_t SubmitBounds(bool uvStartsAtTop) => new VRTextureBounds_t
            { uMin = 0, uMax = 1, vMin = uvStartsAtTop ? 1 : 0, vMax = uvStartsAtTop ? 0 : 1 };
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { bridge?.Dispose(); }
            finally { ReleaseNative(); }
        }
        void ReleaseNative()
        {
            try { if (initialized) OpenVR.Shutdown(); }
            finally { initialized = false; library?.Dispose(); library = null; if (ownsSession) active = false; ownsSession = false; }
        }
    }
}
