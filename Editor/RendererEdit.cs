using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace GokouKotori.MaterialPreview
{
    internal static class RendererEdit
    {
        internal sealed class Target
        {
            internal Component Component;
            internal bool Active;
            internal string Label;
        }

        internal static bool CanOpen(Renderer renderer) => renderer != null
            && (renderer is MeshRenderer || renderer is SkinnedMeshRenderer)
            && !ExistingOverrides.EditorOnly(renderer.transform)
            && ExistingOverrides.FindAvatar(renderer.gameObject) != null;

        internal static List<Target> Targets(ComparisonSession session, int slotIndex)
        {
            var result = new List<Target>();
            var slot = session.Slots.FirstOrDefault(s => s.Renderer == session.PreviewRenderer && s.Index == slotIndex);
            if (slot == null || slot.Source == null) return result;
            var ao = Integration.Type(ExistingOverrides.Ao);
            if (ao != null)
            {
                var assignments = new ExistingOverrides.AoAssignments(session.Slots);
                var processor = Integration.Require("Aoyon.MaterialEditor.Processor.MaterialEditorProcessor");
                foreach (var component in session.Avatar.GetComponentsInChildren(ao, true))
                    if (ExistingOverrides.FindAvatar(component.gameObject) == session.Avatar
                        && !ExistingOverrides.EditorOnly(component.transform) && assignments.Select(component).Contains(slot))
                        Add(component, (bool)Integration.Invoke(processor, "IsEffective", component, null));
            }
            var search = Integration.Type("net.rs64.TexTransTool.TexTransBehaviorSearch");
            if (search != null)
            {
                var phases = (IDictionary)Integration.Invoke(search, "FindAtPhase", session.Avatar);
                var order = (IEnumerable)Integration.Invoke(Integration.Require("net.rs64.TexTransTool.TexTransPhaseUtility"), "EnumerateAllPhase");
                foreach (var phase in order)
                    foreach (var component in ((IEnumerable)phases[phase]).Cast<Component>())
                        if (ExistingOverrides.IsTtt(component) && !ExistingOverrides.EditorOnly(component.transform)
                            && ExistingOverrides.FindAvatar(component.gameObject) == session.Avatar
                            && Integration.Get(component, "TargetMaterial") as Material == slot.Source)
                            Add(component, (bool)Integration.Invoke(search, "CheckIsActiveBehavior", component, session.Avatar));
            }
            return result;

            void Add(Component component, bool active)
            {
                var path = AnimationUtility.CalculateTransformPath(component.transform, session.Avatar.transform);
                var index = Array.IndexOf(component.gameObject.GetComponents<Component>(), component);
                result.Add(new Target { Component = component, Active = active,
                    Label = (result.Count + 1) + ": " + component.GetType().Name + " / "
                        + session.Avatar.name + "/" + path + " [" + index + "]" + (active ? "" : "（無効）") });
            }
        }
    }
}
