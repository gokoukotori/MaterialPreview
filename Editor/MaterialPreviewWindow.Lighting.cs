using System;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace GokouKotori.MaterialPreview
{
    public sealed partial class MaterialPreviewWindow
    {
        [SerializeField] PreviewLighting lighting = new PreviewLighting();
        LightDirectionControl lightDirection;
        float lightOrbitRemaining;
        double lastLightOrbitTime;

        void CreateLightingUI()
        {
            if (lighting == null) lighting = new PreviewLighting();
            var preset = rootVisualElement.Q<DropdownField>("light-preset");
            preset.choices = PreviewLighting.Presets.ToList();
            preset.RegisterValueChangedCallback(e =>
            {
                StopLightOrbit(); lighting.ApplyPreset(preset.index);
                SyncLightingUI(); RequestCameraRender();
            });
            lightDirection = new LightDirectionControl(lighting, LightingChanged);
            rootVisualElement.Q("light-direction-slot").Add(lightDirection);
            BindLightSlider("light-azimuth", value => lighting.Azimuth = value);
            BindLightSlider("light-elevation", value => lighting.Elevation = value);
            BindLightSlider("light-intensity", value => lighting.Intensity = value);
            BindLightSlider("light-ambient", value => lighting.Ambient = value);
            rootVisualElement.Q<ColorField>("light-color").RegisterValueChangedCallback(e => { lighting.Color = e.newValue; LightingChanged(); });
            BindLightToggle("light-shadows", value => lighting.Shadows = value);
            BindLightToggle("light-floor", value => lighting.Floor = value);
            BindLightToggle("light-occluder", value => lighting.Occluder = value);
            rootVisualElement.Q<Button>("light-orbit").clicked += () =>
            {
                if (lightOrbitRemaining > 0) { StopLightOrbit(); return; }
                lightOrbitRemaining = 360; lastLightOrbitTime = EditorApplication.timeSinceStartup;
                rootVisualElement.Q<Button>("light-orbit").text = "光を停止";
            };
            SyncLightingUI();
        }

        void BindLightSlider(string name, Action<float> assign)
        {
            var slider = rootVisualElement.Q<Slider>(name);
            slider.showInputField = true;
            slider.RegisterValueChangedCallback(e => { assign(Mathf.Clamp(e.newValue, slider.lowValue, slider.highValue)); LightingChanged(); });
        }

        void BindLightToggle(string name, Action<bool> assign)
        {
            rootVisualElement.Q<Toggle>(name).RegisterValueChangedCallback(e => { assign(e.newValue); RequestCameraRender(); });
        }

        void LightingChanged()
        {
            StopLightOrbit(); lighting.Preset = PreviewLighting.Presets.Length - 1;
            SyncLightingUI(); RequestCameraRender();
        }

        void SyncLightingUI()
        {
            rootVisualElement.Q<DropdownField>("light-preset").SetValueWithoutNotify(PreviewLighting.Presets[lighting.Preset]);
            rootVisualElement.Q<Slider>("light-azimuth").SetValueWithoutNotify(lighting.Azimuth);
            rootVisualElement.Q<Slider>("light-elevation").SetValueWithoutNotify(lighting.Elevation);
            rootVisualElement.Q<Slider>("light-intensity").SetValueWithoutNotify(lighting.Intensity);
            rootVisualElement.Q<Slider>("light-ambient").SetValueWithoutNotify(lighting.Ambient);
            rootVisualElement.Q<ColorField>("light-color").SetValueWithoutNotify(lighting.Color);
            rootVisualElement.Q<Toggle>("light-shadows").SetValueWithoutNotify(lighting.Shadows);
            rootVisualElement.Q<Toggle>("light-floor").SetValueWithoutNotify(lighting.Floor);
            rootVisualElement.Q<Toggle>("light-occluder").SetValueWithoutNotify(lighting.Occluder);
            lightDirection?.MarkDirtyRepaint();
        }

        void StopLightOrbit()
        {
            lightOrbitRemaining = 0;
            var button = rootVisualElement.Q<Button>("light-orbit");
            if (button != null) button.text = "光を一周";
        }

        void TickLightOrbit()
        {
            if (lightOrbitRemaining <= 0) return;
            if (rendering == null || propertyComparison) { StopLightOrbit(); return; }
            var now = EditorApplication.timeSinceStartup;
            var step = Mathf.Min(lightOrbitRemaining, (float)Math.Min(now - lastLightOrbitTime, .1) * 36);
            lastLightOrbitTime = now;
            lighting.Azimuth = Mathf.Repeat(lighting.Azimuth + step + 180, 360) - 180;
            lighting.Preset = PreviewLighting.Presets.Length - 1;
            lightOrbitRemaining -= step;
            SyncLightingUI();
            // Keep periodic material validation running during automatic playback.
            cameraRenderPending = true;
            if (lightOrbitRemaining <= 0) StopLightOrbit();
        }
    }
}
