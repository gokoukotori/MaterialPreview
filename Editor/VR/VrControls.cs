using UnityEngine;

namespace GokouKotori.MaterialPreview
{
    internal struct VrHandInput
    {
        internal bool Active, Menu, Select, Grip, PreferPick;
        internal Vector2 Stick;
        internal Pose Pose;
    }

    internal sealed class VrHoldToggle
    {
        bool released, latched;
        float held;
        internal bool Visible { get; private set; }
        internal bool Update(bool active, bool pressed, float dt)
        {
            if (!active) { Reset(); return false; }
            if (!pressed) { released = true; latched = false; held = 0; return false; }
            if (!released || latched) return false;
            held += dt;
            if (held < .5f) return false;
            latched = true; Visible = !Visible; return true;
        }
        internal void Close() { Visible = false; }
        internal void Reset() { released = false; latched = false; held = 0; Visible = false; }
    }

    internal enum VrMenuPage { Root, Presets, Direction, Intensity, Ambient, Color }

    internal sealed class VrHandMenu
    {
        static readonly string[] rootItems = { "閉じる", "照明", "左右・高さ", "光の強さ", "環境の明るさ", "光の色", "影", "オプション" };
        static readonly string[] presetItems = { "戻る", PreviewLighting.Presets[0], PreviewLighting.Presets[1], PreviewLighting.Presets[2],
            PreviewLighting.Presets[3], PreviewLighting.Presets[4], PreviewLighting.Presets[5], PreviewLighting.Presets[6] };
        static readonly string[] optionItems = { "戻る", "床", "遮蔽物", "HMDから現実の目の高さを取得（VR開始後）", "位置リセット", "VRを終了", "フリック選択" };
        internal readonly VrHoldToggle Toggle = new VrHoldToggle();
        internal VrMenuPage Page;
        internal int Selected = -1;
        internal bool Ready { get; private set; }
        bool selectReleased, modeChosen, radialTracking;
        float radialAngle, radialValue;
        internal bool Recenter, Exit, MeasureHeight, More;
        internal bool FlickEnabled { get; private set; } = true;
        internal Vector2 Cursor { get; private set; }
        internal bool Visible => Toggle.Visible;
        internal bool IsPuppet => Page != VrMenuPage.Root && Page != VrMenuPage.Presets;
        internal string[] Labels => Page == VrMenuPage.Presets ? presetItems : More ? optionItems : rootItems;

        internal bool? ItemState(int index, PreviewLighting lighting)
        {
            if (Page == VrMenuPage.Presets) return index > 0 ? lighting.Preset == index - 1 : (bool?)null;
            if (More)
            {
                if (index == 1) return lighting.Floor;
                if (index == 2) return lighting.Occluder;
                if (index == 6) return FlickEnabled;
            }
            else if (index == 6) return lighting.Shadows;
            return null;
        }

