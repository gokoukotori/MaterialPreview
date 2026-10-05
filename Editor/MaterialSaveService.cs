using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GokouKotori.MaterialPreview
{
    internal enum SaveMode { TTTMaterialModifier, AOMaterialEditor, DirectMaterial, MaterialVariant, [InspectorName("既存設定を更新")] ExistingOverrides }

    internal sealed class SaveTransaction : IDisposable
    {
        readonly Dictionary<string, byte[]> files = new Dictionary<string, byte[]>();
        readonly Dictionary<Material, string> materials = new Dictionary<Material, string>();
        readonly List<string> created = new List<string>();
        readonly int undoGroup;
        bool committed;
        internal SaveTransaction()
        {
            Undo.IncrementCurrentGroup(); undoGroup = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("Material Preview を保存");
        }
        internal void Backup(Material material)
        {
            if (materials.ContainsKey(material)) return;
            materials.Add(material, EditorJsonUtility.ToJson(material));
            var path = AssetDatabase.GetAssetPath(material);
            if (!string.IsNullOrEmpty(path) && File.Exists(path) && !files.ContainsKey(path)) files.Add(path, File.ReadAllBytes(path));
            Undo.RecordObject(material, "Material Preview を保存");
        }
        internal void Created(string path) => created.Add(path);
        internal void Commit() { committed = true; Undo.CollapseUndoOperations(undoGroup); }
        public void Dispose()
        {
            if (committed) return;
            Undo.RevertAllDownToGroup(undoGroup);
            foreach (var pair in materials) if (pair.Key != null) EditorJsonUtility.FromJsonOverwrite(pair.Value, pair.Key);
            foreach (var path in created) AssetDatabase.DeleteAsset(path);
            foreach (var pair in files)
            {
                File.WriteAllBytes(pair.Key, pair.Value);
                AssetDatabase.ImportAsset(pair.Key, ImportAssetOptions.ForceUpdate);
            }
        }
    }

    internal static class MaterialSaveService
    {
        internal static Dictionary<int, MaterialDelta> Changes(ComparisonSession session, Candidate candidate)
        {
            var result = new Dictionary<int, MaterialDelta>();
            for (int i = 0; i < session.Entries.Count; i++)
            {
                var delta = MaterialDelta.Between(session.Entries[i].Baseline, candidate.Materials[i]);
                if (delta.Changed) result.Add(i, delta);
            }
            return result;
        }
        internal static List<string> Check(ComparisonSession session, Candidate candidate, SaveMode mode)
        {
            var errors = new List<string>();
            try { session.Validate(); session.SyncComponentEdits(); } catch (Exception e) { errors.Add(e.Message); return errors; }
            var changes = Changes(session, candidate);
            if (!session.CandidateChanged(candidate)) errors.Add("変更がありません。");
            if (session.EditingComponent)
            {
                if (mode != SaveMode.ExistingOverrides) errors.Add("コンポーネント編集では選択した既存設定へ保存してください。");
                if (session.EditTarget == null) errors.Add("編集対象が削除されました。");
                foreach (var property in candidate.Override.Properties)
                    if (property.Texture != null && !AssetDatabase.Contains(property.Texture)) errors.Add(property.Name + ": 一時Textureは保存できません。");
            }
            try
            {
                Integration.RequireNdmfBuild();
                if (mode == SaveMode.TTTMaterialModifier) Integration.Require(ExistingOverrides.Ttt);
                if (mode == SaveMode.AOMaterialEditor) Integration.Require(ExistingOverrides.Ao);
            }
            catch (Exception e) { errors.Add(e.Message); }
            if (session.Renderers.GroupBy(r => AnimationUtility.CalculateTransformPath(r.transform, session.Avatar.transform)).Any(g => g.Count() > 1))
                errors.Add("同じ階層パスの複数Rendererは保存先を一意に確認できません。");
            foreach (var change in changes)
            {
                var entry = session.Entries[change.Key]; var delta = change.Value;
                if (mode == SaveMode.ExistingOverrides && !session.EditingComponent)
                {
                    var target = candidate.UpdateTargets[change.Key];
                    if (target == null || !entry.Layers.Any(l => l.Component == target))
                        errors.Add(entry.Source.name + ": 更新先のAOME / TTT設定を選択してください。Prefab割り当てのみの場合はVariant保存等を選択してください。");
                }
                if (!session.EditingComponent && mode != SaveMode.MaterialVariant && session.Avatar.GetComponentsInChildren<Renderer>(true)
                    .Any(r => !session.Renderers.Contains(r) && r.sharedMaterials.Contains(entry.Source)))
                    errors.Add(entry.Source.name + ": 比較対象に含まれないRendererと共有しています。Variantを選択してください。");
                errors.AddRange(delta.Unsupported.Select(s => entry.Source.name + ": " + s + " の保存は未対応です。"));
                foreach (var prop in delta.Properties.Where(p => p.Type == UnityEngine.Rendering.ShaderPropertyType.Texture))
                    if (prop.Texture != null && !AssetDatabase.Contains(prop.Texture)) errors.Add(entry.Source.name + ": 一時Textureは保存できません。");
                if (mode == SaveMode.TTTMaterialModifier || mode == SaveMode.DirectMaterial)
                {
                    if (session.Slots.Any(s => s.Source == entry.Source && s.Entry != change.Key))
                        errors.Add(entry.Source.name + ": 別の編集項目または対象外と共有しています。AO、既存設定の更新、またはVariantを選択してください。");
                }
                var path = AssetDatabase.GetAssetPath(entry.Source);
                if (mode == SaveMode.DirectMaterial)
                {
                    if (!path.StartsWith("Assets/", StringComparison.Ordinal) || Path.GetExtension(path) != ".mat"
                        || !AssetDatabase.IsOpenForEdit(entry.Source)) errors.Add(entry.Source.name + ": 書き込み可能なAssets内の.matが必要です。");
                    if (entry.Source.isVariant) errors.Add(entry.Source.name + ": 元がVariantの場合の直接更新は未対応です。");
                }
                if (mode == SaveMode.MaterialVariant)
                {
                    if (!AssetDatabase.Contains(entry.Source)) errors.Add(entry.Source.name + ": Variantの親は永続アセットが必要です。");
                    if (delta.ShaderChanged) errors.Add(entry.Source.name + ": Shader変更を含むVariant保存は未対応です。");
                    foreach (var prop in delta.Properties)
                        if (entry.Source.IsPropertyLocked(prop.Name) || entry.Source.IsPropertyLockedByAncestor(prop.Name))
                            errors.Add(entry.Source.name + ": " + prop.Name + " は親でロックされています。");
                }
            }
            return errors;
        }

        internal static void Save(ComparisonSession session, Candidate candidate, SaveMode mode, string outputFolder, bool discardOtherChanges = false)
        {
            if (!session.Candidates.Contains(candidate)) throw new InvalidOperationException("保存対象の案が見つかりません。");
            if (!discardOtherChanges && session.Candidates.Any(c => c != candidate && session.CandidateChanged(c)))
                throw new InvalidOperationException("他にも未保存の案があります。他の案を破棄する確認なしには保存を終了できません。");
            var errors = Check(session, candidate, mode);
            if (errors.Count != 0) throw new InvalidOperationException(string.Join("\n", errors));
            if (mode == SaveMode.MaterialVariant && (!outputFolder.StartsWith("Assets", StringComparison.Ordinal)
                || (outputFolder != "Assets" && !outputFolder.StartsWith("Assets/", StringComparison.Ordinal)) || !AssetDatabase.IsValidFolder(outputFolder)))
                throw new InvalidOperationException("Variant保存先はAssets配下の既存フォルダを指定してください。");
            var changes = Changes(session, candidate);
            Verify(session, candidate, mode, changes, true);
            session.Validate();
            using (var transaction = new SaveTransaction())
            {
                var rendererMap = session.Renderers.ToDictionary(r => r, r => r);
                var materialMap = session.Entries.Select(e => e.Source).Distinct().ToDictionary(m => m, m => m);
                Apply(session, candidate, mode, changes, session.Avatar, rendererMap, materialMap, outputFolder, transaction);
                Undo.FlushUndoRecordObjects();
                Verify(session, candidate, mode, changes, false);
                EditorSceneManager.MarkSceneDirty(session.Avatar.scene);
                transaction.Commit();
            }
            session.Saved = true;
            foreach (var other in session.Candidates.Where(c => c != candidate).ToArray()) session.Remove(other);
        }

        internal static void Verify(ComparisonSession session, Candidate candidate, SaveMode mode,
            Dictionary<int, MaterialDelta> changes, bool proposed)
        {
            Integration.RequireNdmfBuild();
            var folderName = "MaterialPreviewCheck-" + Guid.NewGuid().ToString("N");
            var temp = "Assets/" + folderName;
            GameObject clone = null;
            var transient = new List<Object>();
            List<Material> componentExpected = null;
            try
            {
                if (session.EditingComponent) componentExpected = session.ComponentResult(candidate);
                AssetDatabase.CreateFolder("Assets", folderName);
                clone = Object.Instantiate(session.Avatar); clone.name = "Material Preview 保存検証";
                if (PrefabUtility.IsPartOfPrefabInstance(clone))
                    PrefabUtility.UnpackPrefabInstance(clone, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                var originals = session.Avatar.GetComponentsInChildren<Renderer>(true);
                var clones = clone.GetComponentsInChildren<Renderer>(true);
                if (originals.Length != clones.Length) throw new InvalidOperationException("検証用Rendererを対応付けできません。");
                var rendererMap = originals.Select((r,i) => new {r, copy = clones[i]}).ToDictionary(p => p.r, p => p.copy);
                var originalComponents = session.Avatar.GetComponentsInChildren<Component>(true);
                var cloneComponents = clone.GetComponentsInChildren<Component>(true);
                if (originalComponents.Length != cloneComponents.Length) throw new InvalidOperationException("検証用Componentを対応付けできません。");
                var componentMap = originalComponents.Select((c, i) => new {c, copy = cloneComponents[i]})
                    .Where(p => p.c != null).ToDictionary(p => p.c, p => p.copy);
                var materialMap = new Dictionary<Material, Material>();
                foreach (var renderer in clones)
                    renderer.sharedMaterials = renderer.sharedMaterials.Select(m =>
                    {
                        if (m == null) return null;
                        if (!materialMap.TryGetValue(m, out var copy))
                        {
                            copy = ComparisonSession.Copy(m); copy.hideFlags = HideFlags.None;
                            AssetDatabase.CreateAsset(copy, temp + "/input-" + materialMap.Count + ".mat");
                            materialMap.Add(m, copy);
                        }
                        return copy;
                    }).ToArray();
                // Existing material-targeting components must refer to the isolated input materials too.
                foreach (var component in clone.GetComponentsInChildren<Component>(true).Where(c => c != null && !(c is Transform)))
                {
                    var serialized = new SerializedObject(component); var iterator = serialized.GetIterator();
                    while (iterator.Next(true))
                        if (iterator.propertyType == SerializedPropertyType.ObjectReference && iterator.objectReferenceValue is Material m
                            && materialMap.TryGetValue(m, out var copy)) iterator.objectReferenceValue = copy;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
                if (proposed) Apply(session, candidate, mode, changes, clone, rendererMap, materialMap, temp, null, componentMap);
                var contextType = Integration.Type("nadena.dev.ndmf.BuildContext");
                var context = Activator.CreateInstance(contextType, new object[] {clone, temp, true});
                var phaseType = Integration.Type("nadena.dev.ndmf.BuildPhase");
                var process = Integration.Type("nadena.dev.ndmf.AvatarProcessor").GetMethod("ProcessAvatar", Integration.Flags,
                    null, new[] {contextType, phaseType, phaseType}, null);
                try { process.Invoke(null, new[] {context, phaseType.GetField("FirstChance").GetValue(null), phaseType.GetField("PlatformFinish").GetValue(null)}); }
                finally { contextType.GetMethod("Finish", Integration.Flags, null, System.Type.EmptyTypes, null).Invoke(context, null); }
                if (!(bool)contextType.GetProperty("Successful").GetValue(context)) throw new InvalidOperationException("NDMFがエラーを報告しました。保存を中止します。");
                foreach (var slot in session.Slots.Where(s => s.Baseline != null))
                {
                    var target = rendererMap[slot.Renderer];
                    if (target == null || target.sharedMaterials.Length <= slot.Index || target.sharedMaterials[slot.Index] == null)
                        throw new InvalidOperationException(slot.Path + ": ビルド後のRenderer/スロットを追跡できません。");
                    var expected = ComparisonSession.Copy(componentExpected == null ? slot.Baseline : componentExpected[session.Slots.IndexOf(slot)]); transient.Add(expected);
                    if (!session.EditingComponent && slot.InScope && changes.TryGetValue(slot.Entry, out var slotChange)) slotChange.Apply(expected);
                    var difference = MaterialDelta.Between(expected, target.sharedMaterials[slot.Index]);
                    // A Variant can serialize an inherited queue differently while resolving to the same queue.
                    // Preserve raw queue differences during editing, but compare effective values after a build.
                    if (expected.renderQueue == target.sharedMaterials[slot.Index].renderQueue) difference.QueueChanged = false;
                    // Persistent input textures retain their references; newly generated textures deliberately fail closed.
                    if (difference.Changed) throw new InvalidOperationException(slot.Path + " [" + slot.Index + "]: 保存結果のMaterial状態が期待値と一致しません。\n" + string.Join(", ", difference.Names()));
                }
            }
            finally
            {
                if (componentExpected != null) foreach (var material in componentExpected) if (material != null) Object.DestroyImmediate(material);
                if (clone != null) Object.DestroyImmediate(clone);
                foreach (var obj in transient) if (obj != null) Object.DestroyImmediate(obj);
                // This unique folder is the only project data deleted by the verification.
                if (AssetDatabase.IsValidFolder(temp)) AssetDatabase.DeleteAsset(temp);
            }
        }

        static void Apply(ComparisonSession session, Candidate candidate, SaveMode mode,
            Dictionary<int, MaterialDelta> changes, GameObject avatar,
            Dictionary<Renderer, Renderer> rendererMap, Dictionary<Material, Material> materialMap,
            string outputFolder, SaveTransaction transaction, Dictionary<Component, Component> componentMap = null)
        {
            if (session.EditingComponent)
            {
                var target = componentMap == null ? session.EditTarget : componentMap[session.EditTarget];
                if (transaction != null) Undo.RecordObject(target, "既存マテリアル設定を編集");
                candidate.Override.Write(target);
                EditorUtility.SetDirty(target);
                if (transaction != null) PrefabUtility.RecordPrefabInstancePropertyModifications(target);
                return;
            }
            GameObject container = null;
            if (mode == SaveMode.AOMaterialEditor || mode == SaveMode.TTTMaterialModifier)
            {
                container = new GameObject("Material Preview - " + candidate.Name);
                container.transform.SetParent(avatar.transform, false);
                if (transaction != null) Undo.RegisterCreatedObjectUndo(container, "Material Preview 設定を作成");
            }
            foreach (var change in changes)
            {
                var source = materialMap[session.Entries[change.Key].Source]; var delta = change.Value;
                if (mode == SaveMode.ExistingOverrides)
                {
                    var original = candidate.UpdateTargets[change.Key];
                    var target = componentMap == null ? original : componentMap[original];
                    if (transaction != null) Undo.RecordObject(target, "既存マテリアル設定を更新");
                    ExistingOverrides.Merge(target, delta);
                    EditorUtility.SetDirty(target);
                    if (transaction != null) PrefabUtility.RecordPrefabInstancePropertyModifications(target);
                }
                else if (mode == SaveMode.DirectMaterial)
                {
                    transaction?.Backup(source); delta.Apply(source);
                    EditorUtility.SetDirty(source); AssetDatabase.SaveAssetIfDirty(source);
                }
                else if (mode == SaveMode.MaterialVariant)
                {
                    var variant = new Material(source) {parent = source}; delta.Apply(variant);
                    var filename = string.Concat(session.Entries[change.Key].Source.name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                    var path = AssetDatabase.GenerateUniqueAssetPath(outputFolder + "/" + filename + "-Variant.mat");
                    transaction?.Created(path); AssetDatabase.CreateAsset(variant, path);
                    foreach (var slot in session.Slots.Where(s => s.InScope && s.Entry == change.Key))
                    {
                        var renderer = rendererMap[slot.Renderer];
                        if (transaction != null) Undo.RecordObject(renderer, "Material Variantを割り当て");
                        var array = renderer.sharedMaterials; array[slot.Index] = variant; renderer.sharedMaterials = array;
                        if (transaction != null) PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
                    }
                }
                else
                {
                    var componentType = mode == SaveMode.TTTMaterialModifier
                        ? Integration.Require(ExistingOverrides.Ttt) : Integration.Require(ExistingOverrides.Ao);
                    var componentObject = new GameObject(session.Entries[change.Key].Source.name);
                    componentObject.transform.SetParent(container.transform, false);
                    var component = componentObject.AddComponent(componentType);
                    object settings;
                    var ttt = mode == SaveMode.TTTMaterialModifier;
                    if (ttt) { settings = component; Integration.Set(component, "TargetMaterial", source); }
                    else
                    {
                        settings = Integration.Get(component, "OverrideSettings");
                        var single = Integration.Get(Integration.Get(component, "TargetSettings"), "SingleMaterial");
                        Integration.Set(single, "TargetMaterial", source);
                        var exclusions = (IList)Integration.Get(single, "ExcludedSlots");
                        foreach (var slot in session.Slots.Where(s => s.Entry != change.Key && s.Source == session.Entries[change.Key].Source))
                        {
                            var reference = Activator.CreateInstance(Integration.Require("Aoyon.MaterialEditor.MaterialSlotReference"), true);
                            Integration.Set(reference, "MaterialIndex", slot.Index);
                            var objectReference = Integration.Get(reference, "RendererReference");
                            Integration.InvokeInstance(objectReference, "Set", rendererMap[slot.Renderer].gameObject);
                            exclusions.Add(reference);
                        }
                    }
                    Integration.Set(settings, ttt ? "IsOverrideShader" : "OverrideShader", delta.ShaderChanged);
                    Integration.Set(settings, ttt ? "OverrideShader" : "TargetShader", delta.Shader);
                    Integration.Set(settings, ttt ? "IsOverrideRenderQueue" : "OverrideRenderQueue", delta.QueueChanged);
                    Integration.Set(settings, ttt ? "OverrideRenderQueue" : "RenderQueueValue", delta.Queue);
                    var properties = (IList)Integration.Get(settings, ttt ? "OverrideProperties" : "PropertyOverrides");
                    var propertyType = Integration.Require(ttt ? "net.rs64.TexTransTool.MaterialProperty" : "Aoyon.MaterialEditor.MaterialProperty");
                    foreach (var value in delta.Properties)
                    {
                        var property = Activator.CreateInstance(propertyType);
                        Integration.Set(property, "PropertyName", value.Name); Integration.Set(property, "PropertyType", value.Type);
                        Integration.Set(property, "ColorValue", value.Color); Integration.Set(property, "VectorValue", value.Vector);
                        Integration.Set(property, "FloatValue", value.Float); Integration.Set(property, "IntValue", value.Int);
                        Integration.Set(property, "TextureValue", value.Texture); Integration.Set(property, "TextureScaleValue", value.Scale);
                        Integration.Set(property, "TextureOffsetValue", value.Offset); properties.Add(property);
                    }
                    EditorUtility.SetDirty(component);
                }
            }
        }
    }
}
