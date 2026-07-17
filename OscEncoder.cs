using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace WDE.PedalOsc
{
    /// <summary>
    /// Minimal OSC 1.0 message encoder. Hand-rolled on purpose: one message type is ~30 lines,
    /// and avoiding a NuGet OSC package keeps the deployed machine to a single .dll (no extra
    /// dependency dll to copy into C:\Program Files\ReBuzz).
    ///
    /// An OSC message is three parts, each 4-byte aligned:
    ///   1. Address pattern  - ASCII, null-terminated, padded with nulls to a multiple of 4.
    ///   2. Type-tag string  - ',' then one tag char per arg ('f'=float32), null-term, padded to 4.
    ///   3. Arguments        - each 'f' is a big-endian (network order) IEEE-754 float32.
    /// </summary>
    public static class OscEncoder
    {
        /// <summary>Encode a message carrying a single float32 (e.g. "/rebuzz/rms", 0.42f).</summary>
        public static byte[] EncodeFloat(string address, float value)
            => EncodeFloats(address, stackalloc float[] { value });

        /// <summary>Encode a message carrying N float32 args (for the later feature frame).</summary>
        public static byte[] EncodeFloats(string address, ReadOnlySpan<float> args)
        {
            var buf = new List<byte>(32);

            // 1. address pattern
            WriteOscString(buf, address);

            // 2. type-tag string: ',' + one 'f' per arg
            var tag = new StringBuilder(1 + args.Length);
            tag.Append(',');
            for (int i = 0; i < args.Length; i++) tag.Append('f');
            WriteOscString(buf, tag.ToString());

            // 3. args - big-endian float32 each
            Span<byte> tmp = stackalloc byte[4];
            foreach (float f in args)
            {
                BinaryPrimitives.WriteSingleBigEndian(tmp, f);
                buf.Add(tmp[0]); buf.Add(tmp[1]); buf.Add(tmp[2]); buf.Add(tmp[3]);
            }

            return buf.ToArray();
        }

        /// <summary>
        /// Write an OSC-string: ASCII bytes, at least one null terminator, then pad with nulls to
        /// the next 4-byte boundary. Because every prior element is already 4-aligned, buf.Count
        /// is a correct running offset to align against.
        /// </summary>
        static void WriteOscString(List<byte> buf, string s)
        {
            for (int i = 0; i < s.Length; i++)
                buf.Add((byte)s[i]);             // OSC addresses/tags are 7-bit ASCII
            buf.Add(0);                          // mandatory null terminator
            while (buf.Count % 4 != 0) buf.Add(0);
        }
    }
}
