// SPDX-License-Identifier: GPL-3.0-only
// Flint test infrastructure (no upstream counterpart): the PortAllocator/PortLease pair —
// the single allocation source for test TCP ports (16 call sites converged here).

using System.Net;
using System.Net.Sockets;

namespace FlintLauncher.Tests;

/// <summary>
/// Single allocation source for free TCP ports in tests. <see cref="Reserve"/> binds a
/// <see cref="TcpListener"/> on loopback port 0 and keeps it listening: the port stays held
/// until the caller's real listener takes it over (or the lease is released), closing the
/// probe-then-rebind window the per-class GetFreePort() copies had — between the probe's
/// Stop() and the real listener's Start() any peer test class (xunit parallelizes classes)
/// or a TIME_WAIT entry could yield "address already in use".
/// </summary>
internal static class PortAllocator
{
    /// <summary>Bind an ephemeral loopback port and hold it for the caller.</summary>
    public static PortLease Reserve() => new();

    /// <summary>
    /// Start an <see cref="HttpListener"/> on a freshly reserved loopback port. HttpListener
    /// cannot adopt the lease's socket (the documented HttpListener case of
    /// <see cref="PortLease"/>), so the takeover keeps a Stop→Start rebind window; under a
    /// loaded run a peer listener or a TIME_WAIT entry can take the freed port in that window
    /// ("address already in use" — observed once per several full-suite runs on the gate).
    /// The start therefore RETRIES on a fresh lease: every attempt gets a brand-new port, so
    /// losing the window delays the fixture start instead of failing it.
    /// </summary>
    public static (HttpListener Listener, string Url) StartHttpListener()
    {
        var listener = new HttpListener();
        for (var attempt = 1;; attempt++)
        {
            string url;
            using (var lease = Reserve())
            {
                url = $"http://127.0.0.1:{lease.Port}";
            }

            listener.Prefixes.Clear();
            listener.Prefixes.Add(url + "/");
            try
            {
                listener.Start();
                return (listener, url);
            }
            catch (HttpListenerException) when (attempt < 8)
            {
                // the freed port was taken inside the rebind window — next lease, next port
            }
        }
    }

    /// <summary>A held reservation of a free loopback port.</summary>
    internal sealed class PortLease : IDisposable
    {
        private TcpListener? listener;

        public PortLease()
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        /// <summary>The reserved port, still held by the lease.</summary>
        public int Port { get; }

        /// <summary>
        /// Take over the reservation: hands the caller the already-bound, already-listening
        /// TcpListener itself, so no Stop/rebind — and therefore no window — ever happens.
        /// The lease becomes inert and its Dispose is a no-op afterwards. For listeners that
        /// cannot adopt a socket (HttpListener), dispose the lease immediately before Start().
        /// </summary>
        public TcpListener TakeListener()
        {
            ObjectDisposedException.ThrowIf(this.listener is null, this);
            var listener = this.listener;
            this.listener = null;
            return listener;
        }

        public void Dispose()
        {
            listener?.Stop();
            listener = null;
        }
    }
}
