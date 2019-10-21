using RSocket.Transports;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RSocket
{
    public class RSocketAcceptor
    {
        private Socket _listener;
        private IPEndPoint _endpoint;

        public Uri Url { get; private set; }

        public RSocketAcceptor(Uri url)
        {
            Url = url;

            if (string.Compare(url.Scheme, "TCP", true) != 0)
                throw new ArgumentException("Only TCP connections are supported.", nameof(Url));

            if (url.Port == -1)
                throw new ArgumentException("TCP Port must be specified.", nameof(Url));
        }

        public async IAsyncEnumerable<IRSocketServerTransport> AcceptAsync([EnumeratorCancellation]CancellationToken cancel = default)
        {
            var dns = await Dns.GetHostEntryAsync(Url.Host);
            if (dns.AddressList.Length == 0)
                throw new InvalidOperationException($"Unable to resolve address.");

            _endpoint = new IPEndPoint(dns.AddressList[0], Url.Port);

            _listener = new Socket(_endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            _listener.Bind(_endpoint);
            _listener.Listen(10);

            while (!cancel.IsCancellationRequested)
            {
                var clientSocket = await _listener.AcceptAsync();
                clientSocket.NoDelay = true;

                yield return new ServerSocketTransport(clientSocket);
            }
        }
    }
}
