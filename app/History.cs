// Everything a port has sent, kept in memory up to a limit (1 GiB by
// default), addressed by absolute position: byte 0 is the first byte the
// bench received on the port. Once full, the oldest bytes go as new ones
// arrive. Memory is taken 16 MiB at a time as output arrives, so a quiet
// port costs almost nothing. Not thread-safe; Line holds its lock around it.

namespace CorsacBench;

public sealed class History
{
    const int ChunkBits = 24;
    const int Chunk = 1 << ChunkBits;                    // 16 MiB
    const long ChunkMask = Chunk - 1;

    readonly List<byte[]> _chunks = new();
    long _firstChunk;                                     // chunk number of _chunks[0]
    byte[]? _spare;                                       // the last chunk let go, for reuse
    readonly long _capacity;

    // When bytes arrived: an entry at most once a second, plus one per chunk.
    readonly List<(long At, DateTime When)> _times = new();

    public History(long capacity) => _capacity = Math.Max(Chunk, capacity);

    /// The oldest position still held.
    public long Start { get; private set; }
    /// The position the next byte will have.
    public long End { get; private set; }
    public long Capacity => _capacity;

    public void Append(ReadOnlySpan<byte> data, DateTime when)
    {
        if (data.IsEmpty) return;
        if (_times.Count == 0 || when - _times[^1].When >= TimeSpan.FromSeconds(1) || End - _times[^1].At >= Chunk)
            _times.Add((End, when));
        while (!data.IsEmpty)
        {
            long chunk = End >> ChunkBits;
            if (chunk >= _firstChunk + _chunks.Count)
            {
                if (_chunks.Count == 0) _firstChunk = chunk;
                _chunks.Add(_spare ?? GC.AllocateUninitializedArray<byte>(Chunk));
                _spare = null;
            }
            int at = (int)(End & ChunkMask);
            int n = Math.Min(data.Length, Chunk - at);
            data[..n].CopyTo(_chunks[(int)(chunk - _firstChunk)].AsSpan(at));
            data = data[n..];
            End += n;
        }

        // THE OLDEST GOES once the limit is passed: by the byte for Start,
        // by the chunk for the memory.
        Start = Math.Max(Start, End - _capacity);
        while (_chunks.Count > 0 && (_firstChunk + 1) << ChunkBits <= Start)
        {
            _spare = _chunks[0];
            _chunks.RemoveAt(0);
            _firstChunk++;
        }
        while (_times.Count > 1 && _times[1].At <= Start) _times.RemoveAt(0);
    }

    /// The bytes from `from` up to `to`, clipped to what is held.
    public byte[] Read(long from, long to)
    {
        from = Math.Clamp(from, Start, End);
        to = Math.Clamp(to, from, End);
        var outp = new byte[to - from];
        long at = from;
        int o = 0;
        while (at < to)
        {
            var piece = Piece(at, to);
            piece.CopyTo(outp.AsSpan(o));
            o += piece.Length;
            at += piece.Length;
        }
        return outp;
    }

    /// The held bytes from `at` to the end of its chunk or `to`, whichever is first.
    ReadOnlySpan<byte> Piece(long at, long to)
    {
        int off = (int)(at & ChunkMask);
        int n = (int)Math.Min(to - at, Chunk - off);
        return _chunks[(int)((at >> ChunkBits) - _firstChunk)].AsSpan(off, n);
    }

    /// The first position at or after `from` where `needle` starts and ends
    /// before `to`, or -1.
    public long Find(long from, long to, ReadOnlySpan<byte> needle)
    {
        from = Math.Clamp(from, Start, End);
        to = Math.Clamp(to, from, End);
        if (needle.IsEmpty || to - from < needle.Length) return -1;
        long at = from;
        while (at < to)
        {
            var piece = Piece(at, to);
            int i = piece.IndexOf(needle);
            if (i >= 0) return at + i;
            long next = at + piece.Length;
            // A match across the chunk boundary.
            if (next < to && needle.Length > 1)
            {
                long s = Math.Max(from, next - needle.Length + 1);
                var seam = Read(s, Math.Min(to, next + needle.Length - 1));
                int j = seam.AsSpan().IndexOf(needle);
                if (j >= 0) return s + j;
            }
            at = next;
        }
        return -1;
    }

    /// The last position before `to` (and at or after `from`) where `needle`
    /// starts and ends before `to`, or -1.
    public long FindLast(long from, long to, ReadOnlySpan<byte> needle)
    {
        from = Math.Clamp(from, Start, End);
        to = Math.Clamp(to, from, End);
        if (needle.IsEmpty || to - from < needle.Length) return -1;
        long end = to;
        while (end > from)
        {
            long pieceStart = Math.Max(from, ((end - 1) >> ChunkBits) << ChunkBits);
            var piece = Read(pieceStart, end);           // at most one chunk
            int i = piece.AsSpan().LastIndexOf(needle);
            if (i >= 0) return pieceStart + i;
            if (pieceStart > from && needle.Length > 1)
            {
                long s = Math.Max(from, pieceStart - needle.Length + 1);
                var seam = Read(s, Math.Min(end, pieceStart + needle.Length - 1));
                int j = seam.AsSpan().LastIndexOf(needle);
                if (j >= 0) return s + j;
            }
            end = pieceStart;
        }
        return -1;
    }

    /// Roughly when the byte at `at` arrived: to the second.
    public DateTime? When(long at)
    {
        if (_times.Count == 0 || at < Start) return null;
        int lo = 0, hi = _times.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_times[mid].At <= at) lo = mid; else hi = mid - 1;
        }
        return _times[lo].When;
    }
}
