// BandAnalyser.cs — part of Pedal OSC
// Copyright (C) 2026 thepedal
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT ANY
// WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A
// PARTICULAR PURPOSE. See the GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License along with
// this program. If not, see <https://www.gnu.org/licenses/>.

using System;

namespace WDE.PedalOsc
{
    /// <summary>
    /// Radix-2 FFT plus a log-spaced band-energy analyser. Self-contained (pure BCL) so the
    /// machine still deploys as a single .dll with no dependency assemblies.
    ///
    /// Sizing (Core-style tradeoff, see the invFFT addendum §1 for the general shape):
    ///   N = 2048 at 48 kHz  ->  bin width 23.4 Hz, latency ~21 ms.
    /// Smaller N tracks transients faster but cannot resolve bass: at N=1024 the lowest band
    /// spans a single bin, which is too fragile to drive a visual. 21 ms is a little over one
    /// video frame - imperceptible here, and not in the audio path at all since this runs on
    /// the sender thread from a copy of the signal.
    /// </summary>
    public sealed class BandAnalyser
    {
        public const int FftSize = 2048;
        public const int MaxBands = 8;

        const float LowHz = 40f;
        const float HighHz = 16000f;

        readonly float[] _re = new float[FftSize];
        readonly float[] _im = new float[FftSize];
        readonly float[] _window = new float[FftSize];
        readonly float[] _cos;              // twiddle tables, FftSize/2 entries
        readonly float[] _sin;

        readonly int[] _binLo = new int[MaxBands];
        readonly int[] _binHi = new int[MaxBands];

        int _sampleRate = -1;
        int _bandCount = -1;

        // A full-scale tone summed as energy across its band reads sqrt(1.5) * N/4 before
        // normalisation. sqrt(1.5) is the Hann window's energy correction: sum(w^2)/N = 3/8
        // against (sum(w)/N)^2 = 1/4, ratio 1.5. Dividing by this puts a full-scale tone at
        // 1.0 regardless of which band it lands in.
        static readonly float Norm = (float)Math.Sqrt(1.5) * FftSize / 4f;

        public BandAnalyser()
        {
            for (int i = 0; i < FftSize; i++)
                _window[i] = 0.5f - 0.5f * (float)Math.Cos(2.0 * Math.PI * i / FftSize);

            int half = FftSize / 2;
            _cos = new float[half];
            _sin = new float[half];
            for (int k = 0; k < half; k++)
            {
                double ang = -2.0 * Math.PI * k / FftSize;
                _cos[k] = (float)Math.Cos(ang);
                _sin[k] = (float)Math.Sin(ang);
            }
        }

        /// <summary>
        /// Recompute band bin ranges. Cheap, and skipped entirely when nothing changed, so it
        /// is safe to call every analysis pass.
        /// </summary>
        public void Configure(int sampleRate, int bandCount)
        {
            if (sampleRate == _sampleRate && bandCount == _bandCount) return;
            if (sampleRate <= 0) return;

            _sampleRate = sampleRate;
            _bandCount = bandCount < 1 ? 1 : (bandCount > MaxBands ? MaxBands : bandCount);

            float binWidth = (float)sampleRate / FftSize;
            int maxBin = FftSize / 2 - 1;

            // Logarithmic edges: musical energy is not linear in frequency, so equal-ratio
            // bands give each one comparable musical significance.
            for (int i = 0; i < _bandCount; i++)
            {
                float f0 = LowHz * (float)Math.Pow(HighHz / LowHz, (double)i / _bandCount);
                float f1 = LowHz * (float)Math.Pow(HighHz / LowHz, (double)(i + 1) / _bandCount);

                int b0 = (int)Math.Round(f0 / binWidth);
                int b1 = (int)Math.Round(f1 / binWidth) - 1;

                if (b0 < 1) b0 = 1;                  // skip DC
                if (b1 > maxBin) b1 = maxBin;
                if (b1 < b0) b1 = b0;

                _binLo[i] = b0;
                _binHi[i] = b1;
            }
        }

        /// <summary>
        /// Analyse one frame. <paramref name="mono"/> must hold FftSize samples in the native
        /// +/-32768 domain; results are written to <paramref name="bands"/> normalised so a
        /// full-scale tone reads ~1.0 in whichever band contains it.
        /// </summary>
        public void Analyse(float[] mono, float sampleScale, float[] bands)
        {
            for (int i = 0; i < FftSize; i++)
            {
                _re[i] = mono[i] * _window[i];
                _im[i] = 0f;
            }

            Transform();

            for (int b = 0; b < _bandCount; b++)
            {
                double energy = 0.0;
                int lo = _binLo[b], hi = _binHi[b];
                for (int k = lo; k <= hi; k++)
                {
                    float re = _re[k], im = _im[k];
                    energy += (double)re * re + (double)im * im;
                }
                // Energy sum (not magnitude average): a tone carries the same total energy
                // however many bins its band spans, so response stays flat across the
                // spectrum. Averaging magnitude would dilute a tone in a wide band by the
                // ratio of band width to lobe width - roughly 120x at the top end.
                bands[b] = (float)Math.Sqrt(energy) / Norm / sampleScale;
            }
            for (int b = _bandCount; b < MaxBands; b++) bands[b] = 0f;
        }

        // ------------------------------------------------------------------
        // In-place iterative radix-2 FFT. Twiddles come from a precomputed table indexed by
        // stride rather than a running recurrence, which avoids accumulated phase drift.
        // Verified against a reference implementation to float precision.
        // ------------------------------------------------------------------
        void Transform()
        {
            int n = FftSize;

            // Bit-reversal permutation.
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    (_re[i], _re[j]) = (_re[j], _re[i]);
                    (_im[i], _im[j]) = (_im[j], _im[i]);
                }
            }

            for (int len = 2; len <= n; len <<= 1)
            {
                int half = len >> 1;
                int stride = n / len;
                for (int i = 0; i < n; i += len)
                {
                    for (int k = 0; k < half; k++)
                    {
                        int t = k * stride;
                        float wr = _cos[t], wi = _sin[t];
                        int a = i + k, b = a + half;

                        float xr = _re[b] * wr - _im[b] * wi;
                        float xi = _re[b] * wi + _im[b] * wr;

                        _re[b] = _re[a] - xr;
                        _im[b] = _im[a] - xi;
                        _re[a] += xr;
                        _im[a] += xi;
                    }
                }
            }
        }
    }
}
