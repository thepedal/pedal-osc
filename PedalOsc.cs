using System;
using System.Net.Sockets;
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
        const string DestHost      = "127.0.0.1";
        const int    DestPort      = 9000;
        const int    SendHz        = 125;

        // ReBuzz samples are +/-32768 float. Confirmed in the engine source: the master output
        // stage scales by audioOutMul = 1/32768.0f (WorkManager).
        const float  SampleScale   = 32768f;

        // ---- parameters (>= 1 required or the machine fails to load; see Build 11.1) ----

        [ParameterDecl(Name = "Sensitivity", Description = "Scales level before sending (64 = x1.0).",
                       MinValue = 0, MaxValue = 127, DefValue = 64)]
        public int Sensitivity { get; set; } = 64;

        [ParameterDecl(Name = "Smooth", Description = "One-pole smoothing on the sent level (0 = none).",
                       MinValue = 0, MaxValue = 127, DefValue = 0)]
        public int Smooth { get; set; } = 0;

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

        readonly IBuzzMachineHost host;

        // Cached addresses, rebuilt on the sender thread when the machine is renamed.
        string _nameSeen = "";
        string _addrRms = TapPrefix + "unnamed/rms";
        string _addrPeak = TapPrefix + "unnamed/peak";

        Thread? _sender;
        volatile bool _running;
        UdpClient? _udp;

        public PedalOscMachine(IBuzzMachineHost host)
        {
            this.host = host;

            _udp = new UdpClient();
            _udp.Connect(DestHost, DestPort);     // resolve the endpoint once, reuse per send

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

                float gain = Sensitivity / 64f;   // 64 = x1.0
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
            var msgs = new (string, float)[3];    // reused; sender thread only

            while (_running)
            {
                UdpClient? udp = _udp;            // snapshot (may be nulled by Dispose)
                if (udp == null) break;

                RefreshAddresses();

                LevelFrame f = _slots[_newest];   // volatile read, then one struct copy

                msgs[0] = (AddrVersion, SchemaVersion);
                msgs[1] = (_addrRms,    f.Rms);
                msgs[2] = (_addrPeak,   f.Peak);

                try
                {
                    byte[] pkt = OscEncoder.EncodeBundle(msgs);
                    udp.Send(pkt, pkt.Length);
                }
                catch { /* transient send errors must not kill the thread */ }

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
            try { _udp?.Close(); }      catch { }
            _udp = null;
        }
    }
}
