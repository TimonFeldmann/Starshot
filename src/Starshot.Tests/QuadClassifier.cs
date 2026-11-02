namespace Starshot.Tests;

/// <summary>
/// Classifies the source-rate yuv420p10le frames of the quad flat-field recording into
/// RED / GREEN / BLUE / YELLOW / BLACK / NEUTRAL / UNKNOWN and groups consecutive
/// same-class frames into segments. C# port of the orchestrator's quad_segments.py:
/// same central-crop means, same scale-invariant chroma-direction vectors (the chroma
/// DIRECTION is brightness-invariant, so midtone fields classify like full-scale ones).
/// </summary>
internal sealed class QuadClassifier
{
    public sealed record FrameClass(
        int Index,
        string Class,
        double Ym,
        double Um,
        double Vm,
        double Amp
    );

    public sealed record Segment(
        string Class,
        int StartFrame,
        int Frames,
        double Ym,
        double Um,
        double Vm,
        double Amp
    );

    // Unit chroma vectors of the saturated 10000-nit fields (measured from the
    // ground-truth clip; see PqMath for the analytic source of the same numbers).
    private static readonly (string Name, double U, double V)[] Vectors =
    {
        ("RED", -0.269, 0.964),
        ("GREEN", -0.615, -0.785),
        ("BLUE", 0.997, -0.080),
        ("YELLOW", -0.997, 0.080),
    };

    private readonly int _w;
    private readonly int _h;
    private readonly int _frameSize;
    private readonly byte[] _buf;
    private readonly List<FrameClass> _frames = [];

    public QuadClassifier(int width, int height)
    {
        _w = width;
        _h = height;
        // 10-bit planar: Y (W*H*2) + U (W*H/4) + V (W*H/4) bytes per frame.
        _frameSize = width * height * 3;
        _buf = new byte[_frameSize];
    }

    public int Frames => _frames.Count;

    public IReadOnlyList<FrameClass> FrameList => _frames;

    /// <summary>Read planar frames from a stream until EOF (an ffmpeg rawvideo pipe).</summary>
    public async Task ReadFramesAsync(Stream s)
    {
        int filled = 0;
        while (true)
        {
            while (filled < _frameSize)
            {
                int r = await s.ReadAsync(_buf.AsMemory(filled, _frameSize - filled));
                if (r == 0)
                    return;
                filled += r;
            }
            _frames.Add(ClassifyFrame());
            filled = 0;
        }
    }

    /// <summary>
    /// Central-crop means + chroma-direction class (the Python classifier's logic):
    /// Y crop rows [H/4, 3H/4) x cols [W/4, 3W/4); U/V rows [H/8, 3H/8) x cols [W/8, 3W/8);
    /// BLACK if Ym &lt; 20, NEUTRAL if chroma amplitude &lt; 8, else the best-matching
    /// chroma direction.
    /// The 20 threshold sits below the dimmest recorded color field (a 240-nit BLUE is
    /// Y' ≈ 36 in the H.273 PQ-domain convention, see PqMath) but above true black (~0).
    /// The old 60 threshold predated the PQ-domain luma (BLUE was Y ≈ 711 then) and would
    /// classify the low-nit BLUE fields as BLACK.
    /// </summary>
    private FrameClass ClassifyFrame()
    {
        int nY = _w * _h;
        int halfW = _w / 2;
        int r0 = _h / 4;
        int r1 = 3 * _h / 4;
        int c0 = _w / 4;
        int c1 = 3 * _w / 4;

        double ySum = 0;
        long yN = 0;
        for (int r = r0; r < r1; r++)
        {
            int off = (r * _w + c0) * 2;
            for (int c = 0; c < c1 - c0; c++)
            {
                ySum += U16(off + c * 2);
                yN++;
            }
        }

        double uSum = 0;
        double vSum = 0;
        long uvN = 0;
        int ur0 = r0 / 2;
        int ur1 = r1 / 2;
        int uc0 = c0 / 2;
        int uc1 = c1 / 2;
        // Planar yuv420p10le layout: Y (nY*2 bytes) | U (nY/2 bytes) | V (nY/2 bytes).
        int uBase = nY * 2;
        int vBase = nY * 2 + nY / 2;
        for (int r = ur0; r < ur1; r++)
        {
            int offU = uBase + (r * halfW + uc0) * 2;
            int offV = vBase + (r * halfW + uc0) * 2;
            for (int c = 0; c < uc1 - uc0; c++)
            {
                uSum += U16(offU + c * 2);
                vSum += U16(offV + c * 2);
                uvN++;
            }
        }

        double ym = ySum / yN;
        double um = uSum / uvN;
        double vm = vSum / uvN;
        double du = um - 512.0;
        double dv = vm - 512.0;
        double amp = Math.Sqrt(du * du + dv * dv);
        string cls;
        if (ym < 20.0)
        {
            cls = "BLACK";
        }
        else if (amp < 8.0)
        {
            cls = "NEUTRAL";
        }
        else
        {
            cls = "UNKNOWN";
            double bestCos = -2.0;
            foreach ((string name, double vu, double vv) in Vectors)
            {
                double cos = (du * vu + dv * vv) / amp;
                if (cos > bestCos)
                {
                    bestCos = cos;
                    cls = name;
                }
            }
        }
        return new FrameClass(_frames.Count, cls, ym, um, vm, amp);
    }

    private int U16(int off) => _buf[off] | (_buf[off + 1] << 8);

    /// <summary>Group consecutive same-class frames (the Python classifier's segments).</summary>
    public List<Segment> Segments()
    {
        var segs = new List<Segment>();
        int i = 0;
        while (i < _frames.Count)
        {
            int j = i;
            while (j + 1 < _frames.Count && _frames[j + 1].Class == _frames[i].Class)
                j++;
            var slice = _frames.GetRange(i, j - i + 1);
            segs.Add(
                new Segment(
                    slice[0].Class,
                    slice[0].Index,
                    slice.Count,
                    slice.Average(f => f.Ym),
                    slice.Average(f => f.Um),
                    slice.Average(f => f.Vm),
                    slice.Average(f => f.Amp)
                )
            );
            i = j + 1;
        }
        return segs;
    }

    /// <summary>The longest segment of a color (its mean is the recording's measured code).</summary>
    public Segment? Longest(string color) =>
        Segments().Where(s => s.Class == color).OrderByDescending(s => s.Frames).FirstOrDefault();
}
