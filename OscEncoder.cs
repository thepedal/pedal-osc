using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace WDE.PedalOsc
{
    /// <summary>
    /// Minimal OSC 1.0 encoder (messages + bundles). Hand-rolled on purpose: the whole format
    /// is ~60 lines, and avoiding a NuGet OSC package keeps the deployed machine to a single
    /// .dll (no extra dependency dll in C:\Program Files\ReBuzz\Gear\Effects).
    ///
    /// An OSC message is three parts, each padded to a 4-byte boundary:
    ///   1. Address pattern  - ASCII, null-terminated, null-padded to a multiple of 4.
    ///   2. Type-tag string  - ',' then one tag char per arg ('f'=float32), null-term, padded.
    ///   3. Arguments        - each 'f' is a big-endian (network order) IEEE-754 float32.
    ///
    /// An OSC bundle is: "#bundle" OSC-string, a 64-bit time tag, then for each element a
    /// big-endian int32 length followed by that element's bytes.
    /// </summary>
    public static class OscEncoder
    {
        /// <summary>Encode a message carrying a single float32 (e.g. "/rebuzz/rms", 0.42f).</summary>
        public static byte[] EncodeFloat(string address, float value)
            => EncodeFloats(address, stackalloc float[] { value });

        /// <summary>Encode a message carrying N float32 args.</summary>
        public static byte[] EncodeFloats(string address, ReadOnlySpan<float> args)
        {
            var buf = new List<byte>(32);
            WriteMessage(buf, address, args);
            return buf.ToArray();
        }

        /// <summary>
        /// Encode several single-float messages as one OSC bundle. Bundling keeps every value
        /// from the same audio block atomic (no tearing across packets) while still using
        /// per-value addresses, which off-the-shelf OSC tools map to channels natively.
        /// </summary>
        public static byte[] EncodeBundle((string Address, float Value)[] messages)
        {
            var buf = new List<byte>(256);

            WriteOscString(buf, "#bundle");

            // Time tag: NTP-style 64-bit. The special value 1 means "immediately".
            for (int i = 0; i < 7; i++) buf.Add(0);
            buf.Add(1);

            var element = new List<byte>(32);
            Span<byte> size = stackalloc byte[4];

            foreach (var (address, value) in messages)
            {
                element.Clear();
                WriteMessage(element, address, stackalloc float[] { value });

                BinaryPrimitives.WriteInt32BigEndian(size, element.Count);
                buf.Add(size[0]); buf.Add(size[1]); buf.Add(size[2]); buf.Add(size[3]);
                buf.AddRange(element);
            }

            return buf.ToArray();
        }

        // ------------------------------------------------------------------
        static void WriteMessage(List<byte> buf, string address, ReadOnlySpan<float> args)
        {
            WriteOscString(buf, address);

            // Type-tag string: ',' + one 'f' per arg.
            var tag = new StringBuilder(1 + args.Length);
            tag.Append(',');
            for (int i = 0; i < args.Length; i++) tag.Append('f');
            WriteOscString(buf, tag.ToString());

            Span<byte> tmp = stackalloc byte[4];
            foreach (float f in args)
            {
                BinaryPrimitives.WriteSingleBigEndian(tmp, f);
                buf.Add(tmp[0]); buf.Add(tmp[1]); buf.Add(tmp[2]); buf.Add(tmp[3]);
            }
        }

        /// <summary>
        /// Write an OSC-string: ASCII bytes, at least one null terminator, then pad with nulls
        /// to the next 4-byte boundary. Every prior element is already 4-aligned, so the
        /// running count is a valid offset to align against.
        /// </summary>
        static void WriteOscString(List<byte> buf, string s)
        {
            int start = buf.Count;
            for (int i = 0; i < s.Length; i++)
                buf.Add((byte)s[i]);             // OSC addresses/tags are 7-bit ASCII
            buf.Add(0);                          // mandatory null terminator
            while ((buf.Count - start) % 4 != 0) buf.Add(0);
        }
    }
}
