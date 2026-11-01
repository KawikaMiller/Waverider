using SkiaSharp;
using Waverider.Audio;
using Waverider.Theming;
using Waverider.Visualization;

namespace Waverider.Disc;

/// <summary>
/// Draws the spinning disc in three layers so only the part that should rotate does:
/// <see cref="DrawShadow"/> (static, underneath), <see cref="DrawBody"/> (rotated by the view) and
/// <see cref="DrawSheen"/> (static light reflections on top, so the highlight stays put while the
/// disc turns underneath it, like real light on a real record). All three share one geometry.
/// </summary>
internal static class DiscRenderer
{
    // Proportions as fractions of the disc radius.
    private const float VinylLabel = 0.36f;   // album-art label (a real 12" LP is ~0.33)
    private const float VinylHole = 0.03f;
    private const float CdHole = 0.125f;
    private const float CdHubOuter = 0.30f;   // clear plastic hub
    private const float CdMirrorOuter = 0.36f; // reflective mirror band; printed area starts here
    private const float CdRim = 0.975f;

    /// <summary>
    /// Disc center and radius for a canvas. Normally leaves a margin so the drop shadow fits; when
    /// <paramref name="flush"/> the disc fills the canvas edge to edge (the mini player clips its
    /// window to the disc, so there is nothing to put a shadow on).
    /// </summary>
    private static (float Cx, float Cy, float R) Geometry(SKImageInfo info, bool flush)
        => (info.Width / 2f, info.Height / 2f, Math.Min(info.Width, info.Height) * (flush ? 0.5f : 0.455f));

    /// <summary>The spindle hole's radius as a fraction of the disc radius (for cutting it out of a window).</summary>
    public static float HoleFraction(DiscSkin skin) => skin == DiscSkin.Cd ? CdHole : VinylHole;

    public static void DrawShadow(SKCanvas c, SKImageInfo info, DiscSkin skin)
    {
        c.Clear(SKColors.Transparent);
        var (cx, cy, r) = Geometry(info, flush: false);
        using (var paint = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(0, 0, 0, 120),
            ImageFilter = SKImageFilter.CreateBlur(r * 0.035f, r * 0.035f),
        })
        {
            c.DrawCircle(cx, cy + r * 0.03f, r * 0.99f, paint);
        }

