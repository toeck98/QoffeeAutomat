using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace QoffeeAutomat.Agent.Discovery;

/// <summary>A question of a DNS message. The name is a list of labels without dots.</summary>
/// <param name="Name">The labels of the name.</param>
/// <param name="Type">The record type (<see cref="DnsWire"/> constants).</param>
/// <param name="Class">The class with the "unicast response" bit (0x8000) still in place.</param>
internal sealed record DnsQuestion(IReadOnlyList<string> Name, ushort Type, ushort Class)
{
    /// <summary>The class without the unicast-response bit; 1 is "IN".</summary>
    public ushort BaseClass => (ushort)(Class & 0x7FFF);
}

/// <summary>A resource record that the responder sends. The data is already in wire format (names are not compressed).</summary>
/// <param name="Name">The owner name as labels.</param>
/// <param name="Type">The record type.</param>
/// <param name="CacheFlush">Whether the record is unique to this host (RFC 6762: the cache-flush bit).</param>
/// <param name="Ttl">The time to live in seconds; zero withdraws the record.</param>
/// <param name="Rdata">The record data.</param>
internal sealed record DnsRecord(IReadOnlyList<string> Name, ushort Type, bool CacheFlush, uint Ttl, byte[] Rdata);

/// <summary>A record in the answer section of a query (a "known answer"); only pointer targets are decoded.</summary>
/// <param name="Name">The owner name.</param>
/// <param name="Type">The record type.</param>
/// <param name="Ttl">The remaining time to live.</param>
/// <param name="Target">The target name of a PTR record, otherwise null.</param>
internal sealed record DnsAnswer(IReadOnlyList<string> Name, ushort Type, uint Ttl, IReadOnlyList<string>? Target);

/// <summary>The parts of a received DNS message that the responder needs.</summary>
/// <param name="Id">The message id.</param>
/// <param name="Flags">The header flags.</param>
/// <param name="Questions">The questions.</param>
/// <param name="Answers">The answer section.</param>
internal sealed record DnsPacket(ushort Id, ushort Flags, IReadOnlyList<DnsQuestion> Questions, IReadOnlyList<DnsAnswer> Answers)
{
    /// <summary>True for a query (QR bit clear) with the standard operation code.</summary>
    public bool IsQuery => (Flags & 0x8000) == 0 && ((Flags >> 11) & 0xF) == 0;
}

/// <summary>
/// The small part of the DNS wire format (RFC 1035) that multicast DNS and DNS-SD (RFC 6762, 6763) need: writing
/// responses with uncompressed names and reading queries with compressed names. A message that is malformed in any way
/// is rejected as a whole; the limits keep a hostile packet from costing more than a few hundred small allocations.
/// </summary>
internal static class DnsWire
{
    public const ushort TypeA = 1;
    public const ushort TypePtr = 12;
    public const ushort TypeTxt = 16;
    public const ushort TypeSrv = 33;
    public const ushort TypeAny = 255;
    public const ushort ClassIn = 1;
    public const ushort CacheFlushBit = 0x8000;

    /// <summary>The header flags of an authoritative response: QR and AA.</summary>
    public const ushort ResponseFlags = 0x8400;

    private const int MaxItems = 256;
    private const int MaxLabels = 127;
    private const int MaxPointerJumps = 32;

    /// <summary>Encodes a name in wire format.</summary>
    /// <param name="labels">The labels; each one 1 to 63 bytes of UTF-8.</param>
    /// <returns>The encoded name including the terminating zero.</returns>
    public static byte[] EncodeName(IReadOnlyList<string> labels)
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        WriteName(buffer, labels);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Compares two names label by label, ignoring ASCII case (RFC 1035).</summary>
    /// <param name="left">The first name.</param>
    /// <param name="right">The second name.</param>
    /// <returns>True when the names are the same.</returns>
    public static bool NameEquals(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index], right[index], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Builds a message.</summary>
    /// <param name="id">The message id; zero for multicast responses.</param>
    /// <param name="flags">The header flags.</param>
    /// <param name="questions">The question section (empty for multicast responses).</param>
    /// <param name="answers">The answer section.</param>
    /// <param name="additionals">The additional section.</param>
    /// <returns>The datagram.</returns>
    public static byte[] WriteMessage(
        ushort id,
        ushort flags,
        IReadOnlyList<DnsQuestion> questions,
        IReadOnlyList<DnsRecord> answers,
        IReadOnlyList<DnsRecord> additionals)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        WriteUInt16(buffer, id);
        WriteUInt16(buffer, flags);
        WriteUInt16(buffer, questions.Count);
        WriteUInt16(buffer, answers.Count);
        WriteUInt16(buffer, 0);
        WriteUInt16(buffer, additionals.Count);

        foreach (DnsQuestion question in questions)
        {
            WriteName(buffer, question.Name);
            WriteUInt16(buffer, question.Type);
            WriteUInt16(buffer, question.Class);
        }

