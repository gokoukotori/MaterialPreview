using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace GokouKotori.MaterialPreview
{
    internal static class Integration
    {
        internal const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        internal static Type Type(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(t => t != null);
        internal static Type Require(string name) => Type(name)
            ?? throw new InvalidOperationException("必要な連携コンポーネントまたは型が見つかりません: " + name);
        static FieldInfo Field(object obj, string name)
        {
            if (obj == null) throw new InvalidOperationException("連携設定が見つかりません。必要なフィールド: " + name);
            return obj.GetType().GetField(name, Flags)
                ?? throw new InvalidOperationException("必要な連携フィールドが見つかりません: " + obj.GetType().FullName + "." + name);
        }
        internal static object Get(object obj, string name) => Field(obj, name).GetValue(obj);
        internal static void Set(object obj, string name, object value) => Field(obj, name).SetValue(obj, value);
        internal static void RequireNdmfBuild()
        {
            var context = Type("nadena.dev.ndmf.BuildContext");
            CheckNdmfBuildApi(context, Type("nadena.dev.ndmf.BuildPhase"), Type("nadena.dev.ndmf.AvatarProcessor"));
        }
        internal static void CheckNdmfBuildApi(Type context, Type phase, Type processor)
        {
            const BindingFlags instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            const BindingFlags statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            var successful = context?.GetProperty("Successful", BindingFlags.Public | BindingFlags.Instance);
            var finish = context?.GetMethod("Finish", instance, null, System.Type.EmptyTypes, null);
            var first = phase?.GetField("FirstChance", BindingFlags.Public | BindingFlags.Static);
            var last = phase?.GetField("PlatformFinish", BindingFlags.Public | BindingFlags.Static);
            var process = context == null || phase == null ? null
                : processor?.GetMethod("ProcessAvatar", statics, null, new[] {context, phase, phase}, null);
            if (context?.GetConstructor(new[] {typeof(GameObject), typeof(string), typeof(bool)}) == null
                || successful?.PropertyType != typeof(bool) || successful.GetGetMethod() == null || successful.GetIndexParameters().Length != 0
                || finish?.ReturnType != typeof(void) || first == null || first.FieldType != phase || first.GetValue(null) == null
                || last == null || last.FieldType != phase || last.GetValue(null) == null || process?.ReturnType != typeof(void))
                throw new InvalidOperationException("NDMFの保存APIに互換性がありません。BuildContext / BuildPhase / ProcessAvatarを確認してください。");
        }
        internal static void CheckNdmfPreviewApi(Type preview)
        {
            if (preview?.GetConstructor(System.Type.EmptyTypes) == null || !typeof(IDisposable).IsAssignableFrom(preview)
                || preview.GetMethod("OverrideCamera", new[] {typeof(Camera)})?.ReturnType != typeof(void))
                throw new InvalidOperationException("NDMFのプレビューAPIに互換性がありません。PreviewSession / OverrideCameraを確認してください。");
        }
        internal static object Invoke(Type type, string name, params object[] args) => InvokeMethod(type, null, name, args);
        internal static object InvokeInstance(object obj, string name, params object[] args) => InvokeMethod(obj?.GetType(), obj, name, args);
        static object InvokeMethod(Type type, object instance, string name, object[] args)
        {
            if (type == null) throw new InvalidOperationException("必要な連携型が見つかりません。必要なメソッド: " + name);
            var flags = BindingFlags.Public | BindingFlags.NonPublic | (instance == null ? BindingFlags.Static : BindingFlags.Instance);
            var methods = type.GetMethods(flags).Where(m => m.Name == name && !m.ContainsGenericParameters
                && m.GetParameters().Length == args.Length
                && m.GetParameters().Select((p, i) => args[i] == null
                    ? !p.ParameterType.IsValueType || Nullable.GetUnderlyingType(p.ParameterType) != null
                    : p.ParameterType.IsInstanceOfType(args[i])).All(matches => matches)).ToArray();
            if (methods.Length == 0)
                throw new InvalidOperationException("必要な引数に対応する連携メソッドが見つかりません: " + type.FullName + "." + name);
            if (methods.Length > 1)
                throw new InvalidOperationException("連携メソッドを一意に特定できません: " + type.FullName + "." + name);
            return methods[0].Invoke(instance, args);
        }
        internal static string Message(Exception ex)
        {
            while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
            return ex.Message;
        }
    }

}
