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
    internal sealed class ComparisonRenderer : IDisposable
    {
        sealed class Display
        {
            internal Renderer Original;
            internal GameObject Object;
            internal Mesh Mesh;
            internal MeshRenderer Renderer;
            internal MaterialSlot[] Slots;
            internal Material[] BaselineMaterials;
        }
        readonly Scene scene;
        readonly Camera camera;
        readonly Light light;
        readonly GameObject floor, occluder;
        readonly Material environmentMaterial;
        readonly IDisposable emptySession;
        readonly ComparisonSession session;
        readonly List<Display> displays = new List<Display>();
        readonly List<Material> drawingMaterials = new List<Material>();
        readonly Dictionary<Candidate, RenderTexture> targets = new Dictionary<Candidate, RenderTexture>();
        readonly RenderTexture baseline;
        internal float Yaw = 0, Pitch = 0, Zoom = 1;
        internal Vector3 PanOffset;
        internal int FocusEntry = -1;
        internal Color Background = new Color(.075f, .08f, .09f);
        internal Bounds Bounds;
        internal PreviewLighting Lighting = new PreviewLighting();
        int cachedRevision = -1;
        bool disposed;
        bool geometryReady;
        readonly Dictionary<Candidate, Material[][]> candidateMaterials = new Dictionary<Candidate, Material[][]>();

        internal ComparisonRenderer(ComparisonSession session)
        {
            this.session = session;
            scene = EditorSceneManager.NewPreviewScene();
            try
            {
            var cameraObject = NewObject("Material Preview Camera"); camera = cameraObject.AddComponent<Camera>(); camera.enabled = false;
            camera.scene = scene; camera.orthographic = true; camera.clearFlags = CameraClearFlags.SolidColor;
            camera.renderingPath = RenderingPath.Forward;
            camera.nearClipPlane = .01f; camera.farClipPlane = 1000;
            light = NewObject("Material Preview Light").AddComponent<Light>(); light.type = LightType.Directional;
            light.lightmapBakeType = LightmapBakeType.Realtime;
            light.shadowBias = .02f; light.shadowNormalBias = .1f;
            light.intensity = 1; light.transform.rotation = Quaternion.Euler(35, 180, 0);
            environmentMaterial = new Material(Shader.Find("Standard")) { hideFlags = HideFlags.HideAndDontSave, color = new Color(.45f, .45f, .45f) };
            environmentMaterial.SetFloat("_Glossiness", 0);
            floor = EnvironmentObject("Material Preview Floor", PrimitiveType.Plane);
            floor.GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.Off;
            occluder = EnvironmentObject("Material Preview Occluder", PrimitiveType.Cube);
            var previewType = Integration.Type("nadena.dev.ndmf.preview.PreviewSession");
            if (previewType != null || Integration.Type("nadena.dev.ndmf.BuildContext") != null)
            {
                Integration.CheckNdmfPreviewApi(previewType);
                emptySession = (IDisposable)Activator.CreateInstance(previewType);
                previewType.GetMethod("OverrideCamera", new[] {typeof(Camera)}).Invoke(emptySession, new object[] {camera});
            }
            foreach (var renderer in session.Renderers)
            {
                // Keep the complete avatar in the session for save validation, but only
                // create preview geometry for the selected avatar/outfit roots.
                var transform = renderer.transform;
                if (!session.Roots.Any(root => transform.IsChildOf(root.transform))) continue;
                var obj = NewObject(renderer.name);
                var mesh = new Mesh {hideFlags = HideFlags.HideAndDontSave};
                obj.AddComponent<MeshFilter>().sharedMesh = mesh;
                var slots = session.Slots.Where(s => s.Renderer == renderer).OrderBy(s => s.Index).ToArray();
                displays.Add(new Display {Original = renderer, Object = obj, Mesh = mesh, Renderer = obj.AddComponent<MeshRenderer>(),
                    Slots = slots, BaselineMaterials = slots.Select(s => s.Baseline).ToArray()});
            }
            baseline = Target();
            }
            catch { Dispose(); throw; }
        }
        GameObject NewObject(string name)
        {
            var obj = new GameObject(name) {hideFlags = HideFlags.HideAndDontSave};
            SceneManager.MoveGameObjectToScene(obj, scene); return obj;
        }
        GameObject EnvironmentObject(string name, PrimitiveType type)
        {
            var obj = GameObject.CreatePrimitive(type);
            obj.name = name; obj.hideFlags = HideFlags.HideAndDontSave;
            SceneManager.MoveGameObjectToScene(obj, scene);
            Object.DestroyImmediate(obj.GetComponent<Collider>());
            obj.GetComponent<Renderer>().sharedMaterial = environmentMaterial;
            obj.SetActive(false);
            return obj;
        }
        static RenderTexture Target() => new RenderTexture(512, 512, 24) {hideFlags = HideFlags.HideAndDontSave, name = "Material Preview"};
        internal RenderTexture Texture(Candidate candidate)
        {
            if (candidate == null) return baseline;
            if (!targets.TryGetValue(candidate, out var texture)) { texture = Target(); targets.Add(candidate, texture); }
            return texture;
        }
        internal void Update(IEnumerable<Candidate> visible, bool refreshGeometry = true)
        {
            refreshGeometry |= !geometryReady;
            bool first = true;
            bool firstVisible = true;
            var fullBounds = new Bounds(session.Avatar.transform.position, Vector3.one);
            foreach (var display in displays)
            {
                var original = display.Original;
                if (original == null) continue;
                if (refreshGeometry)
                {
                var renderer = original;
                display.Object.SetActive(original.enabled && original.gameObject.activeInHierarchy);
                if (renderer is SkinnedMeshRenderer smr) smr.BakeMesh(display.Mesh);
                else
                {
                    var source = renderer.GetComponent<MeshFilter>()?.sharedMesh;
                    if (source == null) { display.Object.SetActive(false); continue; }
                    // Copy native mesh data without assuming the imported Mesh is CPU-readable.
                    var copy = Object.Instantiate(source);
                    Object.DestroyImmediate(display.Mesh); display.Mesh = copy;
                    display.Object.GetComponent<MeshFilter>().sharedMesh = copy;
                }
                display.Mesh.RecalculateBounds();
                display.Object.transform.SetPositionAndRotation(renderer.transform.position, renderer.transform.rotation);
                display.Object.transform.localScale = renderer.transform.lossyScale;
                display.Renderer.shadowCastingMode = renderer.shadowCastingMode;
                display.Renderer.receiveShadows = renderer.receiveShadows;
                display.Renderer.lightProbeUsage = LightProbeUsage.Off;
                display.Renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                }
                if (!display.Object.activeSelf) continue;
                if (firstVisible) { fullBounds = display.Renderer.bounds; firstVisible = false; }
                else fullBounds.Encapsulate(display.Renderer.bounds);
                if (FocusEntry >= 0 && !display.Slots.Any(s => s.Entry == FocusEntry)) continue;
                if (first) { Bounds = display.Renderer.bounds; first = false; } else Bounds.Encapsulate(display.Renderer.bounds);
            }
            geometryReady = true;
            if (cachedRevision != session.Revision)
            {
                foreach (var material in drawingMaterials) Object.DestroyImmediate(material);
                drawingMaterials.Clear(); candidateMaterials.Clear(); cachedRevision = session.Revision;
                foreach (var removed in targets.Keys.Where(c => !session.Candidates.Contains(c)).ToArray())
                { targets[removed].Release(); Object.DestroyImmediate(targets[removed]); targets.Remove(removed); }
            }
            camera.backgroundColor = Background;
            var size = Mathf.Max(.1f, Mathf.Max(Bounds.extents.y, Bounds.extents.x));
            var rotation = Quaternion.Euler(Pitch, Yaw, 0);
            var center = Bounds.center + PanOffset;
            camera.transform.position = center + rotation * Vector3.forward * Mathf.Max(2, Bounds.size.magnitude * 2);
            camera.transform.LookAt(center);
            camera.orthographicSize = size * 1.12f * Zoom;
            camera.farClipPlane = Mathf.Max(10, Vector3.Distance(camera.transform.position, fullBounds.center) + fullBounds.size.magnitude * 3);
            // Keep the light's azimuth relative to the viewing direction, but elevation relative to the floor.
            var direction = Quaternion.Euler(0, Yaw, 0) * Lighting.Direction;
            light.transform.rotation = Quaternion.LookRotation(-direction);
            light.color = Lighting.Color; light.intensity = Lighting.Intensity;
            light.shadows = Lighting.Shadows ? LightShadows.Soft : LightShadows.None;
            floor.SetActive(Lighting.Floor && !firstVisible);
            var extent = Mathf.Max(.1f, fullBounds.extents.magnitude);
            floor.transform.position = new Vector3(fullBounds.center.x, fullBounds.min.y - .01f, fullBounds.center.z);
            floor.transform.localScale = Vector3.one * extent * .6f;
            occluder.SetActive(Lighting.Occluder && !firstVisible);
            occluder.transform.position = fullBounds.center + direction * extent * 1.2f;
            occluder.transform.localScale = Vector3.one * extent * .6f;

            // Unity's own PreviewRenderUtility uses this scene-scoped lighting override.
            // Never fall back to editing the active scene's RenderSettings.
            if (!Unsupported.SetOverrideLightingSettings(scene))
                throw new InvalidOperationException("プレビュー専用の照明設定を開始できませんでした。");
            var oldShadows = QualitySettings.shadows;
            var oldDistance = QualitySettings.shadowDistance;
            var oldResolution = QualitySettings.shadowResolution;
            var oldPixelLights = QualitySettings.pixelLightCount;
            try
            {
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = Color.white * Lighting.Ambient;
                RenderSettings.ambientIntensity = 1;
                RenderSettings.reflectionIntensity = 0;
                RenderSettings.fog = false;
                RenderSettings.skybox = null;
                QualitySettings.shadows = Lighting.Shadows ? ShadowQuality.All : ShadowQuality.Disable;
                QualitySettings.shadowDistance = camera.farClipPlane;
                QualitySettings.shadowResolution = ShadowResolution.High;
                QualitySettings.pixelLightCount = Mathf.Max(1, oldPixelLights);
                Draw(null);
                foreach (var candidate in visible) Draw(candidate);
            }
            finally
            {
                QualitySettings.shadows = oldShadows;
                QualitySettings.shadowDistance = oldDistance;
                QualitySettings.shadowResolution = oldResolution;
                QualitySettings.pixelLightCount = oldPixelLights;
                Unsupported.RestoreOverrideLightingSettings();
            }
        }
        internal void Pan(Vector2 delta, float viewportSize)
        {
            if (viewportSize <= 0) return;
            var scale = 2 * camera.orthographicSize / viewportSize;
            PanOffset += (-camera.transform.right * delta.x + camera.transform.up * delta.y) * scale;
        }
        void Draw(Candidate candidate)
        {
            Material[][] materials = null;
            if (candidate != null && !candidateMaterials.TryGetValue(candidate, out materials))
            {
                materials = new Material[displays.Count][]; candidateMaterials.Add(candidate, materials);
                var componentResult = session.EditingComponent ? session.ComponentResult(candidate) : null;
                if (componentResult != null) drawingMaterials.AddRange(componentResult.Where(m => m != null));
                for (int i = 0; i < displays.Count; i++)
                {
                    var slots = displays[i].Slots;
                    materials[i] = new Material[slots.Length];
                    for (int j = 0; j < slots.Length; j++)
                    {
                        var slot = slots[j];
                        if (componentResult != null) { materials[i][j] = componentResult[session.Slots.IndexOf(slot)]; continue; }
                        if (slot.Entry < 0 || slot.Baseline == null) { materials[i][j] = slot.Baseline; continue; }
                        var copy = ComparisonSession.Copy(slot.Baseline); drawingMaterials.Add(copy);
                        MaterialDelta.Between(session.Entries[slot.Entry].Baseline, candidate.Materials[slot.Entry]).ApplyPreview(copy);
                        materials[i][j] = copy;
                    }
                }
            }
            for (int i = 0; i < displays.Count; i++)
                displays[i].Renderer.sharedMaterials = materials == null ? displays[i].BaselineMaterials : materials[i];
            camera.targetTexture = Texture(candidate);
            try { camera.Render(); } finally { camera.targetTexture = null; }
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { emptySession?.Dispose(); }
            finally
            {
            foreach (var target in targets.Values) { target.Release(); Object.DestroyImmediate(target); }
            if (baseline != null) { baseline.Release(); Object.DestroyImmediate(baseline); }
            foreach (var material in drawingMaterials) if (material != null) Object.DestroyImmediate(material);
            if (environmentMaterial != null) Object.DestroyImmediate(environmentMaterial);
            foreach (var display in displays) if (display.Mesh != null) Object.DestroyImmediate(display.Mesh);
            if (scene.IsValid()) EditorSceneManager.ClosePreviewScene(scene);
            }
        }
    }
}
