using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace RSocket
{
    public struct RSocketFrame
	{ 
        public RSocketFrame(ReadOnlySequence<byte> metadata = default, ReadOnlySequence<byte> data = default)
        {
            Metadata = metadata;
            Data = data;
        }

		public ReadOnlySequence<byte> Metadata;
		public ReadOnlySequence<byte> Data;
    }
}
