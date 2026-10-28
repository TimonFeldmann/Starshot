using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Windows.Foundation;
using Windows.UI;

namespace Starshot.Features.Screenshot;

/// <summary>
/// 标注元素序列化：保存时以 &lt;ss:Markup&gt;base64(JSON)&lt;/ss:Markup&gt; 嵌入文件 XMP，
/// 重新打开编辑器时解码恢复元素列表（旧元素可再选择/移动/删除）。
/// 载荷 base64 编码（纯 [A-Za-z0-9+/=] 字符，XML 安全、免转义）。
/// src 字段记录底图（原始截图）文件名，供重开时定位原图并在其上恢复元素
/// （避免高亮叠加变深、模糊二次复合）。
/// </summary>
internal static class ScreenshotMarkupCodec
{
    private static readonly Regex MarkupRegex = new(
        @"<ss:Markup>(.+?)</ss:Markup>",
        RegexOptions.Singleline,
        TimeSpan.FromSeconds(1)
    );

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>元素列表 + 底图文件名 → base64 JSON 载荷（XMP 内嵌用）；无内容可嵌时返回 null。</summary>
    public static string? Encode(IReadOnlyList<ScreenshotOp>? ops, string? sourceFileName)
    {
        if ((ops is null || ops.Count == 0) && string.IsNullOrEmpty(sourceFileName))
        {
            return null;
        }
        var payload = new MarkupPayload
        {
            Src = sourceFileName,
            Ops = new List<OpDto>(ops?.Count ?? 0),
        };
        if (ops is not null)
        {
            foreach (ScreenshotOp op in ops)
            {
                OpDto? dto = EncodeOp(op);
                if (dto is not null)
                {
                    payload.Ops.Add(dto);
                }
            }
        }
        return Convert.ToBase64String(
            Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(payload, SerializerOptions)
            )
        );
    }

    /// <summary>从 XMP（整块）解析元素列表 + 底图文件名；未嵌入或解析失败返回 null。</summary>
    public static (IReadOnlyList<ScreenshotOp> Ops, string? SourceFileName)? Decode(
        byte[]? xmp
    )
    {
        if (xmp is null || xmp.Length == 0)
        {
            return null;
        }
        Match m = MarkupRegex.Match(Encoding.UTF8.GetString(xmp));
        if (!m.Success)
        {
            return null;
        }
        byte[] json;
        try
        {
            json = Convert.FromBase64String(m.Groups[1].Value.Trim());
        }
        catch (FormatException)
        {
            return null;
        }
        MarkupPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<MarkupPayload>(
                json,
                SerializerOptions
            );
        }
        catch (JsonException)
        {
            return null;
        }
        if (payload is null)
        {
            return null;
        }
        var ops = new List<ScreenshotOp>(payload.Ops?.Count ?? 0);
        if (payload.Ops is not null)
        {
            foreach (OpDto dto in payload.Ops)
            {
                ScreenshotOp? op = DecodeOp(dto);
                if (op is not null)
                {
                    ops.Add(op);
                }
            }
        }
        return (ops, payload.Src);
    }

    private static OpDto? EncodeOp(ScreenshotOp op)
    {
        if (op.Stroke is not null)
        {
            ScreenshotStroke st = op.Stroke;
            Color c = st.Color;
            var pts = new List<double[]>(st.Points.Count);
            foreach (Vector2 p in st.Points)
            {
                pts.Add(new double[] { MathF.Round(p.X, 2), MathF.Round(p.Y, 2) });
            }
            return new OpDto
            {
                T = "s",
                C = new int[] { c.A, c.R, c.G, c.B },
                Th = MathF.Round(st.Thickness, 2),
                Hl = st.IsHighlight,
                P = pts,
            };
        }
        if (op.Blur is not null)
        {
            ScreenshotBlurRegion b = op.Blur;
            return new OpDto
            {
                T = "b",
                R = RectValues(b.Rect),
                St = MathF.Round(b.Strength, 2),
            };
        }
        if (op.Erase is not null)
        {
            ScreenshotEraseRegion e = op.Erase;
            return new OpDto
            {
                T = "e",
                R = RectValues(e.Rect),
                Sp = new double[]
                {
                    MathF.Round(e.Sample.X, 2),
                    MathF.Round(e.Sample.Y, 2),
                },
            };
        }
        return null;
    }

    private static ScreenshotOp? DecodeOp(OpDto dto)
    {
        switch (dto.T)
        {
            case "s":
                if (dto.P is null)
                {
                    return null;
                }
                var stroke = new ScreenshotStroke
                {
                    Color = Color.FromArgb(
                        (byte)Math.Clamp(dto.C?[0] ?? 0xFF, 0, 255),
                        (byte)Math.Clamp(dto.C?[1] ?? 0xFF, 0, 255),
                        (byte)Math.Clamp(dto.C?[2] ?? 0, 0, 255),
                        (byte)Math.Clamp(dto.C?[3] ?? 0, 0, 255)
                    ),
                    Thickness = (float)Math.Max(1.0, dto.Th),
                    IsHighlight = dto.Hl,
                };
                foreach (double[] p in dto.P)
                {
                    if (p is not null && p.Length >= 2)
                    {
                        stroke.Points.Add(new Vector2((float)p[0], (float)p[1]));
                    }
                }
                if (stroke.Points.Count == 0)
                {
                    return null;
                }
                return new ScreenshotOp { Stroke = stroke };
            case "b":
                if (dto.R is null || dto.R.Length < 4)
                {
                    return null;
                }
                return new ScreenshotOp
                {
                    Blur = new ScreenshotBlurRegion
                    {
                        Rect = new Rect(
                            (float)dto.R[0],
                            (float)dto.R[1],
                            (float)dto.R[2],
                            (float)dto.R[3]
                        ),
                        Strength = (float)dto.St,
                    },
                };
            case "e":
                if (dto.R is null || dto.R.Length < 4)
                {
                    return null;
                }
                var erase = new ScreenshotEraseRegion
                {
                    Rect = new Rect(
                        (float)dto.R[0],
                        (float)dto.R[1],
                        (float)dto.R[2],
                        (float)dto.R[3]
                    ),
                    // 采样点缺失时回落到区域左上角；SampleBytes 不持久化，编辑器按新底图重采样
                    Sample =
                        dto.Sp is not null && dto.Sp.Length >= 2
                            ? new Vector2((float)dto.Sp[0], (float)dto.Sp[1])
                            : new Vector2((float)dto.R[0], (float)dto.R[1]),
                };
                return new ScreenshotOp { Erase = erase };
            default:
                return null;
        }
    }

    private static double[] RectValues(Rect r)
    {
        // 该 Win2D 投影里 Windows.Foundation.Rect 的成员是 double
        return new double[]
        {
            Math.Round(r.X, 2),
            Math.Round(r.Y, 2),
            Math.Round(r.Width, 2),
            Math.Round(r.Height, 2),
        };
    }

    private sealed class MarkupPayload
    {
        public int V { get; set; } = 1;

        public string? Src { get; set; }

        public List<OpDto>? Ops { get; set; }
    }

    /// <summary>
    /// 单条元素（camelCase 短键）：s=笔画（c 色 ARGB、th 粗细、hl 高亮、p 点列）；
    /// b=模糊（r 矩形 x/y/w/h、st 强度）；e=擦除（r 矩形、sp 采样点）。
    /// </summary>
    private sealed class OpDto
    {
        public string? T { get; set; }

        public int[]? C { get; set; }

        public double Th { get; set; }

        public bool Hl { get; set; }

        public List<double[]>? P { get; set; }

        public double[]? R { get; set; }

        public double St { get; set; }

        public double[]? Sp { get; set; }
    }
}
