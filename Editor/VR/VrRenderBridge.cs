using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using UnityEngine;
using UnityEngine.Rendering;
using GokouKotori.MaterialPreview.OpenVRApi;

namespace GokouKotori.MaterialPreview
{
    // Only unmanaged code runs in Unity's render callback. Pending events retain
    // OpenVR and the D3D11 resources independently of the managed domain's lifetime.
    internal sealed class VrRenderBridge : IDisposable
    {
        internal enum Phase { Idle, Waiting, Ready, Submitting, Submitted }
        [StructLayout(LayoutKind.Sequential)] internal struct Functions
        {
            internal IntPtr Wait, Submit, Handoff, CanRender, Shutdown;
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int IntCall();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr PointerCall();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr CreateCall(ref Functions functions);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void CloseCall(IntPtr context);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int StatusCall(ContextHandle context, out EVRCompositorError error);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int TargetsCall(ContextHandle context, IntPtr left, IntPtr right);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int AcquireCall(ContextHandle context);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SubmitCall(ContextHandle context, ref HmdMatrix34_t pose, ref VRTextureBounds_t bounds);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SkipCall(ContextHandle context);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void AbortCall(ContextHandle context, int eventId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        static extern IntPtr LoadLibraryExW(string file, IntPtr reserved, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
        internal static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)] static extern bool FreeLibrary(IntPtr module);

        sealed class ContextHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            readonly CloseCall close;
            internal ContextHandle(CloseCall close) : base(true) { this.close = close; }
            internal void Initialize(IntPtr value) => SetHandle(value);
            protected override bool ReleaseHandle() { close(handle); return true; }
        }
        sealed class Api
        {
            internal readonly IntCall Active;
            internal readonly CreateCall Create;
            internal readonly CloseCall Close;
            internal readonly StatusCall Status;
            internal readonly TargetsCall Targets;
            internal readonly AcquireCall Acquire;
            internal readonly SubmitCall Submit;
            internal readonly SkipCall Skip;
            internal readonly AbortCall Abort;
            internal readonly IntPtr Callback;
            // The native callback pins the bridge for this Editor process. It
            // never pins OpenVR: each context holds/releases that module itself.
            internal Api()
            {
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(VrDependencies.Package)?.resolvedPath
                    ?? Path.GetFullPath(VrDependencies.Package);
                var bytes = File.ReadAllBytes(Path.Combine(package, "Editor/VR/material_preview_vr_render.dll.bytes"));
                string digest;
                using (var hash = SHA256.Create()) digest = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "");
                var directory = Path.Combine(VrDependencies.Root, "RenderBridge", digest);
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "material_preview_vr_render.dll");
                if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
                var module = LoadLibraryExW(path, IntPtr.Zero, 0x100 | 0x1000);
                if (module == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    if (Export<IntCall>(module, "mp_version")() != 1) throw new InvalidOperationException("VR描画ブリッジの版が一致しません。");
                    Callback = Export<PointerCall>(module, "mp_callback")();
                    if (Callback == IntPtr.Zero) throw new InvalidOperationException("VR描画コールバックを保持できません。");
                    Active = Export<IntCall>(module, "mp_active"); Create = Export<CreateCall>(module, "mp_create");
                    Close = Export<CloseCall>(module, "mp_close"); Status = Export<StatusCall>(module, "mp_status");
                    Targets = Export<TargetsCall>(module, "mp_targets"); Acquire = Export<AcquireCall>(module, "mp_acquire");
                    Submit = Export<SubmitCall>(module, "mp_submit"); Skip = Export<SkipCall>(module, "mp_skip");
                    Abort = Export<AbortCall>(module, "mp_abort_event");
                }
                finally { FreeLibrary(module); } // mp_callback explicitly pins the bridge.
            }
            static T Export<T>(IntPtr module, string name) where T : Delegate
            {
                var pointer = GetProcAddress(module, name);
                if (pointer == IntPtr.Zero) throw new EntryPointNotFoundException(name);
                return Marshal.GetDelegateForFunctionPointer<T>(pointer);
            }
        }
        static Api api;
        static Api Native => api ?? (api = new Api());
        readonly ContextHandle context;
        readonly CommandBuffer commands;
        internal static bool Active => Native.Active() != 0;
        static Functions ResolveFunctions(IntPtr compositorTable, IntPtr shutdown)
        {
            IntPtr Function(string name) => Marshal.ReadIntPtr(compositorTable, Marshal.OffsetOf<IVRCompositor>(name).ToInt32());
            return new Functions { Wait = Function("WaitGetPoses"), Submit = Function("Submit"),
                Handoff = Function("PostPresentHandoff"), CanRender = Function("CanRenderScene"), Shutdown = shutdown };
        }
        internal VrRenderBridge(IntPtr compositorTable, IntPtr shutdown) : this(ResolveFunctions(compositorTable, shutdown)) { }
        internal VrRenderBridge(Functions functions)
        {
            commands = new CommandBuffer { name = "Material Preview OpenVR" };
            try
            {
                context = new ContextHandle(Native.Close);
                context.Initialize(Native.Create(ref functions));
                if (context.IsInvalid) throw new InvalidOperationException("VR描画セッションを作成できません。前の終了処理を待ってください。");
            }
            catch { commands.Release(); throw; }
        }
        internal Phase Status(out EVRCompositorError error) => (Phase)Native.Status(context, out error);
        internal void Bind(RenderTexture left, RenderTexture right)
        {
            // Texture pointers are acquired once, before the first pose request.
            if (Native.Targets(context, left.GetNativeTexturePtr(), right.GetNativeTexturePtr()) == 0)
                throw new InvalidOperationException("VR描画テクスチャを設定できません。");
        }
        internal void Acquire() { if (Native.Acquire(context) == 0) throw new InvalidOperationException("VRフレームは処理中です。"); Queue(1); }
        internal void Submit(HmdMatrix34_t pose, VRTextureBounds_t bounds)
        {
            if (Native.Submit(context, ref pose, ref bounds) == 0) throw new InvalidOperationException("VRフレームを送信できません。");
            Queue(2);
        }
        void Queue(int eventId)
        {
            try
            {
                commands.Clear();
                commands.IssuePluginEventAndData(Native.Callback, eventId, context.DangerousGetHandle());
                Graphics.ExecuteCommandBuffer(commands);
            }
            catch { Native.Abort(context, eventId); throw; }
        }
        internal void Skip() => Native.Skip(context);
        bool disposed;
        public void Dispose() { if (disposed) return; disposed = true; context?.Dispose(); commands?.Release(); }
    }
}