        void WaitForNeutral()
        {
            Ready = false; Selected = -1; Cursor = Vector2.zero; radialTracking = false;
        }
        void Back()
        {
            if (IsPuppet || Page == VrMenuPage.Presets) Page = VrMenuPage.Root;
            else if (More) More = false;
            else Toggle.Close();
            WaitForNeutral();
        }
        internal bool Update(VrHandInput hand, float dt, PreviewLighting lighting, bool canEdit)
        {
            Recenter = Exit = MeasureHeight = false;
            if (!hand.Active)
            {
                Toggle.Reset(); Page = VrMenuPage.Root; More = false; selectReleased = false; WaitForNeutral();
                return false;
            }
            if (!modeChosen) FlickEnabled = !hand.PreferPick;
            // Hiding with the menu button preserves the current folder and puppet.
            if (Toggle.Update(true, hand.Menu, dt)) { WaitForNeutral(); selectReleased = false; }
            if (!Visible) { WaitForNeutral(); return false; }
            if (!Ready)
            {
                if (!hand.Menu && !hand.Select && hand.Stick.magnitude < .2f)
                { Ready = true; selectReleased = true; }
                return false;
            }
            Cursor = Vector2.ClampMagnitude(hand.Stick, 1);
            if (!hand.Select) selectReleased = true;
            var click = hand.Select && selectReleased;
            if (click) selectReleased = false;
            if (IsPuppet)
            {
                // A trigger away from the center must not accidentally leave a puppet.
                if (click && Cursor.magnitude < .2f) { Back(); return false; }
                if (Cursor.magnitude < .2f) { radialTracking = false; return false; }
                if (!canEdit) { radialTracking = false; return false; }
                var before = new Vector4(lighting.Azimuth, lighting.Elevation, lighting.Intensity, lighting.Ambient);
                var beforeColor = lighting.Color;
                switch (Page)
                {
                    case VrMenuPage.Direction:
                        lighting.Azimuth = Cursor.x * 180; lighting.Elevation = Cursor.y * 80; break;
                    case VrMenuPage.Intensity: lighting.Intensity = Radial(Cursor, lighting.Intensity / 5) * 5; break;
                    case VrMenuPage.Ambient: lighting.Ambient = Radial(Cursor, lighting.Ambient); break;
                    case VrMenuPage.Color:
                        Color.RGBToHSV(lighting.Color, out _, out _, out var value);
                        lighting.Color = Color.HSVToRGB((Cursor.x + 1) * .5f, (Cursor.y + 1) * .5f, Mathf.Max(value, .1f)); break;
                }
                var changed = before != new Vector4(lighting.Azimuth, lighting.Elevation, lighting.Intensity, lighting.Ambient)
                    || beforeColor != lighting.Color;
                if (changed) lighting.Preset = PreviewLighting.Presets.Length - 1;
                return changed;
            }
            Selected = Sector(Cursor, Labels.Length);
            if (click && Selected < 0) { Back(); return false; }
            if (Selected < 0 || !(click || FlickEnabled && Cursor.magnitude >= .85f)) return false;
            var selected = Selected;
            WaitForNeutral();
            if (selected == 0) { Back(); return false; }
            if (Page == VrMenuPage.Presets)
            {
                if (!canEdit) return false;
                lighting.ApplyPreset(selected - 1); return true;
            }
            if (More)
            {
                switch (selected)
                {
                    case 1: if (canEdit) { lighting.Floor = !lighting.Floor; return true; } break;
                    case 2: if (canEdit) { lighting.Occluder = !lighting.Occluder; return true; } break;
                    case 3: MeasureHeight = true; break;
                    case 4: Recenter = true; break;
                    case 5: Exit = true; break;
                    case 6: FlickEnabled = !FlickEnabled; modeChosen = true; break;
                }
                return false;
            }
            switch (selected)
            {
                case 1: Page = VrMenuPage.Presets; break;
                case 2: Page = VrMenuPage.Direction; break;
                case 3: Page = VrMenuPage.Intensity; break;
                case 4: Page = VrMenuPage.Ambient; break;
                case 5: Page = VrMenuPage.Color; break;
                case 6: if (canEdit) { lighting.Shadows = !lighting.Shadows; return true; } break;
                case 7: More = true; break;
            }
            return false;
        }
        float Radial(Vector2 axis, float current)
        {
            var angle = Mathf.Repeat(Mathf.Atan2(axis.x, axis.y) / (Mathf.PI * 2), 1);
            // Keep the top seam at its endpoint instead of jumping between 0 and 100%.
            if (!radialTracking) radialValue = angle < .001f && current > .5f ? 1 : angle;
            else radialValue = Mathf.Clamp01(radialValue + Mathf.DeltaAngle(radialAngle * 360, angle * 360) / 360);
            radialTracking = true; radialAngle = angle;
            return radialValue;
        }
        internal static int Sector(Vector2 stick, int count) => count <= 0 || stick.magnitude < .35f ? -1
            : Mathf.FloorToInt(Mathf.Repeat(Mathf.Atan2(stick.x, stick.y) * Mathf.Rad2Deg + 180f / count, 360) / (360f / count));
    }

    internal sealed class VrNavigation
    {
        internal Vector3 Position;
        internal Quaternion Rotation = Quaternion.identity;
        internal float TrackingScale = 1;
        bool moveReady, turnReady;
        internal Pose World(Pose pose) => new Pose(Position + Rotation * (pose.position * TrackingScale), Rotation * pose.rotation);
        internal void SetScale(float scale, Pose head)
        {
            var shift = Rotation * (head.position * (TrackingScale - scale));
            shift.y = 0;
            Position += shift;
            TrackingScale = scale;
        }
        internal void Recenter(Pose head, float distance)
        {
            Rotation = Quaternion.Euler(0, 180 - head.rotation.eulerAngles.y, 0);
            var offset = Rotation * (head.position * TrackingScale);
            Position = new Vector3(-offset.x, 0, distance - offset.z);
            moveReady = turnReady = false;
        }
        internal void Update(Pose head, VrHandInput left, VrHandInput right, bool leftMenu, bool rightMenu, float dt)
        {
            dt = Mathf.Min(dt, .05f);
            if (!left.Active || leftMenu) moveReady = false;
            else if (left.Stick.magnitude < .2f && !left.Grip) moveReady = true;
            if (!right.Active || rightMenu) turnReady = false;
            else if (right.Stick.magnitude < .2f && !right.Grip) turnReady = true;
            if (moveReady && !leftMenu && left.Active)
            {
                var facing = Quaternion.Euler(0, World(head).rotation.eulerAngles.y, 0);
                var axis = left.Stick.magnitude < .2f ? Vector2.zero : Vector2.ClampMagnitude(left.Stick, 1);
                Position += facing * new Vector3(axis.x, 0, axis.y) * dt * (left.Grip ? 2.5f : 1.2f);
            }
            if (turnReady && !rightMenu && right.Active)
            {
                if (right.Grip) Position += Vector3.up * (Mathf.Abs(right.Stick.y) < .2f ? 0 : right.Stick.y * dt);
                else if (Mathf.Abs(right.Stick.x) >= .3f)
                {
                    var pivot = World(head).position;
                    Rotation = Quaternion.Euler(0, right.Stick.x * dt * 90, 0) * Rotation;
                    Position = pivot - Rotation * (head.position * TrackingScale);
                }
            }
        }
    }
}
