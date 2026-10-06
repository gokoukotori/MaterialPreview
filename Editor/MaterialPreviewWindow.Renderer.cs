using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace GokouKotori.MaterialPreview
{
    public sealed partial class MaterialPreviewWindow
    {
        [SerializeField] int rendererSlot = -1;
        [SerializeField] bool rendererTargetPending;

        [MenuItem("GameObject/Gokoukotori/MaterialPreview/Rendererのマテリアルを編集", false, 50)]
        static void OpenRendererContext(MenuCommand command) => GetWindow<MaterialPreviewWindow>()
            .OpenRendererSelection(command.context as GameObject ?? Selection.activeGameObject);

        [MenuItem("GameObject/Gokoukotori/MaterialPreview/Rendererのマテリアルを編集", true)]
        static bool CanOpenRendererContext(MenuCommand command) => !EditorApplication.isPlayingOrWillChangePlaymode
            && (command.context as GameObject ?? Selection.activeGameObject) is GameObject go
            && go.GetComponents<Renderer>().Any(RendererEdit.CanOpen);

        void OpenRendererSelection(GameObject go)
        {
            var targets = go == null ? Array.Empty<Renderer>() : go.GetComponents<Renderer>().Where(RendererEdit.CanOpen).ToArray();
            if (targets.Length == 1) { SelectRenderer(targets[0]); return; }
            if (targets.Length == 0) { Status("Scene上のアバター内の対応Rendererを選択してください。"); return; }
            var menu = new GenericMenu();
            for (int i = 0; i < targets.Length; i++)
            {
                var renderer = targets[i];
                menu.AddItem(new GUIContent((i + 1) + ": " + renderer.GetType().Name), false, () => SelectRenderer(renderer));
            }
            menu.ShowAsContext();
        }

        internal bool SelectRenderer(Renderer target, int slotIndex = -1, Component requested = null, bool automatic = true, bool restart = false)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || !RendererEdit.CanOpen(target)) return false;
            bool opened = false;
            Guard(() =>
            {
                ComparisonSession catalog = null, next = null;
                ComparisonRenderer preview = null;
                try
                {
                    var sameRenderer = session != null && session.RendererOnly && session.PreviewRenderer == target;
                    catalog = sameRenderer && !session.Saved && !restart ? session : ComparisonSession.CreateForRenderer(target);
                    if (catalog == session) catalog.Validate();
                    if (automatic && !restart && catalog == session && slotIndex == rendererSlot && rendererSlot >= 0)
                    { opened = true; return; }
                    if (!catalog.Slots.Any(s => s.Renderer == target && s.Index == slotIndex && s.Source != null))
                        slotIndex = catalog.Slots.Where(s => s.Renderer == target && s.Source != null).Select(s => s.Index).DefaultIfEmpty(-1).First();
                    var choices = RendererEdit.Targets(catalog, slotIndex);
                    var active = choices.Where(c => c.Active).ToArray();
                    var editTarget = automatic ? (active.Length == 1 ? active[0].Component : null) : requested;
                    if (automatic && !restart && catalog == session && session.EditingComponent
                        && choices.Any(c => c.Component == session.EditTarget)) editTarget = session.EditTarget;
                    var pending = automatic && editTarget == null && choices.Count > 0;
                    if (!automatic && requested != null && !choices.Any(c => c.Component == requested))
                        throw new InvalidOperationException("選択した設定はこのRendererのマテリアルを対象にしていません。");
                    var reuse = !restart && catalog == session && session.EditTarget == editTarget;
                    if (!reuse)
                    {
                        if (!ConfirmEndComparison()) return;
                        // Saving can change slot assignments. Resolve again before constructing the next editor.
                        if (catalog == session || (session != null && session.Saved))
                        {
                            if (catalog != session) { catalog.Release(); DestroyImmediate(catalog); }
                            catalog = ComparisonSession.CreateForRenderer(target);
                            choices = RendererEdit.Targets(catalog, slotIndex);
                            active = choices.Where(c => c.Active).ToArray();
                            editTarget = automatic ? (active.Length == 1 ? active[0].Component : null) : requested;
                            pending = automatic && editTarget == null && choices.Count > 0;
                            if (requested != null && !choices.Any(c => c.Component == requested))
                                throw new InvalidOperationException("保存後に編集対象が変わりました。マテリアルを選び直してください。");
                        }
                        next = editTarget != null ? ComparisonSession.CreateForComponent(editTarget, target) : catalog;
                        if (next == catalog) catalog = null;
                        preview = new ComparisonRenderer(next) { Lighting = lighting };
                        ReleaseComparison(); session = next; next = null; rendering = preview; preview = null;
                        avatar = session.Avatar; outfit = false; roots.Clear(); editing = saving = 0;
                        saveMode = session.EditingComponent ? SaveMode.ExistingOverrides
                            : saveMode == SaveMode.ExistingOverrides ? SaveMode.AOMaterialEditor : saveMode;
                    }
                    rendererSlot = slotIndex; rendererTargetPending = pending;
                    selected = session.Slots.FirstOrDefault(s => s.Renderer == target && s.Index == slotIndex)?.Entry ?? -1;
                    if (reuse) { Refresh(); SaveHelp(); }
                    else CreateGUI();
                    opened = true;
                }
                finally
                {
                    preview?.Dispose();
                    if (next != null) { next.Release(); DestroyImmediate(next); }
                    if (catalog != null && catalog != session) { catalog.Release(); DestroyImmediate(catalog); }
                }
            });
            return opened;
        }

        void RendererMaterialsUI(ScrollView list)
        {
            foreach (var slot in session.Slots.Where(s => s.Renderer == session.PreviewRenderer).OrderBy(s => s.Index))
            {
                var name = slot.Source == null ? "未割り当て" : slot.Source.name;
                var shader = slot.Source == null || slot.Source.shader == null ? "" : slot.Source.shader.name;
                var text = "[" + slot.Index + "] " + name + "\n" + shader;
                if (!string.IsNullOrEmpty(search) && (text + slot.Path).IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var button = new Button(() => SelectRenderer(session.PreviewRenderer, slot.Index))
                    { name = "renderer-slot-" + slot.Index, text = text, tooltip = slot.Path + " [" + slot.Index + "]" };
                button.AddToClassList("material-row"); button.EnableInClassList("selected-row", rendererSlot == slot.Index);
                button.SetEnabled(slot.Source != null); list.Add(button);
            }
            if (!session.Slots.Any(s => s.Renderer == session.PreviewRenderer)) list.Add(new Label("マテリアルスロットがありません。"));
        }

        void RendererTargetUI()
        {
            var panel = rootVisualElement.Q("renderer-edit-target");
            if (panel == null) return;
            panel.Clear();
            panel.EnableInClassList("hidden", session == null || !session.RendererOnly);
            if (session == null || !session.RendererOnly) return;
            var target = session.PreviewRenderer;
            panel.Add(new Label("表示対象: " + (target == null ? "削除されたRenderer" : target.name + " / " + target.GetType().Name)) { name = "renderer-preview-target" });
            var choices = RendererEdit.Targets(session, rendererSlot);
            var labels = choices.Select(c => c.Label).ToList();
            labels.Insert(0, "通常のマテリアル編集");
            if (rendererTargetPending) labels.Insert(0, "編集先を選択してください");
            var selectedChoice = session.EditingComponent ? choices.FindIndex(c => c.Component == session.EditTarget) + 1 : 0;
            var field = new DropdownField("編集先", labels, rendererTargetPending ? 0 : selectedChoice) { name = "renderer-target" };
            field.RegisterValueChangedCallback(_ =>
            {
                var index = field.index - (rendererTargetPending ? 1 : 0);
                if (index < 0) return;
                var component = index == 0 ? null : choices[index - 1].Component;
                if (!SelectRenderer(target, rendererSlot, component, false)) RendererTargetUI();
            });
            field.SetEnabled(target != null && rendererSlot >= 0 && !blocked);
            panel.Add(field);
            var text = rendererTargetPending ? "複数の設定、または無効な設定が見つかりました。適用順に並ぶ編集先を選択してください。"
                : session.EditingComponent ? RendererEditImpact() : "保存方式を選んで保存します。既存設定を直接編集する場合は編集先を切り替えてください。";
            if (target != null && (!target.enabled || !target.gameObject.activeInHierarchy)) text += "\n対象Rendererは非表示です。プレビューも元の表示状態に従います。";
            var help = new Label(text) { name = "renderer-edit-help" }; help.AddToClassList("help"); panel.Add(help);
        }

        string RendererEditImpact()
        {
            var affected = session.Slots.Where(s => s.Entry >= 0).ToArray();
            return "この設定の適用対象全体（" + affected.Select(s => s.Renderer).Distinct().Count() + " Renderer / " + affected.Length
                + " スロット）を編集します。選択Renderer以外にも反映されます。\n編集欄は設定直後、3Dは後続設定込みです。後段で上書きされる値や無効な設定の編集は3Dに反映されません。";
        }
    }
}
