using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;

namespace Vintagestory.API.Config;

/// <summary>
/// Helpers for sizing Vorbis PCM decode buffers from seekable Ogg streams without
/// MemoryStream growth churn or full-buffer ToArray duplication.
/// </summary>
internal static class OptimumAudioBufferHelper
{
    internal const int OggMaxTailScanBytes = 65536;
    private const int OggMinPageHeaderBytes = 27;
    private const byte OggHeaderFlagEos = 0x04;
    private const long MaxBytesPerCompressedByteSanity = 4096L;

    /// <summary>
    /// Inspects the tail of a seekable single-bitstream Ogg stream to locate the final
    /// EOS page matching <paramref name="firstSerial"/> and compute the expected 16-bit
    /// signed PCM byte count (<c>granulepos * channels * 2</c>). Restores stream position
    /// before returning. Returns false for unseekable, multi-stream/chained, or malformed
    /// streams so callers fall back to dynamic growth.
    /// </summary>
    internal static bool TryEstimateOggPcmByteLength(
        Stream stream,
        int firstSerial,
        int channels,
        out int estimatedByteLength)
    {
        estimatedByteLength = 0;
        if (stream == null || !stream.CanSeek || firstSerial == 0 || channels <= 0)
        {
            return false;
        }

        long length = stream.Length;
        if (length < OggMinPageHeaderBytes)
        {
            return false;
        }

        int tailLength = (int)Math.Min(length, OggMaxTailScanBytes);
        long tailOffset = length - tailLength;
        long savedPosition = stream.Position;
        byte[] rented = ArrayPool<byte>.Shared.Rent(tailLength);
        try
        {
            stream.Position = tailOffset;
            int totalRead = 0;
            while (totalRead < tailLength)
            {
                int read = stream.Read(rented, totalRead, tailLength - totalRead);
                if (read <= 0)
                {
                    break;
                }
                totalRead += read;
            }

            if (totalRead < OggMinPageHeaderBytes)
            {
                return false;
            }

            ReadOnlySpan<byte> tail = rented.AsSpan(0, totalRead);
            for (int pos = totalRead - OggMinPageHeaderBytes; pos >= 0; pos--)
            {
                if (tail[pos] != (byte)'O' ||
                    tail[pos + 1] != (byte)'g' ||
                    tail[pos + 2] != (byte)'g' ||
                    tail[pos + 3] != (byte)'S')
                {
                    continue;
                }

                // Ogg capture version must be 0 and EOS flag (0x04) must be set.
                if (tail[pos + 4] != 0 || (tail[pos + 5] & OggHeaderFlagEos) == 0)
                {
                    continue;
                }

                int pageSerial = BinaryPrimitives.ReadInt32LittleEndian(tail.Slice(pos + 14, 4));
                if (pageSerial != firstSerial)
                {
                    continue;
                }

                int segmentCount = tail[pos + 26];
                int segmentTableEnd = pos + OggMinPageHeaderBytes + segmentCount;
                if (segmentTableEnd > totalRead)
                {
                    continue;
                }

                int bodyBytes = 0;
                for (int s = 0; s < segmentCount; s++)
                {
                    bodyBytes += tail[pos + OggMinPageHeaderBytes + s];
                }

                if (segmentTableEnd + bodyBytes != totalRead)
                {
                    continue;
                }

                long granulePos = BinaryPrimitives.ReadInt64LittleEndian(tail.Slice(pos + 6, 8));
                if (granulePos <= 0)
                {
                    return false;
                }

                // Guard against arithmetic overflow and absurd bogus header values.
                if (granulePos > int.MaxValue / (channels * 2L))
                {
                    return false;
                }

                long totalBytes = granulePos * channels * 2L;
                long sanityCeiling = Math.Max(65536L, length * MaxBytesPerCompressedByteSanity);
                if (totalBytes <= 0 || totalBytes > int.MaxValue || totalBytes > sanityCeiling)
                {
                    return false;
                }

                estimatedByteLength = (int)totalBytes;
                return true;
            }

            return false;
        }
        finally
        {
            try
            {
                stream.Position = savedPosition;
            }
            catch
            {
                // Preserve original exception / allow caller fallback if restoring position fails.
            }
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Reads only the first Ogg Vorbis identification page and the trailing EOS page
    /// <c>granulepos</c> of a seekable single-bitstream Ogg stream to determine channel count,
    /// sample rate, and playback duration without decoding any audio samples.
    /// </summary>
    internal static bool TryEstimateOggDurationAndFormat(
        Stream stream,
        out int channels,
        out int sampleRate,
        out float durationSeconds)
    {
        channels = 0;
        sampleRate = 0;
        durationSeconds = 0f;

        if (stream == null || !stream.CanSeek)
        {
            return false;
        }

        long length = stream.Length;
        if (length < OggMinPageHeaderBytes + 30)
        {
            return false;
        }

        long savedPosition = stream.Position;
        try
        {
            int headToRead = (int)Math.Min(length, 512L);
            Span<byte> head = stackalloc byte[headToRead];
            stream.Position = 0;
            int totalRead = 0;
            while (totalRead < headToRead)
            {
                int read = stream.Read(head.Slice(totalRead));
                if (read <= 0)
                {
                    break;
                }
                totalRead += read;
            }

            if (totalRead < OggMinPageHeaderBytes + 30)
            {
                return false;
            }

            if (head[0] != (byte)'O' ||
                head[1] != (byte)'g' ||
                head[2] != (byte)'g' ||
                head[3] != (byte)'S' ||
                head[4] != 0)
            {
                return false;
            }

            int firstSerial = BinaryPrimitives.ReadInt32LittleEndian(head.Slice(14, 4));
            if (firstSerial == 0)
            {
                return false;
            }

            int segmentCount = head[26];
            int packetOffset = OggMinPageHeaderBytes + segmentCount;
            if (packetOffset + 30 > totalRead)
            {
                return false;
            }

            // Vorbis Identification header: 0x01 + "vorbis" + uint32 version(0) + byte channels + int32 rate
            if (head[packetOffset] != 0x01 ||
                head[packetOffset + 1] != (byte)'v' ||
                head[packetOffset + 2] != (byte)'o' ||
                head[packetOffset + 3] != (byte)'r' ||
                head[packetOffset + 4] != (byte)'b' ||
                head[packetOffset + 5] != (byte)'i' ||
                head[packetOffset + 6] != (byte)'s')
            {
                return false;
            }

            uint vorbisVersion = BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(packetOffset + 7, 4));
            int headerChannels = head[packetOffset + 11];
            int headerRate = BinaryPrimitives.ReadInt32LittleEndian(head.Slice(packetOffset + 12, 4));
            if (vorbisVersion != 0 || headerChannels <= 0 || headerRate <= 0)
            {
                return false;
            }

            if (!TryEstimateOggPcmByteLength(stream, firstSerial, headerChannels, out int estimatedPcmBytes))
            {
                return false;
            }

            long samples = estimatedPcmBytes / (headerChannels * 2L);
            if (samples <= 0)
            {
                return false;
            }

            channels = headerChannels;
            sampleRate = headerRate;
            durationSeconds = (float)((double)samples / headerRate);
            return true;
        }
        finally
        {
            try
            {
                stream.Position = savedPosition;
            }
            catch
            {
            }
        }
    }

    internal static bool ShouldLazyDecodeSfx(
        Stream stream,
        long compressedByteLength,
        float minDurationSeconds = 5f,
        long unseekableCompressedThresholdBytes = 262144L)
    {
        if (TryEstimateOggDurationAndFormat(stream, out _, out _, out float durationSeconds))
        {
            return durationSeconds >= minDurationSeconds;
        }
        return compressedByteLength >= unseekableCompressedThresholdBytes;
    }
}

/// <summary>
/// Contiguous 16-bit PCM byte buffer builder that pre-allocates the exact target array when
/// the Ogg EOS granule count matches decoded output, avoiding MemoryStream growth copies
/// and final ToArray duplication.
/// </summary>
internal struct OptimumPcmBuffer
{
    private const int DefaultInitialCapacity = 8192;
    private byte[] _buffer;
    private int _written;

