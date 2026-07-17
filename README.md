# Pedal OSC

A ReBuzz managed **effect** machine that taps the audio passing through it,
measures its level, and streams that value out over **OSC/UDP** to an external
program — for driving audio-reactive visuals (shaders, VJ tools, lighting) from
inside ReBuzz.

It is **Phase 1 of a ReBuzz → real-time video bridge**: numbers leave the host
over OSC and drive shader uniforms in a separate OpenGL renderer.

## What it does

- Passes audio through unchanged (insert it anywhere; conventionally just before
  **Master** to tap the full mix).
- Measures per-block RMS, applies **Sensitivity** and **Smooth**, and sends the
  result as a single OSC float.
- All network I/O runs on a background sender thread — **never** on the audio
  thread.

## OSC output

| Field     | Value                                   |
|-----------|-----------------------------------------|
| Address   | `/rebuzz/rms`                           |
| Argument  | one big-endian `float32`, ~0..1         |
| Transport | UDP to `127.0.0.1:9000`                 |
| Rate      | ~125 messages/sec                       |

Host, port, and address are currently compile-time constants in `PedalOsc.cs`.

## Parameters

- **Sensitivity** (0–127, default 64 = ×1.0) — scales the level before sending.
- **Smooth** (0–127, default 0 = raw) — one-pole smoothing on the sent value.

## Build & deploy

Requires the .NET 10 SDK and a ReBuzz install providing `ReBuzz.dll` +
`BuzzGUI.Interfaces.dll`.

```powershell
dotnet build .\pedal-osc.csproj -c Release
```

The build writes `Pedal OSC.NET.dll` straight into `%BuzzDir%\Gear\Effects`
(default `C:\Program Files\ReBuzz`; override with `-p:BuzzDir=...`). Restart
ReBuzz to pick up a newly added machine.

## Status

Phase-1 spike — single-scalar output. Roadmap: a fuller feature frame (FFT
bands, BPM, beat phase, MIDI events) and OSC over LAN to a separate render
machine.

## Licence

GPL-3.0 (house default). See `LICENSE`.
