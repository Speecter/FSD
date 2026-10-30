using System;
using RichHudFramework.UI;
using RichHudFramework.UI.Rendering;
using Sandbox.ModAPI;
using VRageMath;

namespace WarpDriveMod
{
    /// <summary>
    /// Marks the selected GPS jump target in the world: lock brackets around it when it is on screen,
    /// otherwise an arrow at the screen edge pointing toward it. Shows the name and distance.
    /// </summary>
    public class GpsLockMarker : HudElementBase
    {
        private const float BoxHalf = 20f;
        private const float EdgeMarginX = 70f;
        private const float EdgeMarginY = 90f;

        private readonly Label label;
        private bool onScreen;
        private Vector2 edgeDir;
        private Color color;
        private string shownText;

        public GpsLockMarker(HudParentBase parent) : base(parent)
        {
            Size = new Vector2(BoxHalf * 2f);
            label = new Label(this)
            {
                AutoResize = false,
                Size = new Vector2(260f, 18f),
                Offset = new Vector2(0f, -BoxHalf - 14f),
                BuilderMode = TextBuilderModes.Unlined,
                VertCenterText = true
            };
            Visible = false;
        }

        /// <param name="halfView">Half the visible area in this element's HUD units.</param>
        public void Update(FsdHudModel m, Vector2 halfView, double distance)
        {
            var camera = MyAPIGateway.Session?.Camera;
            if (!m.HasGpsLock || camera == null)
            {
                Visible = false;
                return;
            }

            Vector3D toTarget = m.GpsCoords - camera.Position;
            double length = toTarget.Length();
            if (length < 1.0)
            {
                Visible = false;
                return;
            }

            // Direction in camera space (x right, y up, -z forward), projected with the camera's own lens
            Vector3D v = Vector3D.TransformNormal(toTarget / length, camera.ViewMatrix);
            MatrixD proj = camera.ProjectionMatrix;

            Vector2 pos = Vector2.Zero;
            onScreen = false;
            if (v.Z < -0.01)
            {
                float x = (float)(proj.M11 * v.X / -v.Z);
                float y = (float)(proj.M22 * v.Y / -v.Z);
                pos = new Vector2(x * halfView.X, y * halfView.Y);
                onScreen = Math.Abs(pos.X) < halfView.X - EdgeMarginX && Math.Abs(pos.Y) < halfView.Y - EdgeMarginY;
            }

            if (!onScreen)
            {
                // Behind or beside the camera: pin it to the screen edge in the direction of the target
                edgeDir = new Vector2((float)v.X, (float)v.Y);
                if (edgeDir.LengthSquared() < 1e-8f)
                    edgeDir = new Vector2(0f, -1f);
                edgeDir.Normalize();

                float sx = (halfView.X - EdgeMarginX) / Math.Max(Math.Abs(edgeDir.X), 1e-4f);
                float sy = (halfView.Y - EdgeMarginY) / Math.Max(Math.Abs(edgeDir.Y), 1e-4f);
                pos = edgeDir * Math.Min(sx, sy);
            }

            color = HudPalette.Fade(m.Phase == HudPhase.Countdown ? HudPalette.Ready : HudPalette.Primary, 0.9f);
            Offset = pos;
            Visible = true;

            // Keep the label inside the screen when pinned to the bottom edge
            label.Offset = new Vector2(0f, !onScreen && pos.Y < 0f ? BoxHalf + 14f : -BoxHalf - 14f);

            string text = m.GpsName + "  " + HudFormat.Distance(distance);
            if (text != shownText)
            {
                shownText = text;
                label.TextBoard.SetText(text, HudPalette.Number(HudPalette.Primary, 0.72f, TextAlignment.Center));
            }
        }

        protected override void Draw()
        {
            MatrixD[] space = HudSpace.PlaneToWorldRef;
            Vector2 c = Position;

            if (onScreen)
            {
                HudDraw.Brackets(space, c, BoxHalf, 8f, 2f, color);
                HudDraw.Diamond(space, c, 2.5f, color);
            }
            else
            {
                HudDraw.Chevron(space, c + edgeDir * 10f, edgeDir, 12f, 3f, color);
                HudDraw.Chevron(space, c, edgeDir, 12f, 3f, color);
            }
        }
    }
}
