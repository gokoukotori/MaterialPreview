using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace GokouKotori.MaterialPreview
{
    // MLIC is captured once, like AS IS. All candidates share its read-only output.
    [Serializable] internal sealed class MlicPreview
    {
        internal const string CanvasType = "net.rs64.TexTransTool.MultiLayerImage.MultiLayerImageCanvas";
        [Serializable] public sealed class Replacement
        {
            public int Slot;
            public string Property;
            public Texture Before, After;
        }
        [Serializable] public sealed class Phase
        {
            public string Name;
            public List<Replacement> Replacements = new List<Replacement>();
        }
        [Serializable] public sealed class Dependency
        {
            public Object Asset;
            public string Path, Hash;
        }
        public List<Phase> Phases = new List<Phase>();
        public List<Texture> Protected = new List<Texture>();
        public List<Object> Owned = new List<Object>();
        public List<Dependency> Dependencies = new List<Dependency>();

        internal static bool IsCanvas(Component component) => component != null && component.GetType().FullName == CanvasType;
        internal bool HasPhase(string phase) => Phases.Any(p => p.Name == phase);
        internal IEnumerable<PropertyValue> ReadOnlyProperties(Material material) => Protected.Count == 0 ? Enumerable.Empty<PropertyValue>()
            : MaterialDelta.Values(material).Where(p => p.Type == ShaderPropertyType.Texture && p.Texture != null && Protected.Contains(p.Texture));

        internal void Capture(ComparisonSession session, string name, Component[] components, Material[] inputs)
        {
            var phase = new Phase { Name = name };
            var scene = EditorSceneManager.NewPreviewScene();
            GameObject clone = null;
            object domain = null, holder = null;
            try
            {
                clone = Object.Instantiate(session.Avatar);
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(clone, scene);
                clone.hideFlags = HideFlags.HideAndDontSave;
                var originals = session.Avatar.GetComponentsInChildren<Component>(true);
                var copies = clone.GetComponentsInChildren<Component>(true);
                var map = originals.Select((c, i) => new { c, copy = copies[i] }).Where(p => p.c != null).ToDictionary(p => p.c, p => p.copy);
                var renderers = session.Slots.Select(s => (Renderer)map[s.Renderer]).Distinct().ToList();
                foreach (var renderer in clone.GetComponentsInChildren<Renderer>(true)) renderer.sharedMaterials = new Material[renderer.sharedMaterials.Length];
                for (var i = 0; i < session.Slots.Count; i++)
                {
                    var renderer = (Renderer)map[session.Slots[i].Renderer];
                    var materials = renderer.sharedMaterials;
                    materials[session.Slots[i].Index] = inputs[i];
                    renderer.sharedMaterials = materials;
                }
                holder = Activator.CreateInstance(Integration.Require("net.rs64.TexTransTool.TempAssetHolder"));
                domain = Activator.CreateInstance(Integration.Require("net.rs64.TexTransTool.RenderersDomain"), renderers, holder);
                foreach (var pair in map) Integration.InvokeInstance(domain, "RegisterReplacement", pair.Key, pair.Value);
                foreach (var previous in Phases.SelectMany(p => p.Replacements).Where(r => r.Before != null).GroupBy(r => r.After).Select(g => g.First()))
                    Integration.InvokeInstance(domain, "RegisterReplacement", previous.Before, previous.After);
                for (var i = 0; i < inputs.Length; i++)
                    if (inputs[i] != null)
                    {
                        Integration.InvokeInstance(holder, "SaveAsset", inputs[i]);
                        Integration.InvokeInstance(domain, "RegisterReplacement", session.Slots[i].Source, inputs[i]);
                    }
                foreach (var component in components)
                {
                    if (!IsCanvas(component) && !ExistingOverrides.IsTtt(component)) continue;
                    if (IsCanvas(component))
                    {
                        var texture = Integration.Get(Integration.Get(component, "TargetTexture"), "SelectTexture") as Texture;
                        if (texture != null && !Protected.Contains(texture)) Protected.Add(texture);
                        CaptureDependencies(component);
                    }
                    Integration.InvokeInstance(map[component], "Apply", domain);
                }
                ((IDisposable)domain).Dispose(); domain = null;
                for (var i = 0; i < session.Slots.Count; i++)
                {
                    var slot = session.Slots[i];
                    var output = ((Renderer)map[slot.Renderer]).sharedMaterials[slot.Index];
                    if (slot.Baseline == null || output == null) continue;
                    foreach (var value in MaterialDelta.Values(output).Where(p => p.Type == ShaderPropertyType.Texture))
                    {
                        var before = slot.Baseline.GetTexture(value.Name);
                        if (value.Texture == before || value.Texture == null || AssetDatabase.Contains(value.Texture)) continue;
                        phase.Replacements.Add(new Replacement { Slot = i, Property = value.Name, Before = before, After = value.Texture });
                        if (!Protected.Contains(value.Texture)) Protected.Add(value.Texture);
                    }
                }
                var generated = (ISet<Object>)Integration.Get(holder, "Transferred");
                foreach (var asset in generated.ToArray())
                    if (asset != null && !inputs.Contains(asset) && phase.Replacements.Any(r => r.After == asset))
                    {
                        // Transfer ownership; compressed, non-readable textures cannot be instantiated.
                        generated.Remove(asset); Owned.Add(asset); asset.hideFlags = HideFlags.HideAndDontSave;
                    }
                Phases.Add(phase);
            }
            finally
            {
                try { if (domain is IDisposable disposable) disposable.Dispose(); }
                finally
                {
                    if (holder is IDisposable temporary) temporary.Dispose();
                    if (clone != null) Object.DestroyImmediate(clone);
                    EditorSceneManager.ClosePreviewScene(scene);
                    foreach (var input in inputs) if (input != null) Object.DestroyImmediate(input);
                }
            }
        }

        internal void Apply(ComparisonSession session, string name)
        {
            var phase = Phases.FirstOrDefault(p => p.Name == name);
            if (phase == null) return;
            foreach (var replacement in phase.Replacements)
            {
                var material = session.Slots[replacement.Slot].Baseline;
                if (material == null || !material.HasProperty(replacement.Property) || material.GetTexture(replacement.Property) != replacement.Before)
                    throw new InvalidOperationException("MLICの対象画像が変更されました。比較を開始し直してください。");
                material.SetTexture(replacement.Property, replacement.After);
            }
        }

        void CaptureDependencies(Component canvas)
        {
            foreach (var component in canvas.GetComponentsInChildren<Component>(true).Where(c => c != null))
            {
                using (var serialized = new SerializedObject(component))
                {
                    var iterator = serialized.GetIterator();
                    while (iterator.Next(true))
                    {
                        if (iterator.propertyType != SerializedPropertyType.ObjectReference) continue;
                        var asset = iterator.objectReferenceValue;
                        var path = AssetDatabase.GetAssetPath(asset);
                        if (string.IsNullOrEmpty(path) || Dependencies.Any(d => d.Path == path)) continue;
                        Dependencies.Add(new Dependency { Asset = asset, Path = path, Hash = AssetDatabase.GetAssetDependencyHash(path).ToString() });
                    }
                }
            }
        }

        internal void Validate()
        {
            foreach (var dependency in Dependencies)
                if (dependency.Asset == null || AssetDatabase.GetAssetPath(dependency.Asset) != dependency.Path
                    || AssetDatabase.GetAssetDependencyHash(dependency.Path).ToString() != dependency.Hash)
                    throw new InvalidOperationException("MLICの入力画像または設定アセットが変更されました。比較を開始し直してください。");
        }

        internal void ValidateCandidate(ComparisonSession session, Candidate candidate)
        {
            if (Protected.Count == 0) return;
            for (var i = 0; i < session.Entries.Count; i++)
            {
                var baseline = session.Entries[i].Baseline;
                var properties = ReadOnlyProperties(baseline).ToArray();
                if (properties.Length == 0) continue;
                var material = candidate.Materials[i];
                if (material.shader == null || properties.Any(p => material.shader.FindPropertyIndex(p.Name) < 0
                    || !p.Same(PropertyValue.Read(material, material.shader.FindPropertyIndex(p.Name)))))
                    throw new InvalidOperationException("MLICの対象画像は読み取り専用です。Texture・Scale・Offsetの変更や、対象画像のプロパティを持たないShaderへの変更はできません。");
            }
            if (session.EditingComponent)
            {
                var names = session.Entries.SelectMany(e => ReadOnlyProperties(e.Baseline)).Select(p => p.Name).ToHashSet();
                foreach (var name in names)
                {
                    var before = session.OriginalOverride.Properties.FirstOrDefault(p => p.Name == name);
                    var after = candidate.Override.Properties.FirstOrDefault(p => p.Name == name);
                    if (before == null ? after != null : !before.Same(after))
                        throw new InvalidOperationException(name + ": MLICの対象画像のOverrideは変更・解除できません。");
                }
            }
        }

        internal void Release()
        {
            foreach (var asset in Owned) if (asset != null) Object.DestroyImmediate(asset);
            Owned.Clear(); Phases.Clear(); Protected.Clear(); Dependencies.Clear();
        }

        internal bool SameOutput(Texture expected, Texture actual)
        {
            if (!Owned.Contains(expected) || !(expected is Texture2D left) || !(actual is Texture2D right)
                || left.width != right.width || left.height != right.height || left.format != right.format
                || left.mipmapCount != right.mipmapCount || left.isDataSRGB != right.isDataSRGB
                || left.filterMode != right.filterMode || left.anisoLevel != right.anisoLevel || left.mipMapBias != right.mipMapBias
                || left.wrapModeU != right.wrapModeU || left.wrapModeV != right.wrapModeV || left.wrapModeW != right.wrapModeW) return false;
            // Readback does not accept BC-compressed textures. Decode each mip to a float target first.
            var shader = AssetDatabase.LoadAssetAtPath<Shader>("Packages/com.gokoukotori.material-preview/Editor/MlicTextureReadback.shader");
            if (shader == null || !shader.isSupported) throw new InvalidOperationException("MLIC画像を検証するシェーダーが利用できません。");
            var decode = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            var active = RenderTexture.active; var srgb = GL.sRGBWrite;
            try
            {
                GL.sRGBWrite = false;
                for (var mip = 0; mip < left.mipmapCount; mip++)
                {
                    var width = Mathf.Max(1, left.width >> mip); var height = Mathf.Max(1, left.height >> mip);
                    var a = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
                    var b = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
                    try
                    {
                        decode.SetFloat("_Mip", mip);
                        Graphics.Blit(left, a, decode); Graphics.Blit(right, b, decode);
                        var firstRequest = AsyncGPUReadback.Request(a); var secondRequest = AsyncGPUReadback.Request(b);
                        firstRequest.WaitForCompletion(); secondRequest.WaitForCompletion();
                        if (firstRequest.hasError || secondRequest.hasError) return false;
                        var first = firstRequest.GetData<byte>(); var second = secondRequest.GetData<byte>();
                        if (first.Length != second.Length) return false;
                        for (var i = 0; i < first.Length; i++) if (first[i] != second[i]) return false;
                    }
                    finally { RenderTexture.ReleaseTemporary(a); RenderTexture.ReleaseTemporary(b); }
                }
                return true;
            }
            finally { RenderTexture.active = active; GL.sRGBWrite = srgb; Object.DestroyImmediate(decode); }
        }
    }
}
