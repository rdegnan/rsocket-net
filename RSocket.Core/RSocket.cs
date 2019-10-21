using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using IRSocketStream = System.IObserver<RSocket.RSocketFrame>;

namespace RSocket
{
	public interface IRSocketChannel
	{
		Task Send(RSocketFrame value);
	}

	public partial class RSocket : IRSocketProtocol
	{
		public const int INITIALDEFAULT = int.MaxValue;
		RSocketOptions Options { get; set; }

		//TODO Hide.
		public IRSocketTransport Transport { get; set; }
		private int StreamId = 1 - 2;       //SPEC: Stream IDs on the client MUST start at 1 and increment by 2 sequentially, such as 1, 3, 5, 7, etc
		private int NewStreamId() => Interlocked.Add(ref StreamId, 2);  //TODO SPEC: To reuse or not... Should tear down the client if this happens or have to skip in-use IDs.

		private ConcurrentDictionary<int, IRSocketStream> Dispatcher = new ConcurrentDictionary<int, IRSocketStream>();
		private int StreamDispatch(int id, IRSocketStream transform) { Dispatcher[id] = transform; return id; }
		private int StreamDispatch(IRSocketStream transform) => StreamDispatch(NewStreamId(), transform);
		//TODO Stream Destruction - i.e. removal from the dispatcher.

		protected IDisposable ChannelSubscription;      //TODO Tracking state for channels

		public RSocket(IRSocketTransport transport, RSocketOptions options = default)
		{
			Transport = transport;
			Options = options ?? RSocketOptions.Default;
		}

		/// <summary>Binds the RSocket to its Transport and begins handling messages.</summary>
		/// <param name="cancel">Cancellation for the handler. Requesting cancellation will stop message handling.</param>
		/// <returns>The handler task.</returns>
		public Task Connect(CancellationToken cancel = default) => RSocketProtocol.Handler(this, Transport.Input, cancel);
		public Task Setup(TimeSpan keepalive, TimeSpan lifetime, string metadataMimeType = null, string dataMimeType = null, ReadOnlySequence<byte> data = default, ReadOnlySequence<byte> metadata = default) => new RSocketProtocol.Setup(keepalive, lifetime, metadataMimeType: metadataMimeType, dataMimeType: dataMimeType, data: data, metadata: metadata).WriteFlush(Transport.Output, data: data, metadata: metadata);


		//TODO SPEC: A requester MUST not send PAYLOAD frames after the REQUEST_CHANNEL frame until the responder sends a REQUEST_N frame granting credits for number of PAYLOADs able to be sent.

		public virtual IAsyncEnumerable<T> RequestChannel<TSource, T>(
            IAsyncEnumerable<TSource> source, Func<TSource, ReadOnlySequence<byte>> sourcemapper,
            Func<RSocketFrame, T> resultmapper, RSocketFrame frame)
			=> new Receiver<TSource, T>(stream => RequestChannel(stream, frame), source, _ => new RSocketFrame(default, sourcemapper(_)), value => resultmapper(value));

		public async Task<IRSocketChannel> RequestChannel(IRSocketStream stream, RSocketFrame frame, int initial = RSocketOptions.INITIALDEFAULT)
		{
			var id = StreamDispatch(stream);
			await new RSocketProtocol.RequestChannel(id, frame, initialRequest: Options.GetInitialRequestSize(initial)).WriteFlush(Transport.Output, frame);
			var channel = new ChannelHandler(this, id);
			return channel;
		}

		protected class ChannelHandler : IRSocketChannel       //TODO hmmm...
		{
			readonly RSocket Socket;
			readonly int Stream;

			public ChannelHandler(RSocket socket, int stream) { Socket = socket; Stream = stream; }

			public Task Send(RSocketFrame value)
			{
				if (!Socket.Dispatcher.ContainsKey(Stream))
					throw new InvalidOperationException("Channel is closed");

				return new RSocketProtocol.Payload(Stream, value.Data, value.Metadata, next: true).WriteFlush(Socket.Transport.Output, value.Data, value.Metadata);
			}
		}

		public virtual IAsyncEnumerable<T> RequestStream<T>(Func<RSocketFrame, T> resultmapper, RSocketFrame frame)
			=> new Receiver<T>(stream => RequestStream(stream, frame), value => resultmapper(value));

		public Task RequestStream(IRSocketStream stream, RSocketFrame frame, int initial = RSocketOptions.INITIALDEFAULT)
		{
			var id = StreamDispatch(stream);
			return new RSocketProtocol.RequestStream(id, frame, initialRequest: Options.GetInitialRequestSize(initial)).WriteFlush(Transport.Output, frame);
		}

		public virtual Task<T> RequestResponse<T>(Func<RSocketFrame, T> resultmapper, RSocketFrame frame)
			=> new Receiver<T>(stream => RequestResponse(stream, frame), resultmapper).ExecuteAsync();

		public Task RequestResponse(IRSocketStream stream, RSocketFrame frame)
		{
			var id = StreamDispatch(stream);
			return new RSocketProtocol.RequestResponse(id, frame).WriteFlush(Transport.Output, frame);
		}

