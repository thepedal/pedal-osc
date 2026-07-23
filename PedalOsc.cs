using System;
using System.Net.Sockets;
using System.Threading;
using Buzz.MachineInterface;   // IBuzzMachine, IBuzzMachineHost, MachineDecl, ParameterDecl,
                               // Sample, WorkModes, MasterInfo
using BuzzGUI.Interfaces;      // IBuzz, ISong (host object graph - for PlayPosition / Playing)

namespace WDE.PedalOsc
{
    [MachineDecl(Name = "Pedal OSC", ShortName = "PedalOSC", Author = "WDE",
                 InputCount = 1, OutputCount = 1)]
    public class PedalOscMachine : IBuzzMachine, IDisposable
    {
        // ---- wire config (compile-time for now) ----
        const string AddrVersion   = "/rebuzz/v";
        const string AddrRms       = "/rebuzz/rms";
        const string AddrPeak      = "/rebuzz/peak";
        const string AddrBeat      = "/rebuzz/beat";
        const string AddrBar       = "/rebuzz/bar";
        const string AddrBpm       = "/rebuzz/bpm";
        const string AddrPlaying   = "/rebuzz/playing";
        const string AddrBeatsBar  = "/rebuzz/beatsperbar";

        const float  SchemaVersion = 1f;          // bump when the frame's meaning changes
        const string DestHost      = "127.0.0.1"; // loopback; LAN is a later phase
        const int    DestPort      = 9000;
        const int    SendHz        = 125;         // sender rate (< the ~188 Hz Work rate)

        // ReBuzz samples are +/-32768 float. Confirmed in the engine source: the master output
        // stage scales by audioOutMul = 1/32768.0f (WorkManager).
        const float  SampleScale   = 32768f;

        // ---- parameters (ReBuzz requires at least one, or the machine fails to load) ----

        [ParameterDecl(Name = "Sensitivity", Description = "Scales level before sending (64 = x1.0).",
                       MinValue = 0, MaxValue = 127, DefValue = 64)]
        public int Sensitivity { get; set; } = 64;

        [ParameterDecl(Name = "Smooth", Description = "One-pole smoothing on the sent level (0 = none).",
                       MinValue = 0, MaxValue = 127, DefValue = 0)]
        public int Smooth { get; set; } = 0;

        [ParameterDecl(Name = "Beats/Bar", Description = "Beats per bar, for the bar-phase output.",
                       MinValue = 1, MaxValue = 16, DefValue = 4)]
        public int BeatsPerBar { get; set; } = 4;

        // ------------------------------------------------------------------
        // Feature frame. Published by the audio thread, consumed by the sender.
        //
        // A small ring of pre-allocated slots + a volatile "newest completed" index gives a
        // lock-free, allocation-free SPSC handoff, and keeps all values of one audio block
        // consistent with each other (a set of independent volatile floats could tear across
        // blocks). The sender copies the struct in one go; with 4 slots and the writer only
        // ~1.5x faster than the reader, a slot cannot be overwritten mid-copy in practice.
        // ------------------------------------------------------------------
        struct FeatureFrame
        {
            public float Rms;
            public float Peak;
            public float BeatPhase;   // 0..1 within the current beat
            public float BarPhase;    // 0..1 within the current bar
            public float Bpm;
            public float Playing;     // 0 or 1
            public float BeatsPerBar;
        }

        const int SlotCount = 4;                  // power of two, for the & mask
        readonly FeatureFrame[] _slots = new FeatureFrame[SlotCount];
        volatile int _newest = 0;

        float _smoothed;                          // audio-thread only (smoothing state)

        readonly IBuzzMachineHost host;

