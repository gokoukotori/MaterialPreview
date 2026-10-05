using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace GokouKotori.MaterialPreview
{
    internal sealed class VrRadialView : IDisposable
    {
        const float Inner = .085f, Outer = .215f;
        static readonly Color Background = new Color(.025f, .038f, .05f, .98f);
        static readonly Color Border = new Color(.12f, .38f, .43f, 1);
        static readonly Color Highlight = new Color(.06f, .58f, .65f, 1);
        readonly GameObject root, cursor;
        readonly Mesh mesh, cursorMesh;
        readonly Material material;
        readonly TextMesh center, hint;
        readonly TextMesh[] labels = new TextMesh[8];
        readonly List<Vector3> vertices = new List<Vector3>();
        readonly List<Color> colors = new List<Color>();
        readonly List<int> triangles = new List<int>();
        string signature;

        internal VrRadialView(VrComparisonScene scene, string name)
        {
            root = scene.NewObject(name);
            mesh = new Mesh { name = name + " sectors", hideFlags = HideFlags.HideAndDontSave };
            cursorMesh = new Mesh { name = name + " cursor", hideFlags = HideFlags.HideAndDontSave };
            material = new Material(Shader.Find("Hidden/MaterialPreview/VRPanel"))
                { hideFlags = HideFlags.HideAndDontSave, renderQueue = 2990 };
            Renderer(root, mesh);
            center = scene.Text("", root.transform, new Vector3(0, 0, -.004f), .007f);
            hint = scene.Text("", root.transform, new Vector3(0, -.252f, -.004f), .0045f);
            for (var i = 0; i < labels.Length; i++)
                labels[i] = scene.Text("", root.transform, Vector3.zero, .0048f);
            cursor = scene.NewObject("Selection cursor", root.transform);
            Renderer(cursor, cursorMesh);
            cursor.GetComponent<MeshRenderer>().sortingOrder = 1;
            Clear(); Disc(Vector2.zero, .008f, Highlight); Disc(Vector2.zero, .0055f, Color.white); Upload(cursorMesh);
            root.SetActive(false);
        }
        void Renderer(GameObject obj, Mesh geometry)
        {
            obj.AddComponent<MeshFilter>().sharedMesh = geometry;
            var renderer = obj.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
        }
        internal void Update(VrHandMenu menu, VrHandInput hand, Pose head, PreviewLighting lighting, float trackingScale = 1)
        {
            root.SetActive(menu.Visible && hand.Active);
            if (!root.activeSelf) return;
            root.transform.localScale = Vector3.one * trackingScale;
            var position = hand.Pose.position + Vector3.up * (.12f * trackingScale);
            var direction = position - head.position;
            if (direction.sqrMagnitude < .01f * trackingScale * trackingScale) direction = head.rotation * Vector3.forward;
            root.transform.SetPositionAndRotation(position, Quaternion.LookRotation(direction));
            var radial = menu.Page == VrMenuPage.Intensity || menu.Page == VrMenuPage.Ambient;
            var amount = menu.Page == VrMenuPage.Intensity ? lighting.Intensity / 5 : lighting.Ambient;
            var state = $"{menu.Page}:{menu.More}:{menu.Selected}:{menu.Ready}:{menu.FlickEnabled}:"
                + $"{lighting.Preset}:{lighting.Shadows}:{lighting.Floor}:{lighting.Occluder}";
            if (menu.IsPuppet) state += $":{amount:0.0000}:{lighting.Color}";
            if (state != signature)
            {
                signature = state;
                Clear();
                Disc(Vector2.zero, Outer + .003f, Border);
                if (menu.IsPuppet) BuildPuppet(radial, amount, lighting.Color);
                else BuildRing(menu, lighting);
                Upload(mesh);
                for (var i = 0; i < labels.Length; i++)
                {
                    labels[i].gameObject.SetActive(!menu.IsPuppet && i < menu.Labels.Length);
                    if (menu.IsPuppet || i >= menu.Labels.Length) continue;
                    var point = Direction(i * 360f / menu.Labels.Length) * .155f;
                    labels[i].transform.localPosition = new Vector3(point.x, point.y + (menu.More && i == 3 ? .008f : menu.More && i == 6 ? -.028f : -.012f), -.004f);
                    var enabled = menu.ItemState(i, lighting);
                    var label = menu.More && i == 3 ? "HMDから\n現実の目の高さ\nを取得\n（VR開始後）"
                        : menu.More && i == 4 ? "位置\nリセット" : menu.More && i == 6 ? "フリック\n選択" : menu.Labels[i];
                    labels[i].characterSize = menu.More && i == 3 ? .0032f : .0048f;
                    labels[i].text = label + (enabled.HasValue && menu.Page != VrMenuPage.Presets
                        ? enabled.Value ? "\n有効" : "\n無効" : "");
                    labels[i].color = i == menu.Selected ? Color.white : new Color(.85f, .92f, .95f);
                }
            }
            cursor.SetActive(menu.Ready);
            var cursorPosition = menu.Cursor * (menu.IsPuppet ? .17f : .19f);
            cursor.transform.localPosition = new Vector3(cursorPosition.x, cursorPosition.y, -.007f);
            switch (menu.Page)
            {
                case VrMenuPage.Direction:
                    center.text = $"左右・高さ\n左右 {lighting.Azimuth:0}°\n高さ {lighting.Elevation:0}°"; break;
                case VrMenuPage.Intensity:
                    center.text = $"光の強さ\n{lighting.Intensity / 5:0%}\n{lighting.Intensity:0.00} / 5"; break;
                case VrMenuPage.Ambient: center.text = $"環境の明るさ\n{lighting.Ambient:0%}"; break;
                case VrMenuPage.Color:
                    Color.RGBToHSV(lighting.Color, out var hue, out var saturation, out _);
                    center.text = $"光の色\n色相 {hue:0%}\n彩度 {saturation:0%}"; break;
                default:
                    var title = menu.More ? "オプション" : "照明";
                    center.text = title; break;
            }
            center.characterSize = menu.IsPuppet ? .006f : .0055f;
            center.transform.localPosition = new Vector3(0, menu.IsPuppet ? 0 : .032f, -.004f);
            if (!menu.Ready) hint.text = "スティックを中央へ戻し\nトリガーを離してください";
            else if (menu.IsPuppet) hint.text = radial ? "角度で調整\n中央＋トリガーで戻る"
                : menu.Page == VrMenuPage.Direction ? "左右：光の左右／上下：光の高さ\n中央＋トリガーで戻る"
                : "左右：色相／上下：彩度\n中央＋トリガーで戻る";
            else hint.text = menu.FlickEnabled ? "倒し切る／トリガーで選択\n中央＋トリガーで戻る"
                : "方向を選んでトリガーで決定\n中央＋トリガーで戻る";
        }
        void BuildRing(VrHandMenu menu, PreviewLighting lighting)
        {
            var count = menu.Labels.Length;
            var step = 360f / count;
            for (var i = 0; i < count; i++)
            {
                Arc(Inner, Outer, i * step - step / 2 + .7f, i * step + step / 2 - .7f,
                    menu.Ready && i == menu.Selected ? Highlight : Background);
                var point = Direction(i * step) * .155f + Vector2.up * .02f;
                if (!(menu.More && i == 3)) Icon(menu.Labels[i], point);
                if (menu.ItemState(i, lighting) == true)
                    Disc(point + new Vector2(.019f, .012f), .004f, new Color(.3f, 1, .72f));
            }
            Disc(Vector2.zero, Inner - .002f, Background);
        }
        void BuildPuppet(bool radial, float amount, Color color)
        {
            Disc(Vector2.zero, Outer, Background);
            if (radial)
            {
                Arc(.18f, .202f, 0, 360, Border);
                if (amount > 0) Arc(.18f, .202f, 0, Mathf.Clamp01(amount) * 360, Highlight);
                var point = Direction(amount * 360) * .191f;
                Disc(point, .009f, Color.white);
                Line(new Vector2(0, .176f), new Vector2(0, .21f), .002f, Color.white);
            }
            else
            {
                Line(new Vector2(-.17f, 0), new Vector2(.17f, 0), .001f, Border);
                Line(new Vector2(0, -.17f), new Vector2(0, .17f), .001f, Border);
                Arc(.166f, .168f, 0, 360, Border);
                if (color != Color.white) Arc(.183f, .198f, 0, 360, color);
            }
            Disc(Vector2.zero, .079f, Background);
        }
        void Icon(string label, Vector2 p)
        {
            var white = new Color(.9f, .97f, 1);
            if (label == "戻る")
            {
                Line(p + Vector2.right * .012f, p + Vector2.left * .012f, .002f, white);
                Line(p + Vector2.left * .012f, p + new Vector2(-.004f, .008f), .002f, white);
                Line(p + Vector2.left * .012f, p + new Vector2(-.004f, -.008f), .002f, white);
            }
            else if (label == "閉じる" || label == "VRを終了")
            {
                Line(p + new Vector2(-.008f, -.008f), p + new Vector2(.008f, .008f), .002f, white);
                Line(p + new Vector2(-.008f, .008f), p + new Vector2(.008f, -.008f), .002f, white);
            }
            else if (label == "光の色")
            {
                Disc(p + new Vector2(-.007f, -.004f), .007f, new Color(1, .4f, .4f));
                Disc(p + new Vector2(.007f, -.004f), .007f, new Color(.4f, 1, .6f));
                Disc(p + new Vector2(0, .007f), .007f, new Color(.4f, .7f, 1));
            }
            else if (label == "左右・高さ" || label == "位置リセット" || label == "フリック選択")
            {
                Line(p - Vector2.right * .012f, p + Vector2.right * .012f, .002f, white);
                Line(p - Vector2.up * .012f, p + Vector2.up * .012f, .002f, white);
                Disc(p, .004f, Highlight);
            }
            else if (label == "床" || label == "遮蔽物" || label == "HMDから現実の目の高さを取得（VR開始後）")
            {
                Line(p + new Vector2(-.01f, -.008f), p + new Vector2(.01f, -.008f), .002f, white);
                Line(p + new Vector2(-.01f, -.008f), p + new Vector2(-.01f, .008f), .002f, white);
                Line(p + new Vector2(.01f, -.008f), p + new Vector2(.01f, .008f), .002f, white);
                if (label != "床") Line(p + new Vector2(-.01f, .008f), p + new Vector2(.01f, .008f), .002f, white);
            }
            else
            {
                Disc(p, .009f, white); Disc(p, .006f, Border);
                if (label == "照明" || label == "光の強さ" || label == "オプション")
                    for (var i = 0; i < 8; i++)
                        Line(p + Direction(i * 45) * .011f, p + Direction(i * 45) * .015f, .0015f, white);
            }
        }
        static Vector2 Direction(float degrees)
        {
            var angle = degrees * Mathf.Deg2Rad;
            return new Vector2(Mathf.Sin(angle), Mathf.Cos(angle));
        }
        void Clear() { vertices.Clear(); colors.Clear(); triangles.Clear(); }
        void Vertex(Vector2 point, Color color) { vertices.Add(new Vector3(point.x, point.y, -.001f)); colors.Add(color); }
        void Triangle(int a, int b, int c) { triangles.Add(a); triangles.Add(b); triangles.Add(c); }
        void Disc(Vector2 point, float radius, Color color)
        {
            var start = vertices.Count; Vertex(point, color);
            const int steps = 64;
            for (var i = 0; i <= steps; i++) Vertex(point + Direction(i * 360f / steps) * radius, color);
            for (var i = 0; i < steps; i++) Triangle(start, start + i + 1, start + i + 2);
        }
        void Arc(float inner, float outer, float from, float to, Color color)
        {
            var steps = Mathf.Max(1, Mathf.CeilToInt((to - from) / 4));
            var start = vertices.Count;
            for (var i = 0; i <= steps; i++)
            {
                var direction = Direction(Mathf.Lerp(from, to, i / (float)steps));
                Vertex(direction * inner, color); Vertex(direction * outer, color);
                if (i == steps) continue;
                var index = start + i * 2;
                Triangle(index, index + 1, index + 2); Triangle(index + 1, index + 3, index + 2);
            }
        }
        void Line(Vector2 from, Vector2 to, float width, Color color)
        {
            var offset = new Vector2(-(to - from).y, (to - from).x).normalized * width;
            var start = vertices.Count;
            Vertex(from - offset, color); Vertex(from + offset, color); Vertex(to - offset, color); Vertex(to + offset, color);
            Triangle(start, start + 1, start + 2); Triangle(start + 1, start + 3, start + 2);
        }
        void Upload(Mesh target)
        {
            target.Clear(); target.SetVertices(vertices); target.SetColors(colors);
            target.SetTriangles(triangles, 0); target.RecalculateBounds();
        }
        public void Dispose()
        {
            if (root != null) Object.DestroyImmediate(root);
            if (mesh != null) Object.DestroyImmediate(mesh);
            if (cursorMesh != null) Object.DestroyImmediate(cursorMesh);
            if (material != null) Object.DestroyImmediate(material);
        }
    }
}
