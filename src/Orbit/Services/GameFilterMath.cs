using System.Globalization;
using Orbit.Models;

namespace Orbit.Services;

/// <summary>
/// Turns a game filter profile into one color matrix. Every setting is a linear color operation
/// (out = A · in + b), so they all combine into a single matrix that Windows applies per pixel.
/// </summary>
public static class GameFilterMath
{
    // Rec. 709 luma weights.
    private const double Lr = 0.2126, Lg = 0.7152, Lb = 0.0722;

    /// <summary>
    /// The profile as a 5×5 matrix in the layout Windows' magnification API wants
    /// (row = input channel R, G, B, A, 1; column = output channel).
    /// </summary>
    public static float[] Build(GameFilterProfile p)
    {
        var a = Identity();
        var b = new double[3];

        // 1. Exposure: ±1 stop.
        Apply(ref a, ref b, Scale(Math.Pow(2, p.Exposure / 100.0)), null);

        // 2. Contrast around middle gray.
        var c = 1 + p.Contrast / 100.0 * 0.6;
        Apply(ref a, ref b, Scale(c), Fill(0.5 * (1 - c)));

        // 3. Brightness.
        Apply(ref a, ref b, Identity(), Fill(p.Brightness / 400.0));

        // 4. Saturation (0 = gray, 2 = double).
        var s = Math.Max(0, 1 + p.Saturation / 100.0);
        Apply(ref a, ref b, Saturation(s), null);

        // 5. Temperature: warmer = more red, less blue.
        var t = p.Temperature / 100.0;
        Apply(ref a, ref b, Diagonal(1 + 0.12 * t, 1 + 0.02 * t, 1 - 0.14 * t), null);

        // 6. Tint: blend towards the brightness of the pixel in the tint color.
        if (p.TintStrength > 0 && TryColor(p.TintColor, out var tr, out var tg, out var tb))
        {
            var luma = tr * Lr + tg * Lg + tb * Lb;
            if (luma > 0.01)
            {
                var k = p.TintStrength / 100.0;
                var tint = new[] { tr / luma, tg / luma, tb / luma };
                var m = new double[3, 3];
                for (var i = 0; i < 3; i++)
                {
                    m[i, 0] = (i == 0 ? 1 - k : 0) + k * tint[i] * Lr;
                    m[i, 1] = (i == 1 ? 1 - k : 0) + k * tint[i] * Lg;
                    m[i, 2] = (i == 2 ? 1 - k : 0) + k * tint[i] * Lb;
                }
                Apply(ref a, ref b, m, null);
            }
        }

        // 7. Look (black & white, sepia, night, color blindness), blended by its strength.
        if (LookMatrix(p.Look) is { } look)
            Apply(ref a, ref b, Blend(Identity(), look, Math.Clamp(p.LookStrength / 100.0, 0, 1)), null);

        return ToMagnifier(a, b);
    }

    public static bool IsIdentity(float[] m)
    {
        var id = ToMagnifier(Identity(), new double[3]);
        for (var i = 0; i < 25; i++)
            if (Math.Abs(m[i] - id[i]) > 0.0005f) return false;
        return true;
    }

    public static float[] IdentityMatrix() => ToMagnifier(Identity(), new double[3]);

    // ---------------- Looks ----------------

    private static double[,]? LookMatrix(string look) => look switch
    {
        "bw" => Saturation(0),
        "sepia" => new[,]
        {
            { 0.393, 0.769, 0.189 },
            { 0.349, 0.686, 0.168 },
            { 0.272, 0.534, 0.131 },
        },
        // Less blue and a little less green – easier on the eyes at night.
        "night" => Diagonal(1.0, 0.80, 0.52),
        "protan" => Daltonize(new[,] { { 0, 2.02344, -2.52581 }, { 0, 1, 0 }, { 0, 0, 1 } }),
        "deutan" => Daltonize(new[,] { { 1, 0, 0 }, { 0.494207, 0, 1.24827 }, { 0, 0, 1 } }),
        "tritan" => Daltonize(new[,] { { 1, 0, 0 }, { 0, 1, 0 }, { -0.395913, 0.801109, 0 } }),
        _ => null,
    };

