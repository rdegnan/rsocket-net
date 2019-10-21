﻿using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace System.Buffers
{
    static partial class SequenceReaderExtensions
    {
        /// <summary>
        /// Try to read the given type out of the buffer if possible. Warning: this is dangerous to use with arbitrary
        /// structs- see remarks for full details.
        /// </summary>
        /// <remarks>
        /// IMPORTANT: The read is a straight copy of bits. If a struct depends on specific state of it's members to
        /// behave correctly this can lead to exceptions, etc. If reading endian specific integers, use the explicit
        /// overloads such as <see cref="TryReadLittleEndian(ref SequenceReader{byte}, out short)"/>
        /// </remarks>
        /// <returns>
        /// True if successful. <paramref name="value"/> will be default if failed (due to lack of space).
        /// </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static unsafe bool TryRead<T>(ref this SequenceReader<byte> reader, out T value) where T : unmanaged
        {
            ReadOnlySpan<byte> span = reader.UnreadSpan;
            if (span.Length < sizeof(T))
                return TryReadMultisegment(ref reader, out value);

#if NETSTANDARD2_1
			value = MemoryMarshal.Read<T>(span);        //FROM https://github.com/dotnet/corefxlab/blob/138c21ab030710c4d9e31d6fab7e928215e3ecc5/src/System.Buffers.ReaderWriter/System/Buffers/Reader/BufferReader_binary.cs#L27
#else
            //This was implemented in the mainline code for performance. Discussion here: https://github.com/dotnet/corefxlab/pull/2537
            value = Unsafe.ReadUnaligned<T>(ref MemoryMarshal.GetReference(span));
#endif
            reader.Advance(sizeof(T));
            return true;
        }

        private static unsafe bool TryReadMultisegment<T>(ref SequenceReader<byte> reader, out T value) where T : unmanaged
        {
            Debug.Assert(reader.UnreadSpan.Length < sizeof(T));

            // Not enough data in the current segment, try to peek for the data we need.
            T buffer = default;
            Span<byte> tempSpan = new Span<byte>(&buffer, sizeof(T));

            if (!reader.TryCopyTo(tempSpan))
            {
                value = default;
                return false;
            }

#if NETSTANDARD2_1
			value = MemoryMarshal.Read<T>(tempSpan);	//FROM https://github.com/dotnet/corefxlab/blob/138c21ab030710c4d9e31d6fab7e928215e3ecc5/src/System.Buffers.ReaderWriter/System/Buffers/Reader/BufferReader_binary.cs#L46
#else
            value = Unsafe.ReadUnaligned<T>(ref MemoryMarshal.GetReference(tempSpan));
#endif
            reader.Advance(sizeof(T));
            return true;
        }

        /// <summary>
		/// Reads an <see cref="Int16"/> as big endian.
		/// </summary>
		/// <returns>False if there wasn't enough data for an <see cref="Int16"/>.</returns>
		public static bool TryReadBigEndian(ref this SequenceReader<byte> reader, out ushort value)
        {
            if (!BitConverter.IsLittleEndian)
            {
                return reader.TryRead(out value);
            }

            return TryReadReverseEndianness(ref reader, out value);
        }

        private static bool TryReadReverseEndianness(ref SequenceReader<byte> reader, out ushort value)
        {
            if (reader.TryRead(out value))
            {
                value = BinaryPrimitives.ReverseEndianness(value);
                return true;
            }

            return false;
        }

    }
}
