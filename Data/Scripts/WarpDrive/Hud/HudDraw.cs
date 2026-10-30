using System;
using RichHudFramework;
using RichHudFramework.UI.Rendering;
using VRageMath;

namespace WarpDriveMod
{
    /// <summary>Flat-colour line art for custom HUD elements, in the element's HUD space (call from Draw() with HudSpace.PlaneToWorldRef).</summary>
    public static class HudDraw
    {
        public static void Quad(MatrixD[] space, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, Color color)
        {
            var quad = new FlatQuad(p0, p1, p2, p3);
            var mat = new BoundedQuadMaterial
            {
                textureID = Material.Default.TextureID,
                bbColor = color.GetBbColor(),
                texBounds = Material.Default.UVBounds
            };
            BillBoardUtils.AddQuad(ref quad, ref mat, space);
        }

        /// <summary>A rectangle centred on a point, long side along u.</summary>
        public static void Segment(MatrixD[] space, Vector2 center, Vector2 u, float halfLength, float halfWidth, Color color)
        {
            Vector2 t = new Vector2(u.Y, -u.X);
            Vector2 a = u * halfLength;
            Vector2 b = t * halfWidth;
            Quad(space, center - a - b, center - a + b, center + a + b, center + a - b, color);
        }

        public static void Line(MatrixD[] space, Vector2 from, Vector2 to, float width, Color color)
        {
            Vector2 d = to - from;
            float len = d.Length();
            if (len < 0.01f)
                return;

            Segment(space, (from + to) * 0.5f, d / len, len * 0.5f, width * 0.5f, color);
        }

        public static void Diamond(MatrixD[] space, Vector2 center, float size, Color color)
        {
            Quad(space, center + new Vector2(0f, size), center + new Vector2(size, 0f), center + new Vector2(0f, -size), center + new Vector2(-size, 0f), color);
        }

        public static void Circle(MatrixD[] space, Vector2 center, float radius, float width, int segments, Color color)
        {
            Vector2 prev = center + new Vector2(0f, radius);
            for (int i = 1; i <= segments; i++)
            {
                double a = i * 2.0 * Math.PI / segments;
                Vector2 next = center + new Vector2((float)Math.Sin(a), (float)Math.Cos(a)) * radius;
                Line(space, prev, next, width, color);
                prev = next;
            }
        }

        /// <summary>The first part of a circle, drawn clockwise from the top: fraction 1 is the whole circle.</summary>
        public static void CircleArc(MatrixD[] space, Vector2 center, float radius, float width, int segments, float fraction, Color color)
        {
            int count = (int)Math.Ceiling(MathHelper.Clamp(fraction, 0f, 1f) * segments);
            Vector2 prev = center + new Vector2(0f, radius);
            for (int i = 1; i <= count; i++)
            {
                double a = Math.Min(i, fraction * segments) * 2.0 * Math.PI / segments;
                Vector2 next = center + new Vector2((float)Math.Sin(a), (float)Math.Cos(a)) * radius;
                Line(space, prev, next, width, color);
                prev = next;
            }
        }

        /// <summary>Four L-shaped corners of a square box.</summary>
        public static void Brackets(MatrixD[] space, Vector2 center, float halfSize, float arm, float width, Color color)
        {
            for (int sx = -1; sx <= 1; sx += 2)
            {
                for (int sy = -1; sy <= 1; sy += 2)
                {
                    Vector2 corner = center + new Vector2(sx * halfSize, sy * halfSize);
                    Line(space, corner, corner - new Vector2(sx * arm, 0f), width, color);
                    Line(space, corner, corner - new Vector2(0f, sy * arm), width, color);
                }
            }
        }

        /// <summary>A "&gt;" chevron with its tip at the given point, pointing along dir.</summary>
        public static void Chevron(MatrixD[] space, Vector2 tip, Vector2 dir, float size, float width, Color color)
        {
            Vector2 perp = new Vector2(dir.Y, -dir.X);
            Vector2 back = tip - dir * size;
            Line(space, tip, back + perp * size * 0.9f, width, color);
            Line(space, tip, back - perp * size * 0.9f, width, color);
        }
    }
}
