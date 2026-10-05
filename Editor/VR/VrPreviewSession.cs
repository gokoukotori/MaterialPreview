using System;
using System.Diagnostics;
using UnityEditor;
using UnityEngine;

namespace GokouKotori.MaterialPreview
{
    internal sealed class VrPreviewSession : IDisposable
    {
        readonly IVrRuntime runtime;
        internal readonly VrComparisonScene Scene;
        readonly VrNavigation navigation = new VrNavigation();
        internal readonly VrHandMenu Left = new VrHandMenu(), Right = new VrHandMenu();
        readonly VrRadialView leftView, rightView;
        readonly Pose[] eyes = new Pose[2];
        readonly Matrix4x4[] projections = new Matrix4x4[2];
        readonly Action lightingChanged;
        readonly Action<float> heightMeasured;
        readonly Action<string> stopped;
        readonly Action repaint;
        readonly Func<bool> canContinue;
        double lastTime, nextRefresh, rateStart, renderSeconds;
        int submittedFrames;
        readonly bool wasRunningInBackground;
        bool centered, disposed, pumping;
        Pose lastHead;
        internal bool Tracking { get; private set; }
        internal float RealEyeHeight { get; private set; }
        internal float EyeHeight { get; private set; }
        internal float TrackingScale => navigation.TrackingScale;
        internal RenderTexture Spectator => Scene.Targets[0];
        internal string Status { get; private set; } = "HMDを待っています。";
        internal float FramesPerSecond { get; private set; }
        internal float RenderMilliseconds { get; private set; }

