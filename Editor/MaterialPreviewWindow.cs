using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace GokouKotori.MaterialPreview
{
    public sealed partial class MaterialPreviewWindow : EditorWindow
    {
        const string Root = "Packages/com.gokoukotori.material-preview/Editor/UI/";
        [SerializeField] GameObject avatar;
        [SerializeField] bool outfit;
        [SerializeField] List<GameObject> roots = new List<GameObject>();
        [SerializeField] ComparisonSession session;
        [SerializeField] int editing, saving, selected;
        [SerializeField] SaveMode saveMode = SaveMode.AOMaterialEditor;
        [SerializeField] string outputFolder = "Assets";

        ComparisonRenderer rendering;
        double nextUpdate;
        double nextCameraFrame, lastCameraInput = double.NegativeInfinity;
        bool cameraRenderPending;
        bool blocked;
        string search = "", error;
        readonly List<Image> images = new List<Image>();
        readonly Dictionary<Candidate, Label> candidateTitles = new Dictionary<Candidate, Label>();


        [SerializeField] float editorWidth = 500;
        [SerializeField] bool propertyComparison;
        UnityEditor.MaterialEditor baselineEditor;
        InspectorElement baselineInspector;
        Material baselineDisplay, baselineSource;
        sealed class CandidateInspector
        {
            internal Material Material;
            internal Shader Shader;
            internal UnityEditor.MaterialEditor Editor;
            internal InspectorElement Inspector;
            internal VisualElement Panel;
            internal Label Title;
        }
        readonly Dictionary<Candidate, CandidateInspector> comparisonInspectors = new Dictionary<Candidate, CandidateInspector>();
        UnityEditor.MaterialEditor shaderEditor;
        InspectorElement shaderInspector;
        Shader editorShader;
        string candidateSignature;
        bool SelectedHasChanges => session != null && saving >= 0 && saving < session.Candidates.Count && session.CandidateChanged(session.Candidates[saving]);

        void RestorePreview()
        {
            if (session == null) return;
            if (rendering == null) rendering = new ComparisonRenderer(session) { Lighting = lighting };

            blocked = false; error = null;
            if (rootVisualElement.Q("cards") != null) CandidatesUI();
        }
        bool ConfirmEndComparison()
        {
            if (session == null || !session.HasChanges) return true;
            var choice = EditorUtility.DisplayDialogComplex("比較を終了", "未保存の案があります。保存する場合は選択中の保存対象を使用します。",
                "保存して終了", "キャンセル", "すべて破棄して終了");
            return choice == 2 || (choice == 0 && TrySave());
        }
        void ReleaseComparison()
        {
            StopVrPreview();
            ReleaseShaderEditor(); rendering?.Dispose(); rendering = null;
            if (session != null) { session.Release(); DestroyImmediate(session); session = null; }
            blocked = false; error = null; hasUnsavedChanges = false;
            cameraRenderPending = false; lastCameraInput = double.NegativeInfinity;
            StopLightOrbit();
        }

        void ReleaseShaderEditor()
        {
            ReleaseCandidateInspectors();
            ReleaseBaselineEditor();
            if (shaderEditor != null) ShaderChangeMarkers.Unregister(shaderEditor.target as Material);
            shaderInspector?.RemoveFromHierarchy(); shaderInspector = null;
            if (shaderEditor != null) DestroyImmediate(shaderEditor);
            shaderEditor = null; editorShader = null;
        }
        void ReleaseBaselineEditor()
        {
            baselineInspector?.RemoveFromHierarchy(); baselineInspector = null;
            if (baselineEditor != null) DestroyImmediate(baselineEditor);
            if (baselineDisplay != null) DestroyImmediate(baselineDisplay);
            baselineEditor = null; baselineDisplay = baselineSource = null;
        }
        void ReleaseCandidateInspector(Candidate candidate)
        {
            var item = comparisonInspectors[candidate];
            ShaderChangeMarkers.Unregister(item.Material);
            item.Panel.RemoveFromHierarchy();
            if (item.Editor != null) DestroyImmediate(item.Editor);
            comparisonInspectors.Remove(candidate);
        }
        void ReleaseCandidateInspectors()
        {
            foreach (var candidate in comparisonInspectors.Keys.ToArray()) ReleaseCandidateInspector(candidate);
        }
        void UpdateCandidateInspectors()
        {
            var columns = rootVisualElement.Q("property-columns");
            foreach (var candidate in comparisonInspectors.Keys.ToArray())
            {
                var item = comparisonInspectors[candidate];
                if (!session.Candidates.Contains(candidate) || candidate == Current || item.Material == null
                    || item.Material != candidate.Materials[selected] || item.Shader != item.Material.shader)
                    ReleaseCandidateInspector(candidate);
            }
            for (int i = 0; i < session.Candidates.Count; i++)
            {
                var candidate = session.Candidates[i];
                VisualElement panel;
                if (candidate == Current) panel = rootVisualElement.Q("editor-panel");
                else
                {
                    if (!comparisonInspectors.TryGetValue(candidate, out var item))
                    {
                        var material = candidate.Materials[selected];
                        item = new CandidateInspector { Material = material, Shader = material.shader,
                            Panel = new VisualElement(), Title = new Label() };
                        comparisonInspectors.Add(candidate, item);
                        item.Panel.AddToClassList("candidate-inspector-panel");
                        item.Title.AddToClassList("heading"); item.Panel.Add(item.Title);
                        var scroll = new ScrollView { verticalScrollerVisibility = ScrollerVisibility.AlwaysVisible };
                        scroll.AddToClassList("candidate-inspector-scroll");
                        item.Panel.Add(scroll);
                        item.Editor = (UnityEditor.MaterialEditor)Editor.CreateEditor(material, typeof(UnityEditor.MaterialEditor));
                        ShaderChangeMarkers.Register(material, session.Entries[selected].Baseline, () => { Changed(); Repaint(); });
                        UnityEditorInternal.InternalEditorUtility.SetIsInspectorExpanded(material, true);
                        item.Inspector = new InspectorElement(item.Editor); scroll.Add(item.Inspector);
                    }
                    item.Title.text = candidate.Name;
                    item.Inspector.SetEnabled(CanEdit());
                    panel = item.Panel;
                }
                if (panel.parent != columns || columns.IndexOf(panel) != i + 1) columns.Insert(i + 1, panel);
            }
            ResizePropertyColumns();
        }
        void ResizePropertyColumns()
        {
            if (!propertyComparison) return;
            var comparison = rootVisualElement.Q<ScrollView>("property-comparison");
            var columns = rootVisualElement.Q("property-columns");
            if (comparison == null || columns == null || columns.childCount == 0) return;
            // Use the viewport, not the shader GUI's measured content width.
            var available = comparison.contentViewport.contentRect.width;
            if (float.IsNaN(available) || available <= 0) return;
            var width = Mathf.Max(400, (available - 8 * (columns.childCount - 1)) / columns.childCount);
            foreach (var column in columns.Children()) column.style.width = width;
        }
        void ResizeEditorPanel()
        {
            var panel = rootVisualElement.Q("editor-panel");
            if (panel == null) return;
            if (propertyComparison) { ResizePropertyColumns(); return; }
            panel.style.width = Mathf.Clamp(editorWidth, 330, Mathf.Max(330, position.width - 480));
        }
        void UpdateDisplayMode()
        {
            var comparison = rootVisualElement.Q("property-comparison");
            var panel = rootVisualElement.Q("editor-panel");
            if (comparison == null || panel == null) return;
            var parent = propertyComparison ? rootVisualElement.Q("property-columns") : rootVisualElement.Q("body");
            if (panel.parent != parent) parent.Add(panel);
            panel.EnableInClassList("comparison-editor", propertyComparison);
            comparison.style.display = propertyComparison ? DisplayStyle.Flex : DisplayStyle.None;
            rootVisualElement.Q("cards").style.display = propertyComparison ? DisplayStyle.None : DisplayStyle.Flex;
            rootVisualElement.Q("lighting-panel").style.display = propertyComparison ? DisplayStyle.None : DisplayStyle.Flex;
            if (propertyComparison) StopLightOrbit();
            foreach (var name in new[] { "focus", "frame", "editor-width" })
                rootVisualElement.Q(name).style.display = propertyComparison ? DisplayStyle.None : DisplayStyle.Flex;
            ResizeEditorPanel();
            UpdateShaderInspector();
        }
        void UpdateShaderInspector()
        {
            if (rootVisualElement.Q("inspector") == null) return;

            if (Current == null || selected < 0 || selected >= Current.Materials.Count)
            { ReleaseShaderEditor(); return; }
            var material = Current.Materials[selected];
            if (material == null || material.shader == null) { ReleaseShaderEditor(); return; }
            material.hideFlags &= ~HideFlags.NotEditable;
            if (shaderEditor == null || shaderEditor.target != material || editorShader != material.shader || shaderInspector?.parent == null)
            {
                ReleaseShaderEditor();
                shaderEditor = (UnityEditor.MaterialEditor)Editor.CreateEditor(material, typeof(UnityEditor.MaterialEditor));
                ShaderChangeMarkers.Register(material, session.Entries[selected].Baseline, () => { Changed(); Repaint(); });
                UnityEditorInternal.InternalEditorUtility.SetIsInspectorExpanded(material, true);
                editorShader = material.shader;
                shaderInspector = new InspectorElement(shaderEditor);
                rootVisualElement.Q<ScrollView>("inspector").Add(shaderInspector);
            }
            shaderInspector.SetEnabled(CanEdit());
            if (!propertyComparison) { ReleaseBaselineEditor(); ReleaseCandidateInspectors(); return; }
            var baseline = session.Entries[selected].Baseline;
            if (baselineEditor == null || baselineSource != baseline || baselineInspector?.parent == null)
            {
                ReleaseBaselineEditor();
                baselineSource = baseline;
                baselineDisplay = ComparisonSession.Copy(baseline);
                baselineEditor = (UnityEditor.MaterialEditor)Editor.CreateEditor(baselineDisplay, typeof(UnityEditor.MaterialEditor));
                UnityEditorInternal.InternalEditorUtility.SetIsInspectorExpanded(baselineDisplay, true);
                baselineInspector = new InspectorElement(baselineEditor);
                baselineInspector.SetEnabled(false);
                rootVisualElement.Q<ScrollView>("baseline-inspector").Add(baselineInspector);
            }
            UpdateCandidateInspectors();
        }

        [MenuItem("Tools/Gokoukotori/MaterialPreview/開く")]
        public static void Open()
        {
            var window = GetWindow<MaterialPreviewWindow>(); window.titleContent = new GUIContent("Material Preview");
            window.minSize = new Vector2(1000, 650);
            if (Selection.activeGameObject != null && Selection.activeGameObject.GetComponents<Component>().Any(ExistingOverrides.IsSupported))
            { window.OpenSelection(Selection.activeObject); return; }
            if (window.avatar == null && Selection.activeGameObject != null)
                for (var t = Selection.activeGameObject.transform; t != null; t = t.parent)
                    if (ComparisonSession.IsAvatar(t.gameObject)) { window.avatar = t.gameObject; break; }
        }
        [MenuItem("GameObject/Gokoukotori/MaterialPreview/編集", false, 49)]
        static void OpenFromContext(MenuCommand command)
        {
            var target = ExistingOverrides.FindAvatar(command.context as GameObject ?? Selection.activeGameObject);
            if (target == null) return;
            var window = GetWindow<MaterialPreviewWindow>();
            window.OpenSelection(command.context != null ? command.context : Selection.activeGameObject);
        }
        [MenuItem("GameObject/Gokoukotori/MaterialPreview/編集", true)]
        static bool CanOpenFromContext(MenuCommand command) => !EditorApplication.isPlayingOrWillChangePlaymode
            && ExistingOverrides.FindAvatar(command.context as GameObject ?? Selection.activeGameObject) != null;

        [MenuItem("CONTEXT/MaterialModifier/Gokoukotori/MaterialPreview/編集")]
        [MenuItem("CONTEXT/MaterialEditorComponent/Gokoukotori/MaterialPreview/編集")]
        static void OpenComponentContext(MenuCommand command) => GetWindow<MaterialPreviewWindow>().SelectComponent(command.context as Component);

        void OpenSelection(Object selection)
        {
            if (selection is Component component && ExistingOverrides.IsSupported(component)) { SelectComponent(component); return; }
            var go = selection as GameObject;
            if (go == null) { Status("Hierarchyで編集する設定を選択してください。"); return; }
            var components = go.GetComponents<Component>().Where(ExistingOverrides.IsSupported).ToArray();
            if (components.Length == 0) { SelectAvatar(go); return; }
            if (components.Length == 1) { SelectComponent(components[0]); return; }
            var menu = new GenericMenu();
            for (int i = 0; i < components.Length; i++)
            {
                var target = components[i];
                menu.AddItem(new GUIContent((i + 1) + ": " + target.GetType().Name), false, () => SelectComponent(target));
            }
            menu.ShowAsContext();
        }
        internal bool SelectComponent(Component target)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || !ExistingOverrides.IsSupported(target) || !ConfirmEndComparison()) return false;
            bool opened = false;
            Guard(() =>
            {
                var next = ComparisonSession.CreateForComponent(target);
                ComparisonRenderer preview;
                try { preview = new ComparisonRenderer(next) { Lighting = lighting }; }
                catch { next.Release(); DestroyImmediate(next); throw; }
                ReleaseComparison(); session = next; rendering = preview; avatar = next.Avatar;
                outfit = false; roots.Clear(); editing = saving = selected = 0;
                saveMode = SaveMode.ExistingOverrides; CreateGUI(); opened = true;
            });
            return opened;
        }

        internal bool SelectAvatar(GameObject target)
        {
            target = ExistingOverrides.FindAvatar(target);
            if (target == null || !ConfirmEndComparison()) return false;
            ReleaseComparison(); avatar = target; outfit = false; roots.Clear();
            CreateGUI(); StartComparison(); return session != null;
        }
        void OnEnable()
        {
            EditorApplication.update += Tick; Undo.undoRedoPerformed += OnUndo;
            titleContent = new GUIContent("Material Preview"); minSize = new Vector2(1000, 650);
            if (session != null)
            {
                try { RestorePreview(); }
                catch (Exception ex) { error = Integration.Message(ex); blocked = true; }
            }
        }
        void OnDisable()
        {
            StopVrPreview();
            if (VrDependencies.Installing) VrDependencies.Cancel();
            StopLightOrbit();
            ReleaseShaderEditor();
            EditorApplication.update -= Tick; Undo.undoRedoPerformed -= OnUndo;
            rendering?.Dispose(); rendering = null;
        }
        void OnDestroy() { if (session != null) { session.Release(); DestroyImmediate(session); } }
        void OnUndo()
        {
            if (session != null)
            {
                session.Revision++;
                if (session.EditingComponent && !session.Saved)
                    foreach (var candidate in session.Candidates) session.RefreshComponentMaterials(candidate);
            }
            Guard(() => { Refresh(); Repaint(); });
        }
        public void CreateGUI()
        {
            ReleaseShaderEditor();
            rootVisualElement.Clear();
            var tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(Root + "MaterialPreview.uxml");
            if (tree == null) { rootVisualElement.Add(new Label("Material Preview UIを読み込めません。")); return; }
            tree.CloneTree(rootVisualElement);
            CreateLightingUI();
            CreateVrUI();
            var display = rootVisualElement.Q<DropdownField>("display-mode");
            display.choices = new List<string> { "3D比較", "プロパティ比較" };
            display.index = propertyComparison ? 1 : 0;
            display.RegisterValueChangedCallback(e => { propertyComparison = display.index == 1; UpdateDisplayMode(); });
            var avatarField = rootVisualElement.Q<ObjectField>("avatar"); avatarField.objectType = typeof(GameObject);
            avatarField.value = avatar; avatarField.RegisterValueChangedCallback(e => avatar = e.newValue as GameObject);
            var scope = rootVisualElement.Q<DropdownField>("scope"); scope.choices = new List<string> {"アバター全体", "衣装全体"};
            scope.index = outfit ? 1 : 0; scope.RegisterValueChangedCallback(e => { outfit = scope.index == 1; RootsUI(); });
            rootVisualElement.Q<Button>("start").clicked += StartComparison;
            rootVisualElement.Q<Button>("edit-selection").clicked += () => OpenSelection(Selection.activeObject);
            rootVisualElement.Q<Button>("change-target").clicked += () => Guard(() =>
            {
                if (!ConfirmEndComparison()) return;
                ReleaseComparison(); Refresh();
            });
            rootVisualElement.Q<Button>("restore-preview").clicked += () => Guard(() =>
            {
                if (session == null) return;
                RestorePreview(); UpdateControls();
            });
            rootVisualElement.Q<ToolbarSearchField>("search").RegisterValueChangedCallback(e => { search = e.newValue; MaterialsUI(); });

            rootVisualElement.Q<Button>("add").clicked += () => Add(false);
            rootVisualElement.Q<Button>("duplicate").clicked += () => Add(true);
            rootVisualElement.Q<Button>("focus").clicked += () => { if (rendering != null) { rendering.FocusEntry = selected; rendering.PanOffset = Vector3.zero; RequestCameraRender(); } };
            rootVisualElement.Q<Button>("frame").clicked += () => { if (rendering != null) { rendering.FocusEntry = -1; rendering.PanOffset = Vector3.zero; RequestCameraRender(); } };
            rootVisualElement.Q<Button>("reset").clicked += () => Guard(() =>
            {
                if (!CanEdit()) return;
                if (session.EditingComponent) { session.ResetComponent(Current); Changed(); return; }
                Undo.RecordObject(Current.Materials[selected], "マテリアルをリセット");
                var target = Current.Materials[selected]; target.shader = session.Entries[selected].Baseline.shader;
                target.CopyPropertiesFromMaterial(session.Entries[selected].Baseline); Changed();
            });
            rootVisualElement.Q<Button>("save").clicked += () => TrySave();
            var mode = rootVisualElement.Q<EnumField>("save-mode"); mode.Init(saveMode);
            mode.RegisterValueChangedCallback(e => { saveMode = (SaveMode)e.newValue; SaveHelp(); });
            var output = rootVisualElement.Q<ObjectField>("output"); output.objectType = typeof(DefaultAsset); output.allowSceneObjects = false;
            output.value = AssetDatabase.LoadAssetAtPath<DefaultAsset>(outputFolder);
            output.RegisterValueChangedCallback(e => outputFolder = AssetDatabase.GetAssetPath(e.newValue));
            rootVisualElement.Q<DropdownField>("save-candidate").RegisterValueChangedCallback(e => { saving = rootVisualElement.Q<DropdownField>("save-candidate").index; UpdateControls(); });
            var width = rootVisualElement.Q<Slider>("editor-width"); width.value = editorWidth;
            Action resize = ResizeEditorPanel;
            width.RegisterValueChangedCallback(e => { editorWidth = e.newValue; resize(); });
            rootVisualElement.RegisterCallback<GeometryChangedEvent>(_ => resize()); resize();
            rootVisualElement.Q<ScrollView>("property-comparison").contentViewport
                .RegisterCallback<GeometryChangedEvent>(_ => ResizePropertyColumns());
            RootsUI(); Refresh(); SaveHelp();
            UpdateDisplayMode();
        }
        Candidate Current => session != null && editing >= 0 && editing < session.Candidates.Count ? session.Candidates[editing] : null;
        bool CanEdit() => session != null && !session.Saved && Current != null && selected >= 0 && selected < session.Entries.Count;
        void UpdateControls()
        {
            if (rootVisualElement.Q("add") == null) return;
            bool editable = session != null && !session.Saved;
            var componentMode = session != null && session.EditingComponent;
            rootVisualElement.Q("save-mode").SetEnabled(!componentMode);
            rootVisualElement.Q<Button>("reset").text = componentMode ? "選択設定の編集をすべてリセット" : "選択マテリアルをリセット";
            var editHelp = rootVisualElement.Q<Label>("edit-mode-help");
            editHelp.style.display = componentMode ? DisplayStyle.Flex : DisplayStyle.None;
            editHelp.text = componentMode
                ? "編集対象: " + ExistingOverrides.Location(session.EditTarget) + "\n編集欄: 選択設定の直後 / 3D: 後続設定込み"
                : "";
            editHelp.tooltip = componentMode ? "変更は対象マテリアル全体に共有されます。後続設定で上書きされる項目は、編集しても3D表示が変わらない場合があります。" : "";
            var reason = session == null ? "先に比較を開始してください。" : session.Saved ? "保存済みです。比較を開始し直してください。" : "比較の準備中です。";
            foreach (var id in new[] {"add", "duplicate", "reset"})
            {
                var button = rootVisualElement.Q<Button>(id);
                bool enabled = editable && (id == "add" || CanEdit());
                button.SetEnabled(enabled); button.tooltip = enabled ? "" : reason;
            }
            foreach (var id in new[] {"focus", "frame"}) rootVisualElement.Q<Button>(id).SetEnabled(rendering != null);
            rootVisualElement.Q("lighting-panel").SetEnabled(rendering != null);
            var save = rootVisualElement.Q<Button>("save");
            save.SetEnabled(editable && !blocked && SelectedHasChanges);
            save.tooltip = blocked ? error ?? "元の設定が変わっています。比較を開始し直してください。" : !editable ? reason : !SelectedHasChanges ? "保存対象の案には変更がありません。" : "";
            rootVisualElement.Q<Button>("restore-preview").SetEnabled(session != null);
            rootVisualElement.Q<Button>("start").SetEnabled(true);
            rootVisualElement.Q<Button>("change-target").SetEnabled(session != null);
            foreach (var id in new[] {"avatar", "scope", "roots"}) rootVisualElement.Q(id).SetEnabled(session == null);

        }
        void RootsUI()
        {
            var parent = rootVisualElement.Q("roots"); parent.Clear();
            if (!outfit) return;
            for (int i = 0; i < roots.Count; i++)
            {
                var row = new VisualElement(); row.AddToClassList("candidate-row");
                var index = i; var root = roots[i];
                var field = new ObjectField("衣装ルート") {objectType = typeof(GameObject), value = root}; field.AddToClassList("candidate-name");
                field.RegisterValueChangedCallback(e => roots[index] = e.newValue as GameObject);
                row.Add(field); row.Add(new Button(() => { roots.RemoveAt(index); RootsUI(); }) {text = "削除"}); parent.Add(row);
            }
            parent.Add(new Button(() => { roots.Add(null); RootsUI(); }) {text = "衣装ルートを追加"});
        }
        void StartComparison()
        {
            if (!ConfirmEndComparison()) return;
            Guard(() =>
            {
                if (!ComparisonSession.IsAvatar(avatar) || EditorUtility.IsPersistent(avatar)) throw new InvalidOperationException("Scene上のアバターを指定してください。");
                var candidateRoots = outfit ? roots : new List<GameObject> {avatar};
                if (candidateRoots.Count == 0 || candidateRoots.Any(r => r == null || !r.transform.IsChildOf(avatar.transform)))
                    throw new InvalidOperationException("衣装ルートをアバター配下に指定してください。");
                var nextSession = ComparisonSession.Create(avatar, candidateRoots);
                ComparisonRenderer nextRendering;
                try { nextRendering = new ComparisonRenderer(nextSession) { Lighting = lighting }; }
                catch { nextSession.Release(); DestroyImmediate(nextSession); throw; }
                ReleaseComparison();
                session = nextSession; rendering = nextRendering; editing = saving = selected = 0;
                hasUnsavedChanges = false; Refresh();
            });
        }
        void Tick()
        {
            TickVrUI();
            if (EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode || rootVisualElement.Q("status") == null) return;
            TickLightOrbit();
            // Camera and lighting input do not change geometry or materials. Coalesce input events into
            // one frame and defer the expensive inspection until the gesture has stopped.
            if (vr == null && session != null && rendering != null && cameraRenderPending)
            {
                if (EditorApplication.timeSinceStartup < nextCameraFrame) return;
                try
                {
                    rendering.Update(session.Candidates.Where(c => c.Visible).Take(3), false);
                    foreach (var image in images) image.MarkDirtyRepaint();
                    cameraRenderPending = false;
                    nextCameraFrame = EditorApplication.timeSinceStartup + 1.0 / 60;
                }
                catch (Exception ex) { cameraRenderPending = false; error = Integration.Message(ex); blocked = true; UpdateControls(); Status(error); }
            }
            if (EditorApplication.timeSinceStartup - lastCameraInput < .15) return;
            if (EditorApplication.timeSinceStartup < nextUpdate) return;
            nextUpdate = EditorApplication.timeSinceStartup + (vr == null ? .1 : .25);
            try
            {
                var candidatesChanged = true;
                var sourceChanged = true;
                if (vr != null && session != null)
                {
                    if (vrChanges == null) vrChanges = new VrChangeTracker(session);
                    candidatesChanged = vrChanges.CandidatesChanged();
                    sourceChanged = vrChanges.SourceChanged();
                    if (!candidatesChanged && !sourceChanged) return;
                }
                UpdateShaderInspector();
                if (session == null) return;
                if (candidatesChanged)
                {
                    session.SyncComponentEdits();
                    var signature = string.Join("\n", session.Candidates.SelectMany(c => c.Materials).Select(m => EditorJsonUtility.ToJson(m)));
                    if (signature != candidateSignature)
                    {
                        candidateSignature = signature; session.Revision++; ChangesUI();
                    }
                    hasUnsavedChanges = session.HasChanges;
                }
                saveChangesMessage = "Material Previewに未保存の案があります。保存では選択中の保存対象を使用します。";
                if (rendering == null) { UpdateControls(); return; }
                string previewMessage = null;
                if (!session.Saved && sourceChanged)
                {
                    try { session.Validate(); blocked = false; vrChanges?.CaptureSources(); }
                    catch (Exception ex) { blocked = true; previewMessage = Integration.Message(ex) + " 案は編集できますが、保存には比較の再開始が必要です。"; }
                }
                if (vr == null)
                {
                    rendering.Update(session.Candidates.Where(c => c.Visible).Take(3), !blocked);
                    foreach (var image in images) image.MarkDirtyRepaint();
                }
                UpdateControls();
                Status(previewMessage ?? error ?? (session.Saved ? "保存済み。AS ISを保持しています。続ける場合は比較を開始し直してください。"
                    : "AS ISを保持 / 編集: " + Current?.Name + " / " + session.Entries.Count + " マテリアル"
                        + (session.Warnings.Count == 0 ? "" : "\n" + string.Join("\n", session.Warnings))));
                vrChanges?.CaptureCandidates();
            }
            catch (Exception ex) { error = Integration.Message(ex); blocked = true; UpdateControls(); Status(error); }
        }
        void RequestCameraRender()
        {
            cameraRenderPending = true;
            lastCameraInput = EditorApplication.timeSinceStartup;
        }
        void Add(bool duplicate)
        {
            Guard(() =>
            {
                if (session == null || session.Saved) return;
                session.AddCandidate(duplicate ? Current : null); editing = session.Candidates.Count - 1; Refresh();
            });
        }
        void Refresh()
        {
            if (rootVisualElement.Q("materials") == null) return;
            MaterialsUI(); CandidatesUI(); ChangesUI();
            UpdateShaderInspector();
            UpdateControls();
            Status(error ?? (session == null ? "アバターを指定して比較を開始してください。" : "比較を更新しています。"));
        }
        void MaterialsUI()
        {
            var list = rootVisualElement.Q<ScrollView>("materials"); list.Clear();
            if (session == null) return;
            for (int i = 0; i < session.Entries.Count; i++)
            {
                var entry = session.Entries[i]; int index = i;
                if (entry.Source == null) { list.Add(new Label("元マテリアルが削除されました。")); continue; }
                var uses = session.Slots.Where(s => s.Entry == index).ToArray();
                var text = entry.Source.name + "  (" + uses.Length + ")\n" + entry.Source.shader.name;
                if (!string.IsNullOrEmpty(search) && (text + string.Join(" ", uses.Select(s => s.Path))).IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var button = new Button(() => { selected = index; MaterialsUI(); ChangesUI(); UpdateShaderInspector(); }) {text = text};
                button.AddToClassList("material-row"); if (selected == index) button.AddToClassList("selected-row");
                button.tooltip = AssetDatabase.GetAssetPath(entry.Source) + "\n" + string.Join("\n", uses.Select(s => s.Path + " [" + s.Index + "]"
                    + (s.Renderer.enabled && s.Renderer.gameObject.activeInHierarchy ? "" : "（非表示）")));
                list.Add(button);
            }
        }
        void CandidatesUI()
        {
            var cards = rootVisualElement.Q("cards"); cards.Clear(); images.Clear(); candidateTitles.Clear();
            var rows = rootVisualElement.Q("candidates"); rows.Clear();
            var save = rootVisualElement.Q<DropdownField>("save-candidate");
            if (session == null) { save.choices = new List<string>(); return; }
            Card(cards, null, "AS IS");
            for (int i = 0; i < session.Candidates.Count; i++)
            {
                var index = i; var candidate = session.Candidates[i];
                if (candidate.Visible) Card(cards, candidate, candidate.Name);
                var row = new VisualElement(); row.AddToClassList("candidate-row");
                var visible = new Toggle("比較") {value = candidate.Visible};
                visible.RegisterValueChangedCallback(e =>
                {
                    if (e.newValue && session.Candidates.Count(c => c.Visible) >= 3)
                    { visible.SetValueWithoutNotify(false); Status("同時に表示するTO BEは3案までです。別の案の比較チェックを外してください。"); return; }
                    candidate.Visible = e.newValue; CandidatesUI();
                });
                row.Add(visible);
                var name = new TextField {value = candidate.Name}; name.AddToClassList("candidate-name");
                name.name = "candidate-name-" + index;
                name.RegisterValueChangedCallback(e =>
                {
                    candidate.Name = e.newValue;
                    if (candidateTitles.TryGetValue(candidate, out var title)) title.text = candidate.Name;
                    UpdateSaveChoices(); ChangesUI();
                }); row.Add(name);
                row.Add(new Button(() => { editing = index; CandidatesUI(); ChangesUI(); UpdateShaderInspector(); UpdateControls(); }) {name = "edit-candidate-" + index, text = index == editing ? "編集中" : "編集"});
                var delete = new Button(() =>
                {
                    if (session.Candidates.Count == 1) return;
                    if (session.CandidateChanged(candidate) && !EditorUtility.DisplayDialog("案の削除", candidate.Name + " の編集を破棄しますか？", "破棄", "キャンセル")) return;
                    var edited = Current; var saved = session.Candidates[saving];
                    if (edited == candidate) ReleaseShaderEditor();
                    else ReleaseCandidateInspectors();
                    session.Remove(candidate); editing = Mathf.Max(0, session.Candidates.IndexOf(edited));
                    saving = Mathf.Max(0, session.Candidates.IndexOf(saved)); Refresh();
                }) {name = "delete-candidate-" + index, text = "削除"};
                delete.SetEnabled(session.Candidates.Count > 1 && !session.Saved); row.Add(delete); rows.Add(row);
            }
            UpdateSaveChoices();
        }
        void UpdateSaveChoices()
        {
            var save = rootVisualElement.Q<DropdownField>("save-candidate");
            save.choices = session.Candidates.Select((c,i) => (i + 1) + ": " + c.Name).ToList();
            saving = Mathf.Clamp(saving, 0, save.choices.Count - 1); save.SetValueWithoutNotify(save.choices[saving]);
        }
        void Card(VisualElement parent, Candidate candidate, string title)
        {
            var card = new VisualElement(); card.AddToClassList("card");
            var label = new Label(title); label.AddToClassList("card-title"); card.Add(label);
            if (candidate != null) candidateTitles[candidate] = label;
            var image = new Image {image = rendering?.Texture(candidate), scaleMode = ScaleMode.ScaleToFit};
            image.AddToClassList("preview-image"); card.Add(image); parent.Add(card); images.Add(image);
            Vector2 previous = default;
            int dragButton = -1;
            image.RegisterCallback<PointerDownEvent>(e =>
            {
                if (dragButton >= 0 || e.button < 0 || e.button > 2) return;
                dragButton = e.button; previous = e.position; image.CapturePointer(e.pointerId);
                e.StopPropagation();
            });
            image.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (!image.HasPointerCapture(e.pointerId) || rendering == null || dragButton < 0) return;
                var current = (Vector2)e.position; var delta = current - previous; previous = current;
                if (dragButton == 2)
                    rendering.Pan(delta, Mathf.Min(image.contentRect.width, image.contentRect.height));
                else
                {
                    rendering.Yaw -= delta.x * .4f;
                    rendering.Pitch = Mathf.Clamp(rendering.Pitch + delta.y * .3f, -80, 80);
                }
                RequestCameraRender();
            });
            image.RegisterCallback<PointerUpEvent>(e =>
            {
                if (e.button != dragButton) return;
                dragButton = -1; image.ReleasePointer(e.pointerId); e.StopPropagation();
            });
            image.RegisterCallback<PointerCaptureOutEvent>(_ => dragButton = -1);
            image.RegisterCallback<WheelEvent>(e => { if (rendering != null) { rendering.Zoom = Mathf.Clamp(rendering.Zoom * Mathf.Exp(e.delta.y * .04f), .1f, 8); RequestCameraRender(); } e.StopPropagation(); });
        }
        void Changed()
        {
            error = null;
            session.SyncComponentEdits();
            session.Revision++; hasUnsavedChanges = session.HasChanges;
            rootVisualElement.schedule.Execute(ChangesUI);
        }
        void ChangesUI()
        {
            var panel = rootVisualElement.Q<ScrollView>("changes"); panel.Clear();
            ExistingUI();
            if (session != null && session.EditingComponent && Current != null)
            {
                foreach (var name in session.OriginalOverride.Names().Except(Current.Override.Names())) panel.Add(new Label(name + ": Override解除"));
            }
            if (Current == null || session.Entries.Any(e => e.Source == null)) return;
            foreach (var change in MaterialSaveService.Changes(session, Current))
                panel.Add(new Label(session.Entries[change.Key].Source.name + ": " + string.Join(", ", change.Value.Names())) {style = {whiteSpace = WhiteSpace.Normal}});
            rootVisualElement.Q<Label>("editor-title").text = Current.Name + (session.Entries.Count > selected ? " / " + session.Entries[selected].Source.name : "");
        }
        void ExistingUI()
        {
            var panel = rootVisualElement.Q<ScrollView>("existing-overrides");
            if (panel == null) return;
            panel.Clear();
            if (Current == null || selected < 0 || selected >= session.Entries.Count || session.Entries[selected].Source == null) return;
            var entry = session.Entries[selected];
            if (entry.Layers.Count == 0) panel.Add(new Label("既存の上書きはありません。"));
            for (int i = 0; i < entry.Layers.Count; i++)
            {
                var layer = entry.Layers[i];
                panel.Add(new Label(layer.Label) {style = {whiteSpace = WhiteSpace.Normal}});
                foreach (var name in layer.ExplicitProperties.Distinct())
                {
                    bool overwritten = layer.Component != null && entry.Layers.Skip(i + 1).Any(l => l.Component != null && l.ExplicitProperties.Contains(name));
                    panel.Add(new Label(name + ": " + ValueText(layer.Before, name) + " → " + ValueText(layer.After, name)
                        + (overwritten ? "（後段で上書き）" : "")) {style = {whiteSpace = WhiteSpace.Normal}});
                }
                if (layer.Before != null && layer.After != null)
                    foreach (var property in MaterialDelta.Between(layer.Before, layer.After).Names().Except(layer.ExplicitProperties))
                        panel.Add(new Label(property + ": " + ValueText(layer.Before, property) + " → " + ValueText(layer.After, property))
                            {style = {whiteSpace = WhiteSpace.Normal}});
                if (layer.Component != null)
                    panel.Add(new Button(() => EditorGUIUtility.PingObject(layer.Component)) {text = "設定元を表示"});
            }
            if (session.EditingComponent)
            {
                panel.Add(new Label("編集中の設定のOverride（解除すると前段の値を継承）"));
                foreach (var name in Current.Override.Names().Distinct().ToArray())
                {
                    var propertyName = name;
                    var remove = new Button(() => Guard(() => { session.RemoveOverride(Current, propertyName); Changed(); }))
                        { text = propertyName + " のOverrideを解除" };
                    remove.SetEnabled(!session.Saved); panel.Add(remove);
                }
                return;
            }
            if (saveMode != SaveMode.ExistingOverrides) return;
            var targets = entry.Layers.Select(l => l.Component).Where(c => c != null).Distinct().ToList();
            if (targets.Count == 0)
            {
                panel.Add(new Label("更新先のAOME / TTTがありません。Variant保存等を選択してください。") {style = {whiteSpace = WhiteSpace.Normal}});
                return;
            }
            var choices = targets.Select((c, i) => (i + 1) + ": " + ExistingOverrides.Location(c)).ToList();
            int index = targets.IndexOf(Current.UpdateTargets[selected]);
            var field = new DropdownField("既存設定の更新先", choices, Mathf.Max(0, index)) {name = "existing-target"};
            field.RegisterValueChangedCallback(e => { Current.UpdateTargets[selected] = targets[field.index]; });
            field.SetEnabled(!session.Saved); panel.Add(field);
        }
        static string ValueText(Material material, string name)
        {
            if (material == null) return "未割り当て";
            if (name == "Shader") return material.shader == null ? "なし" : material.shader.name;
            if (name == "Render Queue") return MaterialDelta.RawQueue(material).ToString();
            var value = MaterialDelta.Values(material).FirstOrDefault(p => p.Name == name);
            if (value == null) return "—";
            switch (value.Type)
            {
                case ShaderPropertyType.Color: return value.Color.ToString("G5");
                case ShaderPropertyType.Vector: return value.Vector.ToString("G5");
                case ShaderPropertyType.Int: return value.Int.ToString();
                case ShaderPropertyType.Texture: return (value.Texture == null ? "なし" : value.Texture.name) + " / Scale " + value.Scale + " / Offset " + value.Offset;
                default: return value.Float.ToString("G5");
            }
        }
        string SaveUndoHelp()
        {
            switch (saveMode)
            {
                case SaveMode.DirectMaterial:
                    return "Undo / Redoの対象はMaterial値です。本ツールは.matを自動保存しないため、ファイルへ反映するにはアセットを再保存してください。";
                case SaveMode.MaterialVariant:
                    return "Undo / Redoの対象はRendererの割り当てです。生成Variantは削除しません。Redoは同じVariantを使用します。Sceneの再保存が必要です。";
                case SaveMode.ExistingOverrides:
                    return "Undo / Redoの対象はScene上の設定値・Prefab Overrideです。Prefabアセットは変更しません。Sceneの再保存が必要です。";
                default:
                    return "Undo / Redoの対象はSceneに追加した設定オブジェクト・コンポーネントです。Sceneの再保存が必要です。";
            }
        }
        void SaveHelp()
        {
            var help = rootVisualElement.Q<Label>("save-help");
            var details = rootVisualElement.Q<Label>("save-details");
            help.text = saveMode == SaveMode.DirectMaterial ? "元の.matを更新します（共有先にも反映）。"
                : saveMode == SaveMode.MaterialVariant ? "Variantを作成して割り当てます。Sceneは別途保存してください。"
                : "Sceneに設定を追加します。Sceneは別途保存してください。";
            details.text = saveMode == SaveMode.DirectMaterial
                ? "元の.matを更新します。参照するPrefab・Sceneにも影響します（全プロジェクトの参照は未走査）。"
                : "保存前後に複製アバターでNDMF評価を行い、Material状態を照合します。不一致・非対応の編集は保存を中止します。Sceneは自動保存しません。";
            rootVisualElement.Q<ObjectField>("output").SetEnabled(saveMode == SaveMode.MaterialVariant);
            if (saveMode == SaveMode.ExistingOverrides)
            {
                help.text = "「既存の上書き」で更新先を選択してください。Sceneは別途保存してください。";
                details.text = "各マテリアルの既存設定欄で更新先を選びます。今回の編集だけをScene上の設定へ保存し、Prefabアセットには適用しません。値を戻す操作は上書きの解除ではありません。";
            }
            if (session != null && session.EditingComponent)
            {
                help.text = "選択中の設定を更新します。Sceneは別途保存してください。";
                details.text = "選択したコンポーネントへ設定とOverride解除を保存します。Prefab由来の設定はScene Overrideになります。対象指定・有効状態は維持します。";
            }
            details.text += "\n" + SaveUndoHelp();
            ExistingUI();
        }
        bool TrySave()
        {
            try
            {
                if (session == null || saving < 0 || saving >= session.Candidates.Count || blocked) return false;
                var candidate = session.Candidates[saving];
                var otherChanges = session.Candidates.Where(c => c != candidate && session.CandidateChanged(c)).Select(c => c.Name).ToArray();
                var errors = MaterialSaveService.Check(session, candidate, saveMode);
                if (errors.Count != 0) throw new InvalidOperationException(string.Join("\n", errors));
                var details = string.Join("\n", MaterialSaveService.Changes(session, candidate).Select(c =>
                    session.Entries[c.Key].Source.name + ": " + string.Join(", ", c.Value.Names()) + "\n" +
                    string.Join(", ", session.Slots.Where(s => s.Entry == c.Key).Select(s => s.Path + "[" + s.Index + "]"))
                    + (saveMode == SaveMode.ExistingOverrides ? "\n更新先: " + ExistingOverrides.Location(candidate.UpdateTargets[c.Key]) : "")));
                if (session.EditingComponent) details = "更新先: " + ExistingOverrides.Location(session.EditTarget) + "\n" + details
                    + "\nOverride解除: " + string.Join(", ", session.OriginalOverride.Names().Except(candidate.Override.Names()));
                if (!EditorUtility.DisplayDialog("保存内容の確認", candidate.Name + "\n検証・保存を開始すると途中キャンセルはできません。完了または失敗までお待ちください。\n方式: " + saveMode + "\n" + details
                    + (saveMode == SaveMode.DirectMaterial ? "\n\n共有元の.matを更新し、他のPrefab・Sceneにも影響します。" : "")
                    + (saveMode == SaveMode.MaterialVariant ? "\n保存先: " + outputFolder : "")
                    + "\n\n保存成功後は編集を終了し、採用案とAS ISを表示用に保持します。続ける場合は比較を開始し直してください。\n" + SaveUndoHelp()
                    + (otherChanges.Length == 0 ? "" : "\n\n保存成功後、次の未保存案は破棄されます（Undoでは復元できません）:\n" + string.Join("\n", otherChanges)),
                    otherChanges.Length == 0 ? "検証して保存" : "保存して他の案を破棄", "キャンセル")) return false;
                EditorUtility.DisplayProgressBar("Material Preview", "保存予定のMaterial状態を検証しています…（途中キャンセル不可）", .3f);
                ReleaseShaderEditor();
                MaterialSaveService.Save(session, candidate, saveMode, outputFolder, true);
                editing = saving = 0;
                hasUnsavedChanges = false; blocked = false; error = null; Refresh();
                return true;
            }
            catch (Exception ex) { error = "保存できませんでした: " + Integration.Message(ex); Status(error); return false; }
            finally { EditorUtility.ClearProgressBar(); }
        }
        public override void SaveChanges()
        {
            if (!TrySave()) throw new InvalidOperationException("Material Previewの保存が完了していません。案を保持してください。");
            base.SaveChanges();
        }
        void Guard(Action action)
        {
            try { action(); } catch (Exception ex) { error = Integration.Message(ex); Status(error); }
        }
        void Status(string text) { var label = rootVisualElement.Q<Label>("status"); if (label != null) label.text = text; }
    }
}


