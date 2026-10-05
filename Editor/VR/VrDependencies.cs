using System;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace GokouKotori.MaterialPreview
{
    // No AssetDatabase import or PackageManager operation is needed for this native SDK.
    internal static class VrDependencies
    {
        internal const string Version = "1.16.8";
        internal const string Commit = "4c85abcb7f7f1f02adaf3812018c99fc593bc341";
        internal const string Sha256 = "c3314d8822729fb7550d070f75c3efdd2d099965e53416b2901996fc6cee744f";
        internal const string DownloadUrl = "https://raw.githubusercontent.com/ValveSoftware/openvr/" + Commit + "/bin/win64/openvr_api.dll";
        internal const string Package = "Packages/com.gokoukotori.material-preview";
        internal static string Root => Path.GetFullPath("Library/MaterialPreview/VR");
        internal static string DllPath => Path.Combine(Root, "OpenVR-" + Version, "material_preview_openvr_api.dll");
        internal static string ActionManifestPath => Path.Combine(
            UnityEditor.PackageManager.PackageInfo.FindForAssetPath(Package)?.resolvedPath ?? Path.GetFullPath(Package),
            "Editor/VR/Input/actions.json");
        static string PartialPath => DllPath + ".download";
        static UnityWebRequest request;
        static bool? installed;
        internal static string Message { get; private set; }
        internal static bool Installing => request != null;
        internal static float Progress => request?.downloadProgress ?? 0;
        internal static bool Installed => installed ?? (installed = VerifyFile(DllPath)).Value;
        internal static bool Supported => Application.platform == RuntimePlatform.WindowsEditor && Environment.Is64BitProcess;

        [Serializable] sealed class Settings { public bool enabled; }
        internal static bool ReadEnabled()
        {
            try
            {
                var path = Path.Combine(Root, "settings.json");
                return File.Exists(path) && JsonUtility.FromJson<Settings>(File.ReadAllText(path)).enabled;
            }
            catch (Exception ex) { Message = "VR設定を読み込めません: " + ex.Message; return false; }
        }
        internal static void WriteEnabled(bool enabled)
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "settings.json"), JsonUtility.ToJson(new Settings { enabled = enabled }, true));
        }
        internal static bool VerifyBytes(byte[] bytes)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").Equals(Sha256, StringComparison.OrdinalIgnoreCase);
        }
        internal static bool VerifyFile(string path)
        {
            try { return File.Exists(path) && VerifyBytes(File.ReadAllBytes(path)); }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        internal static void Install(string url = DownloadUrl)
        {
            if (Installing) return;
            if (!Supported) { Message = "VRプレビューはWindows x64 Editor専用です。"; return; }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DllPath));
                request = UnityWebRequest.Get(url);
                request.downloadHandler = new DownloadHandlerFile(PartialPath) { removeFileOnAbort = true };
                request.timeout = 120;
                request.SendWebRequest();
                Message = "OpenVR " + Version + "をダウンロードしています…";
                EditorApplication.update += Poll;
                AssemblyReloadEvents.beforeAssemblyReload += Cancel;
                EditorApplication.quitting += Cancel;
            }
            catch (Exception ex) { Finish("導入失敗: " + ex.Message); }
        }
        static void Poll()
        {
            if (request == null || !request.isDone) return;
            try
            {
                if (request.result != UnityWebRequest.Result.Success) throw new IOException(request.error);
                request.Dispose(); request = null;
                if (!VerifyFile(PartialPath)) throw new IOException("DLLのSHA-256が対応版と一致しません。");
                // Replace only after verification; a failed download leaves the existing installation intact.
                if (File.Exists(DllPath)) File.Replace(PartialPath, DllPath, null);
                else File.Move(PartialPath, DllPath);
                installed = true;
                Finish("OpenVR " + Version + " 導入済み（Library内）");
            }
            catch (Exception ex) { Finish("導入失敗: " + ex.Message); }
        }
        internal static void Cancel() => Finish("依存導入を中止しました。");
        static void Finish(string message)
        {
            EditorApplication.update -= Poll;
            AssemblyReloadEvents.beforeAssemblyReload -= Cancel;
            EditorApplication.quitting -= Cancel;
            request?.Abort(); request?.Dispose(); request = null;
            try { if (File.Exists(PartialPath)) File.Delete(PartialPath); }
            catch (IOException) { /* Retry overwrites the incomplete file. */ }
            catch (UnauthorizedAccessException) { }
            installed = null; Message = message;
        }
    }
}