    /// <summary>
    /// Color blindness correction ("daltonize"): works out which colors a person with this type can't tell apart
    /// and shifts that difference into colors they can see. All steps are linear, so the result is one matrix.
    /// </summary>
    private static double[,] Daltonize(double[,] simulateLms)
    {
        double[,] rgbToLms =
        {
            { 17.8824, 43.5161, 4.11935 },
            { 3.45565, 27.1554, 3.86714 },
            { 0.0299566, 0.184309, 1.46709 },
        };
        var lmsToRgb = Invert(rgbToLms);
        var simulate = Multiply(lmsToRgb, Multiply(simulateLms, rgbToLms));
        var error = Subtract(Identity(), simulate);
        double[,] shift = { { 0, 0, 0 }, { 0.7, 1, 0 }, { 0.7, 0, 1 } };
        return Add(Identity(), Multiply(shift, error));
    }

    // ---------------- Small matrix helpers (3×3, column vectors) ----------------

    /// <summary>Puts step (m, v) after what's already in (a, b): x → m·(a·x + b) + v.</summary>
    private static void Apply(ref double[,] a, ref double[] b, double[,] m, double[]? v)
    {
        var nb = new double[3];
        for (var i = 0; i < 3; i++)
        {
            nb[i] = (v?[i] ?? 0);
            for (var k = 0; k < 3; k++) nb[i] += m[i, k] * b[k];
        }
        a = Multiply(m, a);
        b = nb;
    }

    private static double[,] Identity() => Diagonal(1, 1, 1);
    private static double[,] Scale(double k) => Diagonal(k, k, k);
    private static double[] Fill(double v) => new[] { v, v, v };
    private static double[,] Diagonal(double r, double g, double b) => new[,] { { r, 0, 0 }, { 0, g, 0 }, { 0, 0, b } };

    private static double[,] Saturation(double s)
    {
        double[] l = { Lr, Lg, Lb };
        var m = new double[3, 3];
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
                m[i, j] = (1 - s) * l[j] + (i == j ? s : 0);
        return m;
    }

    private static double[,] Blend(double[,] x, double[,] y, double k)
    {
        var m = new double[3, 3];
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
                m[i, j] = x[i, j] * (1 - k) + y[i, j] * k;
        return m;
    }

    private static double[,] Multiply(double[,] x, double[,] y)
    {
        var m = new double[3, 3];
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
                for (var k = 0; k < 3; k++)
                    m[i, j] += x[i, k] * y[k, j];
        return m;
    }

    private static double[,] Add(double[,] x, double[,] y)
    {
        var m = new double[3, 3];
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
                m[i, j] = x[i, j] + y[i, j];
        return m;
    }

    private static double[,] Subtract(double[,] x, double[,] y)
    {
        var m = new double[3, 3];
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
                m[i, j] = x[i, j] - y[i, j];
        return m;
    }

    private static double[,] Invert(double[,] m)
    {
        var det = m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1])
                - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
                + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);
        var r = new double[3, 3];
        r[0, 0] = (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) / det;
        r[0, 1] = (m[0, 2] * m[2, 1] - m[0, 1] * m[2, 2]) / det;
        r[0, 2] = (m[0, 1] * m[1, 2] - m[0, 2] * m[1, 1]) / det;
        r[1, 0] = (m[1, 2] * m[2, 0] - m[1, 0] * m[2, 2]) / det;
        r[1, 1] = (m[0, 0] * m[2, 2] - m[0, 2] * m[2, 0]) / det;
        r[1, 2] = (m[0, 2] * m[1, 0] - m[0, 0] * m[1, 2]) / det;
        r[2, 0] = (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]) / det;
        r[2, 1] = (m[0, 1] * m[2, 0] - m[0, 0] * m[2, 1]) / det;
        r[2, 2] = (m[0, 0] * m[1, 1] - m[0, 1] * m[1, 0]) / det;
        return r;
    }

    /// <summary>Column-vector form (out = a·in + b) → the magnifier's row layout.</summary>
    private static float[] ToMagnifier(double[,] a, double[] b)
    {
        var m = new float[25];
        for (var input = 0; input < 3; input++)
            for (var output = 0; output < 3; output++)
                m[input * 5 + output] = (float)a[output, input];
        m[3 * 5 + 3] = 1; // alpha stays
        for (var output = 0; output < 3; output++) m[4 * 5 + output] = (float)b[output];
        m[4 * 5 + 4] = 1;
        return m;
    }

    public static bool TryColor(string hex, out double r, out double g, out double b)
    {
        r = g = b = 0;
        hex = hex.Trim().TrimStart('#');
        if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        r = ((v >> 16) & 0xFF) / 255.0;
        g = ((v >> 8) & 0xFF) / 255.0;
        b = (v & 0xFF) / 255.0;
        return true;
    }
}