        // Keep the spindle hole see-through all the way down.
        using var clear = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.Clear };
        c.DrawCircle(cx, cy, HoleRadius(skin, r) + 1f, clear);
    }

    public static void DrawBody(SKCanvas c, SKImageInfo info, DiscSkin skin, DiscFace face, SKImage? art, SKColor accent, SKColor accent2, bool flush)
    {
        c.Clear(SKColors.Transparent);
        var (cx, cy, r) = Geometry(info, flush);
        // On vinyl the art always stays on the label; on a CD the visualizer takes the whole printed face.
        if (skin == DiscSkin.Cd) DrawCd(c, cx, cy, r, art, accent, accent2, visualizerFace: face == DiscFace.Visualizer);
        else DrawVinyl(c, cx, cy, r, art, accent, accent2);
    }

    /// <summary>
    /// The ring of the disc the visualizer plays in, as fractions of the radius: the grooves on vinyl (the
    /// label stays clear), the printed face on a CD (hub and mirror band stay clear).
    /// </summary>
    private static (float Inner, float Outer) VisualizerRing(DiscSkin skin)
        => skin == DiscSkin.Cd ? (CdMirrorOuter, CdRim) : (VinylLabel * 1.05f, 0.985f);

    /// <summary>Draws the visualizer into the disc's ring. Vinyl blends it over the grooves; a CD shows it opaque.</summary>
    public static void DrawVisualizer(SKCanvas c, SKImageInfo info, DiscSkin skin, bool flush, IVisualizer visualizer, AudioFrame frame, Theme theme, double timeSeconds)
    {
        c.Clear(SKColors.Transparent);
        var (cx, cy, r) = Geometry(info, flush);
        var (inner, outer) = VisualizerRing(skin);

        using var ring = new SKPath { FillType = SKPathFillType.EvenOdd };
        ring.AddCircle(cx, cy, r * outer);
        ring.AddCircle(cx, cy, r * inner);
        c.Save();
        c.ClipPath(ring, antialias: true);

        // The visualizer gets a square the size of the disc, as it would a canvas of its own.
        int side = Math.Max(2, (int)Math.Round(r * 2));
        c.Translate(cx - r, cy - r);
        var square = new SKImageInfo(side, side);
        if (skin == DiscSkin.Vinyl)
        {
            // Screen keeps the grooves showing wherever the visualizer is dark.
            using var screen = new SKPaint { BlendMode = SKBlendMode.Screen };
            c.SaveLayer(screen);
            visualizer.Render(c, square, frame, theme, timeSeconds);
            c.Restore();
        }
        else
        {
            visualizer.Render(c, square, frame, theme, timeSeconds);
        }
        c.Restore();
    }

    /// <summary>A near-black base for the CD face when the visualizer is drawn over it.</summary>
    private static void DrawDarkFace(SKCanvas c, float cx, float cy, float radius)
    {
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateRadialGradient(
                new SKPoint(cx, cy), radius,
                new[] { new SKColor(0x16, 0x18, 0x1D), new SKColor(0x08, 0x09, 0x0C) },
                new[] { 0f, 1f }, SKShaderTileMode.Clamp),
        };
        c.DrawCircle(cx, cy, radius, paint);
    }

    public static void DrawSheen(SKCanvas c, SKImageInfo info, DiscSkin skin, bool flush)
    {
        c.Clear(SKColors.Transparent);
        var (cx, cy, r) = Geometry(info, flush);

        // Reflections only land on the playing surface, not the label / hub / hole.
        float inner = skin == DiscSkin.Cd ? CdMirrorOuter : VinylLabel;
        float outer = skin == DiscSkin.Cd ? CdRim : 0.99f;
        using var ring = new SKPath { FillType = SKPathFillType.EvenOdd };
        ring.AddCircle(cx, cy, r * outer);
        ring.AddCircle(cx, cy, r * inner);
        c.Save();
        c.ClipPath(ring, antialias: true);

        if (skin == DiscSkin.Cd)
        {
            // Two opposite spectral wedges, like a CD catching a window.
            DrawSweep(c, cx, cy, r, SpectralWedges(0.13f, 0.095f, 120), SKBlendMode.Screen, rotateDeg: 0);
            DrawSweep(c, cx, cy, r, SpectralWedges(0.63f, 0.095f, 120), SKBlendMode.Screen, rotateDeg: 0);
            // Plus a faint white glint inside each.
            DrawSweep(c, cx, cy, r, WhiteWedges((0.13f, 0.05f, 60), (0.63f, 0.05f, 60)), SKBlendMode.SoftLight, rotateDeg: 0);
        }
        else
        {
            // Soft anisotropic highlights: two broad wedges and two narrower, dimmer ones.
            DrawSweep(c, cx, cy, r,
                WhiteWedges((0.13f, 0.075f, 82), (0.63f, 0.075f, 82), (0.38f, 0.04f, 30), (0.88f, 0.04f, 30)),
                SKBlendMode.Screen, rotateDeg: 0);
        }
        c.Restore();
    }

    // ---- vinyl ---------------------------------------------------------------------------------

    private static void DrawVinyl(SKCanvas c, float cx, float cy, float r, SKImage? art, SKColor accent, SKColor accent2)
    {
        // Black base with a faint lift toward the middle so it isn't a flat disc.
        using (var basePaint = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateRadialGradient(
                new SKPoint(cx, cy), r,
                new[] { new SKColor(0x24, 0x24, 0x28), new SKColor(0x0C, 0x0C, 0x0E) },
                new[] { 0f, 1f }, SKShaderTileMode.Clamp),
        })
        {
            c.DrawCircle(cx, cy, r, basePaint);
        }

        // Grooves: concentric hairlines with deterministic per-ring brightness so they shimmer
        // rather than look machine-ruled, and a few smooth bands where tracks are separated.
        float grooveStart = r * (VinylLabel + 0.045f);
        float grooveEnd = r * 0.965f;
        float step = Math.Max(1.6f, r * 0.0105f);
        int n = (int)((grooveEnd - grooveStart) / step);
        using (var groove = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(0.6f, step * 0.42f) })
        {
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / n;
                if (InTrackGap(t)) continue;
                int h = (int)(((uint)i * 2654435761u) >> 24); // 0..255, stable per ring
                byte a = (byte)(10 + (h % 26));
                groove.Color = new SKColor(255, 255, 255, a);
                c.DrawCircle(cx, cy, grooveStart + i * step, groove);
            }
        }

        // A few faint scratches / dust arcs, fixed to the disc, so rotation is visible even
        // when the label art is nearly symmetrical.
        using (var scratch = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(0.7f, r * 0.004f), StrokeCap = SKStrokeCap.Round })
        {
            Scratch(c, cx, cy, r * 0.58f, 20, 38, scratch, 46);
            Scratch(c, cx, cy, r * 0.74f, 205, 26, scratch, 38);
            Scratch(c, cx, cy, r * 0.87f, 118, 18, scratch, 34);
        }

        // Smooth rim and a lead-in band just inside it.
        using (var rim = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1f, r * 0.008f), Color = new SKColor(255, 255, 255, 38) })
            c.DrawCircle(cx, cy, r * 0.995f, rim);

        DrawVinylLabel(c, cx, cy, r, art, accent, accent2);

        // Spindle hole.
        float hole = r * VinylHole;
        using var clear = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.Clear };
        c.DrawCircle(cx, cy, hole, clear);
        using var holeRing = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1f, r * 0.006f), Color = new SKColor(200, 205, 212, 150) };
        c.DrawCircle(cx, cy, hole, holeRing);
    }

    private static bool InTrackGap(float t)
        => (t > 0.22f && t < 0.235f) || (t > 0.47f && t < 0.485f) || (t > 0.71f && t < 0.725f) || t > 0.965f;

    private static void Scratch(SKCanvas c, float cx, float cy, float radius, float startDeg, float sweepDeg, SKPaint paint, byte alpha)
    {
        paint.Color = new SKColor(255, 255, 255, alpha);
        var rect = new SKRect(cx - radius, cy - radius, cx + radius, cy + radius);
        using var path = new SKPath();
        path.AddArc(rect, startDeg, sweepDeg);
        c.DrawPath(path, paint);
    }

    private static void DrawVinylLabel(SKCanvas c, float cx, float cy, float r, SKImage? art, SKColor accent, SKColor accent2)
    {
        float lr = r * VinylLabel;

        // Dark seat ring between the grooves and the label.
        using (var seat = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 200) })
            c.DrawCircle(cx, cy, lr * 1.035f, seat);

        c.Save();
        using (var clip = new SKPath())
        {
            clip.AddCircle(cx, cy, lr);
            c.ClipPath(clip, antialias: true);
        }

        if (art is not null) DrawArtCover(c, art, cx, cy, lr);
        else DrawFallbackFace(c, cx, cy, lr, accent, accent2);

        // Label vignette so the art sits in the disc rather than on top of it.
        using (var vignette = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateRadialGradient(
                new SKPoint(cx, cy), lr,
                new[] { new SKColor(0, 0, 0, 0), new SKColor(0, 0, 0, 0), new SKColor(0, 0, 0, 105) },
                new[] { 0f, 0.7f, 1f }, SKShaderTileMode.Clamp),
        })
        {
            c.DrawCircle(cx, cy, lr, vignette);
        }
        c.Restore();

        using var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1f, r * 0.006f), Color = new SKColor(0, 0, 0, 150) };
        c.DrawCircle(cx, cy, lr, edge);
    }

    // ---- CD ------------------------------------------------------------------------------------

    private static void DrawCd(SKCanvas c, float cx, float cy, float r, SKImage? art, SKColor accent, SKColor accent2, bool visualizerFace)
    {
        // Silver substrate; also what shows at the very edge of the printed area.
        using (var substrate = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateRadialGradient(
                new SKPoint(cx, cy), r,
                new[] { new SKColor(0xE9, 0xEC, 0xF1), new SKColor(0xB6, 0xBC, 0xC6) },
                new[] { 0f, 1f }, SKShaderTileMode.Clamp),
        })
        {
            c.DrawCircle(cx, cy, r, substrate);
        }

        // Printed face: the album art across the whole disc, visible between mirror band and rim.
        c.Save();
        using (var face = new SKPath { FillType = SKPathFillType.EvenOdd })
        {
            face.AddCircle(cx, cy, r * CdRim);
            face.AddCircle(cx, cy, r * CdMirrorOuter);
            c.ClipPath(face, antialias: true);
        }
        float faceR = r * CdRim;
        if (visualizerFace) DrawDarkFace(c, cx, cy, faceR);
        else if (art is not null) DrawArtCover(c, art, cx, cy, faceR);
        else DrawFallbackFace(c, cx, cy, faceR, accent, accent2);

        // A subtle concentric sheen on the data area, as on real discs.
        using (var rings = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(0.6f, r * 0.004f) })
        {
            for (float f = CdMirrorOuter + 0.04f; f < CdRim - 0.02f; f += 0.045f)
            {
                rings.Color = new SKColor(255, 255, 255, 14);
                c.DrawCircle(cx, cy, r * f, rings);
            }
        }
        c.Restore();

        // Mirror band: brushed-metal ring around the hub.
        float bandMid = r * (CdHubOuter + CdMirrorOuter) / 2f;
        float bandW = r * (CdMirrorOuter - CdHubOuter);
        c.Save();
        c.RotateDegrees(-20, cx, cy);
        using (var band = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = bandW,
            Shader = SKShader.CreateSweepGradient(
                new SKPoint(cx, cy),
                new[]
                {
                    new SKColor(0xF2, 0xF4, 0xF7), new SKColor(0x8E, 0x95, 0x9F), new SKColor(0xE6, 0xE9, 0xEE),
                    new SKColor(0x9A, 0xA1, 0xAB), new SKColor(0xF2, 0xF4, 0xF7),
                },
                new[] { 0f, 0.25f, 0.5f, 0.75f, 1f }),
        })
        {
            c.DrawCircle(cx, cy, bandMid, band);
        }
        c.Restore();

        // Clear plastic hub: cut it out, then lay a faint frosted tint back so it reads as glass.
        using (var clear = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.Clear })
            c.DrawCircle(cx, cy, r * CdHubOuter, clear);
        using (var frost = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 46) })
            c.DrawCircle(cx, cy, r * CdHubOuter, frost);
        using (var hubEdge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1f, r * 0.006f), Color = new SKColor(255, 255, 255, 120) })
        {
            c.DrawCircle(cx, cy, r * CdHubOuter, hubEdge);
            c.DrawCircle(cx, cy, r * 0.215f, hubEdge); // stacking ring
        }

        // Spindle hole.
        float hole = r * CdHole;
        using (var clear = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.Clear })
            c.DrawCircle(cx, cy, hole, clear);
        using (var holeEdge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1f, r * 0.007f), Color = new SKColor(255, 255, 255, 170) })
            c.DrawCircle(cx, cy, hole, holeEdge);

        // Bright outer edge.
        using var rim = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1f, r * 0.012f), Color = new SKColor(255, 255, 255, 120) };
        c.DrawCircle(cx, cy, r * 0.994f, rim);
    }

    // ---- shared helpers --------------------------------------------------------------------

    private static float HoleRadius(DiscSkin skin, float r) => r * (skin == DiscSkin.Cd ? CdHole : VinylHole);

    /// <summary>Draws the album art center-cropped to fill a circle of radius <paramref name="radius"/>.</summary>
    private static void DrawArtCover(SKCanvas c, SKImage art, float cx, float cy, float radius)
    {
        float side = Math.Min(art.Width, art.Height);
        var src = new SKRect((art.Width - side) / 2f, (art.Height - side) / 2f, (art.Width + side) / 2f, (art.Height + side) / 2f);
        var dst = new SKRect(cx - radius, cy - radius, cx + radius, cy + radius);
        using var paint = new SKPaint { IsAntialias = true };
        c.DrawImage(art, src, dst, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
    }

    /// <summary>Stand-in artwork (no art yet / failed to load): a theme-colored diagonal gradient.</summary>
    private static void DrawFallbackFace(SKCanvas c, float cx, float cy, float radius, SKColor a, SKColor b)
    {
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(cx - radius, cy - radius), new SKPoint(cx + radius, cy + radius),
                new[] { a, b }, new[] { 0f, 1f }, SKShaderTileMode.Clamp),
        };
        c.DrawCircle(cx, cy, radius, paint);
    }

    private static void DrawSweep(SKCanvas c, float cx, float cy, float r, (SKColor[] Colors, float[] Pos) g, SKBlendMode mode, float rotateDeg)
    {
        c.Save();
        if (rotateDeg != 0) c.RotateDegrees(rotateDeg, cx, cy);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            BlendMode = mode,
            Shader = SKShader.CreateSweepGradient(new SKPoint(cx, cy), g.Colors, g.Pos),
        };
        c.DrawCircle(cx, cy, r, paint);
        c.Restore();
    }

    /// <summary>Sweep-gradient stops for soft white wedges: (center, half-width, peak alpha), all in 0..1 of a turn.</summary>
    private static (SKColor[] Colors, float[] Pos) WhiteWedges(params (float Center, float Half, byte Alpha)[] wedges)
    {
        var sorted = wedges.OrderBy(w => w.Center).ToArray();
        var colors = new List<SKColor> { new(255, 255, 255, 0) };
        var pos = new List<float> { 0f };
        foreach (var (center, half, alpha) in sorted)
        {
            colors.Add(new SKColor(255, 255, 255, 0)); pos.Add(Math.Max(pos[^1], center - half));
            colors.Add(new SKColor(255, 255, 255, alpha)); pos.Add(center);
            colors.Add(new SKColor(255, 255, 255, 0)); pos.Add(Math.Min(1f, center + half));
        }
        colors.Add(new SKColor(255, 255, 255, 0)); pos.Add(1f);
        return (colors.ToArray(), pos.ToArray());
    }

    /// <summary>One spectrum-colored wedge (violet to red) centered at <paramref name="center"/> of a turn.</summary>
    private static (SKColor[] Colors, float[] Pos) SpectralWedges(float center, float half, byte alpha)
    {
        SKColor[] spectrum =
        {
            new(140, 80, 255, 0),
            new(140, 80, 255, alpha),
            new(60, 120, 255, alpha),
            new(40, 220, 230, alpha),
            new(80, 240, 120, alpha),
            new(250, 240, 80, alpha),
            new(255, 150, 60, alpha),
            new(255, 70, 90, alpha),
            new(255, 70, 90, 0),
        };
        var colors = new List<SKColor> { new(0, 0, 0, 0) };
        var pos = new List<float> { 0f };
        float start = Math.Max(0.0001f, center - half);
        float span = half * 2f;
        for (int i = 0; i < spectrum.Length; i++)
        {
            colors.Add(spectrum[i]);
            pos.Add(Math.Min(0.9999f, start + span * i / (spectrum.Length - 1)));
        }
        colors.Add(new SKColor(0, 0, 0, 0));
        pos.Add(1f);
        return (colors.ToArray(), pos.ToArray());
    }
}
