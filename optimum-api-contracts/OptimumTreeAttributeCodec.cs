using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace Vintagestory.API.Config;

/// <summary>
/// Ref-counted pooled byte[] slab shared across one chunk's Packet_BlockEntity entries
/// so chunk streaming avoids per-BlockEntity byte[] allocations across both multiplayer
/// synchronous serialization and singleplayer async main-thread packet handoff.
/// </summary>
public sealed class OptimumPooledSlab
{
    private byte[]? buffer;
    private int refCount;

    private OptimumPooledSlab(byte[] buffer)
    {
        this.buffer = buffer;
        Volatile.Write(ref refCount, 1);
    }

    public byte[]? Buffer => Volatile.Read(ref buffer);

    public static OptimumPooledSlab Rent(int minimumLength)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(Math.Max(1, minimumLength));
        return new OptimumPooledSlab(rented);
    }

    public void Retain()
    {
        while (true)
        {
            int current = Volatile.Read(ref refCount);
            if (current <= 0 || Volatile.Read(ref buffer) == null)
            {
                return;
            }
            if (Interlocked.CompareExchange(ref refCount, current + 1, current) == current)
            {
                return;
            }
        }
    }

    public void Release()
    {
        while (true)
        {
            int current = Volatile.Read(ref refCount);
            if (current <= 0)
            {
                return;
            }
            if (Interlocked.CompareExchange(ref refCount, current - 1, current) == current)
            {
                if (current == 1)
                {
                    byte[]? owned = Interlocked.Exchange(ref buffer, null);
                    if (owned != null)
                    {
                        ArrayPool<byte>.Shared.Return(owned);
                    }
                }
                return;
            }
        }
    }
}

/// <summary>
/// Reusable BinaryWriter whose OutStream can be attached for a single serialization call
/// and detached in finally so thread-static writers never retain stream/buffer references.
/// </summary>
internal sealed class OptimumReusableBinaryWriter : BinaryWriter
{
    internal OptimumReusableBinaryWriter()
        : base(Stream.Null, Encoding.UTF8, leaveOpen: true)
    {
    }

    internal void Attach(Stream stream)
    {
        OutStream = stream;
    }

    internal void Detach()
    {
        OutStream = Stream.Null;
    }
}

/// <summary>
/// Seekable read-only Stream view over an existing byte[] segment, paired with one
/// thread-static BinaryReader for custom mod IAttribute fallbacks. Detached in finally.
/// </summary>
internal sealed class OptimumPooledByteArrayStream : Stream
{
    private byte[]? buffer;
    private int position;
    private int length;

    internal void Attach(byte[] buffer, int position, int endOffset)
    {
        this.buffer = buffer;
        this.position = position;
        this.length = endOffset;
    }

