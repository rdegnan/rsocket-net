using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RSocket
{
    partial class RSocketProtocol
    {
        //TODO Consider if this is really needed. Could always wrap a logging protocol handler.
        static void Decoded(string message) => Console.WriteLine(message);
        static void OnSetup(IRSocketProtocol sink, in Setup message) => sink.Setup(message);
        static void OnError(IRSocketProtocol sink, in Error message) => sink.Error(message);
        static void OnPayload(IRSocketProtocol sink, in Payload message, RSocketFrame frame) => sink.Payload(message, frame);
        static void OnRequestStream(IRSocketProtocol sink, in RequestStream message, RSocketFrame frame) => sink.RequestStream(message, frame);
        static void OnRequestResponse(IRSocketProtocol sink, in RequestResponse message, RSocketFrame frame) => sink.RequestResponse(message, frame);
        static void OnRequestFireAndForget(IRSocketProtocol sink, in RequestFireAndForget message, RSocketFrame frame) => sink.RequestFireAndForget(message, frame);
        static void OnRequestChannel(IRSocketProtocol sink, in RequestChannel message, RSocketFrame frame) => sink.RequestChannel(message, frame);
        static public async Task Handler(IRSocketProtocol sink, PipeReader pipereader, CancellationToken cancellation)
        {
            //The original implementation was a state-machine parser with resumability. It doesn't seem like the other implementations follow this pattern and the .NET folks are still figuring this out too - see the internal JSON parser discussion for how they're handling state machine persistence across async boundaries when servicing a Pipeline. So, this version of the handler only processes complete messages at some cost to memory buffer scalability.
            //Note that this means that the Pipeline must be configured to have enough buffering for a complete message before source-quenching. This also means that the downstream consumers don't really have to support resumption, so the interface no longer has the partial buffer methods in it.
            try
            {
                while (true)
                {
                    var read = await pipereader.ReadAsync(cancellation);
                    var buffer = read.Buffer;

                    while (TryParseMessage(ref buffer, out int frameLength, out var payload))
                    {
                        await Process(frameLength, payload);
                    }

                    if (read.IsCompleted)
                    {
                        if (!buffer.IsEmpty)
                        {
                            // Partial frame received and there's no more data coming
                            sink.Error(new Error(ErrorCodes.Connection_Error));
                        }
                        break;
                    }

                    pipereader.AdvanceTo(buffer.Start, buffer.End);
                    // TODO UNIT TEST- this should work now too!!! Need to evaluate if there is more than one packet in the pipe 
                    // including edges like part of the length bytes are there but not all.
                }
            }
            finally
            {
                pipereader.Complete();
            }

            //This is the non-async portion of the handler. SequenceReader<T> and the other stack-allocated items cannot be used in an async context.
            Task Process(int framelength, in ReadOnlySequence<byte> sequence)
            {
                var reader = new SequenceReader<byte>(sequence);
                var header = new Header(ref reader, framelength);

                switch (header.Type)
                {
                    case Types.Reserved: throw new InvalidOperationException($"Protocol Reserved! [{header.Type}]");
                    case Types.Setup:
                        var setup = new Setup(header, ref reader);
                        OnSetup(sink, setup);   //TODO These can have metadata! , setup.ReadMetadata(ref reader), setup.ReadData(ref reader)););
                        break;
                    case Types.Lease:
                        var lease = new Lease(header, ref reader);
                        break;
                    case Types.KeepAlive:
                        var keepalive = new KeepAlive(header, ref reader);
                        break;
                    case Types.Request_Response:
                        var requestresponse = new RequestResponse(header, ref reader);
                        if (requestresponse.Validate()) { OnRequestResponse(sink, requestresponse, new RSocketFrame(requestresponse.ReadMetadata(reader), requestresponse.ReadData(reader))); }
                        break;
                    case Types.Request_Fire_And_Forget:
                        var requestfireandforget = new RequestFireAndForget(header, ref reader);
                        if (requestfireandforget.Validate()) { OnRequestFireAndForget(sink, requestfireandforget, new RSocketFrame(requestfireandforget.ReadMetadata(reader), requestfireandforget.ReadData(reader))); }
                        break;
                    case Types.Request_Stream:
                        var requeststream = new RequestStream(header, ref reader);
                        if (requeststream.Validate()) { OnRequestStream(sink, requeststream, new RSocketFrame(requeststream.ReadMetadata(reader), requeststream.ReadData(reader))); }
                        break;
                    case Types.Request_Channel:
                        var requestchannel = new RequestChannel(header, ref reader);
                        if (requestchannel.Validate()) { OnRequestChannel(sink, requestchannel, new RSocketFrame(requestchannel.ReadMetadata(reader), requestchannel.ReadData(reader))); }
                        break;
                    case Types.Request_N:
                        var requestne = new RequestN(header, ref reader);
                        break;
                    case Types.Cancel:
                        var cancel = new Cancel(header, ref reader);
                        break;
                    case Types.Payload:
                        var payload = new Payload(header, ref reader);
                        //Decoded(payload.ToString());
                        if (payload.Validate()) { OnPayload(sink, payload, new RSocketFrame(payload.ReadMetadata(reader), payload.ReadData(reader))); }
                        break;
                    case Types.Error:
                        var error = new Error(header, ref reader);
                        //Decoded(error.ToString());
                        OnError(sink, error);
                        break;
                    case Types.Metadata_Push:
                        var metadatapush = new MetadataPush(header, ref reader);
                        break;
                    case Types.Resume: { throw new NotSupportedException($"Protocol Resumption not Supported. [{header.Type}]"); }
                    case Types.Resume_OK: { throw new NotSupportedException($"Protocol Resumption not Supported. [{header.Type}]"); }
                    case Types.Extension: if (!header.CanIgnore) { throw new InvalidOperationException($"Protocol Extension Unsupported! [{header.Type}]"); } else break;
                    default: if (!header.CanIgnore) { throw new InvalidOperationException($"Protocol Unknown Type! [{header.Type}]"); } else break;
                }
                return Task.CompletedTask;
            }
        }
    }
}
