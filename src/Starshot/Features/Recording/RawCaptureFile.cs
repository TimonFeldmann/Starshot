using System;
using System.Buffers.Binary;
using System.IO;

namespace Starshot.Features.Recording;

/// <summary>
/// Raw capture buffer for post-encode recording (VideoRecordPostEncode): the capture
/// pipeline converts every frame in real time and appends it to this file; after stop a
/// background drain re-reads the frames and runs the actual encoder, so the capture holds
/// the display's frame rate instead of the encoder's.
///
/// Layout (little-endian, our own format - no ISO endianness constraints):
///   header (64 bytes):
///     0   u32  magic "SRR1" (0x31525253)
///     4   u32  version (1)
///     8   u32  width (even)
///     12  u32  height (even)
///     16  u32  hdr (0 = SDR NV12, 1 = HDR P010)
///     20  u32  fpsNum
///     24  u32  fpsDen
///     28  u64  frameBytes (total plane bytes per frame)
///     36  u64  frameCount (stamped on close; the reader walks to EOF instead)
///     44  zeros (pad to 64)
///   records: [ i64 relPts (100ns, relative to the first video frame) | planes ]
///   planes: HDR = P010 Y (w*h*2) + U (w/2*h/2*2) + V (w/2*h/2*2) = 3*w*h;
///           SDR = NV12 Y (w*h) + UV (w*h/2) = 3*w*h/2
/// A torn tail record (crash mid-write) simply is not a full record: the drain encodes
/// the complete frames only and the file is deleted afterwards, so torn tails are
/// tolerable by construction.
///
/// The file is opened ONCE with ReadWrite access (WriteFrame appends; the drain seeks
/// back to the first record and reads through the same handle). A second, separate
/// open of the same file - even with compatible sharing - can fail on machines where a
/// file-system filter (AV/EDR) grabs a lock on newly written files, so the drain must
/// never re-open the file.
/// </summary>
internal static class RawCaptureFile
{
    public const int HeaderSize = 64;

    /// <summary>"SRR1" little-endian.</summary>
    public const uint Magic = 0x31525253;

    public const uint Version = 1;

    /// <summary>Total plane bytes of one frame (tight rows, see the layout above).</summary>
    public static long FrameBytes(int width, int height, bool hdr)
    {
        // HDR P010: Y w*h*2 + U/V (w*h/2)*2 each = 3*w*h
        // SDR NV12: Y w*h + UV w*h/2 = 3*w*h/2 (w/h are even, so exact)
        return hdr ? (long)width * height * 3 : (long)width * height * 3 / 2;
    }

    /// <summary>
    /// Raw capture file: append-only writer (the capture-side consumer thread) +
    /// sequential reader (the post-stop drain, through the same handle). One instance
    /// per session; frame records are written in pump order, so the drain can simply
    /// walk the file from the first record to EOF.
    /// </summary>
    public sealed class Writer : IDisposable
    {
        private readonly FileStream _fs;
        private readonly int _width;
        private readonly int _height;
        private readonly bool _hdr;
        private readonly int _yLen;
        private readonly int _uLen;
        private readonly int _vLen;
        private long _frameCount;
        private bool _disposed;

        private Writer(
            FileStream fs,
            int width,
            int height,
            bool hdr,
            int fpsNum,
            int fpsDen,
            int yLen,
            int uLen,
            int vLen
        )
        {
            _fs = fs;
            _width = width;
            _height = height;
            _hdr = hdr;
            _yLen = yLen;
            _uLen = uLen;
            _vLen = vLen;
            WriteHeader(fpsNum, fpsDen, 0);
        }

        public long FrameCount => _frameCount;

        /// <summary>Create (overwrite) the raw file and write the header.</summary>
        public static Writer Open(
            string path,
            int width,
            int height,
            bool hdr,
            int fpsNum,
            int fpsDen
        )
        {
            // ReadWrite (not Write): the drain reads the frames back through this same
            // handle after the capture (see the type-level comment on why a second open
            // is not used). FileShare.Read keeps the door open for external read-only
            // tools while the file is alive.
            var fs = new FileStream(
                path,
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.Read,
                4 << 20,
                FileOptions.SequentialScan
            );
            // HDR: Y w*h*2, U/V (w/2)*(h/2)*2 each; SDR: Y w*h, UV w*(h/2)
            int yLen = hdr ? width * height * 2 : width * height;
            int uLen = hdr ? (width / 2) * (height / 2) * 2 : width * (height / 2);
            int vLen = hdr ? uLen : 0;
            return new Writer(fs, width, height, hdr, fpsNum, fpsDen, yLen, uLen, vLen);
        }

