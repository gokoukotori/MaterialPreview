using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace GokouKotori.MaterialPreview
{
    // Only registered TO BE materials participate; ordinary Inspectors remain untouched.
    internal static class ShaderChangeMarkers
    {
        sealed class Context
        {
            internal Dictionary<string, PropertyValue> Baseline;
            internal Action Changed;
        }
        static readonly Dictionary<Material, Context> contexts = new Dictionary<Material, Context>();
        static bool installed;
        static readonly PropertyInfo margin = typeof(EditorGUIUtility).GetProperty("leftMarginCoord", Integration.Flags);
        static readonly PropertyInfo clip = typeof(GUI).Assembly.GetType("UnityEngine.GUIClip")?.GetProperty("topmostRect", Integration.Flags);
        static GUIStyle buttonStyle;
        static GUIContent buttonContent;

        internal static void Register(Material material, Material baseline, Action changed)
        {
            Install();
            contexts[material] = new Context
            {
                Baseline = MaterialDelta.Values(baseline).ToDictionary(p => p.Name),
                Changed = changed
            };
        }
        internal static void Unregister(Material material)
        {
            if (!ReferenceEquals(material, null)) contexts.Remove(material);
        }
        static void Install()
        {
            if (installed) return;
            var harmonyType = Integration.Type("HarmonyLib.Harmony");
            var methodType = Integration.Type("HarmonyLib.HarmonyMethod");
            if (harmonyType == null || methodType == null || margin == null || clip == null)
                throw new InvalidOperationException("変更箇所の表示にはVRC SDKのHarmonyと対応するUnity Editorが必要です。");
            var harmony = Activator.CreateInstance(harmonyType, "com.gokoukotori.material-preview.change-markers");
            var postfix = Activator.CreateInstance(methodType, typeof(ShaderChangeMarkers).GetMethod(nameof(Draw), Integration.Flags));
            var patch = harmonyType.GetMethods().Single(m => m.Name == "Patch");
            var names = new HashSet<string>
            {
                "ShaderPropertyInternal", "DoPowerRangeProperty", "IntegerPropertyInternal",
                "FloatPropertyInternal", "ColorPropertyInternal", "VectorPropertyInternal",
                "TextureProperty", "TexturePropertyMiniThumbnail", "TextureScaleOffsetProperty"
            };
            try
            {
                var count = 0;
                foreach (var method in typeof(UnityEditor.MaterialEditor).GetMethods(Integration.Flags))
                {
                    if (!names.Contains(method.Name)) continue;
                    var args = method.GetParameters();
                    if (args.Length < 2 || (args[0].ParameterType != typeof(Rect) && args[0].ParameterType != typeof(Rect).MakeByRefType())
                        || (args[1].ParameterType != typeof(UnityEditor.MaterialProperty) && args[1].ParameterType != typeof(UnityEditor.MaterialProperty).MakeByRefType())) continue;
                    var parameters = new object[patch.GetParameters().Length];
                    parameters[0] = method; parameters[2] = postfix;
                    patch.Invoke(harmony, parameters); count++;
                }
                if (count == 0) throw new InvalidOperationException("変更箇所の表示に対応するMaterialEditor APIがありません。");
                installed = true;
                AssemblyReloadEvents.beforeAssemblyReload += () => harmonyType.GetMethod("UnpatchSelf").Invoke(harmony, null);
            }
            catch
            {
                harmonyType.GetMethod("UnpatchSelf").Invoke(harmony, null);
                throw;
            }
        }
        static bool Same(PropertyValue value, UnityEditor.MaterialProperty property)
        {
            switch (value.Type)
            {
                case ShaderPropertyType.Color: return property.type == UnityEditor.MaterialProperty.PropType.Color && Vector4.Distance(value.Color, property.colorValue) < .00001f;
                case ShaderPropertyType.Vector: return property.type == UnityEditor.MaterialProperty.PropType.Vector && Vector4.Distance(value.Vector, property.vectorValue) < .00001f;
                case ShaderPropertyType.Int: return value.Int == property.intValue;
                case ShaderPropertyType.Float:
                case ShaderPropertyType.Range: return Mathf.Abs(value.Float - property.floatValue) < .00001f;
                case ShaderPropertyType.Texture:
                    return value.Texture == property.textureValue && property.textureScaleAndOffset == new Vector4(value.Scale.x, value.Scale.y, value.Offset.x, value.Offset.y);
                default: return true;
            }
        }
        static void Draw(Rect __0, UnityEditor.MaterialProperty __1)
        {
            if (contexts.Count == 0 || Event.current == null || __1 == null) return;
            var targets = __1.targets;
            if (targets == null || targets.Length != 1 || !(targets[0] is Material material) || material == null || material.shader == null
                || !contexts.TryGetValue(material, out var context)
                || !context.Baseline.TryGetValue(__1.name, out var original)) return;
            var index = material.shader.FindPropertyIndex(__1.name);
            if (index < 0 || material.shader.GetPropertyType(index) != original.Type || Same(original, __1)) return;
            if (!(margin.GetValue(null) is float left) || !(clip.GetValue(null) is Rect clipping)) return;
            var rect = new Rect(left - Mathf.Max(0, clipping.xMin) + 1, __0.y + (__0.height - 16) / 2, 16, 16);
            if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && GUI.enabled && rect.Contains(Event.current.mousePosition))
            {
                Event.current.Use();
                EditorApplication.delayCall += () =>
                {
                    if (material == null || !contexts.TryGetValue(material, out var active) || active != context) return;
                    Undo.RecordObject(material, "マテリアルの変更を戻す");
                    original.Apply(material);
                    EditorUtility.SetDirty(material);
                    context.Changed();
                };
            }
            else if (Event.current.type == EventType.Repaint)
            {
                if (buttonStyle == null) buttonStyle = new GUIStyle(EditorStyles.miniButton) { padding = new RectOffset(0, 0, 0, 0) };
                if (buttonContent == null) buttonContent = new GUIContent(EditorGUIUtility.IconContent("d_Toolbar Minus").image, "この項目の変更をAS ISに戻す");
                buttonStyle.Draw(rect, buttonContent, false, false, false, false);
            }
        }
    }
}
