namespace NTQlean.Core;

/// <summary>
/// Minimal protobuf wire-format scanner. Reports field number, wire type, value
/// offset and value size without interpreting semantics.
/// </summary>
public static class ProtoWalker
{
    public readonly record struct Field(int Number, int WireType, int ValueOffset, int ValueSize);

    public static IEnumerable<Field> Walk(byte[] buf)
    {
        var pos = 0;
        while (pos < buf.Length)
        {
            if (!TryReadVarint(buf, ref pos, out var key)) yield break;
            var field = (int)(key >> 3);
            var wireType = (int)(key & 7);
            if (field == 0) yield break;

            switch (wireType)
            {
                case 0: // varint
                    var varintStart = pos;
                    if (!TryReadVarint(buf, ref pos, out _)) yield break;
                    yield return new Field(field, wireType, varintStart, pos - varintStart);
                    break;
                case 1: // 64-bit
                    if (pos + 8 > buf.Length) yield break;
                    yield return new Field(field, wireType, pos, 8);
                    pos += 8;
                    break;
                case 2: // length-delimited
                    if (!TryReadVarint(buf, ref pos, out var len)) yield break;
                    if (len > (ulong)(buf.Length - pos) || len > int.MaxValue) yield break;
                    var valueOffset = pos;
                    var size = (int)len;
                    pos += size;
                    yield return new Field(field, wireType, valueOffset, size);
                    break;
                case 5: // 32-bit
                    if (pos + 4 > buf.Length) yield break;
                    yield return new Field(field, wireType, pos, 4);
                    pos += 4;
                    break;
                default:
                    yield break;
            }
        }
    }

    public static bool TryReadVarint(byte[] buf, ref int pos, out ulong value)
    {
        value = 0;
        var shift = 0;
        while (pos < buf.Length && shift < 64)
        {
            var b = buf[pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
        }
        return false;
    }
}
