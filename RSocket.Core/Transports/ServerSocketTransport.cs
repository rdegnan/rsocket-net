using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RSocket.Transports
{
    public class ServerSocketTransport : SocketTransport, IRSocketServerTransport
    {
        private Socket _remoteSocket;

        public ServerSocketTransport(Socket remoteSocket, PipeOptions outputOptions = null, PipeOptions inputOptions = null)
            : base(outputOptions, inputOptions)
        {
            _remoteSocket = remoteSocket;
        }

        public override Task StartAsync(CancellationToken cancel = default)
        {
            _ = ProcessSocketAsync(_remoteSocket);

            return Task.CompletedTask;
        }
    }
}