        // Sender thread + socket. ALL network I/O lives here - never in Work().
        Thread?    _sender;
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
        // AUDIO THREAD. Pass audio through untouched, measure level, read transport,
        // publish one feature frame. No allocation, no locks, no I/O.
        // ------------------------------------------------------------------
        public bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)
        {
            // --- transport / tempo -------------------------------------------------
            // MasterInfo is only valid inside Work and parameter setters, and ReBuzz refreshes
            // it before each Work batch (WorkManager.UpdateMasterAndSubTickInfoToHost).
            float beatPhase = 0f, barPhase = 0f, bpm = 0f, playing = 0f;
            int beatsPerBar = BeatsPerBar < 1 ? 1 : BeatsPerBar;

            MasterInfo mi = host.MasterInfo;
            if (mi != null && mi.TicksPerBeat > 0 && mi.SamplesPerTick > 0)
            {
                bpm = mi.BeatsPerMin;

                // Absolute song position in ticks. Every getter on this path is a plain
                // field read (MachineCore.Graph, SongCore.Buzz, ReBuzzCore.Song,
                // SongCore.PlayPosition), so it is cheap and safe from the audio thread.
                int tick = 0;
                IBuzz? buzz = host.Machine?.Graph?.Buzz;
                if (buzz != null)
                {
                    playing = buzz.Playing ? 1f : 0f;
                    ISong? song = buzz.Song;
                    if (song != null) tick = song.PlayPosition;
                }

                // MasterInfo gives position *within* a tick; PlayPosition gives which tick.
                // Together they interpolate a continuous phase.
                float posInTick = (float)mi.PosInTick / mi.SamplesPerTick;

                int tpb = mi.TicksPerBeat;
                int tickInBeat = ((tick % tpb) + tpb) % tpb;          // % is sign-preserving in C#
                beatPhase = (tickInBeat + posInTick) / tpb;

                int ticksPerBar = tpb * beatsPerBar;
                int tickInBar = ((tick % ticksPerBar) + ticksPerBar) % ticksPerBar;
                barPhase = (tickInBar + posInTick) / ticksPerBar;
            }

            // --- audio -------------------------------------------------------------
            // input is null when nothing upstream is connected/active. Testing it directly
            // lets nullable flow-analysis prove input is non-null in the loop below.
            float rms = 0f, peak = 0f;
            bool wroteSignal;

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

            // --- publish -----------------------------------------------------------
            int next = (_newest + 1) & (SlotCount - 1);
            _slots[next].Rms         = _smoothed;
            _slots[next].Peak        = peak;
            _slots[next].BeatPhase   = beatPhase;
            _slots[next].BarPhase    = barPhase;
            _slots[next].Bpm         = bpm;
            _slots[next].Playing     = playing;
            _slots[next].BeatsPerBar = beatsPerBar;
            _newest = next;                       // volatile write publishes the slot

            return wroteSignal;
        }

        // ------------------------------------------------------------------
        // SENDER THREAD. Copy the newest frame, encode one bundle, fire one UDP packet.
        // ------------------------------------------------------------------
        void SenderLoop()
        {
            int periodMs = Math.Max(1, 1000 / SendHz);
            var msgs = new (string, float)[8];    // reused; sender thread only

            while (_running)
            {
                UdpClient? udp = _udp;            // snapshot (may be nulled by Dispose)
                if (udp == null) break;

                FeatureFrame f = _slots[_newest]; // volatile read, then one struct copy

                msgs[0] = (AddrVersion,  SchemaVersion);
                msgs[1] = (AddrRms,      f.Rms);
                msgs[2] = (AddrPeak,     f.Peak);
                msgs[3] = (AddrBeat,     f.BeatPhase);
                msgs[4] = (AddrBar,      f.BarPhase);
                msgs[5] = (AddrBpm,      f.Bpm);
                msgs[6] = (AddrPlaying,  f.Playing);
                msgs[7] = (AddrBeatsBar, f.BeatsPerBar);

                try
                {
                    byte[] pkt = OscEncoder.EncodeBundle(msgs);
                    udp.Send(pkt, pkt.Length);
                }
                catch { /* transient send errors must not kill the thread */ }

                Thread.Sleep(periodMs);
            }
        }

        // ------------------------------------------------------------------
        // Teardown. Confirmed against the engine source: deleting a machine runs
        // MachineManager.DeleteMachine -> ManagedMachineHost.Release(), which calls
        // Dispose() on machines implementing IDisposable. So this reliably stops the
        // sender on removal; IsBackground covers process exit as a backstop.
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
