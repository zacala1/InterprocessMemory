using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace InterprocessMemory
{
    /// <summary>One field of a stored schema descriptor, in the order the fields are laid out.</summary>
    internal readonly record struct DescriptorField(
        string Name,
        FieldTypeCode TypeCode,
        int ElementSize,
        int ArrayLength,
        int Alignment,
        long Offset,
        string? StructTypeName = null,
        TypeLayoutFingerprint? StructFingerprint = null);

    /// <summary>Where the descriptor block of a structured-memory region is, as recorded in the schema header.</summary>
    internal readonly record struct DescriptorLocation(long Offset, int Length, uint Crc32);

    /// <summary>A descriptor block that has been read and checked for consistency within itself.</summary>
    internal sealed class DecodedDescriptor
    {
        public DecodedDescriptor(int schemaVersion, int schemaHash, IReadOnlyList<DescriptorField> fields)
        {
            SchemaVersion = schemaVersion;
            SchemaHash = schemaHash;
            Fields = fields;
        }

        public int SchemaVersion { get; }

        public int SchemaHash { get; }

        public IReadOnlyList<DescriptorField> Fields { get; }
    }

    /// <summary>
    /// The field table that <see cref="StructuredMemory{TSchema}"/> can store behind the field data of a
    /// region (<see cref="StructuredMemoryOptions.StoreSchemaDescriptor"/>), so that a process without the
    /// schema can find out what the region holds.
    ///
    /// <para>The block is written once, before the schema header is published, and never changes. It is
    /// located through the schema header (marker, length, offset and CRC-32 at bytes 16 to 35). All integers are
    /// little endian.</para>
    ///
    /// <code>
    /// block header (32 bytes)
    ///   0  u32 magic "IMPD"      4  u16 format (1)       6  u16 reserved
    ///   8  u32 field count      12  u32 body length
    ///  16  i32 schema version   20  i32 schema hash     24  u64 reserved
    /// field entry, repeated in layout order
    ///   0  u8 type code          1  u8 flags (bit 0: struct info follows the name)
    ///   2  u16 name length       4  i32 element size     8  i32 array length
    ///  12  i32 alignment        16  i64 offset (from the start of the field data)
    ///  24  name (UTF-8)
    ///      struct info: u16 type name length, type name (UTF-8), u64 fingerprint low, u64 fingerprint high
    /// zero padding up to a multiple of 64 bytes
    /// </code>
    ///
    /// <para>Everything read from a region is untrusted: a decoder failure is always an
    /// <see cref="InvalidDataException"/>, and no allocation is sized from a value that was not checked
    /// against the length of the block first.</para>
    /// </summary>
    internal static class SchemaDescriptor
    {
        /// <summary>"IMPD", in the schema header and as the magic of the block.</summary>
        public const uint Marker = 0x44504D49;

        public const ushort BlockFormat = 1;

        public const int BlockHeaderSize = 32;

        /// <summary>Bytes of a field entry before its name.</summary>
        public const int FixedEntrySize = 24;

        public const int Alignment = 64;

        public const int MaxBlockLength = 1 << 20;

        public const int MaxNameBytes = 1024;

        public const int MaxTypeNameBytes = 1024;

        // Positions inside the 64 byte schema header of a structured-memory region.
        public const int HeaderMarkerOffset = 16;
        public const int HeaderLengthOffset = 20;
        public const int HeaderOffsetOffset = 24;
        public const int HeaderCrcOffset = 32;

        private const int SchemaHeaderSize = 64;
        private const byte FlagStructInfo = 1;

        // Throws instead of replacing what it cannot encode or decode: a name that does not survive a round trip
        // would make the stored schema disagree with the one in the code.
        private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        public static uint ComputeCrc32(ReadOnlySpan<byte> block) => System.IO.Hashing.Crc32.HashToUInt32(block);

        /// <summary>Builds the block for <paramref name="fields"/>, padded to a multiple of <see cref="Alignment"/>.</summary>
        /// <exception cref="ArgumentException">A name or the block as a whole exceeds what a reader accepts.</exception>
        public static byte[] Encode(int schemaVersion, int schemaHash, IReadOnlyList<DescriptorField> fields)
        {
            if (fields.Count == 0)
                throw new ArgumentException("A schema descriptor needs at least one field", nameof(fields));

            var names = new byte[fields.Count][];
            var typeNames = new byte[fields.Count][];
            long body = 0;
            for (int i = 0; i < fields.Count; i++)
            {
                DescriptorField field = fields[i];
                names[i] = EncodeText(field.Name ?? string.Empty, MaxNameBytes, $"The name of field '{field.Name}'");
                if (names[i].Length == 0)
                    throw new ArgumentException("A field name cannot be empty", nameof(fields));
                body += FixedEntrySize + names[i].Length;
                if (field.StructFingerprint.HasValue)
                {
                    typeNames[i] = EncodeText(
                        field.StructTypeName ?? string.Empty, MaxTypeNameBytes,
                        $"The struct type name of field '{field.Name}'");
                    body += sizeof(ushort) + typeNames[i].Length + 2 * sizeof(ulong);
                }
            }

            long padded = (BlockHeaderSize + body + Alignment - 1) / Alignment * Alignment;
            if (padded > MaxBlockLength)
                throw new ArgumentException(
                    $"The stored schema descriptor would take {padded} bytes; at most {MaxBlockLength} are supported. " +
                    "Use fewer or shorter field names, or do not store the descriptor.",
                    nameof(fields));

            var block = new byte[padded];
            Span<byte> span = block;
            BinaryPrimitives.WriteUInt32LittleEndian(span, Marker);
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(4), BlockFormat);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8), (uint)fields.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12), (uint)body);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(16), schemaVersion);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(20), schemaHash);

            int pos = BlockHeaderSize;
            for (int i = 0; i < fields.Count; i++)
            {
                DescriptorField field = fields[i];
                Span<byte> entry = span.Slice(pos);
                entry[0] = (byte)field.TypeCode;
                entry[1] = field.StructFingerprint.HasValue ? FlagStructInfo : (byte)0;
                BinaryPrimitives.WriteUInt16LittleEndian(entry.Slice(2), (ushort)names[i].Length);
                BinaryPrimitives.WriteInt32LittleEndian(entry.Slice(4), field.ElementSize);
                BinaryPrimitives.WriteInt32LittleEndian(entry.Slice(8), field.ArrayLength);
                BinaryPrimitives.WriteInt32LittleEndian(entry.Slice(12), field.Alignment);
                BinaryPrimitives.WriteInt64LittleEndian(entry.Slice(16), field.Offset);
                names[i].CopyTo(entry.Slice(FixedEntrySize));
                pos += FixedEntrySize + names[i].Length;

                if (field.StructFingerprint is { } fingerprint)
                {
                    Span<byte> info = span.Slice(pos);
                    BinaryPrimitives.WriteUInt16LittleEndian(info, (ushort)typeNames[i].Length);
                    typeNames[i].CopyTo(info.Slice(sizeof(ushort)));
                    int fp = sizeof(ushort) + typeNames[i].Length;
                    BinaryPrimitives.WriteUInt64LittleEndian(info.Slice(fp), fingerprint.Low);
                    BinaryPrimitives.WriteUInt64LittleEndian(info.Slice(fp + sizeof(ulong)), fingerprint.High);
                    pos += fp + 2 * sizeof(ulong);
                }
            }

            return block;
        }

        /// <summary>
        /// Reads the descriptor block. The block is checked against <paramref name="expectedCrc"/> and for
        /// consistency within itself; whether it matches a schema is for the caller to decide.
        /// </summary>
        /// <exception cref="InvalidDataException">The block is damaged, inconsistent or of an unsupported format.</exception>
        public static DecodedDescriptor Decode(ReadOnlySpan<byte> block, uint expectedCrc)
        {
            if (block.Length < BlockHeaderSize || block.Length % Alignment != 0 || block.Length > MaxBlockLength)
                throw Invalid($"its length {block.Length} is not valid");

            uint actualCrc = ComputeCrc32(block);
            if (actualCrc != expectedCrc)
                throw Invalid($"its checksum is 0x{actualCrc:X8}, the schema header says 0x{expectedCrc:X8}");

            if (BinaryPrimitives.ReadUInt32LittleEndian(block) != Marker)
                throw Invalid("it does not start with the descriptor magic");

            ushort format = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(4));
            if (format != BlockFormat)
                throw Invalid(
                    $"it has format {format}, which this version cannot read (it reads format {BlockFormat}); " +
                    "the region was written by a newer version of the library");

            uint fieldCount = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(8));
            uint bodyLength = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(12));
            int schemaVersion = BinaryPrimitives.ReadInt32LittleEndian(block.Slice(16));
            int schemaHash = BinaryPrimitives.ReadInt32LittleEndian(block.Slice(20));

            if (bodyLength > (uint)(block.Length - BlockHeaderSize))
                throw Invalid($"its body length {bodyLength} does not fit in the block");
            // Every entry takes at least FixedEntrySize + 1 bytes, which bounds the allocation below.
            if (fieldCount == 0 || fieldCount > bodyLength / (FixedEntrySize + 1))
                throw Invalid($"its field count {fieldCount} does not fit in a body of {bodyLength} bytes");

            ReadOnlySpan<byte> body = block.Slice(BlockHeaderSize, (int)bodyLength);
            var fields = new List<DescriptorField>((int)fieldCount);
            var names = new HashSet<string>(StringComparer.Ordinal);
            int pos = 0;
            for (uint i = 0; i < fieldCount; i++)
            {
                if (body.Length - pos < FixedEntrySize)
                    throw Invalid($"entry {i} is cut off");

                ReadOnlySpan<byte> entry = body.Slice(pos);
                byte typeCode = entry[0];
                byte flags = entry[1];
                int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(2));
                int elementSize = BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(4));
                int arrayLength = BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(8));
                int alignment = BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(12));
                long offset = BinaryPrimitives.ReadInt64LittleEndian(entry.Slice(16));

                if ((flags & ~FlagStructInfo) != 0)
                    throw Invalid($"entry {i} has unknown flags 0x{flags:X2}");
                if (nameLength == 0 || nameLength > MaxNameBytes || entry.Length - FixedEntrySize < nameLength)
                    throw Invalid($"the name of entry {i} has an invalid length {nameLength}");

                string name = DecodeText(entry.Slice(FixedEntrySize, nameLength), $"the name of entry {i}");
                if (string.IsNullOrWhiteSpace(name))
                    throw Invalid($"entry {i} has an empty name");
                if (!names.Add(name))
                    throw Invalid($"the field name '{name}' occurs twice");

                if (typeCode == (byte)FieldTypeCode.Unknown || !Enum.IsDefined(typeof(FieldTypeCode), (int)typeCode))
                    throw Invalid($"field '{name}' has the invalid type code {typeCode}");
                if (elementSize <= 0 || arrayLength <= 0)
                    throw Invalid($"field '{name}' has a non-positive size ({elementSize} x {arrayLength})");
                if (alignment <= 0 || (alignment & (alignment - 1)) != 0)
                    throw Invalid($"field '{name}' has the alignment {alignment}, which is not a power of two");
                if (offset < 0)
                    throw Invalid($"field '{name}' has the negative offset {offset}");

                int used = FixedEntrySize + nameLength;
                string? structTypeName = null;
                TypeLayoutFingerprint? fingerprint = null;
                if ((flags & FlagStructInfo) != 0)
                {
                    ReadOnlySpan<byte> info = entry.Slice(used);
                    if (info.Length < sizeof(ushort))
                        throw Invalid($"the struct information of field '{name}' is cut off");
                    int typeNameLength = BinaryPrimitives.ReadUInt16LittleEndian(info);
                    if (typeNameLength > MaxTypeNameBytes ||
                        info.Length - sizeof(ushort) < typeNameLength + 2 * sizeof(ulong))
                        throw Invalid($"the struct information of field '{name}' has an invalid length");

                    if (typeNameLength > 0)
                        structTypeName = DecodeText(
                            info.Slice(sizeof(ushort), typeNameLength), $"the struct type name of field '{name}'");
                    int fp = sizeof(ushort) + typeNameLength;
                    fingerprint = new TypeLayoutFingerprint(
                        BinaryPrimitives.ReadUInt64LittleEndian(info.Slice(fp)),
                        BinaryPrimitives.ReadUInt64LittleEndian(info.Slice(fp + sizeof(ulong))));
                    used += fp + 2 * sizeof(ulong);
                }

                fields.Add(new DescriptorField(
                    name, (FieldTypeCode)typeCode, elementSize, arrayLength, alignment, offset,
                    structTypeName, fingerprint));
                pos += used;
            }

            if (pos != body.Length)
                throw Invalid($"{body.Length - pos} bytes of its body are not part of any entry");

            return new DecodedDescriptor(schemaVersion, schemaHash, fields);
        }

        /// <summary>Records where the block is in the schema header (bytes 16 to 35).</summary>
        public static void WriteLocation(Span<byte> schemaHeader, DescriptorLocation location)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(schemaHeader.Slice(HeaderMarkerOffset), Marker);
            BinaryPrimitives.WriteUInt32LittleEndian(schemaHeader.Slice(HeaderLengthOffset), (uint)location.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(schemaHeader.Slice(HeaderOffsetOffset), (ulong)location.Offset);
            BinaryPrimitives.WriteUInt32LittleEndian(schemaHeader.Slice(HeaderCrcOffset), location.Crc32);
        }

        /// <summary>
        /// Reads the location of the block from the schema header. Returns <c>false</c> when the header does not
        /// carry the marker, which is how regions that store no descriptor look (including every region written
        /// by 4.0 and 4.1); the rest of that part of the header is not looked at then.
        /// </summary>
        /// <param name="schemaHeader">The 64 byte schema header.</param>
        /// <param name="payloadCapacity">The capacity of the region, without its own 128 byte header.</param>
        /// <param name="location">Where the block is, when the header has the marker.</param>
        /// <exception cref="InvalidDataException">The marker is there but the location does not fit the region.</exception>
        public static bool TryReadLocation(
            ReadOnlySpan<byte> schemaHeader, long payloadCapacity, out DescriptorLocation location)
        {
            location = default;
            if (BinaryPrimitives.ReadUInt32LittleEndian(schemaHeader.Slice(HeaderMarkerOffset)) != Marker)
                return false;

            uint length = BinaryPrimitives.ReadUInt32LittleEndian(schemaHeader.Slice(HeaderLengthOffset));
            ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(schemaHeader.Slice(HeaderOffsetOffset));
            uint crc = BinaryPrimitives.ReadUInt32LittleEndian(schemaHeader.Slice(HeaderCrcOffset));

            if (length < BlockHeaderSize || length > MaxBlockLength || length % Alignment != 0)
                throw Invalid($"the schema header gives it the invalid length {length}");
            if (offset < SchemaHeaderSize || offset % Alignment != 0 || offset > long.MaxValue)
                throw Invalid($"the schema header gives it the invalid offset {offset}");
            if (payloadCapacity < 0 || offset > (ulong)payloadCapacity || length > (ulong)payloadCapacity - offset)
                throw Invalid(
                    $"it would end at {offset + length}, past the {payloadCapacity} bytes of the region");

            location = new DescriptorLocation((long)offset, (int)length, crc);
            return true;
        }

        /// <summary>
        /// Lists, one line per field, how the fields stored in a region differ from the fields of a schema: a field
        /// that only one side has, and a field whose type, size, alignment or offset differs. Empty when the layouts
        /// are the same. At most <paramref name="maxLines"/> lines are given.
        /// </summary>
        public static string DescribeDifferences(
            IReadOnlyList<DescriptorField> stored, IReadOnlyList<DescriptorField> current, int maxLines = 16)
        {
            var currentByName = new Dictionary<string, DescriptorField>(current.Count, StringComparer.Ordinal);
            foreach (DescriptorField field in current)
                currentByName[field.Name] = field;

            var lines = new List<string>();
            var storedNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (DescriptorField field in stored)
            {
                storedNames.Add(field.Name);
                if (!currentByName.TryGetValue(field.Name, out DescriptorField other))
                    lines.Add($"field '{field.Name}': stored in the region only ({DescribeField(field, null)})");
                else if (!SameLayout(field, other))
                    lines.Add($"field '{field.Name}': stored {DescribeField(field, other)}, schema {DescribeField(other, field)}");
            }

            foreach (DescriptorField field in current)
            {
                if (!storedNames.Contains(field.Name))
                    lines.Add($"field '{field.Name}': in the schema only ({DescribeField(field, null)})");
            }

            if (lines.Count > maxLines)
            {
                int more = lines.Count - maxLines;
                lines.RemoveRange(maxLines, more);
                lines.Add($"... and {more} more");
            }

            return string.Join("\n", lines.ConvertAll(line => "  " + line));
        }

        private static bool SameLayout(DescriptorField a, DescriptorField b) =>
            a.TypeCode == b.TypeCode && a.ElementSize == b.ElementSize && a.ArrayLength == b.ArrayLength &&
            a.Alignment == b.Alignment && a.Offset == b.Offset;

        // The size and the alignment are named only when they are what differs from the other side.
        private static string DescribeField(DescriptorField field, DescriptorField? other)
        {
            string text = field.TypeCode + (field.ArrayLength > 1 ? $"[{field.ArrayLength}]" : string.Empty) +
                          $" at offset {field.Offset}";
            if (other is { } o)
            {
                if (o.ElementSize != field.ElementSize)
                    text += $", element size {field.ElementSize}";
                if (o.Alignment != field.Alignment)
                    text += $", alignment {field.Alignment}";
            }

            return text;
        }

        private static byte[] EncodeText(string text, int maxBytes, string what)
        {
            byte[] bytes;
            try
            {
                bytes = Utf8.GetBytes(text);
            }
            catch (EncoderFallbackException ex)
            {
                throw new ArgumentException($"{what} is not valid Unicode text", ex);
            }

            if (bytes.Length > maxBytes)
                throw new ArgumentException(
                    $"{what} takes {bytes.Length} bytes in UTF-8; at most {maxBytes} are supported " +
                    "when the schema descriptor is stored");
            return bytes;
        }

        private static string DecodeText(ReadOnlySpan<byte> bytes, string what)
        {
            try
            {
                return Utf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                throw Invalid($"{what} is not valid UTF-8");
            }
        }

        private static InvalidDataException Invalid(string reason) =>
            new($"The stored schema descriptor cannot be used: {reason}.");
    }
}
