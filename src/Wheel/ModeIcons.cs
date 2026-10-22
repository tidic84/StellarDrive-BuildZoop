using System;
using System.Collections.Generic;
using UnityEngine;

namespace BuildZoop.Wheel
{
    /// <summary>
    /// The mode icons and the round shapes of the wheel, drawn into textures once (white, so GUI.color tints them).
    /// Shapes are rasterised with 4x4 supersampling for smooth edges.
    /// </summary>
    public static class ModeIcons
    {
        private const int Size = 96;
        private static readonly Dictionary<ZoopMode, Texture2D> Icons = new Dictionary<ZoopMode, Texture2D>();
        private static Texture2D _disc;
        private static Texture2D _ring;
        private static Texture2D _glow;

        /// <summary>Draws every texture now (they take a moment), so the wheel opens without a hitch.</summary>
        public static void Warm()
        {
            foreach (var mode in Settings.ModeOrder)
                Icon(mode);
            _ = Disc;
            _ = Ring;
            _ = Glow;
        }

        public static Texture2D Icon(ZoopMode mode)
        {
            if (!Icons.TryGetValue(mode, out var tex) || tex == null)
                Icons[mode] = tex = Draw(mode);
            return tex;
        }

        /// <summary>Filled circle.</summary>
        public static Texture2D Disc => _disc != null ? _disc : _disc = Raster(128, (x, y) => x * x + y * y <= 1f ? 1f : 0f);

        /// <summary>Thin circle outline.</summary>
        public static Texture2D Ring => _ring != null ? _ring : _ring = Raster(128, (x, y) =>
        {
            float r = Mathf.Sqrt(x * x + y * y);
            return r <= 1f && r >= 0.9f ? 1f : 0f;
        });

        /// <summary>Soft round glow fading to the edge.</summary>
        public static Texture2D Glow => _glow != null ? _glow : _glow = Raster(128, (x, y) =>
        {
            float r = Mathf.Sqrt(x * x + y * y);
            return r >= 1f ? 0f : Mathf.Pow(1f - r, 1.6f);
        }, supersample: false);

        // ---------------------------------------------------------------- icons

        private static Texture2D Draw(ZoopMode mode)
        {
            var shapes = new List<Func<float, float, float>>();
            switch (mode)
            {
                case ZoopMode.Line:
                    for (int i = 0; i < 4; i++) shapes.Add(Cell(-0.66f + i * 0.44f, 0f, 0.17f));
                    break;
                case ZoopMode.Vertical:
                    for (int i = 0; i < 4; i++) shapes.Add(Cell(0f, -0.66f + i * 0.44f, 0.17f));
                    break;
                case ZoopMode.Diagonal:
                    for (int i = 0; i < 4; i++) shapes.Add(Cell(-0.6f + i * 0.4f, 0.6f - i * 0.4f, 0.16f));
                    break;
                case ZoopMode.Area:
                    for (int i = -1; i <= 1; i++)
                        for (int j = -1; j <= 1; j++)
                            shapes.Add(Cell(i * 0.5f, j * 0.5f, 0.2f));
                    break;
                case ZoopMode.DiagonalArea:
                    foreach (var (i, j) in DiamondCells())
                        shapes.Add(Cell(i * 0.28f, j * 0.28f, 0.11f));
                    break;
                case ZoopMode.DiagonalAreaEdges:
                    // The same diamond of cells, its stepped edges filled into straight lines (fainter).
                    shapes.Add((x, y) => Mathf.Abs(x) + Mathf.Abs(y) <= 0.92f ? 0.45f : 0f);
                    foreach (var (i, j) in DiamondCells())
                        shapes.Add(Cell(i * 0.28f, j * 0.28f, 0.11f));
                    break;
                case ZoopMode.Pyramid:
                    // Three rows of small triangles, pointing up (solid) and down (fainter), forming one big triangle.
                    const float h = 0.5f, w = 0.29f, top = -0.78f;
                    for (int row = 0; row < 3; row++)
                    {
                        float y0 = top + row * h, y1 = y0 + h;
                        for (int k = 0; k <= row; k++)
                        {
                            float cx = (k - row / 2f) * 2f * w;
                            shapes.Add(Triangle(cx, y0 + 0.05f, cx - w + 0.05f, y1 - 0.03f, cx + w - 0.05f, y1 - 0.03f, 1f));
                            if (k < row)
                                shapes.Add(Triangle(cx + w, y1 - 0.06f, cx + 0.06f, y0 + 0.03f, cx + 2 * w - 0.06f, y0 + 0.03f, 0.55f));
                        }
                    }
                    break;
            }
            return Raster(Size, (x, y) =>
            {
                float a = 0f;
                foreach (var shape in shapes)
                    a = Mathf.Max(a, shape(x, y));
                return a;
            });
        }

        private static IEnumerable<(int, int)> DiamondCells()
        {
            for (int i = -2; i <= 2; i++)
                for (int j = -2; j <= 2; j++)
                    if (Mathf.Abs(i) + Mathf.Abs(j) <= 2)
                        yield return (i, j);
        }

        /// <summary>Rounded square centred on (cx, cy) with half size r.</summary>
        private static Func<float, float, float> Cell(float cx, float cy, float r)
        {
            float round = r * 0.35f;
            return (x, y) =>
            {
                float dx = Mathf.Max(Mathf.Abs(x - cx) - (r - round), 0f);
                float dy = Mathf.Max(Mathf.Abs(y - cy) - (r - round), 0f);
                return dx * dx + dy * dy <= round * round ? 1f : 0f;
            };
        }

        private static Func<float, float, float> Triangle(float ax, float ay, float bx, float by, float cx, float cy, float alpha)
        {
            return (x, y) =>
            {
                float d1 = (x - bx) * (ay - by) - (ax - bx) * (y - by);
                float d2 = (x - cx) * (by - cy) - (bx - cx) * (y - cy);
                float d3 = (x - ax) * (cy - ay) - (cx - ax) * (y - ay);
                bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
                return neg && pos ? 0f : alpha;
            };
        }

        /// <summary>White texture whose alpha is shape(x, y), x and y from -1 to 1 (y down).</summary>
        private static Texture2D Raster(int size, Func<float, float, float> shape, bool supersample = true)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var pixels = new Color32[size * size];
            int n = supersample ? 4 : 1;
            for (int py = 0; py < size; py++)
            {
                for (int px = 0; px < size; px++)
                {
                    float sum = 0f;
                    for (int sy = 0; sy < n; sy++)
                        for (int sx = 0; sx < n; sx++)
                        {
                            float x = ((px + (sx + 0.5f) / n) / size) * 2f - 1f;
                            float y = ((py + (sy + 0.5f) / n) / size) * 2f - 1f;
                            sum += shape(x, y);
                        }
                    // Texture rows go bottom-up; shapes are drawn top-down.
                    pixels[(size - 1 - py) * size + px] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(Mathf.Clamp01(sum / (n * n)) * 255f));
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            return tex;
        }
    }
}
