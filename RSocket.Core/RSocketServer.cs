using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RSocket
{
    public class RSocketServer : RSocket
    {
        Task Handler;
        RSocketOptions Options { get; set; }

        public RSocketServer(IRSocketTransport transport, RSocketOptions options = default) : base(transport, options) { }

        public async Task ConnectAsync()
        {
            await Transport.StartAsync();
            Handler = Connect(CancellationToken.None);
        }

        public override void Setup(in RSocketProtocol.Setup value)
        {

        }
    }
}
