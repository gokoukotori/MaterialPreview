using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace GokouKotori.MaterialPreview
{
    public sealed partial class MaterialPreviewWindow
    {
        VrPreviewSession vr;
        VrChangeTracker vrChanges;
        bool vrEnabled;
        [SerializeField] float vrRealEyeHeight = 1.6f, vrEyeHeight = 1.6f;
        string vrMessage;
        Label vrStatus;
        Button vrInstall, vrCancel, vrStart, vrStop;
        Button vrMeasureHeight, vrAvatarHeight;
        FloatField vrRealHeightField, vrEyeHeightField;
        Label vrViewpointStatus;
        Image vrImage;
        VisualElement vrOptions;
        double nextVrUi;

        void CreateVrUI()
        {
            vrEnabled = VrDependencies.ReadEnabled();
            var foldout = new Foldout { text = "VRプレビュー（オプション）", value = false };
            foldout.name = "vr-preview";
            var status = rootVisualElement.Q("status");
            status.parent.Insert(status.parent.IndexOf(status) + 1, foldout);
            var enabled = new Toggle("VRプレビューを使用する") { value = vrEnabled };
            foldout.Add(enabled);
            enabled.RegisterValueChangedCallback(e =>
            {
                try
                {
                    VrDependencies.WriteEnabled(e.newValue); vrEnabled = e.newValue; vrMessage = null;
                    if (!vrEnabled) { StopVrPreview(); if (VrDependencies.Installing) VrDependencies.Cancel(); }
                }
                catch (Exception ex) { vrMessage = ex.Message; enabled.SetValueWithoutNotify(vrEnabled); }
                UpdateVrUI();
            });
            vrOptions = new VisualElement(); foldout.Add(vrOptions);
            var help = new Label("Windows / Direct3D 11 / SteamVR対応HMD。先にSteamVRを起動し、VRChat等のVRアプリを終了してください。\n"
                + "依存導入はOpenVR " + VrDependencies.Version + "のDLLをValve公式からLibraryへ取得します。SteamVR本体は別途インストールしてください。");
            help.style.whiteSpace = WhiteSpace.Normal; vrOptions.Add(help);
            if (!VrPreviewSession.ValidEyeHeight(vrRealEyeHeight)) vrRealEyeHeight = 1.6f;
            if (!VrPreviewSession.ValidEyeHeight(vrEyeHeight)) vrEyeHeight = 1.6f;
            var viewpointHelp = new Label("床から目までの高さを指定します（頭頂までの身長とは異なります）。\n"
                + "HMDから取得する場合は、SteamVRの床設定を確認し、立って正面を向いて取得してください。高さの比率に合わせてVR内の体感スケールを調整します。");
            viewpointHelp.style.whiteSpace = WhiteSpace.Normal; vrOptions.Add(viewpointHelp);
            vrRealHeightField = new FloatField("現実の目の高さ (cm)")
                { name = "vr-real-eye-height", value = vrRealEyeHeight * 100, isDelayed = true };
            vrRealHeightField.RegisterValueChangedCallback(e => SetVrViewpoint(e.newValue / 100, vrEyeHeight));
            vrOptions.Add(vrRealHeightField);
            vrMeasureHeight = new Button(() =>
            {
                if (vr == null || !vr.MeasureEyeHeight()) vrMessage = "HMDの有効な目の高さを取得できません。VRを開始し、床設定とトラッキングを確認してください。";
                UpdateVrUI();
            }) { name = "vr-measure-height", text = "HMDから現実の目の高さを取得（VR開始後）" };
            vrOptions.Add(vrMeasureHeight);
            vrEyeHeightField = new FloatField("VR空間の目の高さ (cm)")
                { name = "vr-eye-height", value = vrEyeHeight * 100, isDelayed = true };
            vrEyeHeightField.RegisterValueChangedCallback(e => SetVrViewpoint(vrRealEyeHeight, e.newValue / 100));
            vrOptions.Add(vrEyeHeightField);
            vrAvatarHeight = new Button(() =>
            {
                if (VrPreviewSession.TryAvatarEyeHeight(session?.Avatar, out var height)) SetVrViewpoint(vrRealEyeHeight, height);
                else { vrMessage = "比較中アバターのView Positionから有効な目の高さを取得できません。手入力で指定してください。"; UpdateVrUI(); }
            }) { name = "vr-avatar-height", text = "比較中アバターのView Positionから取得" };
            vrOptions.Add(vrAvatarHeight);
            vrViewpointStatus = new Label { name = "vr-viewpoint-status" }; vrViewpointStatus.style.whiteSpace = WhiteSpace.Normal; vrOptions.Add(vrViewpointStatus);
            var row = new VisualElement(); row.style.flexDirection = FlexDirection.Row; vrOptions.Add(row);
            vrInstall = new Button(() => { vrMessage = null; VrDependencies.Install(); }) { text = "必要な依存を導入" }; row.Add(vrInstall);
            row.Add(new Button(() => Application.OpenURL("https://store.steampowered.com/app/250820/SteamVR/")) { text = "SteamVR配布ページ" });
            vrCancel = new Button(VrDependencies.Cancel) { text = "導入を中止" }; row.Add(vrCancel);
            vrStart = new Button(StartVrPreview) { text = "VRで全案を比較" }; row.Add(vrStart);
            vrStop = new Button(StopVrPreview) { text = "VRを終了" }; row.Add(vrStop);
            var usage = new Label("左スティック: 移動 / 左Grip: 加速 / 右スティック: 旋回 / 右Grip＋上下: 高さ\n"
                + "メニュー: 左Y・右B（Indexは左右B、ViveはMenu）を0.5秒長押しで開閉。左右どちらでも操作できます。\n"
                + "方向へ倒し切ってフリック選択（Viveはトリガー確定）。「オプション」の「フリック選択」で切り替えられます。\n"
                + "上端の「戻る／閉じる」で操作します。「光の強さ／環境の明るさ」は角度で調整し、スティック中央＋トリガーで戻ります。");
            usage.style.whiteSpace = WhiteSpace.Normal; vrOptions.Add(usage);
            vrImage = new Image { scaleMode = ScaleMode.ScaleToFit }; vrImage.style.height = 160; vrOptions.Add(vrImage);
            vrStatus = new Label(); vrStatus.style.whiteSpace = WhiteSpace.Normal; foldout.Add(vrStatus);
            UpdateVrUI();
        }
        void SetVrViewpoint(float realEyeHeight, float eyeHeight)
        {
            if (!VrPreviewSession.ValidEyeHeight(realEyeHeight) || !VrPreviewSession.ValidEyeHeight(eyeHeight))
                vrMessage = "目の高さは10〜10000cmで指定してください。";
            else
            {
                vr?.SetViewpoint(realEyeHeight, eyeHeight);
                vrRealEyeHeight = realEyeHeight; vrEyeHeight = eyeHeight; vrMessage = null;
            }
            UpdateVrUI();
        }
        void StartVrPreview()
        {
            if (!vrEnabled || session == null || blocked || vr != null) return;
            try
            {
                if (!session.Saved) session.Validate();
                StopLightOrbit();
                vr = new VrPreviewSession(session, lighting, new OpenVrRuntime(),
                    () => this != null && session != null && !blocked && vrEnabled,
                    () => { StopLightOrbit(); SyncLightingUI(); cameraRenderPending = true; },
                    message => { vr = null; vrChanges?.Dispose(); vrChanges = null; vrMessage = message; UpdateVrUI(); },
                    vrRealEyeHeight, vrEyeHeight,
                    height => { vrRealEyeHeight = height; vrMessage = null; UpdateVrUI(); }, Repaint);
                vrChanges = new VrChangeTracker(session);
                vrMessage = null;
            }
            catch (Exception ex) { vrMessage = "開始できません: " + Integration.Message(ex); }
            UpdateVrUI();
        }
        void StopVrPreview()
        {
            var current = vr; vr = null;
            vrChanges?.Dispose(); vrChanges = null;
            try { current?.Dispose(); if (current != null) vrMessage = "VRプレビューを終了しました。"; }
            catch (Exception ex) { vrMessage = "VR終了時: " + Integration.Message(ex); }
            UpdateVrUI();
        }
        void UpdateVrUI()
        {
            if (vrStatus == null) return;
            vrOptions.style.display = vrEnabled ? DisplayStyle.Flex : DisplayStyle.None;
            var installed = vrEnabled && VrDependencies.Installed;
            // Leave delayed text input intact during the periodic VR status refresh.
            if (vrRealHeightField.value != vrRealEyeHeight * 100) vrRealHeightField.SetValueWithoutNotify(vrRealEyeHeight * 100);
            if (vrEyeHeightField.value != vrEyeHeight * 100) vrEyeHeightField.SetValueWithoutNotify(vrEyeHeight * 100);
            vrMeasureHeight.SetEnabled(vr != null && vr.Tracking);
            vrAvatarHeight.SetEnabled(session != null && session.Avatar != null);
            vrViewpointStatus.text = $"体感スケール: {vrEyeHeight / vrRealEyeHeight:0.###}倍（VR空間 / 現実の目の高さ）\n"
                + "床を基準に目線と手の動きを調整します。しゃがんでも床との対応を保ちます。設定はこのウィンドウに保持します。";
            vrInstall.SetEnabled(VrDependencies.Supported && !installed && vr == null && !VrDependencies.Installing);
            vrInstall.text = installed ? "依存導入済み" : "必要な依存を導入";
            vrCancel.style.display = VrDependencies.Installing ? DisplayStyle.Flex : DisplayStyle.None;
            vrStart.SetEnabled(installed && !VrDependencies.Installing && session != null && !blocked && vr == null);
            vrStop.SetEnabled(vr != null);
            vrImage.image = vr?.Spectator; vrImage.style.display = vr != null ? DisplayStyle.Flex : DisplayStyle.None;
            vrStatus.text = !vrEnabled ? "無効（依存の自動導入・VRの自動起動は行いません）" : vrMessage ?? vr?.Status
                ?? (VrDependencies.Installing ? $"OpenVR導入中… {Mathf.Max(0, VrDependencies.Progress) * 100:0}%" : VrDependencies.Message
                    ?? (installed ? "依存導入済み。比較を開始してからVRへ進んでください。" : "依存を導入するとVRプレビューを利用できます。"));
            if (vr != null)
                vrStatus.text += $"\n描画: {vr.FramesPerSecond:0.0} fps / CPU描画・送信要求: {vr.RenderMilliseconds:0.0} ms / {vr.Spectator.width}×{vr.Spectator.height}（片眼）"
                    + "\nVR表示中はデスクトップ比較画像の更新を休止します。";
        }
        void TickVrUI()
        {
            if (EditorApplication.timeSinceStartup < nextVrUi) return;
            nextVrUi = EditorApplication.timeSinceStartup + .25;
            UpdateVrUI();
            if (vr != null) vrImage?.MarkDirtyRepaint();
        }
    }
}
