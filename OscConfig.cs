// OscConfig.cs — part of Pedal OSC
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
using System.Net.Sockets;

namespace WDE.PedalOsc
{
    /// <summary>
    /// Owns the UDP socket and re-targets it live when the destination changes. Shared by both
    /// bridge machines. The sender thread calls <see cref="Retarget"/> with the current endpoint
    /// each pass (cheap - it only reopens on an actual change), then <see cref="Send"/>.
    ///
    /// Why not open in the machine constructor: the destination is driven by parameters (below),
    /// and while defaults are available at construction, re-opening on change is required
    /// anyway, so the socket simply starts closed and opens on the first Retarget.
    ///
    /// Runtime configuration is parameter-driven rather than via MachineState. ReBuzz persists
    /// parameters with the song, so the endpoint saves and restores with the project for free,
    /// and - unlike MachineState, which can only be set by loading a file or a text-field GUI -
    /// parameters are editable live from the machine's parameter window. The cost is that an IP
    /// must be entered as four octet parameters and the port as an offset (§ addendum); a
    /// full arbitrary host/port/prefix would need a GUI, deferred.
    /// </summary>
    public sealed class OscSender : IDisposable
    {
        UdpClient? _udp;
        string _host = "";
        int _port = -1;
        volatile bool _disposed;

        /// <summary>Reopen the socket if host/port changed. Safe to call every send.</summary>
        public void Retarget(string host, int port)
        {
            if (_disposed) return;
            if (_udp != null && host == _host && port == _port) return;

            UdpClient? old = _udp;
            _udp = null;
            try { old?.Close(); } catch { }

            _host = host;
            _port = port;

            if (string.IsNullOrEmpty(host) || port <= 0 || port > 65535) return;

            try
            {
                var udp = new UdpClient();
                udp.Connect(host, port);      // resolve once; may throw on a bad host string
                _udp = udp;
            }
            catch
            {
                _udp = null;                  // stay dark until the next valid endpoint
            }
        }

        public void Send(byte[] packet, int length)
        {
            UdpClient? udp = _udp;            // snapshot
            if (udp == null) return;
            try { udp.Send(packet, length); }
            catch { /* transient send errors must not kill the caller */ }
        }

        public void Dispose()
        {
            _disposed = true;
            try { _udp?.Close(); } catch { }
            _udp = null;
        }
    }

    /// <summary>
    /// Builds an endpoint from the octet/port parameters both machines expose. Parameters cannot
    /// hold strings, so the destination IP is four octets (0..254; 255 collides with the Byte
    /// NoValue sentinel, Core §9) and the port is an offset from a base.
    /// </summary>
    public static class OscEndpoint
    {
        public const int BasePort = 9000;
        public const string Loopback = "127.0.0.1";

        /// <summary>
        /// All-zero octets mean "unset" and resolve to loopback, so the zero-config default
        /// (a fresh machine, all params at 0) sends to 127.0.0.1 with no setup.
        /// </summary>
        public static string Host(int a, int b, int c, int d)
            => (a == 0 && b == 0 && c == 0 && d == 0)
                ? Loopback
                : a + "." + b + "." + c + "." + d;

        /// <summary>Port offset 0..127 maps to BasePort..BasePort+127.</summary>
        public static int Port(int offset) => BasePort + (offset < 0 ? 0 : offset);
    }
}
