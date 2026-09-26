using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace CarTuningSim.UI;

/// <summary>Simple XY line chart with a left and an optional right axis, grid and legend.</summary>
public partial class LineGraph : Control
{
    public sealed class Series
    {
        public string Name = "";
        public Color Color = Colors.White;
        public bool RightAxis;
        public bool Dashed;
        public float Width = 2f;
        public List<Vector2> Points = new();
    }

    public List<Series> SeriesList { get; } = new();
    public string XLabel = "rpm";
    public string LeftLabel = "";
    public string RightLabel = "";
    public float? XMin, XMax, LeftMin, LeftMax, RightMin, RightMax;
    public float? CursorX;

    private const int FontSize = 12;
    private const float PadLeft = 58, PadRight = 58, PadTop = 26, PadBottom = 34;

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(400, 220);
        Resized += QueueRedraw;
    }

    private static (float, float) Range(IEnumerable<float> values, float? min, float? max)
    {
        var list = values.ToList();
        float lo = min ?? (list.Count > 0 ? list.Min() : 0f);
        float hi = max ?? (list.Count > 0 ? list.Max() : 1f);
        if (min == null) lo = Math.Min(lo, 0f) < 0 ? lo : Math.Min(lo, 0f);
        if (hi - lo < 1e-6f) hi = lo + 1f;
        float span = hi - lo;
        if (max == null) hi += span * 0.08f;
        return (lo, hi);
    }

    private static float NiceStep(float span, int targetTicks)
    {
        float raw = span / targetTicks;
        float mag = MathF.Pow(10, MathF.Floor(MathF.Log10(raw)));
        float norm = raw / mag;
        float nice = norm < 1.5f ? 1 : norm < 3 ? 2 : norm < 7 ? 5 : 10;
        return nice * mag;
    }

    public override void _Draw()
    {
        var font = ThemeDB.FallbackFont;
        var size = Size;
        var plot = new Rect2(PadLeft, PadTop, size.X - PadLeft - PadRight, size.Y - PadTop - PadBottom);
        DrawRect(new Rect2(Vector2.Zero, size), new Color(0.11f, 0.12f, 0.14f));
        DrawRect(plot, new Color(0.15f, 0.16f, 0.19f));

        var all = SeriesList.SelectMany(s => s.Points).ToList();
        var (x0, x1) = Range(all.Select(p => p.X), XMin, XMax);
        if (XMin == null && all.Count > 0) x0 = all.Min(p => p.X);
        if (XMax == null && all.Count > 0) x1 = all.Max(p => p.X);
        if (x1 - x0 < 1e-6f) x1 = x0 + 1;
        var (l0, l1) = Range(SeriesList.Where(s => !s.RightAxis).SelectMany(s => s.Points).Select(p => p.Y), LeftMin, LeftMax);
        var (r0, r1) = Range(SeriesList.Where(s => s.RightAxis).SelectMany(s => s.Points).Select(p => p.Y), RightMin, RightMax);
        bool hasRight = SeriesList.Any(s => s.RightAxis);

        Vector2 Map(Vector2 p, bool right)
        {
            float tx = (p.X - x0) / (x1 - x0);
            float ty = right ? (p.Y - r0) / (r1 - r0) : (p.Y - l0) / (l1 - l0);
            return new Vector2(plot.Position.X + tx * plot.Size.X, plot.End.Y - ty * plot.Size.Y);
        }

        var gridColor = new Color(1, 1, 1, 0.07f);
        var textColor = new Color(0.8f, 0.82f, 0.86f);
        float xs = NiceStep(x1 - x0, 8);
        for (float x = MathF.Ceiling(x0 / xs) * xs; x <= x1 + 1e-3f; x += xs)
        {
            float px = Map(new Vector2(x, l0), false).X;
            DrawLine(new Vector2(px, plot.Position.Y), new Vector2(px, plot.End.Y), gridColor);
            DrawString(font, new Vector2(px - 20, plot.End.Y + 16), x.ToString("0"), HorizontalAlignment.Center, 40, FontSize, textColor);
        }
        float ls = NiceStep(l1 - l0, 6);
        for (float y = MathF.Ceiling(l0 / ls) * ls; y <= l1 + 1e-3f; y += ls)
        {
            float py = Map(new Vector2(x0, y), false).Y;
            DrawLine(new Vector2(plot.Position.X, py), new Vector2(plot.End.X, py), gridColor);
            DrawString(font, new Vector2(4, py + 4), y.ToString(ls < 1 ? "0.00" : "0"), HorizontalAlignment.Right, PadLeft - 8, FontSize, textColor);
        }
        if (hasRight)
        {
            float rs = NiceStep(r1 - r0, 6);
            for (float y = MathF.Ceiling(r0 / rs) * rs; y <= r1 + 1e-3f; y += rs)
            {
                float py = Map(new Vector2(x0, y), true).Y;
                DrawString(font, new Vector2(plot.End.X + 6, py + 4), y.ToString(rs < 1 ? "0.00" : "0"), HorizontalAlignment.Left, PadRight - 8, FontSize, textColor);
            }
        }
        DrawString(font, new Vector2(plot.Position.X, size.Y - 4), XLabel, HorizontalAlignment.Center, plot.Size.X, FontSize, Ui.Muted);
        DrawString(font, new Vector2(4, 14), LeftLabel, HorizontalAlignment.Left, 300, FontSize, Ui.Muted);
        if (hasRight) DrawString(font, new Vector2(size.X - 304, 14), RightLabel, HorizontalAlignment.Right, 300, FontSize, Ui.Muted);

        foreach (var s in SeriesList)
        {
            if (s.Points.Count < 2) continue;
            var pts = s.Points.Select(p => Map(p, s.RightAxis)).ToArray();
            if (!s.Dashed) DrawPolyline(pts, s.Color, s.Width, true);
            else
                for (int i = 1; i < pts.Length; i += 2) DrawLine(pts[i - 1], pts[i], s.Color, s.Width, true);
        }
        if (CursorX is float cx && cx >= x0 && cx <= x1)
        {
            float px = Map(new Vector2(cx, l0), false).X;
            DrawLine(new Vector2(px, plot.Position.Y), new Vector2(px, plot.End.Y), new Color(1, 1, 1, 0.35f), 1f);
        }

        // Legend.
        float lx = plot.Position.X + 8, ly = plot.Position.Y + 6;
        foreach (var s in SeriesList.Where(s => s.Name.Length > 0))
        {
            DrawRect(new Rect2(lx, ly + 4, 14, 3), s.Color);
            DrawString(font, new Vector2(lx + 18, ly + 10), s.Name, HorizontalAlignment.Left, 260, FontSize, textColor);
            ly += 16;
        }
    }
}
