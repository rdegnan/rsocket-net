using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Disposables;
using System.Reactive.Threading.Tasks;

using IRSocketStream = System.IObserver<RSocket.RSocketFrame>;

namespace RSocket
{
	partial class RSocket
	{
		public class Receiver<T> : IAsyncEnumerable<T>
		{
			readonly Func<IRSocketStream, Task> Subscriber;
			readonly Func<RSocketFrame, T> Mapper;

			public Receiver(Func<IRSocketStream, Task> subscriber, Func<RSocketFrame, T> mapper)
			{
				Subscriber = subscriber;
				Mapper = mapper;
			}

			public async Task<T> ExecuteAsync(CancellationToken cancellation = default)
			{
                var observable = Observable.Create<RSocketFrame>(observer => {
                    Subscriber(observer).ConfigureAwait(false);
                    return Disposable.Empty;
                });

                var value = await observable.ToTask(cancellation);
                return Mapper(value);
            }

			public async Task<T> ExecuteAsync(T result, CancellationToken cancellation = default)
			{
                var observable = Observable.Create<RSocketFrame>(observer => {
                    Subscriber(observer).ConfigureAwait(false);
                    return Disposable.Empty;
                });
                await observable.ToTask(cancellation);
                return result;
			}

			public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellation = default)
			{
                var observable = Observable.Create<RSocketFrame>(observer => {
                    Subscriber(observer).ConfigureAwait(false);
                    return Disposable.Empty;
                });
                return observable
                    .Select(value => Mapper(value))
                    .ToAsyncEnumerable()
                    .GetAsyncEnumerator(cancellation);
            }
		}

		public class Receiver<TSource, T> : Receiver<T>
		{
			public Receiver(Func<IRSocketStream, Task<IRSocketChannel>> subscriber, IAsyncEnumerable<TSource> source, Func<TSource, RSocketFrame> sourcemapper, Func<RSocketFrame, T> resultmapper) :
				base(stream => Subscribe(stream, subscriber(stream), source, sourcemapper), resultmapper)
			{
			}

			static async Task Subscribe(IRSocketStream stream, Task<IRSocketChannel> original, IAsyncEnumerable<TSource> source, Func<TSource, RSocketFrame> sourcemapper)
			{
				var channel = await original;     //Let the receiver hook up first before we start generating values.
				var enumerator = source.GetAsyncEnumerator();
				try
				{
					while (await enumerator.MoveNextAsync())
					{
						await channel.Send(sourcemapper(enumerator.Current));
					}
				}
				finally { await enumerator.DisposeAsync(); }
			}
		}
	}
}
