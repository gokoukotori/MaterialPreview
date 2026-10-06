using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace GokouKotori.MaterialPreview
{
    internal sealed class VrComparisonScene : IDisposable
    {
        sealed class Geometry
        {
            internal Renderer Original;
            internal Mesh Mesh;
            internal MaterialSlot[] Slots;
            Mesh sourceMesh;
            int meshDirty, rendererDirty;
            Transform[] bones;
            Matrix4x4[] boneMatrices;
            float[] blendShapes;
            Matrix4x4 sourceMatrix;
            bool captured, visible, receivesShadows;
            ShadowCastingMode shadows;

            internal bool RefreshSource()
            {
                if (Original == null) throw new InvalidOperationException("比較元Rendererが削除されました。比較を開始し直してください。");
                var skinned = Original as SkinnedMeshRenderer;
                var source = skinned != null ? skinned.sharedMesh : Original.GetComponent<MeshFilter>()?.sharedMesh;
                var dirty = source == null ? 0 : EditorUtility.GetDirtyCount(source);
                var rendererVersion = EditorUtility.GetDirtyCount(Original);
                var changed = !captured || sourceMesh != source || meshDirty != dirty;
                var matrix = Original.transform.localToWorldMatrix;
                var matrixChanged = sourceMatrix != matrix;
                if (skinned != null)
                {
                    // Bone motion and blend-shape sliders often do not mark the
                    // renderer dirty. Compare their values before deciding to bake.
                    if (changed || rendererDirty != rendererVersion || bones == null)
                    {
                        bones = skinned.bones; boneMatrices = new Matrix4x4[bones.Length];
                        blendShapes = new float[source == null ? 0 : source.blendShapeCount];
                        changed = true;
                    }
                    changed |= matrixChanged;
                    for (var i = 0; i < bones.Length; i++)
                    {
                        matrix = bones[i] == null ? Matrix4x4.identity : bones[i].localToWorldMatrix;
                        changed |= boneMatrices[i] != matrix; boneMatrices[i] = matrix;
                    }
                    for (var i = 0; i < blendShapes.Length; i++)
                    {
                        var weight = skinned.GetBlendShapeWeight(i);
                        changed |= blendShapes[i] != weight; blendShapes[i] = weight;
                    }
                }
                if (changed)
                {
                    if (source == null) Mesh.Clear();
                    // Bake in the full renderer transform frame; display applies scale once.
                    else if (skinned != null) skinned.BakeMesh(Mesh, true);
                    else
                    {
                        Object.DestroyImmediate(Mesh); Mesh = Object.Instantiate(source); Mesh.hideFlags = HideFlags.HideAndDontSave;
                    }
                    if (skinned != null) Mesh.RecalculateBounds();
                }
                var enabled = Original.enabled && Original.gameObject.activeInHierarchy;
                var layoutChanged = changed || matrixChanged || visible != enabled
                    || shadows != Original.shadowCastingMode || receivesShadows != Original.receiveShadows;
                sourceMesh = source; meshDirty = dirty; rendererDirty = rendererVersion; captured = true;
                sourceMatrix = Original.transform.localToWorldMatrix; visible = enabled;
                shadows = Original.shadowCastingMode; receivesShadows = Original.receiveShadows;
                return layoutChanged;
            }
            internal void CaptureMeshVersion()
            {
                meshDirty = sourceMesh == null ? 0 : EditorUtility.GetDirtyCount(sourceMesh);
            }
        }
        internal sealed class Display
        {
            internal Candidate Candidate;
            internal GameObject Root, Floor, Occluder;
            internal TextMesh Label;
            internal MeshRenderer[] Renderers;
        }
        readonly ComparisonSession session;
        internal readonly Scene Scene;
        internal readonly List<Display> Displays = new List<Display>();
        internal readonly Camera Camera;
        internal readonly RenderTexture[] Targets = new RenderTexture[2];
        readonly RenderTexture[] eyeBuffers = new RenderTexture[2];
        internal readonly PreviewLighting Lighting;
        internal Font Font { get; private set; }
        readonly List<Geometry> geometry = new List<Geometry>();
        readonly List<Material> materials = new List<Material>();
        readonly Light light;
        readonly Material groundMaterial;
        readonly IDisposable ndmfSession;
        int revision = -1;
        // Display bounds describe visible geometry; the avatar origin defines ground.
        Bounds displayBounds;
        Vector3 avatarOrigin;
        const float FloorVisualOffset = -.01f;
        bool disposed;
        internal float StartDistance { get; private set; } = 3;

        internal VrComparisonScene(ComparisonSession session, PreviewLighting lighting, int width, int height)
        {
            this.session = session; Lighting = lighting;
            Scene = EditorSceneManager.NewPreviewScene();
            try
            {
                Camera = NewObject("VR comparison camera").AddComponent<Camera>();
                Camera.enabled = false; Camera.scene = Scene; Camera.stereoTargetEye = StereoTargetEyeMask.None;
                Camera.nearClipPlane = .03f; Camera.farClipPlane = 150; Camera.allowHDR = false; Camera.allowMSAA = true;
                Camera.renderingPath = RenderingPath.Forward; Camera.clearFlags = CameraClearFlags.SolidColor;
                Camera.backgroundColor = new Color(.075f, .08f, .09f);
                var descriptor = new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGB32, 24) { msaaSamples = 4 };
                var samples = SystemInfo.GetRenderTextureSupportedMSAASampleCount(descriptor);
                for (var eye = 0; eye < 2; eye++)
                {
                    // OpenVR receives an ordinary single-sample texture. Render
                    // edges with MSAA first and explicitly resolve both eyes.
                    Targets[eye] = new RenderTexture(width, height, samples > 1 ? 0 : 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
                        { name = "Material Preview VR " + eye, hideFlags = HideFlags.HideAndDontSave, antiAliasing = 1 };
                    if (!Targets[eye].Create()) throw new InvalidOperationException("VR用RenderTextureを作成できません。");
                    if (samples > 1)
                    {
                        eyeBuffers[eye] = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
                            { name = "Material Preview VR MSAA " + eye, hideFlags = HideFlags.HideAndDontSave, antiAliasing = samples, bindTextureMS = true };
                        if (!eyeBuffers[eye].Create()) throw new InvalidOperationException("VR用MSAA RenderTextureを作成できません。");
                    }
                }
                light = NewObject("VR comparison light").AddComponent<Light>(); light.type = LightType.Directional;
                light.lightmapBakeType = LightmapBakeType.Realtime; light.shadowBias = .02f; light.shadowNormalBias = .1f;
                groundMaterial = new Material(Shader.Find("Standard")) { hideFlags = HideFlags.HideAndDontSave, color = new Color(.45f, .45f, .45f) };
                groundMaterial.SetFloat("_Glossiness", 0);
                Font = Font.CreateDynamicFontFromOSFont(new[] { "Meiryo", "Arial" }, 48); Font.hideFlags = HideFlags.HideAndDontSave;
                var previewType = Integration.Type("nadena.dev.ndmf.preview.PreviewSession");
                if (previewType != null || Integration.Type("nadena.dev.ndmf.BuildContext") != null)
                {
                    Integration.CheckNdmfPreviewApi(previewType);
                    ndmfSession = (IDisposable)Activator.CreateInstance(previewType);
                    previewType.GetMethod("OverrideCamera", new[] { typeof(Camera) }).Invoke(ndmfSession, new object[] { Camera });
                }
                foreach (var renderer in session.Renderers)
                {
                    if (renderer == null || !session.IncludesInPreview(renderer)) continue;
                    geometry.Add(new Geometry { Original = renderer,
                        Mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave },
                        Slots = session.Slots.Where(slot => slot.Renderer == renderer).OrderBy(slot => slot.Index).ToArray() });
                }
                Refresh(true);
            }
            catch { Dispose(); throw; }
        }
        internal GameObject NewObject(string name, Transform parent = null)
        {
            var obj = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
            SceneManager.MoveGameObjectToScene(obj, Scene);
            if (parent != null) obj.transform.SetParent(parent, false);
            return obj;
        }
        internal TextMesh Text(string text, Transform parent, Vector3 position, float size)
        {
            var obj = NewObject(text, parent); obj.transform.localPosition = position;
            var label = obj.AddComponent<TextMesh>(); label.font = Font; label.fontSize = 48;
            label.characterSize = size; label.anchor = TextAnchor.MiddleCenter; label.alignment = TextAlignment.Center;
            label.text = text; label.color = Color.white;
            var renderer = label.GetComponent<MeshRenderer>(); renderer.sharedMaterial = Font.material;
            renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
            return label;
        }
        GameObject Primitive(string name, PrimitiveType type, Transform parent)
        {
            var obj = GameObject.CreatePrimitive(type); obj.name = name; obj.hideFlags = HideFlags.HideAndDontSave;
            SceneManager.MoveGameObjectToScene(obj, Scene); obj.transform.SetParent(parent, false);
            Object.DestroyImmediate(obj.GetComponent<Collider>()); obj.GetComponent<Renderer>().sharedMaterial = groundMaterial;
            return obj;
        }
        internal void Refresh(bool refreshGeometry)
        {
            if (session == null) throw new InvalidOperationException("比較が終了しました。");
            var cases = new Candidate[] { null }.Concat(session.Candidates).ToArray();
            var rebuild = !Displays.Select(d => d.Candidate).SequenceEqual(cases);
            if (rebuild)
            {
                foreach (var display in Displays) Object.DestroyImmediate(display.Root);
                Displays.Clear();
                foreach (var candidate in cases)
                {
                    var display = new Display { Candidate = candidate, Root = NewObject(candidate?.Name ?? "AS IS"), Renderers = new MeshRenderer[geometry.Count] };
                    Displays.Add(display);
                    for (var i = 0; i < geometry.Count; i++)
                    {
                        var obj = NewObject(geometry[i].Original.name, display.Root.transform);
                        obj.AddComponent<MeshFilter>().sharedMesh = geometry[i].Mesh;
                        display.Renderers[i] = obj.AddComponent<MeshRenderer>();
                    }
                    display.Floor = Primitive("Floor", PrimitiveType.Plane, display.Root.transform);
                    display.Floor.GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.Off;
                    display.Occluder = Primitive("Occluder", PrimitiveType.Cube, display.Root.transform);
                    display.Label = Text("", display.Root.transform, Vector3.zero, .04f);
                }
            }
            var sourceOrigin = session.Avatar != null ? session.Avatar.transform.position : Vector3.zero;
            var displayChanged = rebuild || avatarOrigin != sourceOrigin;
            if (refreshGeometry)
                foreach (var item in geometry) displayChanged |= item.RefreshSource();
            if (displayChanged)
            {
                avatarOrigin = sourceOrigin;
                UpdateDisplayGeometry(sourceOrigin);
                displayBounds = CalculateDisplayBounds();
                UpdateComparisonLayout(cases.Length);
            }
            foreach (var display in Displays)
            {
                var label = display.Candidate?.Name ?? "AS IS";
                if (display.Label.text != label) display.Label.text = label;
            }
            if (rebuild || revision != session.Revision)
            {
                foreach (var material in materials) if (material != null) Object.DestroyImmediate(material);
                materials.Clear(); revision = session.Revision;
                foreach (var display in Displays)
                {
                    var candidate = display.Candidate;
                    var componentResult = candidate != null && session.EditingComponent ? session.ComponentResult(candidate) : null;
                    if (componentResult != null) materials.AddRange(componentResult.Where(m => m != null));
                    for (var i = 0; i < geometry.Count; i++)
                        display.Renderers[i].sharedMaterials = geometry[i].Slots.Select(slot =>
                        {
                            if (componentResult != null) return componentResult[session.Slots.IndexOf(slot)];
                            if (candidate == null || slot.Entry < 0 || slot.Baseline == null) return slot.Baseline;
                            var copy = ComparisonSession.Copy(slot.Baseline); materials.Add(copy);
                            MaterialDelta.Between(session.Entries[slot.Entry].Baseline, candidate.Materials[slot.Entry]).ApplyPreview(copy);
                            return copy;
                        }).ToArray();
                }
            }
            // Unity can change a shared mesh's dirty count while making another
            // renderer's snapshot. Record versions after all copies are complete
            // so our own snapshot work cannot trigger another round of copies.
            if (refreshGeometry)
                foreach (var item in geometry) item.CaptureMeshVersion();
        }
        void UpdateDisplayGeometry(Vector3 sourceOrigin)
        {
            foreach (var display in Displays)
                for (var i = 0; i < geometry.Count; i++)
                {
                    var item = geometry[i]; var renderer = display.Renderers[i]; var original = item.Original;
                    renderer.gameObject.SetActive(original != null && original.enabled && original.gameObject.activeInHierarchy);
                    if (original == null) continue;
                    renderer.GetComponent<MeshFilter>().sharedMesh = item.Mesh;
                    // Preserve every part's position relative to the avatar's ground origin.
                    renderer.transform.localPosition = original.transform.position - sourceOrigin;
                    renderer.transform.localRotation = original.transform.rotation;
                    renderer.transform.localScale = original.transform.lossyScale;
                    renderer.shadowCastingMode = original.shadowCastingMode; renderer.receiveShadows = original.receiveShadows;
                    renderer.lightProbeUsage = LightProbeUsage.Off; renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                }
        }
        Bounds CalculateDisplayBounds()
        {
            var result = new Bounds(Vector3.zero, Vector3.one);
            var first = true;
            var display = Displays[0];
            for (var i = 0; i < display.Renderers.Length; i++)
            {
                var renderer = display.Renderers[i];
                if (!renderer.gameObject.activeSelf || geometry[i].Mesh.vertexCount == 0) continue;
                var visibleBounds = renderer.bounds;
                visibleBounds.center -= display.Root.transform.position;
                if (first) { result = visibleBounds; first = false; } else result.Encapsulate(visibleBounds);
            }
            return result;
        }
        void UpdateComparisonLayout(int caseCount)
        {
            var spacing = Mathf.Max(2, Mathf.Max(displayBounds.size.x, displayBounds.size.z) + 1.5f);
            StartDistance = Mathf.Max(2.5f, Mathf.Min(40, spacing * caseCount * .55f));
            for (var index = 0; index < Displays.Count; index++)
            {
                var display = Displays[index];
                // +Z is the initial viewing side: descending world X reads left to right from there.
                display.Root.transform.position = new Vector3(((caseCount - 1) * .5f - index) * spacing, 0, 0);
                display.Label.transform.localPosition = new Vector3(displayBounds.center.x, displayBounds.max.y + .2f, displayBounds.center.z);
                // Floor height stays tied to ground; its footprint follows the visible geometry.
                display.Floor.transform.localPosition = new Vector3(displayBounds.center.x, FloorVisualOffset, displayBounds.center.z);
                display.Floor.transform.localScale = Vector3.one * spacing * .085f;
            }
        }
        internal void Draw(Pose head, Pose[] eyes, Matrix4x4[] projections, float trackingScale = 1)
        {
            Camera.nearClipPlane = .03f * trackingScale;
            light.transform.rotation = Quaternion.LookRotation(-Lighting.Direction);
            light.color = Lighting.Color; light.intensity = Lighting.Intensity;
            light.shadows = Lighting.Shadows ? LightShadows.Soft : LightShadows.None;
            var extent = Mathf.Max(.2f, displayBounds.extents.magnitude);
            foreach (var display in Displays)
            {
                display.Floor.SetActive(Lighting.Floor);
                display.Occluder.SetActive(Lighting.Occluder);
                display.Occluder.transform.localPosition = displayBounds.center + Lighting.Direction * extent * 1.2f;
                display.Occluder.transform.localScale = Vector3.one * extent * .6f;
                var direction = display.Label.transform.position - head.position;
                if (direction.sqrMagnitude > .001f) display.Label.transform.rotation = Quaternion.LookRotation(direction);
            }
            if (!Unsupported.SetOverrideLightingSettings(Scene)) throw new InvalidOperationException("VR専用照明を開始できません。");
            var shadows = QualitySettings.shadows; var distance = QualitySettings.shadowDistance;
            var resolution = QualitySettings.shadowResolution; var pixels = QualitySettings.pixelLightCount;
            try
            {
                RenderSettings.ambientMode = AmbientMode.Flat; RenderSettings.ambientLight = Color.white * Lighting.Ambient;
                RenderSettings.ambientIntensity = 1; RenderSettings.reflectionIntensity = 0; RenderSettings.fog = false; RenderSettings.skybox = null;
                QualitySettings.shadows = Lighting.Shadows ? ShadowQuality.All : ShadowQuality.Disable;
                QualitySettings.shadowDistance = 40; QualitySettings.shadowResolution = ShadowResolution.High;
                QualitySettings.pixelLightCount = Mathf.Max(1, pixels);
                for (var eye = 0; eye < 2; eye++)
                {
                    Camera.transform.SetPositionAndRotation(head.position + head.rotation * eyes[eye].position, head.rotation * eyes[eye].rotation);
                    var projection = projections[eye];
                    var depth = Camera.farClipPlane - Camera.nearClipPlane;
                    projection.m22 = (projection.m32 * (Camera.farClipPlane + Camera.nearClipPlane) - 2 * projection.m33) / depth;
                    projection.m23 = (2 * projection.m32 * Camera.farClipPlane * Camera.nearClipPlane
                        - projection.m33 * (Camera.farClipPlane + Camera.nearClipPlane)) / depth;
                    Camera.projectionMatrix = projection; Camera.targetTexture = eyeBuffers[eye] ?? Targets[eye]; Camera.Render();
                    if (eyeBuffers[eye] != null) eyeBuffers[eye].ResolveAntiAliasedSurface(Targets[eye]);
                }
            }
            finally
            {
                Camera.targetTexture = null; QualitySettings.shadows = shadows; QualitySettings.shadowDistance = distance;
                QualitySettings.shadowResolution = resolution; QualitySettings.pixelLightCount = pixels;
                Unsupported.RestoreOverrideLightingSettings();
            }
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { ndmfSession?.Dispose(); }
            finally
            {
                foreach (var target in Targets) if (target != null) { target.Release(); Object.DestroyImmediate(target); }
                foreach (var buffer in eyeBuffers) if (buffer != null) { buffer.Release(); Object.DestroyImmediate(buffer); }
                foreach (var material in materials) if (material != null) Object.DestroyImmediate(material);
                foreach (var item in geometry) if (item.Mesh != null) Object.DestroyImmediate(item.Mesh);
                if (groundMaterial != null) Object.DestroyImmediate(groundMaterial);
                if (Font != null) Object.DestroyImmediate(Font);
                if (Scene.IsValid()) EditorSceneManager.ClosePreviewScene(Scene);
            }
        }
    }
}
