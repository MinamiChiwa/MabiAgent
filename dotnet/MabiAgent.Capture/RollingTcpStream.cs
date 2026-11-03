using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace MabiAgent.Capture;

/// <summary>Bounded receive-only TCP reconstruction; completed frame bodies leave the stream immediately.</summary>
public sealed class RollingTcpStream(Func<GameFrame, bool>? fullSyncValidator = null)
{
    public const int MaxBuffer = GameProtocol.MaxBody + 4096, HistoryLimit = 65536;
    private uint? expected, synSequence;
    private readonly List<byte> buffer = [], history = [];
    private readonly Dictionary<uint, byte[]> pending = [];
    private bool framed;
    public int BufferedBytes => buffer.Count + history.Count + pending.Values.Sum(x => x.Length);
    public bool Healthy => framed && pending.Count == 0 && buffer.Count == 0;
    public IReadOnlyList<GameFrame> Feed(uint sequence, ReadOnlySpan<byte> payload, bool syn = false)
    {
        if (syn)
        {
            if (!payload.IsEmpty) throw new InvalidDataException("Unsupported SYN payload");
            if (expected.HasValue && synSequence != sequence) throw new InvalidDataException("New TCP lifetime requires new stream");
            expected ??= unchecked(sequence + 1); synSequence = sequence; return [];
        }
        if (payload.IsEmpty) return [];
        if (!expected.HasValue) { if (fullSyncValidator is null) return []; expected = sequence; }
        var delta = unchecked((int)(sequence - expected.Value));
        if (delta > 0)
        {
            if (pending.TryGetValue(sequence, out var old))
            {
                if (!payload.SequenceEqual(old)) throw new InvalidDataException("Conflicting TCP reorder");
                return [];
            }
            if (delta > MaxBuffer || pending.Count >= 2048 || BufferedBytes + payload.Length > MaxBuffer) throw new InvalidDataException("TCP reorder budget exceeded");
            pending.Add(sequence, payload.ToArray()); return [];
        }
        Append(sequence, payload);
        while (pending.Count > 0)
        {
            var ready = pending.Keys.Where(k => unchecked((int)(k - expected.Value)) <= 0).ToArray();
            if (ready.Length == 0) break;
            foreach (var key in ready) { var bytes = pending[key]; pending.Remove(key); Append(key, bytes); }
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
                    if (!batch.Frames.Any(f => f.Opcode == GameProtocol.LoginOpcode)) continue;
                    framed = true; buffer.RemoveRange(0, batch.Consumed); return batch.Frames;
                }
                return [];
            }
            if (fullSyncValidator is null) throw new InvalidDataException("Not game server framing");
            for (var start = 0; start <= buffer.Count - 4; start++)
            {
                var bytes = CollectionsMarshal.AsSpan(buffer);
                if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[start..]) != GameProtocol.LoginOpcode) continue;
                if (buffer.Count - start < 9) { buffer.RemoveRange(0, start); return []; }
                var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(start + 4)..]); var flag = bytes[start + 8];
                if (length > GameProtocol.MaxBody || flag > 1) continue;
                if (length > buffer.Count - start - 9) { buffer.RemoveRange(0, start); return []; }
                var frame = new GameFrame(start, GameProtocol.LoginOpcode, flag, bytes.Slice(start + 9, (int)length).ToArray());
                if (!fullSyncValidator(frame)) continue;
                buffer.RemoveRange(0, start); framed = true; break;
            }
            if (!framed) { if (buffer.Count > 8) buffer.RemoveRange(0, buffer.Count - 8); return []; }
        }
        var result = GameProtocol.ParseComplete(CollectionsMarshal.AsSpan(buffer));
        buffer.RemoveRange(0, result.Consumed); return result.Frames;
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
        if (BufferedBytes + payload.Length > MaxBuffer) throw new InvalidDataException("TCP frame budget exceeded");
        buffer.AddRange(payload); history.AddRange(payload);
        if (history.Count > HistoryLimit) history.RemoveRange(0, history.Count - HistoryLimit);
        expected = unchecked(expected.Value + (uint)payload.Length);
    }
}