        /// <summary>
        /// Append one frame record (capture side). HDR: (y, u, v) = the P010 planes;
        /// SDR: (y, uv, null).
        /// </summary>
        public void WriteFrame(long relPts100ns, byte[] y, byte[] u, byte[]? v)
        {
            if (y.Length != _yLen || u.Length != _uLen || (v?.Length ?? 0) != _vLen)
            {
                throw new InvalidDataException(
                    $"plane sizes {y.Length}/{u.Length}/{(v?.Length ?? 0)} do not match the "
                        + $"raw file layout {_yLen}/{_uLen}/{_vLen}"
                );
            }
            Span<byte> pts = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(pts, relPts100ns);
            _fs.Write(pts);
            _fs.Write(y, 0, y.Length);
            _fs.Write(u, 0, u.Length);
            if (v is not null)
            {
                _fs.Write(v, 0, v.Length);
            }
            _frameCount++;
        }

        /// <summary>
        /// Position the stream at the first frame record (the drain calls this once
        /// before reading; all writes must be done - the drain runs after the capture's
        /// writer thread has exited).
        /// </summary>
        public void SeekToFrames()
        {
            _fs.Seek(HeaderSize, SeekOrigin.Begin);
        }

        /// <summary>
        /// Read the next frame record into the preallocated plane arrays (the session's
        /// processor planes - zero per-frame allocation). HDR: (y, u, v); SDR: (y, uv,
        /// null). False at a clean EOF or a torn tail record (the complete frames only);
        /// a real I/O error propagates.
        /// </summary>
        public bool TryReadNext(out long relPts100ns, byte[] y, byte[] u, byte[]? v)
        {
            relPts100ns = 0;
            int yLen = y.Length;
            int uLen = u.Length;
            int vLen = v?.Length ?? 0;
            long layout = FrameBytes(_width, _height, _hdr);
            if ((long)yLen + uLen + vLen != layout)
            {
                throw new InvalidDataException(
                    $"plane sizes {yLen}/{uLen}/{vLen} do not match the raw file layout {layout}"
                );
            }
            Span<byte> pts = stackalloc byte[8];
            try
            {
                _fs.ReadExactly(pts);
            }
            catch (EndOfStreamException)
            {
                return false; // clean EOF (or a tail torn before the pts)
            }
            relPts100ns = BinaryPrimitives.ReadInt64LittleEndian(pts);
            try
            {
                _fs.ReadExactly(y, 0, yLen);
                _fs.ReadExactly(u, 0, uLen);
                if (v is not null)
                {
                    _fs.ReadExactly(v, 0, vLen);
                }
            }
            catch (EndOfStreamException)
            {
                return false; // torn tail record: keep the complete frames only
            }
            return true;
        }

        private void WriteHeader(int fpsNum, int fpsDen, long frameCount)
        {
            Span<byte> h = stackalloc byte[HeaderSize];
            BinaryPrimitives.WriteUInt32LittleEndian(h, Magic);
            BinaryPrimitives.WriteUInt32LittleEndian(h[4..], Version);
            BinaryPrimitives.WriteUInt32LittleEndian(h[8..], (uint)_width);
            BinaryPrimitives.WriteUInt32LittleEndian(h[12..], (uint)_height);
            BinaryPrimitives.WriteUInt32LittleEndian(h[16..], _hdr ? 1u : 0u);
            BinaryPrimitives.WriteUInt32LittleEndian(h[20..], (uint)fpsNum);
            BinaryPrimitives.WriteUInt32LittleEndian(h[24..], (uint)fpsDen);
            BinaryPrimitives.WriteInt64LittleEndian(h[28..], FrameBytes(_width, _height, _hdr));
            BinaryPrimitives.WriteInt64LittleEndian(h[36..], frameCount);
            _fs.Write(h);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                // Stamp the final frame count into the header (the drain walks to EOF
                // anyway; this is for tools/inspection).
                _fs.Seek(36, SeekOrigin.Begin);
                Span<byte> c = stackalloc byte[8];
                BinaryPrimitives.WriteInt64LittleEndian(c, _frameCount);
                _fs.Write(c);
            }
            catch
            {
                // a failed stamp does not matter (the header count is informational)
            }
            _fs.Dispose();
        }
    }
}