    internal void Detach()
    {
        buffer = null;
        position = 0;
        length = 0;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;

    public override long Position
    {
        get => position;
        set
        {
            if (value < 0 || value > length)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            position = (int)value;
        }
    }

    public override int ReadByte()
    {
        byte[]? buf = buffer;
        if (buf == null || position >= length)
        {
            return -1;
        }
        return buf[position++];
    }

    public override int Read(byte[] destBuffer, int offset, int count)
    {
        byte[]? buf = buffer;
        if (buf == null || count <= 0)
        {
            return 0;
        }
        int available = length - position;
        if (available <= 0)
        {
            return 0;
        }
        int toCopy = Math.Min(count, available);
        System.Buffer.BlockCopy(buf, position, destBuffer, offset, toCopy);
        position += toCopy;
        return toCopy;
    }

    public override int Read(Span<byte> destBuffer)
    {
        byte[]? buf = buffer;
        if (buf == null || destBuffer.IsEmpty)
        {
            return 0;
        }
        int available = length - position;
        if (available <= 0)
        {
            return 0;
        }
        int toCopy = Math.Min(destBuffer.Length, available);
        buf.AsSpan(position, toCopy).CopyTo(destBuffer);
        position += toCopy;
        return toCopy;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => position + offset,
            SeekOrigin.End => length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        if (target < 0 || target > length)
        {
            throw new IOException("Attempted to seek outside stream bounds.");
        }
        position = (int)target;
        return position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Span-based TreeAttribute and BlockEntity serialization/deserialization helpers
/// eliminating per-BlockEntity MemoryStream, BinaryReader/BinaryWriter, FloatAttribute byte[4],
/// intern-key throwaway strings, and intermediate byte[] allocations.
/// </summary>
public static class OptimumTreeAttributeCodec
{
    private const int KeyCacheSize = 1024;
    private const int KeyCacheMask = KeyCacheSize - 1;
    private const int MaxCachedKeyBytes = 64;

    private const int ClassnameCacheSize = 256;
    private const int ClassnameCacheMask = ClassnameCacheSize - 1;
    private const int MaxCachedClassnameBytes = 96;

    private const int MaxRetainedBufferBytes = 1048576;

    private sealed class CachedUtf8String(byte[] utf8, string value)
    {
        public readonly byte[] Utf8 = utf8;
        public readonly string Value = value;
    }

    private static readonly CachedUtf8String?[] KeyCache = new CachedUtf8String?[KeyCacheSize];
    private static readonly CachedUtf8String?[] ClassnameCache = new CachedUtf8String?[ClassnameCacheSize];

    private static volatile bool customBuiltInRegistered;

    [ThreadStatic]
    private static FastMemoryStream? threadToBytesStream;

    [ThreadStatic]
    private static OptimumReusableBinaryWriter? threadToBytesWriter;

    [ThreadStatic]
    private static OptimumReusableBinaryWriter? threadExternalWriter;

    [ThreadStatic]
    private static TreeAttribute? threadReusableTree;

    [ThreadStatic]
    private static OptimumPooledByteArrayStream? threadReadStream;

    [ThreadStatic]
    private static BinaryReader? threadBinaryReader;

    public static void OnAttributeRegistered(int attrId, Type? type)
    {
        if (attrId >= 1 && attrId <= 16 && type != GetDefaultBuiltInType(attrId))
        {
            customBuiltInRegistered = true;
        }
    }

    public static IAttribute CreateBuiltInOrMappedAttribute(byte attrId)
    {
        if (!customBuiltInRegistered)
        {
            switch (attrId)
            {
                case 1: return new IntAttribute();
                case 2: return new LongAttribute();
                case 3: return new DoubleAttribute();
                case 4: return new FloatAttribute();
                case 5: return new StringAttribute();
                case 6: return new TreeAttribute();
                case 7: return new ItemstackAttribute();
                case 8: return new ByteArrayAttribute();
                case 9: return new BoolAttribute();
                case 10: return new StringArrayAttribute();
                case 11: return new IntArrayAttribute();
                case 12: return new FloatArrayAttribute();
                case 13: return new DoubleArrayAttribute();
                case 14: return new TreeArrayAttribute();
                case 15: return new LongArrayAttribute();
                case 16: return new BoolArrayAttribute();
            }
        }

        if (TreeAttribute.AttributeIdMapping.TryGetValue(attrId, out Type? t) && t != null)
        {
            return (IAttribute)Activator.CreateInstance(t)!;
        }
        throw new Exception($"Cannot find attribute id mapping for {attrId}");
    }

    private static Type? GetDefaultBuiltInType(int attrId)
    {
        return attrId switch
        {
            1 => typeof(IntAttribute),
            2 => typeof(LongAttribute),
            3 => typeof(DoubleAttribute),
            4 => typeof(FloatAttribute),
            5 => typeof(StringAttribute),
            6 => typeof(TreeAttribute),
            7 => typeof(ItemstackAttribute),
            8 => typeof(ByteArrayAttribute),
            9 => typeof(BoolAttribute),
            10 => typeof(StringArrayAttribute),
            11 => typeof(IntArrayAttribute),
            12 => typeof(FloatArrayAttribute),
            13 => typeof(DoubleArrayAttribute),
            14 => typeof(TreeArrayAttribute),
            15 => typeof(LongArrayAttribute),
            16 => typeof(BoolArrayAttribute),
            _ => null
        };
    }

    public static FastMemoryStream LeaseThreadWriteStream(int initialCapacity = 4096)
    {
        FastMemoryStream? ms = threadToBytesStream;
        threadToBytesStream = null;
        if (ms == null)
        {
            return new FastMemoryStream(initialCapacity);
        }
        ms.Reset();
        return ms;
    }

    public static void ReturnThreadWriteStream(FastMemoryStream? ms)
    {
        if (ms == null)
        {
            return;
        }
        ms.Reset();
        if (threadToBytesStream == null && ms.GetBuffer().Length <= MaxRetainedBufferBytes)
        {
            threadToBytesStream = ms;
        }
    }

    public static byte[] TreeToBytes(TreeAttribute tree)
    {
        if (tree == null)
        {
            throw new ArgumentNullException(nameof(tree));
        }

        FastMemoryStream ms = LeaseThreadWriteStream(1024);
        OptimumReusableBinaryWriter? writer = threadToBytesWriter;
        threadToBytesWriter = null;
        writer ??= new OptimumReusableBinaryWriter();
        writer.Attach(ms);
        try
        {
            tree.ToBytes(writer);
            return ms.ToArray();
        }
        finally
        {
            writer.Detach();
            threadToBytesWriter ??= writer;
            ReturnThreadWriteStream(ms);
        }
    }

    public static int WriteBlockEntityToStream(BlockEntity blockEntity, FastMemoryStream ms)
    {
        return WriteBlockEntityToStream(null, blockEntity, ms, writeClassname: false);
    }

    public static int WriteBlockEntityToStream(string? classname, BlockEntity blockEntity, FastMemoryStream ms, bool writeClassname)
    {
        TreeAttribute? tree = threadReusableTree;
        threadReusableTree = null;
        tree ??= new TreeAttribute();

        OptimumReusableBinaryWriter? writer = threadExternalWriter;
        threadExternalWriter = null;
        writer ??= new OptimumReusableBinaryWriter();

        int startPos = (int)ms.Position;
        writer.Attach(ms);
        try
        {
            if (writeClassname)
            {
                writer.Write(classname ?? string.Empty);
            }
            blockEntity.ToTreeAttributes(tree);
            tree.ToBytes(writer);
            return (int)ms.Position - startPos;
        }
        finally
        {
            writer.Detach();
            threadExternalWriter ??= writer;
            tree.Clear();
            threadReusableTree ??= tree;
        }
    }

    public static TreeAttribute CreateTreeFromBytes(byte[] data)
    {
        TreeAttribute tree = new TreeAttribute();
        ReadTreeFromBytes(tree, data, 0, data.Length);
        return tree;
    }

    public static void ReadTreeFromBytes(TreeAttribute target, byte[] data)
    {
        ReadTreeFromBytes(target, data, 0, data.Length);
    }

    public static void ReadTreeFromBytes(TreeAttribute target, byte[] data, int offset, int length)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }
        if ((uint)offset > (uint)data.Length || (uint)length > (uint)(data.Length - offset))
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        if (target.GetType() != typeof(TreeAttribute))
        {
            ReadViaPooledBinaryReader(target, data, offset, length);
            return;
        }

        int pos = 0;
        ReadTreeInto(data.AsSpan(offset, length), ref pos, data, offset, length, target, 0);
    }

    private static void ReadViaPooledBinaryReader(TreeAttribute target, byte[] data, int offset, int length)
    {
        OptimumPooledByteArrayStream? stream = threadReadStream;
        BinaryReader? reader = threadBinaryReader;
        threadReadStream = null;
        threadBinaryReader = null;
        if (stream == null || reader == null)
        {
            stream = new OptimumPooledByteArrayStream();
            reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        }

        stream.Attach(data, offset, offset + length);
        try
        {
            target.FromBytes(reader);
        }
        finally
        {
            stream.Detach();
            if (threadReadStream == null)
            {
                threadReadStream = stream;
                threadBinaryReader = reader;
            }
        }
    }

    public static void ReadTreeFromSpan(TreeAttribute target, ReadOnlySpan<byte> data)
    {
        int pos = 0;
        ReadTreeInto(data, ref pos, null, 0, data.Length, target, 0);
    }

    public static void ReadBlockEntityFromBytes(byte[] data, int offset, int length, out string classname, TreeAttribute target)
    {
        ReadBlockEntityClassname(data, offset, length, out classname, out int bodyOffset);
        ReadTreeFromBytes(target, data, offset + bodyOffset, length - bodyOffset);
    }

    public static void ReadBlockEntityClassname(byte[] data, int offset, int length, out string classname, out int bodyOffset)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }
        if ((uint)offset > (uint)data.Length || (uint)length > (uint)(data.Length - offset))
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        ReadOnlySpan<byte> span = data.AsSpan(offset, length);
        int pos = 0;
        classname = ReadCachedClassname(span, ref pos);
        bodyOffset = pos;
    }

    private static void ReadTreeInto(
        ReadOnlySpan<byte> span,
        ref int pos,
        byte[]? backingArray,
        int backingOffset,
        int backingLength,
        TreeAttribute target,
        int depth)
    {
        if (depth > 30)
        {
            Console.WriteLine("Can't fully decode AttributeTree, beyond 30 depth limit");
            return;
        }

        target.Clear();

        byte attrId;
        while ((attrId = ReadByte(span, ref pos)) != 0)
        {
            string key = ReadInternedKey(span, ref pos);
            IAttribute attr;
            if (!customBuiltInRegistered && attrId >= 1 && attrId <= 16)
            {
                attr = ReadBuiltInAttribute(attrId, span, ref pos, backingArray, backingOffset, backingLength, depth);
            }
            else
            {
                attr = ReadMappedAttributeWithReader(attrId, span, ref pos, backingArray, backingOffset, backingLength, depth);
            }
            target[key] = attr;
        }
    }

    private static IAttribute ReadBuiltInAttribute(
        byte attrId,
        ReadOnlySpan<byte> span,
        ref int pos,
        byte[]? backingArray,
        int backingOffset,
        int backingLength,
        int depth)
    {
        switch (attrId)
        {
            case 1:
                return new IntAttribute(ReadInt32(span, ref pos));
            case 2:
                return new LongAttribute(ReadInt64(span, ref pos));
            case 3:
                return new DoubleAttribute(ReadDouble(span, ref pos));
            case 4:
                return new FloatAttribute(ReadSingle(span, ref pos));
            case 5:
                return new StringAttribute(ReadString(span, ref pos));
            case 6:
            {
                TreeAttribute child = new TreeAttribute();
                ReadTreeInto(span, ref pos, backingArray, backingOffset, backingLength, child, depth + 1);
                return child;
            }
            case 7:
            {
                bool isNull = ReadByte(span, ref pos) != 0;
                if (isNull)
                {
                    return new ItemstackAttribute();
                }
                EnumItemClass itemClass = (EnumItemClass)ReadInt32(span, ref pos);
                int id = ReadInt32(span, ref pos);
                int stackSize = ReadInt32(span, ref pos);
                ItemStack stack = new ItemStack
                {
                    Class = itemClass,
                    Id = id,
                    StackSize = stackSize
                };
                if (span.Length > pos && span[pos] == 0)
                {
                    pos++;
                }
                else
                {
                    ReadTreeInto(span, ref pos, backingArray, backingOffset, backingLength, (TreeAttribute)stack.Attributes, 0);
                }
                return new ItemstackAttribute(stack);
            }
            case 8:
            {
                int byteLen = ReadInt16(span, ref pos);
                if (byteLen < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(byteLen));
                }
                if (byteLen == 0)
                {
                    return new ByteArrayAttribute(Array.Empty<byte>());
                }
                RequireBytes(span, pos, byteLen);
                byte[] bytes = span.Slice(pos, byteLen).ToArray();
                pos += byteLen;
                return new ByteArrayAttribute(bytes);
            }
            case 9:
                return new BoolAttribute(ReadByte(span, ref pos) != 0);
            case 10:
            {
                int quantity = ReadInt32(span, ref pos);
                if (quantity < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(quantity));
                }
                RequireBytes(span, pos, quantity);
                string[] values = quantity == 0 ? Array.Empty<string>() : new string[quantity];
                for (int i = 0; i < quantity; i++)
                {
                    values[i] = ReadString(span, ref pos);
                }
                return new StringArrayAttribute(values);
            }
            case 11:
            {
                int quantity = ReadInt32(span, ref pos);
                if (quantity < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(quantity));
                }
                RequireBytes(span, pos, (long)quantity * sizeof(int));
                int[] values = quantity == 0 ? Array.Empty<int>() : GC.AllocateUninitializedArray<int>(quantity);
                for (int i = 0; i < quantity; i++)
                {
                    values[i] = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos, 4));
                    pos += 4;
                }
                return new IntArrayAttribute(values);
            }
            case 12:
            {
                int quantity = ReadInt32(span, ref pos);
                if (quantity < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(quantity));
                }
                RequireBytes(span, pos, (long)quantity * sizeof(float));
                float[] values = quantity == 0 ? Array.Empty<float>() : GC.AllocateUninitializedArray<float>(quantity);
                for (int i = 0; i < quantity; i++)
                {
                    values[i] = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(pos, 4));
                    pos += 4;
                }
                return new FloatArrayAttribute(values);
            }
            case 13:
            {
                int quantity = ReadInt32(span, ref pos);
                if (quantity < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(quantity));
                }
                RequireBytes(span, pos, (long)quantity * sizeof(double));
                double[] values = quantity == 0 ? Array.Empty<double>() : GC.AllocateUninitializedArray<double>(quantity);
                for (int i = 0; i < quantity; i++)
                {
                    values[i] = BinaryPrimitives.ReadDoubleLittleEndian(span.Slice(pos, 8));
                    pos += 8;
                }
                return new DoubleArrayAttribute(values);
            }
            case 14:
            {
                int quantity = ReadInt32(span, ref pos);
                if (quantity < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(quantity));
                }
                RequireBytes(span, pos, quantity);
                TreeAttribute[] values = quantity == 0 ? Array.Empty<TreeAttribute>() : new TreeAttribute[quantity];
                for (int i = 0; i < quantity; i++)
                {
                    TreeAttribute itemTree = new TreeAttribute();
                    ReadTreeInto(span, ref pos, backingArray, backingOffset, backingLength, itemTree, 0);
                    values[i] = itemTree;
                }
                return new TreeArrayAttribute(values);
            }
            case 15:
            {
                int quantity = ReadInt32(span, ref pos);
                if (quantity < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(quantity));
                }
                RequireBytes(span, pos, (long)quantity * sizeof(long));
                long[] values = quantity == 0 ? Array.Empty<long>() : GC.AllocateUninitializedArray<long>(quantity);
                for (int i = 0; i < quantity; i++)
                {
                    values[i] = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(pos, 8));
                    pos += 8;
                }
                return new LongArrayAttribute(values);
            }
            case 16:
            {
                int quantity = ReadInt32(span, ref pos);
                if (quantity < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(quantity));
                }
                RequireBytes(span, pos, quantity);
                bool[] values = quantity == 0 ? Array.Empty<bool>() : GC.AllocateUninitializedArray<bool>(quantity);
                for (int i = 0; i < quantity; i++)
                {
                    values[i] = span[pos++] != 0;
                }
                return new BoolArrayAttribute(values);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(attrId));
        }
    }

    private static IAttribute ReadMappedAttributeWithReader(
        byte attrId,
        ReadOnlySpan<byte> span,
        ref int pos,
        byte[]? backingArray,
        int backingOffset,
        int backingLength,
        int depth)
    {
        IAttribute attr = CreateBuiltInOrMappedAttribute(attrId);
        if (attr is TreeAttribute subTree)
        {
            ReadTreeInto(span, ref pos, backingArray, backingOffset, backingLength, subTree, depth + 1);
            return subTree;
        }

        OptimumPooledByteArrayStream? stream = threadReadStream;
        BinaryReader? reader = threadBinaryReader;
        threadReadStream = null;
        threadBinaryReader = null;
        if (stream == null || reader == null)
        {
            stream = new OptimumPooledByteArrayStream();
            reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        }

        byte[] sourceBytes;
        int sourceBaseOffset;
        int sourceStart;
        int sourceEnd;
        if (backingArray != null)
        {
            sourceBytes = backingArray;
            sourceBaseOffset = backingOffset;
            sourceStart = backingOffset + pos;
            sourceEnd = backingOffset + backingLength;
        }
        else
        {
            sourceBytes = span.Slice(pos).ToArray();
            sourceBaseOffset = -pos;
            sourceStart = 0;
            sourceEnd = sourceBytes.Length;
        }

        stream.Attach(sourceBytes, sourceStart, sourceEnd);
        try
        {
            attr.FromBytes(reader);
            pos = (int)stream.Position - sourceBaseOffset;
            return attr;
        }
        finally
        {
            stream.Detach();
            if (threadReadStream == null)
            {
                threadReadStream = stream;
                threadBinaryReader = reader;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RequireBytes(ReadOnlySpan<byte> span, int pos, long count)
    {
        if (count < 0 || count > span.Length - pos)
        {
            throw new EndOfStreamException();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ReadByte(ReadOnlySpan<byte> span, ref int pos)
    {
        if ((uint)pos >= (uint)span.Length)
        {
            throw new EndOfStreamException();
        }
        return span[pos++];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static short ReadInt16(ReadOnlySpan<byte> span, ref int pos)
    {
        RequireBytes(span, pos, 2);
        short v = BinaryPrimitives.ReadInt16LittleEndian(span.Slice(pos, 2));
        pos += 2;
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadInt32(ReadOnlySpan<byte> span, ref int pos)
    {
        RequireBytes(span, pos, 4);
        int v = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos, 4));
        pos += 4;
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ReadInt64(ReadOnlySpan<byte> span, ref int pos)
    {
        RequireBytes(span, pos, 8);
        long v = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(pos, 8));
        pos += 8;
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ReadSingle(ReadOnlySpan<byte> span, ref int pos)
    {
        RequireBytes(span, pos, 4);
        float v = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(pos, 4));
        pos += 4;
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double ReadDouble(ReadOnlySpan<byte> span, ref int pos)
    {
        RequireBytes(span, pos, 8);
        double v = BinaryPrimitives.ReadDoubleLittleEndian(span.Slice(pos, 8));
        pos += 8;
        return v;
    }

    private static int Read7BitEncodedInt(ReadOnlySpan<byte> span, ref int pos)
    {
        uint result = 0;
        byte b;
        for (int shift = 0; shift < 28; shift += 7)
        {
            b = ReadByte(span, ref pos);
            result |= (b & 0x7Fu) << shift;
            if (b <= 0x7Fu)
            {
                return (int)result;
            }
        }
        b = ReadByte(span, ref pos);
        if (b > 0b_1111u)
        {
            throw new FormatException("Too many bytes in 7-bit encoded integer.");
        }
        result |= (uint)b << 28;
        return (int)result;
    }

    private static string ReadString(ReadOnlySpan<byte> span, ref int pos)
    {
        int byteLen = Read7BitEncodedInt(span, ref pos);
        if (byteLen < 0)
        {
            throw new IOException("Invalid string length.");
        }
        if (byteLen == 0)
        {
            return string.Empty;
        }
        RequireBytes(span, pos, byteLen);
        string value = Encoding.UTF8.GetString(span.Slice(pos, byteLen));
        pos += byteLen;
        return value;
    }

    private static string ReadInternedKey(ReadOnlySpan<byte> span, ref int pos)
    {
        int byteLen = Read7BitEncodedInt(span, ref pos);
        if (byteLen < 0)
        {
            throw new IOException("Invalid string length.");
        }
        if (byteLen == 0)
        {
            return string.Empty;
        }
        RequireBytes(span, pos, byteLen);
        ReadOnlySpan<byte> utf8 = span.Slice(pos, byteLen);
        pos += byteLen;

        if (utf8.Length > MaxCachedKeyBytes)
        {
            return string.Intern(Encoding.UTF8.GetString(utf8));
        }

        uint hash = HashUtf8(utf8);
        int idx = (int)(hash & KeyCacheMask);
        CachedUtf8String? entry = Volatile.Read(ref KeyCache[idx]);
        if (entry != null && utf8.SequenceEqual(entry.Utf8))
        {
            return entry.Value;
        }

        string interned = string.Intern(Encoding.UTF8.GetString(utf8));
        Volatile.Write(ref KeyCache[idx], new CachedUtf8String(utf8.ToArray(), interned));
        return interned;
    }

    private static string ReadCachedClassname(ReadOnlySpan<byte> span, ref int pos)
    {
        int byteLen = Read7BitEncodedInt(span, ref pos);
        if (byteLen < 0)
        {
            throw new IOException("Invalid string length.");
        }
        if (byteLen == 0)
        {
            return string.Empty;
        }
        RequireBytes(span, pos, byteLen);
        ReadOnlySpan<byte> utf8 = span.Slice(pos, byteLen);
        pos += byteLen;

        if (utf8.Length > MaxCachedClassnameBytes)
        {
            return Encoding.UTF8.GetString(utf8);
        }

        uint hash = HashUtf8(utf8);
        int idx = (int)(hash & ClassnameCacheMask);
        CachedUtf8String? entry = Volatile.Read(ref ClassnameCache[idx]);
        if (entry != null && utf8.SequenceEqual(entry.Utf8))
        {
            return entry.Value;
        }

        string decoded = Encoding.UTF8.GetString(utf8);
        Volatile.Write(ref ClassnameCache[idx], new CachedUtf8String(utf8.ToArray(), decoded));
        return decoded;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint HashUtf8(ReadOnlySpan<byte> utf8)
    {
        uint hash = 2166136261u;
        for (int i = 0; i < utf8.Length; i++)
        {
            hash = (hash ^ utf8[i]) * 16777619u;
        }
        return hash;
    }
}