    internal OptimumPcmBuffer(int estimatedByteLength)
    {
        if (estimatedByteLength > 0)
        {
            // When written matches _buffer.Length, every byte is overwritten before return.
            _buffer = GC.AllocateUninitializedArray<byte>(estimatedByteLength);
        }
        else
        {
            _buffer = new byte[DefaultInitialCapacity];
        }
        _written = 0;
    }

    internal int Written => _written;

    internal void Append(byte[] source, int byteCount)
    {
        if (byteCount <= 0)
        {
            return;
        }

        int required = _written + byteCount;
        if ((uint)required > (uint)_buffer.Length)
        {
            Grow(required);
        }

        Buffer.BlockCopy(source, 0, _buffer, _written, byteCount);
        _written = required;
    }

    internal byte[] ToExactArray()
    {
        if (_written == 0)
        {
            return Array.Empty<byte>();
        }

        if (_written == _buffer.Length)
        {
            return _buffer;
        }

        byte[] exact = new byte[_written];
        Buffer.BlockCopy(_buffer, 0, exact, 0, _written);
        return exact;
    }

    private void Grow(int minCapacity)
    {
        long doubled = (long)_buffer.Length * 2L;
        int nextCapacity = doubled > int.MaxValue ? int.MaxValue : (int)doubled;
        if (nextCapacity < minCapacity)
        {
            nextCapacity = minCapacity;
        }

        byte[] next = new byte[nextCapacity];
        if (_written > 0)
        {
            Buffer.BlockCopy(_buffer, 0, next, 0, _written);
        }
        _buffer = next;
    }
}
