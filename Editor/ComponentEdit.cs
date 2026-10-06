using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GokouKotori.MaterialPreview
{
    // A serialized copy of settings only. Target selection and activation stay on the original component.
    [Serializable] internal sealed class OverrideState
    {
        public bool ShaderEnabled, QueueEnabled;
        public Shader Shader;
        public int Queue;
        public List<PropertyValue> Properties = new List<PropertyValue>();
        static readonly string[] ValueFields = { "Color", "Vector", "Float", "Int", "Texture", "Scale", "Offset" };
        static string ExternalField(string name) => name == "Scale" || name == "Offset" ? "Texture" + name + "Value" : name + "Value";
        internal OverrideState Clone() => JsonUtility.FromJson<OverrideState>(JsonUtility.ToJson(this));
        internal static OverrideState Read(Component component)
        {
            var ttt = ExistingOverrides.IsTtt(component); var settings = ExistingOverrides.Settings(component);
            var state = new OverrideState
            {
                ShaderEnabled = (bool)Integration.Get(settings, ttt ? "IsOverrideShader" : "OverrideShader"),
                Shader = (Shader)Integration.Get(settings, ttt ? "OverrideShader" : "TargetShader"),
                QueueEnabled = (bool)Integration.Get(settings, ttt ? "IsOverrideRenderQueue" : "OverrideRenderQueue"),
                Queue = (int)Integration.Get(settings, ttt ? "OverrideRenderQueue" : "RenderQueueValue")
            };
            foreach (var item in (IEnumerable)Integration.Get(settings, ttt ? "OverrideProperties" : "PropertyOverrides"))
            {
                var value = new PropertyValue { Name = (string)Integration.Get(item, "PropertyName"),
                    Type = (UnityEngine.Rendering.ShaderPropertyType)Integration.Get(item, "PropertyType") };
                foreach (var field in ValueFields) Integration.Set(value, field, Integration.Get(item, ExternalField(field)));
                state.Properties.Add(value);
            }
            return state;
        }
        internal IEnumerable<string> Names()
        {
            if (ShaderEnabled) yield return "Shader";
            if (QueueEnabled) yield return "Render Queue";
            foreach (var property in Properties) yield return property.Name;
        }
        internal void Merge(MaterialDelta delta)
        {
            if (delta.ShaderChanged) { ShaderEnabled = true; Shader = delta.Shader; }
            if (delta.QueueChanged) { QueueEnabled = true; Queue = delta.Queue; }
            foreach (var value in delta.Properties)
            {
                Properties.RemoveAll(p => p.Name == value.Name);
                Properties.Add(value);
            }
        }
        internal void Remove(string name)
        {
            if (name == "Shader") ShaderEnabled = false;
            else if (name == "Render Queue") QueueEnabled = false;
            else Properties.RemoveAll(p => p.Name == name);
        }
        internal void Write(Component component) => WriteSettings(ExistingOverrides.Settings(component), ExistingOverrides.IsTtt(component));
        void WriteSettings(object settings, bool ttt)
        {
            Integration.Set(settings, ttt ? "IsOverrideShader" : "OverrideShader", ShaderEnabled);
            Integration.Set(settings, ttt ? "OverrideShader" : "TargetShader", Shader);
            Integration.Set(settings, ttt ? "IsOverrideRenderQueue" : "OverrideRenderQueue", QueueEnabled);
            Integration.Set(settings, ttt ? "OverrideRenderQueue" : "RenderQueueValue", Queue);
            var list = (IList)Integration.Get(settings, ttt ? "OverrideProperties" : "PropertyOverrides");
            list.Clear();
            var type = Integration.Require(ttt ? "net.rs64.TexTransTool.MaterialProperty" : "Aoyon.MaterialEditor.MaterialProperty");
            foreach (var value in Properties)
            {
                var item = Activator.CreateInstance(type);
                Integration.Set(item, "PropertyName", value.Name); Integration.Set(item, "PropertyType", value.Type);
                foreach (var field in ValueFields) Integration.Set(item, ExternalField(field), Integration.Get(value, field));
                list.Add(item);
            }
        }
        internal object ToAoSettings()
        {
            var settings = Activator.CreateInstance(Integration.Require("Aoyon.MaterialEditor.MaterialOverrideSettings"), true);
            WriteSettings(settings, false); return settings;
        }
        internal void ApplyTtt(Material material)
        {
            // Preserve TTT's own shader/queue and property-type semantics.
            var type = Integration.Require(ExistingOverrides.Ttt);
            var propertyType = Integration.Require("net.rs64.TexTransTool.MaterialProperty");
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(propertyType));
            foreach (var value in Properties)
            {
                var item = Activator.CreateInstance(propertyType);
                Integration.Set(item, "PropertyName", value.Name); Integration.Set(item, "PropertyType", value.Type);
                foreach (var field in ValueFields) Integration.Set(item, ExternalField(field), Integration.Get(value, field));
                list.Add(item);
            }
            Integration.Invoke(type, "ConfigureMaterial", material, ShaderEnabled, Shader, QueueEnabled, Queue, list);
        }
    }

    internal sealed partial class ComparisonSession
    {
        public bool EditingComponent;
        public Component EditTarget;
        public OverrideState OriginalOverride;

        internal static ComparisonSession CreateForComponent(Component component, Renderer previewRenderer = null)
        {
            if (!ExistingOverrides.IsSupported(component)) throw new InvalidOperationException("MaterialModifierまたはAOMEを選択してください。");
            var avatar = ExistingOverrides.FindAvatar(component.gameObject);
            if (avatar == null) throw new InvalidOperationException("Scene上のアバター内の設定を選択してください。");
            if (previewRenderer != null && (!RendererEdit.CanOpen(previewRenderer) || !Belongs(previewRenderer, avatar)))
                throw new InvalidOperationException("編集設定と同じアバター内のRendererを指定してください。");
            var session = Create(avatar, new[] {avatar}, previewRenderer);
            try
            {
                foreach (var candidate in session.Candidates.ToArray()) session.Remove(candidate);
                foreach (var entry in session.Entries) Object.DestroyImmediate(entry.Baseline);
                session.Entries.Clear();
                session.EditingComponent = true; session.EditTarget = component;
                session.OriginalOverride = OverrideState.Read(component);
                var evaluation = session.Evaluate(session.OriginalOverride, true);
                try
                {
                    for (int i = 0; i < session.Slots.Count; i++)
                    {
                        var slot = session.Slots[i]; slot.Entry = -1; slot.InScope = false;
                        var evaluated = evaluation.Slots[i];
                        var layer = evaluated.Layers.LastOrDefault(l => l.Component == component);
                        if (layer == null) continue;
                        slot.InScope = true;
                        var entry = session.Entries.FindIndex(e => e.Source == slot.Source
                            && !MaterialDelta.Between(e.Baseline, evaluated.Baseline).Changed
                            && !MaterialDelta.Between(e.OverrideBaseline, layer.Before).Changed);
                        if (entry < 0)
                        {
                            entry = session.Entries.Count;
                            session.Entries.Add(new MaterialEntry { Source = slot.Source, Baseline = Copy(evaluated.Baseline),
                                OverrideBaseline = Copy(layer.Before),
                                OriginalJson = EditorJsonUtility.ToJson(slot.Source), DependencyHash = Dependencies(slot.Source), Layers = slot.Layers });
                        }
                        slot.Entry = entry;
                    }
                }
                finally { evaluation.Release(); Object.DestroyImmediate(evaluation); }
                if (session.Entries.Count == 0) throw new InvalidOperationException("選択した設定の対象マテリアルが見つかりません。対象指定とTTTの配置を確認してください。");
                session.Warnings.Add("コンポーネント編集: 編集欄は選択設定の適用直後、3D比較は後続設定を含む結果です。無効な設定は3D結果に反映されません。");
                session.AddCandidate(null);
                return session;
            }
            catch { session.Release(); Object.DestroyImmediate(session); throw; }
        }

        // Scratch evaluation never changes scene objects. The original component resolves target paths.
        ComparisonSession Evaluate(OverrideState state, bool stopAtTarget)
        {
            var result = CreateInstance<ComparisonSession>(); result.hideFlags = HideFlags.HideAndDontSave;
            result.Avatar = Avatar;
            result.Mlic = Mlic; result.OwnsMlic = false;
            try
            {
                result.Slots = Slots.Select(s => new MaterialSlot { Renderer = s.Renderer, Index = s.Index, Source = s.Source, Baseline = Copy(s.Source) }).ToList();
                ExistingOverrides.Capture(result, EditTarget, state, stopAtTarget, false);
                return result;
            }
            catch { result.Release(); Object.DestroyImmediate(result); throw; }
        }
        internal List<Material> ComponentResult(Candidate candidate)
        {
            var result = Evaluate(candidate.Override, false);
            try
            {
                for (int i = 0; i < Slots.Count; i++)
                {
                    var entry = Slots[i].Entry;
                    if (entry >= 0) MaterialDelta.Between(candidate.SyncedMaterials[entry], candidate.Materials[entry]).ApplyUnsupportedPreview(result.Slots[i].Baseline);
                }
                return result.Slots.Select(s => Copy(s.Baseline)).ToList();
            }
            finally { result.Release(); Object.DestroyImmediate(result); }
        }
        internal void RefreshComponentMaterials(Candidate candidate, bool preserveUnsupported = true)
        {
            var result = Evaluate(candidate.Override, true);
            try
            {
                for (int i = 0; i < Entries.Count; i++)
                {
                    var slot = Slots.FindIndex(s => s.Entry == i);
                    var material = result.Slots[slot].Baseline;
                    var previous = preserveUnsupported ? Copy(candidate.Materials[i]) : null;
                    try
                    {
                        var extras = previous == null ? null : MaterialDelta.Between(candidate.SyncedMaterials[i], previous);
                        candidate.Materials[i].shader = material.shader;
                        candidate.Materials[i].CopyPropertiesFromMaterial(material);
                        candidate.SyncedMaterials[i].shader = material.shader;
                        candidate.SyncedMaterials[i].CopyPropertiesFromMaterial(material);
                        extras?.ApplyUnsupportedPreview(candidate.Materials[i]);
                    }
                    finally { if (previous != null) Object.DestroyImmediate(previous); }
                }
            }
            finally { result.Release(); Object.DestroyImmediate(result); }
            Revision++;
        }
        internal void SyncComponentEdits()
        {
            if (!EditingComponent || Saved) return;
            foreach (var candidate in Candidates)
            {
                Mlic.ValidateCandidate(this, candidate);
                var changes = candidate.Materials.Select((m, i) => MaterialDelta.Between(candidate.SyncedMaterials[i], m)).Where(d => d.Changed).ToArray();
                if (changes.Length == 0) continue;
                // Keep unsupported GUI state for preview and validation, without repeating synchronization every frame.
                if (!changes.Any(d => d.ShaderChanged || d.QueueChanged || d.Properties.Count != 0)) continue;
                Undo.RecordObject(this, "コンポーネント設定を編集");
                Undo.RecordObjects(candidate.Materials.Cast<Object>().ToArray(), "コンポーネント設定を編集");
                foreach (var delta in changes) candidate.Override.Merge(delta);
                RefreshComponentMaterials(candidate);
            }
        }
        internal void RemoveOverride(Candidate candidate, string name)
        {
            if (Entries.Any(e => Mlic.ReadOnlyProperties(e.Baseline).Any(p => p.Name == name)))
                throw new InvalidOperationException(name + ": MLICの対象画像のOverrideは解除できません。");
            SyncComponentEdits();
            Undo.RecordObject(this, "Overrideを解除");
            Undo.RecordObjects(candidate.Materials.Cast<Object>().ToArray(), "Overrideを解除");
            candidate.Override.Remove(name); RefreshComponentMaterials(candidate);
        }
        internal void ResetComponent(Candidate candidate)
        {
            Undo.RecordObject(this, "コンポーネント設定をリセット");
            Undo.RecordObjects(candidate.Materials.Cast<Object>().ToArray(), "コンポーネント設定をリセット");
            candidate.Override = OriginalOverride.Clone(); RefreshComponentMaterials(candidate, false);
        }
    }
}
