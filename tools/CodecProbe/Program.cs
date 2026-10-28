using System;
using System.Text;
using Starward.Codec.AVIF;
using Starward.Codec.PNG;

// 无界面复现「带 XMP 的 AVIF 编码 + 新读取逻辑（单次 GetNthImage(0) 同时取 XMP 与像素）」，
// 并对照旧逻辑（GetNthImage 后再 GetNextImage）验证 "No images remaining" 根因；
// 以及 PNG iTXt XMP 提取逻辑验证。
RoundTripProbe.Run();
RoundTripProbe.RunPng();

static class RoundTripProbe
{
    public static void Run()
    {
        int w = 64;
        int h = 32;
        uint depth = 8;
        byte[] pixels = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            pixels[i * 4] = (byte)(i % 256);
            pixels[i * 4 + 1] = 0x40;
            pixels[i * 4 + 2] = 0x80;
            pixels[i * 4 + 3] = 0xFF;
        }
        string xmp =
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmlns:ss=\"http://starshot.app/marks/\"><xmp:CreatorTool>Starshot</xmp:CreatorTool><xmp:CreateDate>2026-10-28T05:49:53+08:00</xmp:CreateDate><ss:Markup>TESTPAYLOADBASE64</ss:Markup></rdf:Description></rdf:RDF></x:xmpmeta>";
        byte[] xmpBytes = Encoding.UTF8.GetBytes(xmp);

        // ---- 编码（与 ImageSaver.SaveAsAvifAsync 的 SDR 路径同款）----
        byte[] avif;
        using (var encoder = new avifEncoderLite())
        {
            encoder.Quality = 90;
            encoder.QualityAlpha = 90;
            using var rgb = new avifRGBImageWrapper((uint)w, (uint)h, depth, avifRGBFormat.RGBA);
            rgb.SetPixelBytes(pixels);
            using var image = new avifImageWrapper((uint)w, (uint)h, depth, avifPixelFormat.YUV444);
            image.SetXMPMetadata(xmpBytes);
            image.FromRGBImage(rgb);
            encoder.AddImage(image, 1, avifAddImageFlag.Single);
            avif = encoder.Encode().ToArray();
        }
        Console.WriteLine($"[enc] avif bytes: {avif.Length}");

        // ---- 新读取逻辑：单次 GetNthImage(0)，XMP 与像素同一包装器取回 ----
        using (var decoder = avifDecoderLite.Create(avif))
        {
            int dw = (int)decoder.Width;
            int dh = (int)decoder.Height;
            uint d = decoder.Depth > 8u ? 16u : 8u;
            using var img = decoder.GetNthImage(0);
            ReadOnlySpan<byte> xm = img.GetXMPMetadata();
            string xmpText = Encoding.UTF8.GetString(xm);
            Console.WriteLine(
                $"[dec] XMP: {xmpText.Length} bytes, has ss:Markup = {xmpText.Contains("ss:Markup")}"
            );
            using var outRgb = img.ToRGBImage(d, avifRGBFormat.RGBA);
            byte[] px = outRgb.GetPixelBytes().ToArray();
            Console.WriteLine(
                $"[dec] pixels: {px.Length} bytes (expect {dw * dh * 4}), first=0x{px[0].ToString("X2")} last=0x{px[^1].ToString("X2")}"
            );
        }

        // ---- 对照：旧逻辑（GetNthImage(0) 之后再 GetNextImage）应复现崩溃 ----
        using (var decoder2 = avifDecoderLite.Create(avif))
        {
            using var img2 = decoder2.GetNthImage(0);
            _ = img2;
            try
            {
                using var _ = decoder2.GetNextImage();
                Console.WriteLine("[ctrl] GetNextImage after GetNthImage(0): OK (与日志不符!)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ctrl] GetNextImage after GetNthImage(0) throws: {ex.Message}");
            }
        }
    }

    // PNG iTXt XMP 提取验证：与 ImageSaver 写 iTXt 同款构造，确认 PngChunk.ContentData
    // 是「内容段」（关键字 + 5 个 0 + 载荷）而非含 type/length/CRC 的整块，以及偏移 21 起跳零的提取逻辑正确。
    public static void RunPng()
    {
        string xmp = "<x:xmpmeta><ss:Markup>PNGTEST</ss:Markup></x:xmpmeta>";
        byte[] xmpBytes = Encoding.UTF8.GetBytes(xmp);
        Span<byte> chunkContent = new byte[22 + xmpBytes.Length];
        "XML:com.adobe.xmp"u8.CopyTo(chunkContent);
        xmpBytes.CopyTo(chunkContent[22..]);
        var chunk = new PngChunk(PngChunkType.iTXt, chunkContent);

        int cl = chunk.ContentLength;
        Span<byte> cd = chunk.ContentData;
        Console.WriteLine(
            $"[png] ContentLength={cl} ContentData.Length={cd.Length} (expect {22 + xmpBytes.Length})"
        );
        bool keywordOk = cd.Length >= 17 && cd[..17].SequenceEqual("XML:com.adobe.xmp"u8);
        Console.WriteLine($"[png] keyword@0..17 ok = {keywordOk}");
        // 复现 LoadPngAsync 的提取逻辑
        int start = 21;
        while (start < cd.Length - 1 && cd[start] == 0)
        {
            start++;
        }
        bool extractedOk = start < cd.Length - 1;
        string extracted = extractedOk ? Encoding.UTF8.GetString(cd[start..]) : "(none)";
        Console.WriteLine($"[png] extracted start={start}: {extracted}");
        Console.WriteLine($"[png] roundtrip ok = {extractedOk && extracted == xmp}");
    }
}