        foreach (DnsRecord record in answers)
        {
            WriteRecord(buffer, record);
        }

        foreach (DnsRecord record in additionals)
        {
            WriteRecord(buffer, record);
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Reads the questions and the answer section of a message.</summary>
    /// <param name="data">The datagram.</param>
    /// <param name="packet">The decoded message when the datagram is valid.</param>
    /// <returns>True when the datagram could be read.</returns>
    public static bool TryParse(ReadOnlySpan<byte> data, [NotNullWhen(true)] out DnsPacket? packet)
    {
        packet = null;
        if (data.Length < 12)
        {
            return false;
        }

        ushort id = BinaryPrimitives.ReadUInt16BigEndian(data);
        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        int questionCount = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
        int answerCount = BinaryPrimitives.ReadUInt16BigEndian(data[6..]);
        if (questionCount > MaxItems || answerCount > MaxItems)
        {
            return false;
        }

        int offset = 12;
        var questions = new List<DnsQuestion>(questionCount);
        for (int index = 0; index < questionCount; index++)
        {
            if (!TryReadName(data, ref offset, out string[]? name) || offset + 4 > data.Length)
            {
                return false;
            }

            ushort type = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            ushort questionClass = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
            offset += 4;
            questions.Add(new DnsQuestion(name, type, questionClass));
        }

        var answers = new List<DnsAnswer>(answerCount);
        for (int index = 0; index < answerCount; index++)
        {
            if (!TryReadName(data, ref offset, out string[]? name) || offset + 10 > data.Length)
            {
                return false;
            }

            ushort type = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            uint ttl = BinaryPrimitives.ReadUInt32BigEndian(data[(offset + 4)..]);
            int length = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 8)..]);
            int dataStart = offset + 10;
            if (dataStart + length > data.Length)
            {
                return false;
            }

            string[]? target = null;
            if (type == TypePtr)
            {
                int position = dataStart;
                if (!TryReadName(data, ref position, out target))
                {
                    return false;
                }
            }

            answers.Add(new DnsAnswer(name, type, ttl, target));
            offset = dataStart + length;
        }

        packet = new DnsPacket(id, flags, questions, answers);
        return true;
    }

    private static void WriteRecord(ArrayBufferWriter<byte> buffer, DnsRecord record)
    {
        WriteName(buffer, record.Name);
        WriteUInt16(buffer, record.Type);
        WriteUInt16(buffer, ClassIn | (record.CacheFlush ? CacheFlushBit : 0));
        WriteUInt32(buffer, record.Ttl);
        WriteUInt16(buffer, record.Rdata.Length);
        buffer.Write(record.Rdata);
    }

    private static void WriteName(ArrayBufferWriter<byte> buffer, IReadOnlyList<string> labels)
    {
        foreach (string label in labels)
        {
            int length = Encoding.UTF8.GetByteCount(label);
            if (length is 0 or > 63)
            {
                throw new ArgumentException("A DNS label has 1 to 63 bytes.", nameof(labels));
            }

            Span<byte> span = buffer.GetSpan(1 + length);
            span[0] = (byte)length;
            Encoding.UTF8.GetBytes(label, span[1..]);
            buffer.Advance(1 + length);
        }

        buffer.GetSpan(1)[0] = 0;
        buffer.Advance(1);
    }

    private static void WriteUInt16(ArrayBufferWriter<byte> buffer, int value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(buffer.GetSpan(2), (ushort)value);
        buffer.Advance(2);
    }

    private static void WriteUInt32(ArrayBufferWriter<byte> buffer, uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(buffer.GetSpan(4), value);
        buffer.Advance(4);
    }

    private static bool TryReadName(ReadOnlySpan<byte> data, ref int offset, [NotNullWhen(true)] out string[]? labels)
    {
        labels = null;
        var list = new List<string>();
        int position = offset;
        int jumps = 0;
        int end = -1;

        while (true)
        {
            if (position >= data.Length)
            {
                return false;
            }

            int length = data[position];
            if (length == 0)
            {
                position++;
                break;
            }

            if ((length & 0xC0) == 0xC0)
            {
                // A compression pointer: the name continues elsewhere in the message.
                if (position + 1 >= data.Length)
                {
                    return false;
                }

                int pointer = ((length & 0x3F) << 8) | data[position + 1];
                if (end < 0)
                {
                    end = position + 2;
                }

                jumps++;
                if (jumps > MaxPointerJumps || pointer >= data.Length)
                {
                    return false;
                }

                position = pointer;
                continue;
            }

            if ((length & 0xC0) != 0)
            {
                return false;
            }

            position++;
            if (position + length > data.Length || list.Count >= MaxLabels)
            {
                return false;
            }

            list.Add(Encoding.UTF8.GetString(data.Slice(position, length)));
            position += length;
        }

        offset = end >= 0 ? end : position;
        labels = [.. list];
        return true;
    }
}