        internal VrPreviewSession(ComparisonSession comparison, PreviewLighting lighting, IVrRuntime runtime,
            Func<bool> canContinue, Action lightingChanged, Action<string> stopped,
            float realEyeHeight = 1.6f, float eyeHeight = 1.6f, Action<float> heightMeasured = null, Action repaint = null)
        {
            this.runtime = runtime; this.canContinue = canContinue; this.lightingChanged = lightingChanged; this.stopped = stopped;
            this.heightMeasured = heightMeasured;
            this.repaint = repaint;
            wasRunningInBackground = Application.runInBackground;
            try
            {
                SetViewpoint(realEyeHeight, eyeHeight);
                Scene = new VrComparisonScene(comparison, lighting, runtime.Width, runtime.Height);
                leftView = new VrRadialView(Scene, "Left lighting menu"); rightView = new VrRadialView(Scene, "Right lighting menu");
                runtime.BindTargets(Scene.Targets[0], Scene.Targets[1]);
                lastTime = rateStart = EditorApplication.timeSinceStartup;
                Application.runInBackground = true;
                EditorApplication.update += Tick;
                Application.onBeforeRender += BeforeRender;
                EditorApplication.playModeStateChanged += PlayModeChanged;
                AssemblyReloadEvents.beforeAssemblyReload += Dispose;
                EditorApplication.quitting += Dispose;
            }
            catch { Dispose(); throw; }
        }
        internal static bool ValidEyeHeight(float height) => height >= .1f && height <= 100;
        internal void SetViewpoint(float realEyeHeight, float eyeHeight)
        {
            if (!ValidEyeHeight(realEyeHeight) || !ValidEyeHeight(eyeHeight))
                throw new ArgumentOutOfRangeException(nameof(eyeHeight), "目の高さは10〜10000cmで指定してください。");
            navigation.SetScale(eyeHeight / realEyeHeight, lastHead);
            RealEyeHeight = realEyeHeight; EyeHeight = eyeHeight;
        }
        internal bool MeasureEyeHeight()
        {
            if (disposed || !Tracking) return false;
            var eyeCenter = (runtime.Eye(0).position + runtime.Eye(1).position) * .5f;
            var height = (lastHead.position + lastHead.rotation * eyeCenter).y;
            if (!ValidEyeHeight(height)) return false;
            SetViewpoint(height, EyeHeight); heightMeasured?.Invoke(height); return true;
        }
        internal static bool TryAvatarEyeHeight(GameObject avatar, out float height)
        {
            height = 0;
            if (avatar == null) return false;
            foreach (var component in avatar.GetComponents<Component>())
            {
                if (component == null || component.GetType().FullName != "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor") continue;
                using (var serialized = new SerializedObject(component))
                {
                    var view = serialized.FindProperty("ViewPosition");
                    if (view == null || view.propertyType != SerializedPropertyType.Vector3) return false;
                    height = avatar.transform.TransformVector(view.vector3Value).y;
                    return ValidEyeHeight(height);
                }
            }
            return false;
        }
        void PlayModeChanged(PlayModeStateChange state) => End("Play Modeへの切り替えでVRを終了しました。");
        void End(string message)
        {
            Dispose(); stopped?.Invoke(message);
        }
        internal void Tick()
        {
            if (disposed) return;
            // QueuePlayerLoopUpdate alone does not keep an unfocused Editor
            // window rendering. Repaint also drains the native graphics events
            // while a VR frame is waiting, rather than just refreshing the 4Hz UI.
            EditorApplication.QueuePlayerLoopUpdate();
            repaint?.Invoke();
            PumpFrame();
        }
        // Also consume ready frames at Unity's last pose-update opportunity.
        // The native phase prevents Tick and BeforeRender submitting the same frame.
        [BeforeRenderOrder(-30000)]
        internal void BeforeRender() => PumpFrame();
        void PumpFrame()
        {
            if (disposed || pumping) return;
            if (EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode || !canContinue())
            { End("比較元の状態が変わったためVRを終了しました。比較を確認して再開してください。"); return; }
            pumping = true;
            try
            {
                var now = EditorApplication.timeSinceStartup;
                if (now >= nextRefresh) { Scene.Refresh(true); nextRefresh = now + .2; }
                if (runtime.ReadyForFrame) runtime.RequestFrame();
                if (!runtime.TryRead(out var frame)) return;
                now = EditorApplication.timeSinceStartup;
                var dt = Mathf.Max((float)(now - lastTime), 0); lastTime = now;
                try { Step(frame, dt, now); }
                finally { if (!disposed) runtime.FinishFrame(); }
            }
            catch (Exception ex) { End("VR停止: " + Integration.Message(ex)); }
            finally { pumping = false; }
        }
        internal void Step(VrFrame frame, float dt, double now)
        {
            Tracking = frame.Tracking;
            if (Tracking) lastHead = frame.Head;
            if (!frame.Focus || !frame.Tracking || frame.SceneHidden) { frame.Left.Active = false; frame.Right.Active = false; }
            var changed = Left.Update(frame.Left, dt, Scene.Lighting, true);
            // Resolve simultaneous shared-light writes deterministically; either hand works on its own.
            changed |= Right.Update(frame.Right, dt, Scene.Lighting, !changed);
            if (changed) lightingChanged?.Invoke();
            if (Left.MeasureHeight || Right.MeasureHeight) MeasureEyeHeight();
            if (Left.Exit || Right.Exit) { End("VRプレビューを終了しました。"); return; }
            if (!centered && frame.Tracking || Left.Recenter || Right.Recenter)
            { navigation.Recenter(frame.Head, Scene.StartDistance); centered = true; }
            navigation.Update(frame.Head, frame.Left, frame.Right, Left.Visible, Right.Visible, dt);
            var head = navigation.World(frame.Head);
            var left = frame.Left; left.Pose = navigation.World(left.Pose);
            var right = frame.Right; right.Pose = navigation.World(right.Pose);
            leftView.Update(Left, left, head, Scene.Lighting, TrackingScale); rightView.Update(Right, right, head, Scene.Lighting, TrackingScale);
            Status = !frame.Tracking ? "HMDのトラッキングを待っています。" : frame.SceneHidden ? "SteamVRの表示権限を待っています。"
                : !frame.Focus ? "VR表示中（ダッシュボード操作中は移動・メニュー入力を停止）" : "VR表示中: AS IS + 全TO BE";
            if (frame.Tracking && !frame.SceneHidden)
            {
                var started = Stopwatch.GetTimestamp();
                for (var i = 0; i < 2; i++)
                {
                    eyes[i] = runtime.Eye(i); eyes[i].position *= TrackingScale;
                    projections[i] = runtime.Projection(i);
                }
                Scene.Draw(head, eyes, projections, TrackingScale); runtime.Submit();
                renderSeconds += (Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency;
                submittedFrames++;
            }
            if (now - rateStart >= 1)
            {
                FramesPerSecond = (float)(submittedFrames / (now - rateStart));
                RenderMilliseconds = submittedFrames == 0 ? 0 : (float)(renderSeconds * 1000 / submittedFrames);
                rateStart = now; submittedFrames = 0; renderSeconds = 0;
            }
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Tracking = false;
            EditorApplication.update -= Tick; EditorApplication.playModeStateChanged -= PlayModeChanged;
            Application.onBeforeRender -= BeforeRender;
            AssemblyReloadEvents.beforeAssemblyReload -= Dispose; EditorApplication.quitting -= Dispose;
            Application.runInBackground = wasRunningInBackground;
            try { runtime.Dispose(); }
            finally { leftView?.Dispose(); rightView?.Dispose(); Scene?.Dispose(); }
        }
    }
}
