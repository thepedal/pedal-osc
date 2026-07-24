// PedalOsc.cs — part of Pedal OSC
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
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Buzz.MachineInterface;   // IBuzzMachine, IBuzzMachineHost, MachineDecl, ParameterDecl,
                               // Sample, WorkModes
using BuzzGUI.Interfaces;      // IMachine (for the instance name)

namespace WDE.PedalOsc
{
    /// <summary>
    /// Effect-class audio tap: passes audio through unchanged, measures level, and streams it
    /// over OSC/UDP. Addresses are namespaced by the machine's own instance name, so several
    /// taps can run on different busses without colliding.
    ///
    /// Song-global data (transport, tempo, beat/bar phase, machine parameters) is the job of
    /// the companion control machine, Pedal OSC Data. The two are independent - neither needs
    /// the other at runtime - and both send to the same endpoint, where the receiver merges
    /// them by address.
    /// </summary>
    [MachineDecl(Name = "Pedal OSC", ShortName = "PedalOSC", Author = "WDE",
                 InputCount = 1, OutputCount = 1)]
    public class PedalOscMachine : IBuzzMachine, IDisposable
    {
        // ---- wire config (compile-time for now; matches Pedal OSC Data) ----
        const string AddrVersion   = "/rebuzz/v";
        const string TapPrefix     = "/rebuzz/tap/";
        const float  SchemaVersion = 2f;
        const int    SendHz        = 125;

        // ReBuzz samples are +/-32768 float. Confirmed in the engine source: the master output
        // stage scales by audioOutMul = 1/32768.0f (WorkManager).
        const float  SampleScale   = 32768f;

        // ---- parameters (>= 1 required or the machine fails to load; see Build 11.1) ----

        [ParameterDecl(Name = "Sensitivity", Description = "Scales level before sending (64 = x1.0, 127 = x8).",
                       MinValue = 0, MaxValue = 127, DefValue = 64)]
        public int Sensitivity { get; set; } = 64;

        // Exponential gain: 2^((Sensitivity-64)/21). 64 -> x1.0 (so existing patches are
        // unchanged), 127 -> x8, 0 -> x0.12. Replaces the old linear Sensitivity/64 (max x1.98),
        // which could not reach full scale from a typical ~0.26 RMS peak for consumers with no
        // gain of their own.
        float Gain => (float)Math.Pow(2.0, (Sensitivity - 64) / 21.0);

        [ParameterDecl(Name = "Smooth", Description = "One-pole smoothing on the sent level (0 = none).",
                       MinValue = 0, MaxValue = 127, DefValue = 0)]
        public int Smooth { get; set; } = 0;

        [ParameterDecl(Name = "Bands", Description = "Log-spaced FFT bands to publish (0 = off).",
                       MinValue = 0, MaxValue = BandAnalyser.MaxBands, DefValue = BandAnalyser.MaxBands)]
        public int Bands { get; set; } = BandAnalyser.MaxBands;

        // ---- destination (parameter-driven; persists with the song) ----
        // IP as four octets; all-zero = loopback. Port as an offset from 9000. See OscConfig.cs.

        [ParameterDecl(Name = "Dst IP 1", Description = "Destination IP octet 1 (0 = loopback).",
                       MinValue = 0, MaxValue = 254, DefValue = 0)]
        public int DstIp1 { get; set; } = 0;

        [ParameterDecl(Name = "Dst IP 2", Description = "Destination IP octet 2.",
                       MinValue = 0, MaxValue = 254, DefValue = 0)]
        public int DstIp2 { get; set; } = 0;

        [ParameterDecl(Name = "Dst IP 3", Description = "Destination IP octet 3.",
                       MinValue = 0, MaxValue = 254, DefValue = 0)]
        public int DstIp3 { get; set; } = 0;

        [ParameterDecl(Name = "Dst IP 4", Description = "Destination IP octet 4.",
                       MinValue = 0, MaxValue = 254, DefValue = 0)]
        public int DstIp4 { get; set; } = 0;

        [ParameterDecl(Name = "Port +", Description = "Destination port offset from 9000.",
                       MinValue = 0, MaxValue = 127, DefValue = 0)]
        public int PortOffset { get; set; } = 0;

        // ------------------------------------------------------------------
        // Level frame. Published by the audio thread, consumed by the sender.
        // 4-slot pre-allocated ring + volatile index: lock-free, allocation-free, and the
        // whole frame stays internally consistent across the handoff.
        // ------------------------------------------------------------------
        struct LevelFrame
        {
            public float Rms;
            public float Peak;
        }

