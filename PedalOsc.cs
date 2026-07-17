using System;
using System.Net.Sockets;
using System.Threading;
using Buzz.MachineInterface;   // IBuzzMachine, IBuzzMachineHost, MachineDecl, ParameterDecl, Sample, WorkModes

namespace WDE.PedalOsc
{
    [MachineDecl(Name = "Pedal OSC", ShortName = "PedalOSC", Author = "WDE",
                 InputCount = 1, OutputCount = 1)]
    public class PedalOscMachine : IBuzzMachine, IDisposable
    {
        // ---- spike config (hard-coded now; promote to params later if useful) ----
        const string OscAddress  = "/rebuzz/rms";
        const string DestHost    = "127.0.0.1";   // loopback for the spike — no LAN yet
        const int    DestPort    = 9000;
        const int    SendHz      = 125;           // drain-thread send rate (< the ~188 Hz Work rate)

        // Native sample domain. ReBuzz's internal samples are +/-32768 float (confirmed in the
        // project notes: PedalComp §1), so normalise by this to land the sent value in ~0..1.
        const float  SampleScale = 32768f;

        // ---- parameters ----
        // ReBuzz REQUIRES at least one [ParameterDecl] or LoadManagedMachine throws
        // "at least one parameter is required" and the machine never enters the browser.
        // Both are plain 0..127 ints -> Byte params (MaxValue <= 254 avoids the NoValue
        // sentinel; MinValue >= 0 avoids the silent range-offset). Setters and Work() both
        // run on the audio thread (CallTick), so reading them in Work() needs no locking.

        [ParameterDecl(Name = "Sensitivity", Description = "Scales RMS before sending (64 = x1.0).",
                       MinValue = 0, MaxValue = 127, DefValue = 64)]
        public int Sensitivity { get; set; } = 64;

        [ParameterDecl(Name = "Smooth", Description = "One-pole smoothing on the sent value (0 = none).",
                       MinValue = 0, MaxValue = 127, DefValue = 0)]
        public int Smooth { get; set; } = 0;

        // Latest value: written on the audio thread, read on the sender thread (last-writer-wins).
        volatile float _latestRms;
        float _smoothed;                          // audio-thread only (smoothing state)

        // Sender thread + socket. ALL network I/O lives here — never in Work().
        Thread?    _sender;
        volatile bool _running;
        UdpClient? _udp;

        public PedalOscMachine(IBuzzMachineHost host)
        {
            // host is unused in this spike. Store it when you add host-driven features
            // (MasterInfo for BPM / beat phase, transport state, MsToSamples, etc.).

            _udp = new UdpClient();
            _udp.Connect(DestHost, DestPort);     // resolve the endpoint once, reuse per send

            _running = true;
            _sender = new Thread(SenderLoop) { IsBackground = true, Name = "PedalOscSender" };
            _sender.Start();
        }

        // ------------------------------------------------------------------
        // AUDIO THREAD. Pass input straight to output, measure block RMS, apply
        // Sensitivity + Smooth, publish for the sender. No allocation / lock / I/O here.
        // ------------------------------------------------------------------
        public bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)
        {
            // input is null when nothing upstream is connected/active. Testing it directly
            // lets nullable flow-analysis prove input is non-null in the loop below.
            if (input == null || n <= 0)
            {
                if (output != null)
                    for (int i = 0; i < n; i++) { output[i].L = 0f; output[i].R = 0f; }
                _smoothed  = 0f;
                _latestRms = 0f;
                return false;   // silent
            }

            double sumSq = 0.0;
            for (int i = 0; i < n; i++)
            {
                float l = input[i].L;
                float r = input[i].R;
                output[i].L = l;                  // true pass-through
                output[i].R = r;
                sumSq += (double)l * l + (double)r * r;
            }

            // Mean energy across both channels -> RMS -> normalise.
            float rms = (float)Math.Sqrt(sumSq / (2.0 * n)) / SampleScale;
            rms *= Sensitivity / 64f;             // 64 = x1.0
            if (rms > 1f) rms = 1f;

            // Block-rate one-pole smoothing: Smooth 0 -> coef 1 (raw), 127 -> coef ~0.02 (heavy).
            float coef = 1f - (Smooth / 127f) * 0.98f;
            _smoothed += coef * (rms - _smoothed);
            _latestRms = _smoothed;

            return true;   // we wrote signal
        }

        // ------------------------------------------------------------------
        // SENDER THREAD. Snapshot the latest value, encode, fire one UDP packet.
        // ------------------------------------------------------------------
        void SenderLoop()
        {
            int periodMs = Math.Max(1, 1000 / SendHz);
            while (_running)
            {
                UdpClient? udp = _udp;            // snapshot (may be nulled by Dispose)
                if (udp == null) break;

                float rms = _latestRms;           // last-writer-wins snapshot
                try
                {
                    byte[] pkt = OscEncoder.EncodeFloat(OscAddress, rms);
                    udp.Send(pkt, pkt.Length);
                }
                catch { /* spike: swallow transient send errors, keep the thread alive */ }
                Thread.Sleep(periodMs);
            }
        }

        // ------------------------------------------------------------------
        // Best-effort teardown. If ReBuzz calls Dispose() on removal, the sender stops cleanly;
        // otherwise it's a background thread and dies with the process on exit. PRODUCTION TODO:
        // wire the confirmed machine-removed hook so a mid-session delete stops it immediately.
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
