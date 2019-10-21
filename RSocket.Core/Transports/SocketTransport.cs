using System;
using System.Net;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Buffers;

namespace RSocket.Transports
{
    //TODO Readd transport logging - worth it during debugging.
    public abstract class SocketTransport
    {
        //internal Task Running { get; private set; } = Task.CompletedTask;
        //private CancellationTokenSource Cancellation;

#pragma warning disable CS0649
        private volatile bool Aborted;      //TODO Implement cooperative cancellation (and remove warning suppression)
#pragma warning restore CS0649

        private LoggerFactory Logger;

        protected IDuplexPipe Front, Back;

        public PipeReader Input => Front.Input;
        public PipeWriter Output => Front.Output;

        public SocketTransport(PipeOptions outputOptions = null, PipeOptions inputOptions = null)
        {
            (Front, Back) = DuplexPipe.CreatePair(outputOptions, inputOptions);
        }

        public abstract Task StartAsync(CancellationToken cancel = default);

        public Task StopAsync() => Task.CompletedTask;

        protected async Task ProcessSocketAsync(Socket socket)
        {
            var receiving = StartReceiving(socket);
            var sending = StartSending(socket);

            await Task.WhenAny(receiving, sending);
        }

        private async Task StartReceiving(Socket socket)
        {
            var token = default(CancellationToken); //Cancellation?.Token ?? default;

            try
            {
                while (!token.IsCancellationRequested)
                {
#if NETSTANDARD2_1
                    // Do a 0 byte read so that idle connections don't allocate a buffer when waiting for a read
                    var received = await socket.ReceiveAsync(Memory<byte>.Empty, SocketFlags.None, token);
                    //if(received == 0) { continue; }
                    var memory = Back.Output.GetMemory(out var memoryFrame, haslength: true);    //RSOCKET Framing
                    received = await socket.ReceiveAsync(memory, SocketFlags.None, token);
#else
					var memory = Back.Output.GetMemory(out var memoryframe, haslength: true);    //RSOCKET Framing
					var isArray = MemoryMarshal.TryGetArray<byte>(memory, out var arraySegment); Debug.Assert(isArray);
					var received = await socket.ReceiveAsync(arraySegment, SocketFlags.None);   //TODO Cancellation?
#endif
                    //Log.MessageReceived(_logger, receive.MessageType, receive.Count, receive.EndOfMessage);
                    Back.Output.Advance(received);
                    var flushResult = await Back.Output.FlushAsync();
                    if (flushResult.IsCanceled || flushResult.IsCompleted) { break; }
                }
            }
            //catch (SocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
            //{
            //	// Client has closed the WebSocket connection without completing the close handshake
            //	Log.ClosedPrematurely(_logger, ex);
            //}
            catch (OperationCanceledException)
            {
                // Ignore aborts, don't treat them like transport errors
            }
            catch (Exception ex)
            {
                if (!Aborted && !token.IsCancellationRequested) { Back.Output.Complete(ex); throw; }
            }
            finally { Back.Output.Complete(); }
        }

        private async Task StartSending(Socket socket)
        {
            Exception error = null;

            try
            {
                while (true)
                {
                    var result = await Back.Input.ReadAsync();
                    var buffer = result.Buffer;
                    var consumed = buffer.Start;        //RSOCKET Framing

                    try
                    {
                        if (result.IsCanceled) { break; }
                        if (!buffer.IsEmpty)
                        {
                            try
                            {
                                //Log.SendPayload(_logger, buffer.Length);
                                consumed = await socket.SendAsync(buffer, buffer.Start, SocketFlags.None);     //RSOCKET Framing
                            }
                            catch (Exception)
                            {
                                if (!Aborted) { /*Log.ErrorWritingFrame(_logger, ex);*/ }
                                break;
                            }
                        }
                        else if (result.IsCompleted) { break; }
                    }
                    finally
                    {
                        Back.Input.AdvanceTo(consumed, buffer.End);     //RSOCKET Framing
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                //// Send the close frame before calling into user code
                //if (WebSocketCanSend(socket))
                //{
                //	// We're done sending, send the close frame to the client if the websocket is still open
                //	await socket.CloseOutputAsync(error != null ? WebSocketCloseStatus.InternalServerError : WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                //}
                Back.Input.Complete();
            }

        }
    }
}


namespace System.Net.Sockets
{
    internal static class SocketExtensions
    {
        public static ValueTask<int> SendAsync(this Socket socket, ReadOnlySequence<byte> buffer, SocketFlags socketFlags, CancellationToken cancellationToken = default)
        {
#if NETSTANDARD2_1
            if (buffer.IsSingleSegment)
            {
                return socket.SendAsync(buffer.First, socketFlags, cancellationToken);
            }
            else { return SendMultiSegmentAsync(socket, buffer, socketFlags, cancellationToken); }
#else
			if (buffer.IsSingleSegment)
			{
				var isArray = MemoryMarshal.TryGetArray(buffer.First, out var segment);
				Debug.Assert(isArray);
				return new ValueTask(socket.SendAsync(segment, socketFlags));       //TODO Cancellation?
			}
			else { return SendMultiSegmentAsync(socket, buffer, socketFlags, cancellationToken); }
#endif
        }

        static async ValueTask<int> SendMultiSegmentAsync(Socket socket, ReadOnlySequence<byte> buffer, SocketFlags socketFlags, CancellationToken cancellationToken = default)
        {
#if NETSTANDARD2_1
            var position = buffer.Start;
            buffer.TryGet(ref position, out var prevSegment);
            while (buffer.TryGet(ref position, out var segment))
            {
                await socket.SendAsync(prevSegment, socketFlags);
                prevSegment = segment;
            }
            return await socket.SendAsync(prevSegment, socketFlags);
#else
			var position = buffer.Start;
			buffer.TryGet(ref position, out var prevSegment);
			while (buffer.TryGet(ref position, out var segment))
			{
				var isArray = MemoryMarshal.TryGetArray(prevSegment, out var arraySegment);
				Debug.Assert(isArray);
				await socket.SendAsync(arraySegment, socketFlags);
				prevSegment = segment;
			}
			var isArrayEnd = MemoryMarshal.TryGetArray(prevSegment, out var arraySegmentEnd);
			Debug.Assert(isArrayEnd);
			await socket.SendAsync(arraySegmentEnd, socketFlags);
#endif
        }
    }
}