        const int SlotCount = 4;
        readonly LevelFrame[] _slots = new LevelFrame[SlotCount];
        volatile int _newest = 0;

        float _smoothed;                          // audio-thread only (smoothing state)

        // ------------------------------------------------------------------
        // Sample ring for spectral analysis.
        //
        // The FFT does NOT run on the audio thread. Work() only writes mono samples into this
        // circular buffer (a couple of adds per sample); the sender thread copies the newest
        // frame out and transforms it at its own rate. So band analysis costs the audio thread
        // essentially nothing, however many bands are published - the same principle that
        // keeps the control machine's parameter export off-thread.
        //
        // Sizing: the writer advances 48000 samples/sec, the reader takes FftSize every ~8 ms
        // (~384 samples of write in that window) and copies in microseconds. 8192 gives an
        // order of magnitude of headroom against the reader being lapped mid-copy.
        // ------------------------------------------------------------------
        const int RingSize = 8192;                // power of two, for the & mask
        readonly float[] _ring = new float[RingSize];
        volatile int _ringWrite = 0;
        volatile int _sampleRate = 0;             // captured in Work(); MasterInfo is only valid there

        readonly IBuzzMachineHost host;

        // Cached addresses, rebuilt on the sender thread when the machine is renamed.
        string _nameSeen = "";
        string _addrRms = TapPrefix + "unnamed/rms";
        string _addrPeak = TapPrefix + "unnamed/peak";
        readonly string[] _addrBands = new string[BandAnalyser.MaxBands];

        Thread? _sender;
        volatile bool _running;
        readonly OscSender _osc = new OscSender();

        public PedalOscMachine(IBuzzMachineHost host)
        {
            this.host = host;

            for (int b = 0; b < BandAnalyser.MaxBands; b++)
                _addrBands[b] = TapPrefix + "unnamed/band" + b;

            _running = true;
            _sender = new Thread(SenderLoop) { IsBackground = true, Name = "PedalOscSender" };
            _sender.Start();
        }

        // ------------------------------------------------------------------
        // AUDIO THREAD. Pass audio through untouched, measure level, publish.
        // No allocation, no locks, no I/O.
        // ------------------------------------------------------------------
        public bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)
        {
            // MasterInfo is only valid inside Work and parameter setters. The sender thread
            // needs the sample rate to place band edges, so capture it here.
            MasterInfo mi = host.MasterInfo;
            if (mi != null && mi.SamplesPerSec > 0) _sampleRate = mi.SamplesPerSec;

            float rms = 0f, peak = 0f;
            bool wroteSignal;

            // input is null when nothing upstream is connected/active. Testing it directly
            // lets nullable flow-analysis prove input is non-null in the loop below.
            if (input == null || n <= 0)
            {
                if (output != null)
                    for (int i = 0; i < n; i++) { output[i].L = 0f; output[i].R = 0f; }
                _smoothed = 0f;
                wroteSignal = false;
            }
            else
            {
                double sumSq = 0.0;
                for (int i = 0; i < n; i++)
                {
                    float l = input[i].L;
                    float r = input[i].R;
                    output[i].L = l;              // true pass-through
                    output[i].R = r;

                    sumSq += (double)l * l + (double)r * r;

                    float al = l < 0f ? -l : l; if (al > peak) peak = al;
                    float ar = r < 0f ? -r : r; if (ar > peak) peak = ar;
                }

                // Feed the analysis ring with a mono sum. Kept separate from the level maths
                // above so the FFT sees the raw signal, not the Sensitivity-scaled value.
                int w = _ringWrite;
                for (int i = 0; i < n; i++)
                    _ring[(w + i) & (RingSize - 1)] = (input[i].L + input[i].R) * 0.5f;
                _ringWrite = (w + n) & (RingSize - 1);

                float gain = Gain;
                rms  = (float)Math.Sqrt(sumSq / (2.0 * n)) / SampleScale * gain;
                peak = peak / SampleScale * gain;

                if (rms > 1f) rms = 1f;
                if (peak > 1f) peak = 1f;

                wroteSignal = true;
            }

            // Block-rate one-pole: Smooth 0 -> coef 1 (raw), 127 -> coef ~0.02 (heavy).
            float coef = 1f - (Smooth / 127f) * 0.98f;
            _smoothed += coef * (rms - _smoothed);

            int next = (_newest + 1) & (SlotCount - 1);
            _slots[next].Rms  = _smoothed;
            _slots[next].Peak = peak;
            _newest = next;                       // volatile write publishes the slot

            return wroteSignal;
        }

