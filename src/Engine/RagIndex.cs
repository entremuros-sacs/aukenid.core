namespace Aukenid.Core.Engine;

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using Microsoft.Win32.SafeHandles;

public unsafe sealed class RagIndex : IDisposable
{
    private const int FixedHeaderSize = 18;
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly SafeMemoryMappedViewHandle _handle;
    private byte* _basePtr;
    private readonly ulong[] _textOffsets;
    private readonly uint[] _textLengths;
    private readonly long[] _sourceUrlOffsets;
    private readonly ushort[] _sourceUrlLengths;
    private readonly long[] _titleOffsets;
    private readonly ushort[] _titleLengths;
    private readonly long _textPayloadBase;
    private readonly long _vectorBase;

    public string Path { get; }
    public uint ChunkCount { get; }
    public ushort Dim { get; }
    public uint Version { get; }
    public string Instructions { get; }

    private float* VectorBasePtr => (float*)(_basePtr + _vectorBase);

    private RagIndex(
        string path,
        MemoryMappedFile mmf,
        MemoryMappedViewAccessor accessor,
        SafeMemoryMappedViewHandle handle,
        byte* basePtr,
        uint chunkCount,
        ushort dim,
        uint version,
        string instructions,
        ulong[] textOffsets,
        uint[] textLengths,
        long[] sourceUrlOffsets,
        ushort[] sourceUrlLengths,
        long[] titleOffsets,
        ushort[] titleLengths,
        long textPayloadBase,
        long vectorBase)
    {
        Path = path;
        _mmf = mmf;
        _accessor = accessor;
        _handle = handle;
        _basePtr = basePtr;
        ChunkCount = chunkCount;
        Dim = dim;
        Version = version;
        Instructions = instructions;
        _textOffsets = textOffsets;
        _textLengths = textLengths;
        _sourceUrlOffsets = sourceUrlOffsets;
        _sourceUrlLengths = sourceUrlLengths;
        _titleOffsets = titleOffsets;
        _titleLengths = titleLengths;
        _textPayloadBase = textPayloadBase;
        _vectorBase = vectorBase;
    }

    public static RagIndex Open(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Missing path.", nameof(path));

        var fileLength = new FileInfo(path).Length;
        var mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, fileLength, MemoryMappedFileAccess.Read);
        var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

