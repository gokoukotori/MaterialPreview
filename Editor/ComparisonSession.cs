using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GokouKotori.MaterialPreview
{
    [Serializable] internal sealed class MaterialEntry
    {
        public Material Source, Baseline, OverrideBaseline;
        internal Material ComparisonBaseline => OverrideBaseline != null ? OverrideBaseline : Baseline;
        public string OriginalJson, DependencyHash;
        public List<ExistingLayer> Layers = new List<ExistingLayer>();
    }
    [Serializable] internal sealed class MaterialSlot
    {
        public Renderer Renderer;
        public string Path;
        public int Index, Entry = -1;
        public Material Source, Baseline;
        public bool InScope;
        public List<ExistingLayer> Layers = new List<ExistingLayer>();
    }
    [Serializable] internal sealed class Candidate
    {
        public string Name = "TO BE";
        public bool Visible = true;
        public List<Material> Materials = new List<Material>();
        public List<Component> UpdateTargets = new List<Component>();
        public OverrideState Override;
        public List<Material> SyncedMaterials = new List<Material>();
    }

    internal sealed partial class ComparisonSession : ScriptableObject
    {
        public GameObject Avatar;
        public List<GameObject> Roots = new List<GameObject>();
        public bool RendererOnly;
        public Renderer PreviewRenderer;
        public List<MaterialEntry> Entries = new List<MaterialEntry>();
        public List<MaterialSlot> Slots = new List<MaterialSlot>();
        public List<Candidate> Candidates = new List<Candidate>();
        public List<Renderer> Renderers = new List<Renderer>();
        public List<string> Warnings = new List<string>();
        public string Configuration;
        public bool Saved;
        public int Revision;
        public MlicPreview Mlic = new MlicPreview();
        public bool OwnsMlic = true;

        internal static bool IsAvatar(GameObject obj) => obj != null && obj.GetComponents<Component>()
            .Any(c => c != null && c.GetType().FullName == "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor");
        internal static bool Belongs(Renderer renderer, GameObject avatar)
        {
            for (var t = renderer.transform; t != null; t = t.parent)
                if (IsAvatar(t.gameObject)) return t.gameObject == avatar;
            return false;
        }
        internal static Material Copy(Material source)
        {
            if (source == null) return null;
            var m = new Material(source) {hideFlags = HideFlags.HideAndDontSave & ~HideFlags.NotEditable};
            m.parent = null;
            return m;
        }
        internal bool IncludesInPreview(Renderer renderer) => RendererOnly ? renderer == PreviewRenderer
            : Roots.Any(root => root != null && renderer.transform.IsChildOf(root.transform));

        internal static ComparisonSession CreateForRenderer(Renderer renderer)
        {
            if (!RendererEdit.CanOpen(renderer)) throw new InvalidOperationException("Scene上のアバター内のMeshRendererまたはSkinnedMeshRendererを指定してください。EditorOnlyは対象外です。");
            return Create(ExistingOverrides.FindAvatar(renderer.gameObject), new[] { renderer.gameObject }, renderer);
        }

        internal static ComparisonSession Create(GameObject avatar, IEnumerable<GameObject> roots, Renderer previewRenderer = null)
        {
            if (ExistingOverrides.FindAvatar(avatar) != avatar || avatar == null) throw new InvalidOperationException("Scene上のVRCAvatarDescriptorを持つアバターを指定してください（Prefab編集モードは対象外）。");
            var session = CreateInstance<ComparisonSession>(); session.hideFlags = HideFlags.HideAndDontSave;
            session.Avatar = avatar; session.Roots = roots.Distinct().ToList();
            session.RendererOnly = previewRenderer != null; session.PreviewRenderer = previewRenderer;
            try
            {
                if (session.Roots.Count == 0 || session.Roots.Any(r => r == null || !r.transform.IsChildOf(avatar.transform)))
                    throw new InvalidOperationException("対象ルートは選択アバター配下に指定してください。");
                foreach (var renderer in avatar.GetComponentsInChildren<Renderer>(true).Where(r => Belongs(r, avatar) && !ExistingOverrides.EditorOnly(r.transform)))
                {
                    if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer))
                    { session.Warnings.Add(renderer.name + ": このRendererはプレビュー非対応です。"); continue; }
                    if (renderer.HasPropertyBlock()) throw new InvalidOperationException(renderer.name + ": MaterialPropertyBlockによる上書きは比較・保存に未対応です。");
                    session.Renderers.Add(renderer);
                    var inScope = session.IncludesInPreview(renderer);
                    var originals = renderer.sharedMaterials;
                    for (int i = 0; i < originals.Length; i++)
                    {
                        session.Slots.Add(new MaterialSlot {Renderer = renderer, Path = AnimationUtility.CalculateTransformPath(renderer.transform, avatar.transform),
                            Index = i, InScope = inScope, Source = originals[i], Baseline = Copy(originals[i])});
                        if (inScope && originals[i] == null) session.Warnings.Add(renderer.name + " [" + i + "]: 未割り当て");
                    }
                }
                ExistingOverrides.Capture(session);
                foreach (var slot in session.Slots.Where(s => s.InScope && s.Source != null))
                {
                    var entry = session.Entries.FindIndex(e => e.Source == slot.Source
                        && e.Layers.Select(l => l.Component).SequenceEqual(slot.Layers.Select(l => l.Component))
                        && e.Layers.Select(l => l.Label).SequenceEqual(slot.Layers.Select(l => l.Label)));
                    if (entry < 0)
                    {
                        entry = session.Entries.Count;
                        session.Entries.Add(new MaterialEntry {Source = slot.Source, Baseline = Copy(slot.Baseline), Layers = slot.Layers,
                            OriginalJson = EditorJsonUtility.ToJson(slot.Source), DependencyHash = Dependencies(slot.Source)});
                    }
                    slot.Entry = entry;
                }
                session.Configuration = session.ConfigurationSignature();
                session.AddCandidate(null);
                return session;
            }
            catch { session.Release(); Object.DestroyImmediate(session); throw; }
        }

        internal Candidate AddCandidate(Candidate from)
        {
            SyncComponentEdits();
            var candidate = new Candidate {Name = "TO BE " + (Candidates.Count + 1), Visible = Candidates.Count(c => c.Visible) < 3};
            candidate.Materials = (from == null ? Entries.Select(e => e.Baseline) : from.Materials).Select(Copy).ToList();
            candidate.UpdateTargets = from == null ? Entries.Select(e => e.Layers.LastOrDefault(l => ExistingOverrides.IsSupported(l.Component))?.Component).ToList()
                : new List<Component>(from.UpdateTargets);
            if (EditingComponent)
            {
                candidate.Override = (from == null ? OriginalOverride : from.Override).Clone();
                candidate.UpdateTargets = Entries.Select(e => EditTarget).ToList();
                candidate.SyncedMaterials = (from == null ? candidate.Materials : from.SyncedMaterials).Select(Copy).ToList();
            }
            Candidates.Add(candidate); Revision++;
            return candidate;
        }
        internal bool CandidateChanged(Candidate candidate) => (EditingComponent && JsonUtility.ToJson(candidate.Override) != JsonUtility.ToJson(OriginalOverride))
            || Entries.Select((e,i) => MaterialDelta.Between(e.Baseline, candidate.Materials[i]).Changed).Any(v => v);
        internal bool HasChanges => !Saved && Candidates.Any(CandidateChanged);
        static string Dependencies(Material material)
        {
            var path = AssetDatabase.GetAssetPath(material);
            return string.IsNullOrEmpty(path) ? "" : AssetDatabase.GetAssetDependencyHash(path).ToString();
        }
        internal string ConfigurationSignature()
        {
            if (Avatar == null) return "";
            return string.Join("\n", Avatar.GetComponentsInChildren<Component>(true)
                .Where(c => c != null && !(c is Transform) && !(c is Renderer) && !(c is Animator)
                    && c.GetType().FullName != "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor")
                .Select(c => c.GetInstanceID() + ":" + EditorJsonUtility.ToJson(c)))
                + "\n" + string.Join("\n", Avatar.GetComponentsInChildren<Transform>(true).Select(t =>
                    t.GetInstanceID() + ":" + t.GetSiblingIndex() + ":" + t.gameObject.activeSelf + ":" + t.gameObject.tag));
        }
        internal void Validate()
        {
            if (Avatar == null) throw new InvalidOperationException("対象アバターが削除されました。");
            if (RendererOnly && (PreviewRenderer == null || !RendererEdit.CanOpen(PreviewRenderer)
                || !Belongs(PreviewRenderer, Avatar))) throw new InvalidOperationException("対象Rendererが削除または変更されました。比較を開始し直してください。");
            if (Saved) throw new InvalidOperationException("保存済みです。新しい比較を開始してください。");
            if (Roots.Any(r => r == null || !r.transform.IsChildOf(Avatar.transform))) throw new InvalidOperationException("対象ルートが変更されました。比較を開始し直してください。");
            var current = Avatar.GetComponentsInChildren<Renderer>(true).Where(r => Belongs(r, Avatar) && !ExistingOverrides.EditorOnly(r.transform) && (r is MeshRenderer || r is SkinnedMeshRenderer)).ToArray();
            if (!new HashSet<Renderer>(current).SetEquals(Renderers)) throw new InvalidOperationException("Renderer構成が変更されました。");
            foreach (var renderer in Renderers)
            {
                if (renderer.HasPropertyBlock()) throw new InvalidOperationException(renderer.name + ": MaterialPropertyBlockが設定されたため比較を続行できません。");
                if (renderer.sharedMaterials.Length != Slots.Count(s => s.Renderer == renderer))
                    throw new InvalidOperationException("マテリアルスロット数が変更されました。");
            }
            foreach (var slot in Slots)
                if (slot.Renderer == null || slot.Renderer.sharedMaterials.Length <= slot.Index
                    || slot.Renderer.sharedMaterials[slot.Index] != slot.Source
                    || AnimationUtility.CalculateTransformPath(slot.Renderer.transform, Avatar.transform) != slot.Path)
                    throw new InvalidOperationException("Rendererの割り当てまたは階層が変更されました。");
            foreach (var entry in Entries)
                if (entry.Source == null || EditorJsonUtility.ToJson(entry.Source) != entry.OriginalJson || Dependencies(entry.Source) != entry.DependencyHash)
                    throw new InvalidOperationException("元マテリアルが外部で変更されました。比較を開始し直してください。");
            if (ConfigurationSignature() != Configuration) throw new InvalidOperationException("アバターの設定が変更されました。比較を開始し直してください。");
            Mlic.Validate();
        }
        internal void Remove(Candidate candidate)
        {
            foreach (var material in candidate.SyncedMaterials) if (material != null) Object.DestroyImmediate(material);
            foreach (var material in candidate.Materials) if (material != null) Object.DestroyImmediate(material);
            Candidates.Remove(candidate); Revision++;
        }
        internal void Release()
        {
            foreach (var candidate in Candidates.ToArray()) Remove(candidate);
            foreach (var entry in Entries)
            {
                if (entry.Baseline != null) Object.DestroyImmediate(entry.Baseline);
                if (entry.OverrideBaseline != null) Object.DestroyImmediate(entry.OverrideBaseline);
            }
            foreach (var slot in Slots)
            {
                if (slot.Baseline != null) Object.DestroyImmediate(slot.Baseline);
                foreach (var layer in slot.Layers)
                {
                    if (layer.Before != null) Object.DestroyImmediate(layer.Before);
                    if (layer.After != null) Object.DestroyImmediate(layer.After);
                }
            }
            Entries.Clear(); Slots.Clear();
            if (OwnsMlic) Mlic.Release();
        }
    }
}