		public virtual Task RequestFireAndForget(RSocketFrame frame)
			=> new Receiver<bool>(stream => RequestFireAndForget(stream, frame), _ => true).ExecuteAsync(result: true);

		public Task RequestFireAndForget(IRSocketStream stream, RSocketFrame frame)
		{
			var id = StreamDispatch(stream);
			return new RSocketProtocol.RequestFireAndForget(id, frame).WriteFlush(Transport.Output, frame);
		}

		void IRSocketProtocol.Payload(in RSocketProtocol.Payload message, RSocketFrame frame)
		{
			if (Dispatcher.TryGetValue(message.Stream, out var transform))
			{
				if (message.IsNext)
					transform.OnNext(frame);

				if (message.IsComplete)
					transform.OnCompleted();
			}
			else
			{
				//TODO Log missing stream here.
				System.Diagnostics.Debugger.Break();
			}
		}

		void Schedule(int stream, Func<int, CancellationToken, Task> operation, CancellationToken cancel = default)
		{
			var task = operation(stream, cancel);

			if (!task.IsCompleted)
				task.ConfigureAwait(false); //FUTURE Someday might want to schedule these in a different pool or perhaps track all in-flight tasks.      
		}

		public virtual void Setup(in RSocketProtocol.Setup value) => throw new InvalidOperationException($"Client cannot process Setup frames");    //TODO This exception just stalls processing. Need to make sure it's handled.
		void IRSocketProtocol.Error(in RSocketProtocol.Error message) { throw new NotImplementedException(); }  //TODO Handle Errors!
		void IRSocketProtocol.RequestFireAndForget(in RSocketProtocol.RequestFireAndForget message, RSocketFrame frame) => throw new NotImplementedException();

		public void Respond<TRequest, TResult>(
            Func<RSocketFrame, TRequest> requestTransform,
            Func<TRequest, IAsyncEnumerable<TResult>> producer,
            Func<TResult, RSocketFrame> resultTransform) =>
			Responder = (request) => (from result in producer(requestTransform(request)) select resultTransform(result)).FirstAsync();

		public Func<RSocketFrame, ValueTask<RSocketFrame>> Responder { get; set; } = request => throw new NotImplementedException();

		void IRSocketProtocol.RequestResponse(in RSocketProtocol.RequestResponse message, RSocketFrame frame)
		{
			Schedule(message.Stream, async (stream, cancel) =>
			{
				var value = await Responder(frame);     //TODO Handle Errors.
				await new RSocketProtocol.Payload(stream, value.Data, value.Metadata, next: true, complete: true).WriteFlush(Transport.Output, value.Data, value.Metadata);
			});
		}

		public void Stream<TRequest, TResult>(
			Func<RSocketFrame, TRequest> requestTransform,
			Func<TRequest, IAsyncEnumerable<TResult>> producer,
			Func<TResult, RSocketFrame> resultTransform) =>
			Streamer = (request) => from result in producer(requestTransform(request)) select resultTransform(result);

		public Func<RSocketFrame, IAsyncEnumerable<RSocketFrame>> Streamer { get; set; } = request => throw new NotImplementedException();

		void IRSocketProtocol.RequestStream(in RSocketProtocol.RequestStream message, RSocketFrame frame)
		{
			Schedule(message.Stream, async (stream, cancel) => await ProcessFrames(Streamer(frame).AsAsyncEnumerable(), stream, Transport));
		}

        //TODO, probably need to have an IAE<T> pipeline overload too.
        public void Channel<TRequest, TIncoming, TOutgoing>(Func<TRequest, IObservable<TIncoming>, IAsyncEnumerable<TOutgoing>> pipeline,
            Func<RSocketFrame, TRequest> requestTransform,
            Func<RSocketFrame, TIncoming> incomingTransform,
            Func<TOutgoing, RSocketFrame> outgoingTransform) =>
            Channeler = (request, incoming) => from result in pipeline(requestTransform(request), from item in incoming select incomingTransform(item)) select outgoingTransform(result);

        public Func<RSocketFrame, IObservable<RSocketFrame>, IAsyncEnumerable<RSocketFrame>> Channeler { get; set; } = (request, incoming) => throw new NotImplementedException();

        void IRSocketProtocol.RequestChannel(in RSocketProtocol.RequestChannel message, RSocketFrame frame)
		{
			Schedule(message.Stream, async (stream, cancel) =>
			{
                var obs = Observable.Create<RSocketFrame>(observer => {
                    StreamDispatch(stream, observer);
                    return Disposable.Empty;
                });

				await ProcessFrames(Channeler(frame, obs).AsAsyncEnumerable(), stream, Transport);
            });
		}

		static async Task ProcessFrames(IAsyncEnumerable<RSocketFrame> frame, int stream, IRSocketTransport transport, CancellationToken cancellationToken = default)
		{
			await foreach (var value in frame.WithCancellation(cancellationToken))
			{
				await new RSocketProtocol.Payload(stream, value.Data, value.Metadata, next: true)
					.WriteFlush(transport.Output, value.Data, value.Metadata);
			}

			await new RSocketProtocol.Payload(stream, complete: true).WriteFlush(transport.Output);
		}
	}
}