        unsafe
        {
            byte* ptr = null;
            accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            var handle = accessor.SafeMemoryMappedViewHandle;

            try
            {
                if (fileLength < FixedHeaderSize)
                    throw new InvalidDataException("RAG file is too small.");

                if (ptr[0] != (byte)'A' || ptr[1] != (byte)'U' || ptr[2] != (byte)'K' || ptr[3] != (byte)'N')
                    throw new InvalidDataException("RAG file magic bytes do not match.");

                var version = ReadUInt32(ptr + 4);
                var chunkCount = ReadUInt32(ptr + 8);
                var dim = ReadUInt16(ptr + 12);
                var instructionsLen = ReadUInt32(ptr + 14);

                if (instructionsLen > int.MaxValue)
                    throw new InvalidDataException("RAG file instructions section is too large.");

                var instructionsOffset = FixedHeaderSize;
                var vectorBase = checked((long)FixedHeaderSize + instructionsLen);
                if (vectorBase > fileLength)
                    throw new InvalidDataException("RAG file is truncated (instructions section).");

                var instructions = instructionsLen == 0
                    ? string.Empty
                    : Encoding.UTF8.GetString(new ReadOnlySpan<byte>(ptr + instructionsOffset, checked((int)instructionsLen)));

                var vectorsBytes = (long)chunkCount * dim * sizeof(float);
                var metaStart = vectorBase + vectorsBytes;
                if (metaStart > fileLength)
                    throw new InvalidDataException("RAG file is truncated (vectors section).");

                var textOffsets = new ulong[chunkCount];
                var textLengths = new uint[chunkCount];
                var sourceUrlOffsets = new long[chunkCount];
                var sourceUrlLengths = new ushort[chunkCount];
                var titleOffsets = new long[chunkCount];
                var titleLengths = new ushort[chunkCount];

                long cursor = metaStart;
                for (var i = 0; i < chunkCount; i++)
                {
                    if (cursor + 2 > fileLength)
                        throw new InvalidDataException("RAG file is truncated (metadata).");

                    var titleLen = ReadUInt16(ptr + cursor);
                    cursor += 2;
                    if (cursor + titleLen > fileLength)
                        throw new InvalidDataException("RAG file is truncated (title).");
                    titleOffsets[i] = cursor;
                    titleLengths[i] = titleLen;
                    cursor += titleLen;

                    if (cursor + 2 > fileLength)
                        throw new InvalidDataException("RAG file is truncated (source url).");

                    var sourceUrlLen = ReadUInt16(ptr + cursor);
                    cursor += 2;
                    if (cursor + sourceUrlLen > fileLength)
                        throw new InvalidDataException("RAG file is truncated (source url).");
                    sourceUrlOffsets[i] = cursor;
                    sourceUrlLengths[i] = sourceUrlLen;
                    cursor += sourceUrlLen;

                    if (cursor + 8 + 4 > fileLength)
                        throw new InvalidDataException("RAG file is truncated (offset table).");

                    textOffsets[i] = ReadUInt64(ptr + cursor);
                    cursor += 8;
                    textLengths[i] = ReadUInt32(ptr + cursor);
                    cursor += 4;
                }

                var textPayloadBase = cursor;
                if (textPayloadBase > fileLength)
                    throw new InvalidDataException("RAG file is truncated (text payload).");

                return new RagIndex(
                    path,
                    mmf,
                    accessor,
                    handle,
                    (byte*)ptr,
                    chunkCount,
                    dim,
                    version,
                    instructions,
                    textOffsets,
                    textLengths,
                    sourceUrlOffsets,
                    sourceUrlLengths,
                    titleOffsets,
                    titleLengths,
                    textPayloadBase,
                    vectorBase);
            }
            catch
            {
                accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                accessor.Dispose();
                mmf.Dispose();
                throw;
            }
        }
    }

    public IReadOnlyList<RagHit> Search(float[] queryVector, int topK)
    {
        if (queryVector is null)
            throw new ArgumentNullException(nameof(queryVector));
        if (queryVector.Length != Dim)
            throw new ArgumentException($"Query vector length {queryVector.Length} does not match index dim {Dim}.");
        if (topK <= 0)
            return [];

        var k = Math.Min(topK, (int)ChunkCount);
        var bestScores = new float[k];
        var bestIdx = new int[k];
        for (var i = 0; i < k; i++)
        {
            bestScores[i] = float.NegativeInfinity;
            bestIdx[i] = -1;
        }

        unsafe
        {
            fixed (float* queryPtr = queryVector)
            {
                for (var i = 0; i < ChunkCount; i++)
                {
                    var score = Dot(queryPtr, VectorBasePtr + ((long)i * Dim), Dim);
                    // Replace the worst current result.
                    var worstAt = 0;
                    var worstScore = bestScores[0];
                    for (var j = 1; j < k; j++)
                    {
                        if (bestScores[j] < worstScore)
                        {
                            worstScore = bestScores[j];
                            worstAt = j;
                        }
                    }

                    if (score > worstScore)
                    {
                        bestScores[worstAt] = score;
                        bestIdx[worstAt] = i;
                    }
                }
            }
        }

        var hits = new List<RagHit>(k);
        for (var i = 0; i < k; i++)
        {
            if (bestIdx[i] >= 0)
            {
                hits.Add(new RagHit(bestIdx[i], bestScores[i]));
            }
        }

        hits.Sort((a, b) => b.Score.CompareTo(a.Score));
        return hits;
    }

    public RagChunk ReadChunk(int index)
    {
        if (index < 0 || index >= ChunkCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        var title = ReadUtf8(_titleOffsets[index], _titleLengths[index]);
        var sourceUrl = ReadUtf8(_sourceUrlOffsets[index], _sourceUrlLengths[index]);
        var textOffset = checked((long)_textOffsets[index]);
        var textLength = _textLengths[index];
        var text = ReadUtf8(_textPayloadBase + textOffset, textLength);
        return new RagChunk(index, title, sourceUrl, text);
    }

    private string ReadUtf8(long offset, uint length)
    {
        if (length == 0)
            return string.Empty;

        var bytes = new ReadOnlySpan<byte>(_basePtr + offset, checked((int)length));
        return Encoding.UTF8.GetString(bytes);
    }

    public void Dispose()
    {
        if (_basePtr != null)
        {
            _handle.ReleasePointer();
            _basePtr = null;
        }

        _accessor.Dispose();
        _mmf.Dispose();
    }

    public readonly record struct RagHit(int Index, float Score);

    public readonly record struct RagChunk(int Index, string Title, string SourceUrl, string Text);

    private static unsafe float Dot(float* a, float* b, int dim)
    {
        float sum = 0;
        for (var i = 0; i < dim; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    private static unsafe uint ReadUInt32(byte* p) =>
        (uint)(p[0] | (p[1] << 8) | (p[2] << 16) | (p[3] << 24));

    private static unsafe ushort ReadUInt16(byte* p) =>
        (ushort)(p[0] | (p[1] << 8));

    private static unsafe ulong ReadUInt64(byte* p)
    {
        var lo = ReadUInt32(p);
        var hi = ReadUInt32(p + 4);
        return lo | ((ulong)hi << 32);
    }
}