        // ------------------------------------------------------------------
        // SENDER THREAD. Copy the newest frame, encode one bundle, fire one UDP packet.
        // ------------------------------------------------------------------
        void SenderLoop()
        {
            int periodMs = Math.Max(1, 1000 / SendHz);
            var msgs = new List<(string, float)>(3 + BandAnalyser.MaxBands);

            // Sender-thread only: analysis scratch and per-band smoothing state.
            var analyser = new BandAnalyser();
            var frame = new float[BandAnalyser.FftSize];
            var bands = new float[BandAnalyser.MaxBands];
            var bandsSmoothed = new float[BandAnalyser.MaxBands];

            while (_running)
            {
                // Re-point the socket from the destination parameters. Cheap unless it changed.
                _osc.Retarget(OscEndpoint.Host(DstIp1, DstIp2, DstIp3, DstIp4),
                              OscEndpoint.Port(PortOffset));

                RefreshAddresses();

                LevelFrame f = _slots[_newest];   // volatile read, then one struct copy

                msgs.Clear();
                msgs.Add((AddrVersion, SchemaVersion));
                msgs.Add((_addrRms,    f.Rms));
                msgs.Add((_addrPeak,   f.Peak));

                int wantBands = Bands;
                int sr = _sampleRate;
                if (wantBands > 0 && sr > 0)
                {
                    // Copy the newest FftSize samples out of the ring, oldest first. The mask
                    // handles wrap; C# bitwise AND on a negative index is correct two's
                    // complement, so no separate branch is needed near the origin.
                    int w = _ringWrite;
                    for (int i = 0; i < BandAnalyser.FftSize; i++)
                        frame[i] = _ring[(w - BandAnalyser.FftSize + i) & (RingSize - 1)];

                    analyser.Configure(sr, wantBands);
                    analyser.Analyse(frame, SampleScale, bands);

                    float gain = Gain;
                    float coef = 1f - (Smooth / 127f) * 0.98f;

                    for (int b = 0; b < wantBands; b++)
                    {
                        float v = bands[b] * gain;
                        if (v > 1f) v = 1f;
                        bandsSmoothed[b] += coef * (v - bandsSmoothed[b]);
                        msgs.Add((_addrBands[b], bandsSmoothed[b]));
                    }
                }

                byte[] pkt = OscEncoder.EncodeBundle(msgs.ToArray());
                _osc.Send(pkt, pkt.Length);

                Thread.Sleep(periodMs);
            }
        }

        /// <summary>
        /// Namespace this instance's addresses by the machine's own name, so two taps on
        /// different busses do not clobber each other. IMachine.Name is a plain field read
        /// (MachineCore.Name => name), so checking it per send is cheap; the sanitise and
        /// string concat only run when the name actually changes.
        /// </summary>
        void RefreshAddresses()
        {
            try
            {
                IMachine? m = host.Machine;
                string current = m?.Name ?? "";
                if (current == _nameSeen) return;

                _nameSeen = current;
                string safe = Sanitise(current);
                _addrRms  = TapPrefix + safe + "/rms";
                _addrPeak = TapPrefix + safe + "/peak";
                for (int b = 0; b < BandAnalyser.MaxBands; b++)
                    _addrBands[b] = TapPrefix + safe + "/band" + b;
            }
            catch { /* keep the previous addresses */ }
        }

        /// <summary>
        /// Make a string safe for an OSC address element. OSC reserves space and
        /// # * , / ? [ ] { } - everything outside [a-z0-9_] is folded to '_'.
        /// </summary>
        static string Sanitise(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unnamed";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c >= 'a' && c <= 'z') sb.Append(c);
                else if (c >= 'A' && c <= 'Z') sb.Append((char)(c + 32));
                else if (c >= '0' && c <= '9') sb.Append(c);
                else sb.Append('_');
            }
            return sb.Length == 0 ? "unnamed" : sb.ToString();
        }

        // ------------------------------------------------------------------
        // Teardown. Confirmed in the engine source: MachineManager.DeleteMachine ->
        // ManagedMachineHost.Release() calls Dispose() on IDisposable machines.
        // ------------------------------------------------------------------
        public void Dispose()
        {
            _running = false;
            try { _sender?.Join(200); } catch { }
            _osc.Dispose();
        }
    }
}
