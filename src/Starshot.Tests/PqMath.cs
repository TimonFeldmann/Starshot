namespace Starshot.Tests;

/// <summary>
/// Analytic ground truth for the HDR flat-field test: the exact 10-bit HDR10 codes the
/// pipeline must produce for an N-nit BT.2020 flat field. Same ST 2084 OETF + BT.2020
/// YUV math as FrameProcessor (double precision here), so any code difference on a real
/// capture is a pipeline/encoder defect, not a reference mismatch.
/// </summary>
internal static class PqMath
{
    public static readonly string[] Colors = ["RED", "GREEN", "BLUE", "YELLOW"];

    // ST 2084 OETF constants (identical to FrameProcessor.BuildPqLut and the host's PqCode).
    private const double C1 = 3424.0 / 4096.0;
    private const double C2 = 32.0 * 2413.0 / 4096.0;
    private const double C3 = 32.0 * 2392.0 / 4096.0;
    private const double M1 = 0.25 * 2610.0 / 4096.0;
    private const double M2 = 128.0 * 2523.0 / 4096.0;

    // BT.2020 luma / chroma coefficients (FrameProcessor's Yr/Yg/Yb).
    private const double Yr = 0.2627;
    private const double Yg = 0.678;
    private const double Yb = 0.0593;

    /// <summary>
    /// 10-bit HDR10 codes for an N-nit flat field of the given color (1.0 = 10000 nits in
    /// linear light), per the H.273 / HEVC bt2020nc convention: each RGB channel is PQ-OETF'd,
    /// then the BT.2020 matrix is applied in the PQ domain (Y'/Cb'/Cr' are the coded values;
    /// Cb/Cr neutral = 512).
    /// </summary>
    public static (int Y, int U, int V) ExpectedCodes(string color, int nits)
    {
        double s = nits / 10000.0;
        double r = color is "RED" or "YELLOW" ? s : 0.0;
        double g = color is "GREEN" or "YELLOW" ? s : 0.0;
        double b = color is "BLUE" ? s : 0.0;
        // H.273: PQ-OETF each channel, then the BT.2020 matrix in the PQ domain.
        double rp = PqOETF10(r) / 1023.0;
        double gp = PqOETF10(g) / 1023.0;
        double bp = PqOETF10(b) / 1023.0;
        double yp = Yr * rp + Yg * gp + Yb * bp;
        double u = 0.5 + (bp - yp) / (2.0 * (1.0 - Yb));
        double v = 0.5 + (rp - yp) / (2.0 * (1.0 - Yr));
        return (Linear10(yp), Linear10(u), Linear10(v));
    }

    /// <summary>ST 2084 PQ OETF of a 0..1 linear-light value (1.0 = 10000 nits) → 10-bit code.</summary>
    public static int PqOETF10(double y01)
    {
        if (y01 <= 0.0)
            return 0;
        if (y01 > 1.0)
            y01 = 1.0;
        double t = Math.Pow(y01, M1);
        double pq = Math.Pow((C1 + C2 * t) / (1.0 + C3 * t), M2);
        return (int)Math.Round(pq * 1023.0);
    }

    /// <summary>0..1 coded value (luma Y' or chroma Cb'/Cr') → 10-bit full-range code (0..1023; chroma neutral 512).</summary>
    public static int Linear10(double v01) => (int)Math.Round(Math.Clamp(v01, 0.0, 1.0) * 1023.0);
}
