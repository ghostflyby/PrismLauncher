using System.Net;
using System.Net.Sockets;

namespace Flint.Tests;

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

    /// <summary>A held reservation of a free loopback port.</summary>
    internal sealed class PortLease : IDisposable
    {
        private TcpListener? _listener;

        public PortLease()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
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
            ObjectDisposedException.ThrowIf(_listener is null, this);
            var listener = _listener;
            _listener = null;
            return listener;
        }

        public void Dispose()
        {
            _listener?.Stop();
            _listener = null;
        }
    }
}
