using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace GokouKotori.MaterialPreview
{
    [Serializable] internal sealed class ExistingLayer
    {
        public Component Component;
        public string Label;
        public Material Before, After;
        public List<string> ExplicitProperties = new List<string>();
    }

    internal static class ExistingOverrides
    {
        internal const string Ao = "Aoyon.MaterialEditor.MaterialEditorComponent";
        internal const string Ttt = "net.rs64.TexTransTool.MaterialModifier";
        internal static bool IsSupported(Component component) => component != null && (component.GetType().FullName == Ttt || component.GetType().FullName == Ao);
        internal static bool IsTtt(Component component) => component.GetType().FullName == Ttt;
        internal static object Settings(Component component) => IsTtt(component) ? component : Integration.Get(component, "OverrideSettings");

        internal static GameObject FindAvatar(GameObject target)
        {
            if (target == null || EditorUtility.IsPersistent(target) || !target.scene.IsValid()
                || UnityEditor.SceneManagement.PrefabStageUtility.GetPrefabStage(target) != null) return null;
            for (var t = target.transform; t != null; t = t.parent)
                if (ComparisonSession.IsAvatar(t.gameObject)) return t.gameObject;
            return null;
        }

        internal static string Location(Component component)
        {
            if (component == null) return "編集対象が削除されました";
            var path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(component);
            return component.name + " / " + component.GetType().Name
                + (string.IsNullOrEmpty(path) ? "（Scene）" : "（Prefab: " + path + " / Sceneで更新）");
        }

        internal static void Capture(ComparisonSession session, Component selected = null, OverrideState replacement = null, bool stopAtSelected = false, bool capturePrefab = true)
        {
            if (capturePrefab) foreach (var slot in session.Slots.Where(s => s.Source != null)) CapturePrefab(slot);
            var aoType = Integration.Type(Ao);
            var aoComponents = aoType == null ? Array.Empty<Component>() : session.Avatar.GetComponentsInChildren(aoType, true);
            if (aoComponents.Length > 0)
            {
                var processor = Integration.Require("Aoyon.MaterialEditor.Processor.MaterialEditorProcessor");
                var assignments = new AoAssignments(session.Slots);
                var merged = new Dictionary<MaterialSlot, object>();
                foreach (var component in aoComponents)
                {
                    if (!(stopAtSelected && component == selected) && !(bool)Integration.Invoke(processor, "IsEffective", component, null)) continue;
                    foreach (var slot in assignments.Select(component))
                    {
                        if (!merged.TryGetValue(slot, out var settings))
                        {
                            settings = Activator.CreateInstance(Integration.Require("Aoyon.MaterialEditor.MaterialOverrideSettings"), true);
                            merged.Add(slot, settings);
                        }
                        var sourceSettings = component == selected && replacement != null ? replacement.ToAoSettings() : Settings(component);
                        Integration.Invoke(settings.GetType(), "MergeInto", sourceSettings, settings);
                        AddLayer(slot, component, settings, component == selected ? replacement : null, stopAtSelected && component == selected);
                    }
                    if (stopAtSelected && component == selected) return;
                }
            }
            var behaviorType = Integration.Type("net.rs64.TexTransTool.TexTransBehavior");
            if (behaviorType == null || session.Avatar.GetComponentsInChildren(behaviorType, true).Length == 0) return;
            var search = Integration.Require("net.rs64.TexTransTool.TexTransBehaviorSearch");
            var phases = (IDictionary)Integration.Invoke(search, "FindAtPhase", session.Avatar);
            var order = (IEnumerable)Integration.Invoke(Integration.Require("net.rs64.TexTransTool.TexTransPhaseUtility"), "EnumerateAllPhase");
            foreach (var phase in order)
            {
                var active = ((IEnumerable)phases[phase]).Cast<Component>().Where(component =>
                    (stopAtSelected && component == selected) || ((bool)Integration.Invoke(search, "CheckIsActiveBehavior", component, session.Avatar)
                        && !EditorOnly(component.transform))).ToArray();
                var canvases = active.Where(MlicPreview.IsCanvas).ToArray();
                var inputs = canvases.Length > 0 && !session.Mlic.HasPhase(phase.ToString()) ? session.Slots.Select(s => ComparisonSession.Copy(s.Baseline)).ToArray() : null;
                try
                {
                    foreach (var component in active)
                    {
                        if (MlicPreview.IsCanvas(component)) continue;
                        if (!IsTtt(component))
                        {
                            session.Warnings.Add(component.name + ": " + component.GetType().Name + " の生成・合成結果は表示対象外です。保存時に最終結果を検証します。");
                            continue;
                        }
                        var material = Integration.Get(component, "TargetMaterial") as Material;
                        foreach (var slot in session.Slots.Where(s => s.Source != null && s.Source == material)) AddLayer(slot, component, null, component == selected ? replacement : null, stopAtSelected && component == selected);
                        if (stopAtSelected && component == selected) return;
                    }
                    if (canvases.Length > 0)
                    {
                        if (inputs != null) session.Mlic.Capture(session, phase.ToString(), active, inputs);
                        var before = session.Slots.Select(s => ComparisonSession.Copy(s.Baseline)).ToArray();
                        try
                        {
                            session.Mlic.Apply(session, phase.ToString());
                            for (var i = 0; i < session.Slots.Count; i++)
                            {
                                var slot = session.Slots[i];
                                if (before[i] == null || slot.Baseline == null) continue;
                                var names = MaterialDelta.Between(before[i], slot.Baseline).Properties.Select(p => p.Name).ToList();
                                if (names.Count == 0) continue;
                                slot.Layers.Add(new ExistingLayer { Component = canvases[0], Label = "MLIC（読み取り専用）: " + string.Join(", ", canvases.Select(c => c.name)),
                                    Before = before[i], After = ComparisonSession.Copy(slot.Baseline), ExplicitProperties = names });
                                before[i] = null;
                            }
                        }
                        finally { foreach (var material in before) if (material != null) UnityEngine.Object.DestroyImmediate(material); }
                    }
                }
                finally { if (inputs != null) foreach (var material in inputs) if (material != null) UnityEngine.Object.DestroyImmediate(material); }
            }
        }

        internal sealed class AoAssignments
        {
            readonly object set;
            readonly Dictionary<object, MaterialSlot> slots = new Dictionary<object, MaterialSlot>();
            internal AoAssignments(IEnumerable<MaterialSlot> source)
            {
                var assignmentType = Integration.Require("Aoyon.MaterialEditor.Processor.MaterialAssignment");
                var slotType = Integration.Require("Aoyon.MaterialEditor.Processor.MaterialSlotId");
                set = Activator.CreateInstance(typeof(HashSet<>).MakeGenericType(assignmentType));
                var add = set.GetType().GetMethod("Add");
                foreach (var slot in source.Where(s => s.Source != null))
                {
                    var id = Activator.CreateInstance(slotType, new object[] { slot.Renderer, slot.Index });
                    var assignment = Activator.CreateInstance(assignmentType, new object[] { id, slot.Source });
                    add.Invoke(set, new[] { assignment }); slots.Add(assignment, slot);
                }
            }
            internal IEnumerable<MaterialSlot> Select(Component component)
            {
                var processor = Integration.Require("Aoyon.MaterialEditor.Processor.MaterialEditorProcessor");
                var targets = (IEnumerable)Integration.Invoke(processor, "SelectTargetAssignments", set, component, null, null, null);
                foreach (var target in targets) yield return slots[target];
            }
        }

        internal static bool EditorOnly(Transform transform)
        {
            for (var t = transform; t != null; t = t.parent) if (t.CompareTag("EditorOnly")) return true;
            return false;
        }

        static void CapturePrefab(MaterialSlot slot)
        {
            var chain = new List<Renderer> {slot.Renderer};
            for (var source = PrefabUtility.GetCorrespondingObjectFromSource(slot.Renderer); source != null;
                source = PrefabUtility.GetCorrespondingObjectFromSource(source)) chain.Add(source);
            chain.Reverse();
            for (int i = 1; i < chain.Count; i++)
            {
                var previous = chain[i - 1].sharedMaterials;
                var current = chain[i].sharedMaterials;
                var before = slot.Index < previous.Length ? previous[slot.Index] : null;
                var after = slot.Index < current.Length ? current[slot.Index] : null;
                var serialized = new SerializedObject(chain[i]);
                var materials = serialized.FindProperty("m_Materials");
                bool explicitOverride = materials != null && slot.Index < materials.arraySize
                    && materials.GetArrayElementAtIndex(slot.Index).prefabOverride;
                if (before == after && !explicitOverride) continue;
                var path = AssetDatabase.GetAssetPath(chain[i]);
                slot.Layers.Add(new ExistingLayer {Label = "Prefab割り当て: " + (string.IsNullOrEmpty(path) ? "Scene" : path)
                    + " / " + (before == null ? "未割り当て" : before.name) + " → " + (after == null ? "未割り当て" : after.name),
                    Before = ComparisonSession.Copy(before), After = ComparisonSession.Copy(after),
                    ExplicitProperties = new List<string> {"Material参照（値が同じでも明示Overrideを保持）"}});
            }
            if (slot.Source.isVariant && slot.Source.parent != null)
                slot.Layers.Add(new ExistingLayer {Label = "Material Variant: " + slot.Source.parent.name + " → " + slot.Source.name,
                    Before = ComparisonSession.Copy(slot.Source.parent), After = ComparisonSession.Copy(slot.Source),
                    ExplicitProperties = MaterialDelta.Values(slot.Source).Where(p => slot.Source.IsPropertyOverriden(Shader.PropertyToID(p.Name))).Select(p => p.Name).ToList()});
        }

        static void AddLayer(MaterialSlot slot, Component component, object mergedAoSettings = null, OverrideState replacement = null, bool includeEmpty = false)
        {
            var names = (replacement == null ? ExplicitNames(component) : replacement.Names()).ToList();
            if (names.Count == 0 && !includeEmpty) return;
            var before = ComparisonSession.Copy(slot.Baseline);
            try
            {
                if (mergedAoSettings == null)
                {
                    if (replacement == null) ApplySettings(component, slot.Baseline);
                    else replacement.ApplyTtt(slot.Baseline);
                }
                else
                {
                    slot.Baseline.shader = slot.Source.shader;
                    slot.Baseline.CopyPropertiesFromMaterial(slot.Source);
                    Integration.Invoke(Integration.Require("Aoyon.MaterialEditor.MaterialUtility"), "ApplyOverrideSettings", slot.Baseline, mergedAoSettings);
                }
                slot.Layers.Add(new ExistingLayer {Component = component, Label = Location(component),
                    Before = before, After = ComparisonSession.Copy(slot.Baseline), ExplicitProperties = names});
            }
            catch { UnityEngine.Object.DestroyImmediate(before); throw; }
        }

        internal static IEnumerable<string> ExplicitNames(Component component)
        {
            bool ttt = IsTtt(component); var settings = Settings(component);
            if ((bool)Integration.Get(settings, ttt ? "IsOverrideShader" : "OverrideShader")) yield return "Shader";
            if ((bool)Integration.Get(settings, ttt ? "IsOverrideRenderQueue" : "OverrideRenderQueue")) yield return "Render Queue";
            foreach (var property in (IEnumerable)Integration.Get(settings, ttt ? "OverrideProperties" : "PropertyOverrides"))
                yield return (string)Integration.Get(property, "PropertyName");
        }

        internal static void ApplySettings(Component component, Material material)
        {
            if (IsTtt(component)) Integration.Invoke(component.GetType(), "ConfigureMaterial", material, component);
            else Integration.Invoke(Integration.Require("Aoyon.MaterialEditor.MaterialUtility"), "ApplyOverrideSettings", material, Settings(component));
        }

        internal static void Merge(Component component, MaterialDelta delta)
        {
            bool ttt = IsTtt(component); var settings = Settings(component);
            if (delta.ShaderChanged)
            {
                Integration.Set(settings, ttt ? "IsOverrideShader" : "OverrideShader", true);
                Integration.Set(settings, ttt ? "OverrideShader" : "TargetShader", delta.Shader);
            }
            if (delta.QueueChanged)
            {
                Integration.Set(settings, ttt ? "IsOverrideRenderQueue" : "OverrideRenderQueue", true);
                Integration.Set(settings, ttt ? "OverrideRenderQueue" : "RenderQueueValue", delta.Queue);
            }
            var properties = (IList)Integration.Get(settings, ttt ? "OverrideProperties" : "PropertyOverrides");
            var type = Integration.Require(ttt ? "net.rs64.TexTransTool.MaterialProperty" : "Aoyon.MaterialEditor.MaterialProperty");
            foreach (var value in delta.Properties)
            {
                var property = Activator.CreateInstance(type);
                Integration.Set(property, "PropertyName", value.Name); Integration.Set(property, "PropertyType", value.Type);
                Integration.Set(property, "ColorValue", value.Color); Integration.Set(property, "VectorValue", value.Vector);
                Integration.Set(property, "FloatValue", value.Float); Integration.Set(property, "IntValue", value.Int);
                Integration.Set(property, "TextureValue", value.Texture); Integration.Set(property, "TextureScaleValue", value.Scale);
                Integration.Set(property, "TextureOffsetValue", value.Offset);
                for (int i = properties.Count - 1; i >= 0; i--)
                    if ((string)Integration.Get(properties[i], "PropertyName") == value.Name) properties.RemoveAt(i);
                properties.Add(property);
            }
        }
    }
}
