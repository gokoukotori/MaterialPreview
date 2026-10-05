using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace GokouKotori.MaterialPreview
{
    [Serializable]
    internal sealed class PreviewLighting
    {
        internal static readonly string[] Presets = { "標準", "逆光", "横光", "暗所", "強い光", "暖色", "寒色", "カスタム" };
        public int Preset;
        public float Azimuth = -30, Elevation = 35, Intensity = 1, Ambient = .2f;
        public Color Color = Color.white;
        public bool Shadows = true, Floor, Occluder;

        internal void ApplyPreset(int index)
        {
            if (index < 0 || index >= Presets.Length) return;
            Preset = index;
            if (index == Presets.Length - 1) return;
            Azimuth = -30; Elevation = 35; Intensity = 1; Ambient = .2f; Color = Color.white;
            switch (index)
            {
                case 1: Azimuth = 180; Elevation = 20; Intensity = 1.5f; Ambient = .08f; break;
                case 2: Azimuth = 90; Elevation = 20; Ambient = .12f; break;
                case 3: Intensity = 0; Ambient = .01f; break;
                case 4: Intensity = 3; break;
                case 5: Color = new Color(1, .68f, .4f); Ambient = .15f; break;
                case 6: Color = new Color(.45f, .65f, 1); Ambient = .15f; break;
            }
        }

        // Direction toward the light, relative to a level camera: +Z is the viewer's side.
        internal Vector3 Direction
        {
            get
            {
                var azimuth = Azimuth * Mathf.Deg2Rad;
                var elevation = Elevation * Mathf.Deg2Rad;
                return new Vector3(Mathf.Sin(azimuth) * Mathf.Cos(elevation), Mathf.Sin(elevation),
                    Mathf.Cos(azimuth) * Mathf.Cos(elevation));
            }
        }
    }

    internal sealed class LightDirectionControl : VisualElement
    {
        readonly PreviewLighting lighting;
        readonly Action changed;
        Vector2 previous;

        internal LightDirectionControl(PreviewLighting lighting, Action changed)
        {
            this.lighting = lighting; this.changed = changed;
            name = "light-direction";
            tooltip = "ドラッグで光の方向を変更。黄丸は手前、白い輪は背後の光です。";
            generateVisualContent += Draw;
            RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0) return;
                previous = e.position; this.CapturePointer(e.pointerId); e.StopPropagation();
            });
            RegisterCallback<PointerMoveEvent>(e =>
            {
                if (!this.HasPointerCapture(e.pointerId)) return;
                var current = (Vector2)e.position;
                var delta = current - previous; previous = current;
                lighting.Azimuth = Mathf.Repeat(lighting.Azimuth + delta.x * 1.5f + 180, 360) - 180;
                lighting.Elevation = Mathf.Clamp(lighting.Elevation - delta.y, -80, 80);
                changed(); MarkDirtyRepaint(); e.StopPropagation();
            });
            RegisterCallback<PointerUpEvent>(e =>
            {
                if (e.button != 0) return;
                this.ReleasePointer(e.pointerId); e.StopPropagation();
            });
        }

        void Draw(MeshGenerationContext context)
        {
            var painter = context.painter2D;
            var center = contentRect.center;
            var radius = Mathf.Min(contentRect.width, contentRect.height) * .38f;
            painter.fillColor = new Color(.16f, .18f, .21f);
            painter.strokeColor = new Color(.5f, .53f, .58f); painter.lineWidth = 1;
            painter.BeginPath(); painter.Arc(center, radius, 0, 360); painter.ClosePath(); painter.Fill(); painter.Stroke();
            painter.BeginPath(); painter.MoveTo(center + Vector2.left * radius); painter.LineTo(center + Vector2.right * radius); painter.Stroke();
            painter.BeginPath(); painter.MoveTo(center + Vector2.up * radius); painter.LineTo(center + Vector2.down * radius); painter.Stroke();
            var direction = lighting.Direction;
            var point = center + new Vector2(direction.x, -direction.y) * radius;
            painter.strokeColor = direction.z >= 0 ? new Color(1, .8f, .3f) : Color.white;
            painter.fillColor = painter.strokeColor; painter.lineWidth = 2;
            painter.BeginPath(); painter.MoveTo(center); painter.LineTo(point); painter.Stroke();
            painter.BeginPath(); painter.Arc(point, 5, 0, 360); painter.ClosePath();
            if (direction.z >= 0) painter.Fill(); else painter.Stroke();
        }
    }
}
