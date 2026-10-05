using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace GokouKotori.MaterialPreview
{
    [Serializable]
    internal sealed class PropertyValue
    {
        public string Name;
        public ShaderPropertyType Type;
        public Color Color;
        public Vector4 Vector;
        public float Float;
        public int Int;
        public Texture Texture;
        public Vector2 Scale, Offset;

        public static PropertyValue Read(Material material, int index)
        {
            var value = new PropertyValue {Name = material.shader.GetPropertyName(index), Type = material.shader.GetPropertyType(index)};
            switch (value.Type)
            {
                case ShaderPropertyType.Color: value.Color = material.GetColor(value.Name); break;
                case ShaderPropertyType.Vector: value.Vector = material.GetVector(value.Name); break;
                case ShaderPropertyType.Int: value.Int = material.GetInteger(value.Name); break;
                case ShaderPropertyType.Float:
                case ShaderPropertyType.Range: value.Float = material.GetFloat(value.Name); break;
                case ShaderPropertyType.Texture:
                    value.Texture = material.GetTexture(value.Name);
                    value.Scale = material.GetTextureScale(value.Name);
                    value.Offset = material.GetTextureOffset(value.Name);
                    break;
            }
            return value;
        }

        public bool Same(PropertyValue other)
        {
            if (other == null || Name != other.Name || Type != other.Type) return false;
            switch (Type)
            {
                case ShaderPropertyType.Color: return Vector4.Distance(Color, other.Color) < .00001f;
                case ShaderPropertyType.Vector: return Vector4.Distance(Vector, other.Vector) < .00001f;
                case ShaderPropertyType.Int: return Int == other.Int;
                case ShaderPropertyType.Float:
                case ShaderPropertyType.Range: return Mathf.Abs(Float - other.Float) < .00001f;
                case ShaderPropertyType.Texture: return Texture == other.Texture && Scale == other.Scale && Offset == other.Offset;
                default: return false;
            }
        }

        public void Apply(Material material)
        {
            switch (Type)
            {
                case ShaderPropertyType.Color: material.SetColor(Name, Color); break;
                case ShaderPropertyType.Vector: material.SetVector(Name, Vector); break;
                case ShaderPropertyType.Int: material.SetInteger(Name, Int); break;
                case ShaderPropertyType.Float:
                case ShaderPropertyType.Range: material.SetFloat(Name, Float); break;
                case ShaderPropertyType.Texture:
                    material.SetTexture(Name, Texture); material.SetTextureScale(Name, Scale); material.SetTextureOffset(Name, Offset); break;
            }
        }
    }

    internal sealed class MaterialDelta
    {
        public Shader Shader;
        public bool ShaderChanged, QueueChanged;
        public int Queue;
        public readonly List<PropertyValue> Properties = new List<PropertyValue>();
        public readonly List<string> Unsupported = new List<string>();
        Material previewSource;
        public bool Changed => ShaderChanged || QueueChanged || Properties.Count != 0 || Unsupported.Count != 0;

        internal static int RawQueue(Material material) => new SerializedObject(material).FindProperty("m_CustomRenderQueue").intValue;
        internal static List<PropertyValue> Values(Material material)
        {
            var result = new List<PropertyValue>();
            if (material == null || material.shader == null) return result;
            var names = new HashSet<string>();
            for (int i = 0; i < material.shader.GetPropertyCount(); i++)
                if (names.Add(material.shader.GetPropertyName(i))) result.Add(PropertyValue.Read(material, i));
            return result;
        }
        public static MaterialDelta Between(Material before, Material after)
        {
            var delta = new MaterialDelta {Shader = after.shader, ShaderChanged = before.shader != after.shader,
                Queue = RawQueue(after), QueueChanged = RawQueue(before) != RawQueue(after), previewSource = after};
            var old = Values(before).ToDictionary(p => p.Name);
            foreach (var property in Values(after))
                if (!old.TryGetValue(property.Name, out var previous) || !property.Same(previous)) delta.Properties.Add(property);
            if (!new HashSet<string>(before.shaderKeywords).SetEquals(after.shaderKeywords)) delta.Unsupported.Add("Shader Keyword");
            if (before.enableInstancing != after.enableInstancing) delta.Unsupported.Add("GPU Instancing");
            if (before.doubleSidedGI != after.doubleSidedGI || before.globalIlluminationFlags != after.globalIlluminationFlags) delta.Unsupported.Add("GI設定");
            var beforeTags = Tags(before); var afterTags = Tags(after);
            if (beforeTags.Count != afterTags.Count || beforeTags.Any(p => !afterTags.TryGetValue(p.Key, out var value) || value != p.Value)) delta.Unsupported.Add("stringTagMap");
            if (!new HashSet<string>(DisabledPasses(before)).SetEquals(DisabledPasses(after))) delta.Unsupported.Add("disabledShaderPasses");
            return delta;
        }
        static Dictionary<string, string> Tags(Material material)
        {
            var result = new Dictionary<string, string>();
            var property = new SerializedObject(material).FindProperty("stringTagMap");
            for (int i = 0; property != null && i < property.arraySize; i++)
            {
                var pair = property.GetArrayElementAtIndex(i);
                var value = pair.FindPropertyRelative("second").stringValue;
                if (!string.IsNullOrEmpty(value)) result[pair.FindPropertyRelative("first").stringValue] = value;
            }
            return result;
        }
        static string[] DisabledPasses(Material material)
        {
            var property = new SerializedObject(material).FindProperty("disabledShaderPasses");
            return property == null ? Array.Empty<string>() : Enumerable.Range(0, property.arraySize).Select(i => property.GetArrayElementAtIndex(i).stringValue).ToArray();
        }
        public void Apply(Material material)
        {
            var queue = RawQueue(material);
            if (ShaderChanged) { material.shader = Shader; material.renderQueue = queue; }
            if (QueueChanged) material.renderQueue = Queue;
            foreach (var value in Properties) value.Apply(material);
        }
        internal void ApplyPreview(Material material)
        {
            Apply(material);
            ApplyUnsupportedPreview(material);
        }
        internal void ApplyUnsupportedPreview(Material material)
        {
            if (Unsupported.Contains("Shader Keyword")) material.shaderKeywords = previewSource.shaderKeywords;
            if (Unsupported.Contains("GPU Instancing")) material.enableInstancing = previewSource.enableInstancing;
            if (Unsupported.Contains("GI設定"))
            {
                material.doubleSidedGI = previewSource.doubleSidedGI;
                material.globalIlluminationFlags = previewSource.globalIlluminationFlags;
            }
            if (Unsupported.Contains("stringTagMap"))
            {
                var tags = Tags(previewSource);
                foreach (var key in Tags(material).Keys.Except(tags.Keys)) material.SetOverrideTag(key, "");
                foreach (var pair in tags) material.SetOverrideTag(pair.Key, pair.Value);
            }
            if (Unsupported.Contains("disabledShaderPasses"))
            {
                var disabled = new HashSet<string>(DisabledPasses(previewSource));
                foreach (var pass in DisabledPasses(material).Union(disabled)) material.SetShaderPassEnabled(pass, !disabled.Contains(pass));
            }
        }
        public IEnumerable<string> Names()
        {
            if (ShaderChanged) yield return "Shader";
            if (QueueChanged) yield return "Render Queue";
            foreach (var value in Properties) yield return value.Name;
            foreach (var value in Unsupported) yield return value + "（保存非対応）";
        }
    }
}
