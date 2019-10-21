using System;
using System.Buffers;

namespace RSocket
{
    /// <summary>
    /// Defines a handler for the raw protocol. Clients and Servers both implement parts of this interface; when they do not, they should throw NotImplementedException for that method.
    /// </summary>
    public interface IRSocketProtocol
    {
        void Setup(in RSocketProtocol.Setup message);
        void Error(in RSocketProtocol.Error message);
        void Payload(in RSocketProtocol.Payload message, RSocketFrame frame);
        void RequestStream(in RSocketProtocol.RequestStream message, RSocketFrame frame);
        void RequestResponse(in RSocketProtocol.RequestResponse message, RSocketFrame frame);
        void RequestFireAndForget(in RSocketProtocol.RequestFireAndForget message, RSocketFrame frame);
        void RequestChannel(in RSocketProtocol.RequestChannel message, RSocketFrame frame);
    }
}
