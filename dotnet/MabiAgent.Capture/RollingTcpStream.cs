using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace MabiAgent.Capture;

/// <summary>Bounded receive-only TCP reconstruction; completed frame bodies leave the stream immediately.</summary>
public sealed class RollingTcpStream(Func<GameFrame, bool>? fullSyncValidator = null, uint loginOpcode = GameProtocol.LoginOpcode)
{
    public const int MaxBuffer = GameProtocol.MaxBody + 4096, HistoryLimit = 65536;
    private uint? expected, synSequence;
    private readonly List<byte> buffer = [], history = [];
    private readonly Dictionary<uint, byte[]> pending = [];
    private bool framed;
    public int BufferedBytes => buffer.Count + history.Count + pending.Values.Sum(x => x.Length);
    public bool Healthy => framed && pending.Count == 0 && buffer.Count == 0;
    internal object TraceState()
    {
        var bytes = CollectionsMarshal.AsSpan(buffer);
        return new
        {
            expected_sequence = expected, syn_sequence = synSequence, framed,
            contiguous_bytes = buffer.Count, history_bytes = history.Count,
            pending_segments = pending.Count, pending_bytes = pending.Values.Sum(x => x.Length),
            next_pending_sequence = pending.Count == 0 ? (uint?)null : pending.Keys.MinBy(k => unchecked((int)(k - expected!.Value))),
            head_hex = Convert.ToHexStringLower(bytes[..Math.Min(32, bytes.Length)]),
            head_opcode = bytes.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : (uint?)null,
            head_body_length = bytes.Length >= 8 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) : (uint?)null,
            head_flag = bytes.Length >= 9 ? bytes[8] : (byte?)null
        };
    }
    public IReadOnlyList<GameFrame> Feed(uint sequence, ReadOnlySpan<byte> payload, bool syn = false, bool acknowledgement = false)
    {
        if (syn)
        {
            if (!payload.IsEmpty) throw new InvalidDataException("Unsupported SYN payload");
            if (expected.HasValue && synSequence != sequence) throw new InvalidDataException("New TCP lifetime requires new stream");
            expected ??= unchecked(sequence + 1); synSequence = sequence; return [];
        }
        if (payload.IsEmpty) return [];
        // RFC 9293 3.8.4: a keep-alive may carry one garbage octet at
        // SND.NXT-1. It is outside the receive window, not a data retransmit.
        if (acknowledgement && payload.Length == 1 && expected.HasValue && sequence == unchecked(expected.Value - 1)) return [];
        if (!expected.HasValue) { if (fullSyncValidator is null) return []; expected = sequence; }
        var delta = unchecked((int)(sequence - expected.Value));
        // Retransmission can split or combine segments differently. Compare the
        // overlapping bytes, not the segment lengths, before retaining anything.
        foreach (var (queuedSequence, bytes) in pending)
        {
            var offset = (long)unchecked((int)(queuedSequence - sequence));
            var start = Math.Max(0, offset); var end = Math.Min(payload.Length, offset + bytes.Length);
            if (end > start && !payload.Slice((int)start, (int)(end - start)).SequenceEqual(bytes.AsSpan((int)(start - offset), (int)(end - start))))
                throw new InvalidDataException("Conflicting TCP reorder");
        }
        if (delta > 0)
        {
            QueueFuture(payload, delta); return [];
        }
        Append(sequence, payload);
        while (pending.Count > 0)
        {
            var ready = pending.Keys.Where(k => unchecked((int)(k - expected.Value)) <= 0).OrderBy(k => unchecked((int)(k - expected.Value))).ToArray();
            if (ready.Length == 0) break;
            foreach (var key in ready) { if (pending.Remove(key, out var bytes)) Append(key, bytes); }
        }
        if (!framed)
        {
            if (buffer.Count < 4) return [];
            // The original server preamble is followed by strictly framed traffic.
            if (CollectionsMarshal.AsSpan(buffer)[..4].SequenceEqual("MMCG"u8))
            {
                for (var start = 4; start < Math.Min(buffer.Count, 4096); start++)
                {
                    FrameBatch batch;
                    try { batch = GameProtocol.ParseComplete(CollectionsMarshal.AsSpan(buffer), start); }
                    catch (InvalidDataException) { continue; }
                    if (!batch.Frames.Any(f => f.Opcode == loginOpcode)) continue;
                    framed = true; buffer.RemoveRange(0, batch.Consumed); return batch.Frames;
                }
                return [];
            }
            if (fullSyncValidator is null) throw new InvalidDataException("Not game server framing");
            for (var start = 0; start <= buffer.Count - 4; start++)
            {
                var bytes = CollectionsMarshal.AsSpan(buffer);
                if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[start..]) != loginOpcode) continue;
                if (buffer.Count - start < 9) { buffer.RemoveRange(0, start); return []; }
                var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(start + 4)..]); var flag = bytes[start + 8];
                if (length > GameProtocol.MaxBody || flag > 1) continue;
                if (length > buffer.Count - start - 9) { buffer.RemoveRange(0, start); return []; }
                var frame = new GameFrame(start, loginOpcode, flag, bytes.Slice(start + 9, (int)length).ToArray());
                if (!fullSyncValidator(frame)) continue;
                buffer.RemoveRange(0, start); framed = true; break;
            }
            if (!framed) { if (buffer.Count > 8) buffer.RemoveRange(0, buffer.Count - 8); return []; }
        }
        var result = GameProtocol.ParseComplete(CollectionsMarshal.AsSpan(buffer));
        buffer.RemoveRange(0, result.Consumed); return result.Frames;
    }
    private void QueueFuture(ReadOnlySpan<byte> payload, int delta)
    {
        if (delta > MaxBuffer) throw new InvalidDataException("TCP reorder budget exceeded");
        long start = delta, end = start + payload.Length; var retained = 0;
        var mergedKeys = new List<uint>();
        foreach (var key in pending.Keys.OrderBy(k => unchecked((int)(k - expected!.Value))))
        {
            var offset = (long)unchecked((int)(key - expected!.Value)); var bytes = pending[key];
            if (offset + bytes.Length < start) continue;
            if (offset > end) break;
            start = Math.Min(start, offset); end = Math.Max(end, offset + bytes.Length);
            mergedKeys.Add(key); retained += bytes.Length;
        }
        var length = end - start;
        if (pending.Count - mergedKeys.Count >= 2048 || BufferedBytes - retained + length > MaxBuffer)
            throw new InvalidDataException("TCP reorder budget exceeded");
        if (mergedKeys.Count == 1 && length == pending[mergedKeys[0]].Length) return;
        var merged = new byte[(int)length];
        foreach (var key in mergedKeys)
        {
            pending[key].CopyTo(merged, (int)(unchecked((int)(key - expected!.Value)) - start));
            pending.Remove(key);
        }
        payload.CopyTo(merged.AsSpan((int)(delta - start)));
        pending.Add(unchecked(expected!.Value + (uint)start), merged);
    }
    private void Append(uint sequence, ReadOnlySpan<byte> payload)
    {
        var overlap = -(long)unchecked((int)(sequence - expected!.Value));
        if (overlap > 0)
        {
            var start = (int)Math.Max(0, overlap - history.Count); var stop = (int)Math.Min(overlap, payload.Length);
            if (stop > start && !payload.Slice(start, stop - start).SequenceEqual(CollectionsMarshal.AsSpan(history).Slice((int)(history.Count - overlap + start), stop - start)))
                throw new InvalidDataException("Conflicting TCP retransmit");
            if (overlap >= payload.Length) return;
            payload = payload[(int)overlap..];
        }
        // Feed already compared these bytes. Release queued copies before charging
        // the newly contiguous prefix, retaining only a possible trailing suffix.
        foreach (var key in pending.Keys.ToArray())
        {
            var covered = (long)payload.Length - unchecked((int)(key - expected!.Value));
            if (covered <= 0) continue;
            var bytes = pending[key]; pending.Remove(key);
            if (covered < bytes.Length) pending.Add(unchecked(key + (uint)covered), bytes[(int)covered..]);
        }
        if (BufferedBytes + payload.Length > MaxBuffer) throw new InvalidDataException("TCP frame budget exceeded");
        buffer.AddRange(payload); history.AddRange(payload);
        if (history.Count > HistoryLimit) history.RemoveRange(0, history.Count - HistoryLimit);
        expected = unchecked(expected.Value + (uint)payload.Length);
    }
}
