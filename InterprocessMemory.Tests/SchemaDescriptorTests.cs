using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using InterprocessMemory;

namespace InterprocessMemory.Tests;

/// <summary>
/// The field list that a structured-memory region can store behind its field data
/// (<see cref="StructuredMemoryOptions.StoreSchemaDescriptor"/>).
/// </summary>
[TestFixture]
public class SchemaDescriptorTests
{
    private static string N(string prefix) => $"Descriptor_{prefix}_{Guid.NewGuid():N}";

    public struct DescPoint
    {
        public int X;
        public int Y;
    }

    public struct DescribedSchema : IMemorySchema
    {
        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<int>("Sequence");
            yield return FieldDefinition.Scalar<double>("Temperature");
            yield return FieldDefinition.String("Status", 32);
            yield return FieldDefinition.Array<float>("Samples", 8);
            yield return FieldDefinition.Struct<DescPoint>("Origin");
            yield return FieldDefinition.StructArray<DescPoint>("Path", 4);
            yield return FieldDefinition.Blob("Payload", 100);
            yield return FieldDefinition.Utf8String("Label", 40);
        }
    }

    public struct SmallSchema : IMemorySchema
    {
        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<int>("Sequence");
        }
    }

    public struct LargeSchema : IMemorySchema
    {
        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<int>("Sequence");
            yield return FieldDefinition.Array<byte>("Buffer", 4096);
        }
    }

    public struct VersionOne : IVersionedSchema
    {
        public int Version => 1;
        public bool IsCompatibleWith(int otherVersion) => true;

        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<int>("Sequence");
        }
    }

    public struct VersionTwo : IVersionedSchema
    {
        public int Version => 2;
        public bool IsCompatibleWith(int otherVersion) => true;

        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<int>("Sequence");
            yield return FieldDefinition.Array<byte>("Added", 1000);
        }
    }

    // Grows by 100 bytes: 64 + 128 = 192 bytes, exactly the size of a VersionOne region plus its descriptor.
    public struct VersionTwoSmallGrowth : IVersionedSchema
    {
        public int Version => 2;
        public bool IsCompatibleWith(int otherVersion) => true;

        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<int>("Sequence");
            yield return FieldDefinition.Array<byte>("Added", 100);
        }
    }

    public struct LongNameSchema : IMemorySchema
    {
        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<int>(new string('n', 2000));
        }
    }

    private static readonly StructuredMemoryOptions Stored = new() { StoreSchemaDescriptor = true };

    // ── The codec ────────────────────────────────────────────────────────────

    private static List<DescriptorField> SampleFields() => new()
    {
        new DescriptorField("Sequence", FieldTypeCode.Int32, 4, 1, 4, 0),
        new DescriptorField("Temperature", FieldTypeCode.Double, 8, 1, 8, 8),
        new DescriptorField("Status", FieldTypeCode.Char, 2, 32, 2, 16),
        new DescriptorField("Origin", FieldTypeCode.Struct, 8, 1, 8, 80, "Tests.Point", new TypeLayoutFingerprint(1, ulong.MaxValue)),
        new DescriptorField("Anonymous", FieldTypeCode.Struct, 12, 3, 8, 88, null, new TypeLayoutFingerprint(7, 8)),
        new DescriptorField("Größe-名前", FieldTypeCode.Blob, 1, 104, 4, 128),
    };

    private static byte[] SampleBlock() => SchemaDescriptor.Encode(7, 0x1234ABCD, SampleFields());

    private static uint Crc(byte[] block) => SchemaDescriptor.ComputeCrc32(block);

    [Test]
    public void Codec_RoundTripsEveryKindOfField()
    {
        var fields = SampleFields();
        byte[] block = SchemaDescriptor.Encode(7, 0x1234ABCD, fields);

        DecodedDescriptor decoded = SchemaDescriptor.Decode(block, Crc(block));

        Assert.That(decoded.SchemaVersion, Is.EqualTo(7));
        Assert.That(decoded.SchemaHash, Is.EqualTo(0x1234ABCD));
        Assert.That(decoded.Fields.SequenceEqual(fields), Is.True);
    }

    [Test]
    public void Codec_PadsTheBlockToAMultipleOf64()
    {
        for (int count = 1; count <= 40; count++)
        {
            var fields = Enumerable.Range(0, count)
                .Select(i => new DescriptorField("Field" + i, FieldTypeCode.Int32, 4, 1, 4, i * 4L))
                .ToList();
            byte[] block = SchemaDescriptor.Encode(1, 2, fields);
            Assert.That(block.Length % 64, Is.EqualTo(0));
            Assert.That(block.Length, Is.GreaterThanOrEqualTo(32 + count * 25));
        }
    }

    [Test]
    public void Codec_RefusesWhatReadersWouldRefuse()
    {
        Assert.Throws<ArgumentException>(() => SchemaDescriptor.Encode(1, 2, new List<DescriptorField>()));

        // A name that does not fit, and one that is not text.
        Assert.Throws<ArgumentException>(() => SchemaDescriptor.Encode(1, 2, new List<DescriptorField>
        {
            new(new string('n', SchemaDescriptor.MaxNameBytes + 1), FieldTypeCode.Int32, 4, 1, 4, 0)
        }));
        Assert.Throws<ArgumentException>(() => SchemaDescriptor.Encode(1, 2, new List<DescriptorField>
        {
            new("lone\uD800surrogate", FieldTypeCode.Int32, 4, 1, 4, 0)
        }));
        Assert.Throws<ArgumentException>(() => SchemaDescriptor.Encode(1, 2, new List<DescriptorField>
        {
            new("", FieldTypeCode.Int32, 4, 1, 4, 0)
        }));

        // The largest name is accepted: the limit counts UTF-8 bytes, not characters.
        byte[] block = SchemaDescriptor.Encode(1, 2, new List<DescriptorField>
        {
            new(new string('n', SchemaDescriptor.MaxNameBytes), FieldTypeCode.Int32, 4, 1, 4, 0)
        });
        Assert.That(SchemaDescriptor.Decode(block, Crc(block)).Fields[0].Name.Length, Is.EqualTo(SchemaDescriptor.MaxNameBytes));

        // A block of more than 1 MiB.
        var many = Enumerable.Range(0, 1200)
            .Select(i => new DescriptorField(i.ToString("D4") + new string('x', 1000), FieldTypeCode.Int32, 4, 1, 4, i * 4L))
            .ToList();
        var tooBig = Assert.Throws<ArgumentException>(() => SchemaDescriptor.Encode(1, 2, many));
        Assert.That(tooBig!.Message, Does.Contain("at most"));
    }

    [Test]
    public void Decode_RejectsEveryKindOfDamage()
    {
        byte[] baseline = SampleBlock();
        // The first entry is "Sequence": its type code is at 32, flags at 33, name length at 34, element size at 36,
        // array length at 40, alignment at 44, offset at 48 and the name from 56 on.
        var cases = new List<(string What, Action<byte[]> Damage)>
        {
            ("magic", b => b[0] ^= 0xFF),
            ("format", b => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), 2)),
            ("field count of 0", b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), 0)),
            ("huge field count", b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), uint.MaxValue)),
            ("one field too many", b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), 7)),
            ("body longer than the block", b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), int.MaxValue)),
            ("body that cuts the last entry", b => BinaryPrimitives.WriteUInt32LittleEndian(
                b.AsSpan(12), BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(12)) - 1)),
            ("body with bytes after the last entry", b => BinaryPrimitives.WriteUInt32LittleEndian(
                b.AsSpan(12), BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(12)) + 1)),
            ("unknown flags", b => b[33] = 0x80),
            ("type code 0", b => b[32] = 0),
            ("undefined type code", b => b[32] = 200),
            ("name length 0", b => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(34), 0)),
            ("name length past the body", b => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(34), 0xFFFF)),
            ("name that is not UTF-8", b => b[56] = 0xFF),
            ("name of blanks", b => b.AsSpan(56, 8).Fill((byte)' ')),
            ("element size 0", b => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(36), 0)),
            ("negative array length", b => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(40), -1)),
            ("alignment 0", b => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(44), 0)),
            ("alignment 3", b => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(44), 3)),
            ("negative offset", b => BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(48), -1)),
        };

        foreach (var (what, damage) in cases)
        {
            byte[] block = (byte[])baseline.Clone();
            damage(block);
            // Seal the damaged block, so that the checksum does not hide what is being tested.
            var error = Assert.Throws<InvalidDataException>(
                () => SchemaDescriptor.Decode(block, Crc(block)), what);
            Assert.That(error!.Message, Does.Contain("stored schema descriptor"));
        }

        // The checksum itself, and a block whose length is not a multiple of 64.
        Assert.Throws<InvalidDataException>(() => SchemaDescriptor.Decode(baseline, Crc(baseline) ^ 1));
        Assert.Throws<InvalidDataException>(() => SchemaDescriptor.Decode(baseline.AsSpan(0, 33), 0));
        Assert.Throws<InvalidDataException>(() => SchemaDescriptor.Decode(ReadOnlySpan<byte>.Empty, 0));
    }

    [Test]
    public void Decode_RejectsDuplicateNamesAndDamagedStructInformation()
    {
        byte[] duplicate = SchemaDescriptor.Encode(1, 2, new List<DescriptorField>
        {
            new("Same", FieldTypeCode.Int32, 4, 1, 4, 0),
            new("Same", FieldTypeCode.Int32, 4, 1, 4, 4),
        });
        var error = Assert.Throws<InvalidDataException>(() => SchemaDescriptor.Decode(duplicate, Crc(duplicate)));
        Assert.That(error!.Message, Does.Contain("Same"));

        // One struct field named "S": 32 byte block header, 24 byte entry, 1 byte name, then the struct information.
        byte[] withStruct = SchemaDescriptor.Encode(1, 2, new List<DescriptorField>
        {
            new("S", FieldTypeCode.Struct, 8, 1, 8, 0, "T", new TypeLayoutFingerprint(1, 2)),
        });
        Assert.That(SchemaDescriptor.Decode(withStruct, Crc(withStruct)).Fields[0].StructTypeName, Is.EqualTo("T"));

        foreach (ushort typeNameLength in new ushort[] { 0xFFFF, (ushort)(SchemaDescriptor.MaxTypeNameBytes + 1), 100 })
        {
            byte[] damaged = (byte[])withStruct.Clone();
            BinaryPrimitives.WriteUInt16LittleEndian(damaged.AsSpan(32 + 24 + 1), typeNameLength);
            Assert.Throws<InvalidDataException>(() => SchemaDescriptor.Decode(damaged, Crc(damaged)));
        }
    }

    [Test, Timeout(60000)]
    public void Decode_OfRandomlyDamagedBlocks_OnlyEverThrowsInvalidDataException()
    {
        byte[] baseline = SampleBlock();
        var random = new Random(20261030);

        for (int i = 0; i < 30000; i++)
        {
            byte[] block = (byte[])baseline.Clone();
            int changes = random.Next(1, 7);
            for (int c = 0; c < changes; c++)
            {
                // Mostly the header and the first entries, where the lengths are, but anywhere now and then.
                int at = random.Next(4) == 0 ? random.Next(block.Length) : random.Next(Math.Min(block.Length, 120));
                block[at] = (byte)random.Next(256);
            }

            ReadOnlySpan<byte> span = block;
            if (random.Next(8) == 0)
                span = span.Slice(0, random.Next(0, block.Length / 64 + 1) * 64);

            try
            {
                DecodedDescriptor decoded = SchemaDescriptor.Decode(span, SchemaDescriptor.ComputeCrc32(span));
                // What gets through is internally consistent.
                Assert.That(decoded.Fields.Count, Is.GreaterThan(0));
                Assert.That(decoded.Fields.Select(f => f.Name).Distinct().Count(), Is.EqualTo(decoded.Fields.Count));
                foreach (DescriptorField f in decoded.Fields)
                {
                    Assert.That(f.ElementSize, Is.GreaterThan(0));
                    Assert.That(f.ArrayLength, Is.GreaterThan(0));
                    Assert.That(f.Offset, Is.GreaterThanOrEqualTo(0L));
                }
            }
            catch (InvalidDataException)
            {
            }
            catch (Exception ex) when (ex is not AssertionException)
            {
                Assert.Fail($"iteration {i}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static byte[] HeaderWith(uint marker, uint length, ulong offset, uint crc)
    {
        var header = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(SchemaDescriptor.HeaderMarkerOffset), marker);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(SchemaDescriptor.HeaderLengthOffset), length);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(SchemaDescriptor.HeaderOffsetOffset), offset);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(SchemaDescriptor.HeaderCrcOffset), crc);
        return header;
    }

    [Test]
    public void Location_IsAbsentWithoutTheMarker_WhateverElseTheHeaderHolds()
    {
        // Regions written before the descriptor existed have zeros there; anything else without the marker is
        // not looked at either.
        Assert.That(SchemaDescriptor.TryReadLocation(new byte[64], 4096, out _), Is.False);
        Assert.That(SchemaDescriptor.TryReadLocation(HeaderWith(0, 17, 3, 5), 4096, out _), Is.False);
        Assert.That(SchemaDescriptor.TryReadLocation(HeaderWith(0x44504D48, 128, 64, 5), 4096, out _), Is.False);
    }

    [Test]
    public void Location_IsReadWhenItFitsTheRegion_AndRejectedWhenItDoesNot()
    {
        Assert.That(SchemaDescriptor.TryReadLocation(HeaderWith(SchemaDescriptor.Marker, 128, 256, 0xABCD), 384, out var location), Is.True);
        Assert.That(location, Is.EqualTo(new DescriptorLocation(256, 128, 0xABCD)));

        var rejected = new (string What, byte[] Header, long Capacity)[]
        {
            ("length 0", HeaderWith(SchemaDescriptor.Marker, 0, 256, 0), 4096),
            ("length 32", HeaderWith(SchemaDescriptor.Marker, 32, 256, 0), 4096),
            ("length not a multiple of 64", HeaderWith(SchemaDescriptor.Marker, 100, 256, 0), 4096),
            ("length over the limit", HeaderWith(SchemaDescriptor.Marker, (1 << 20) + 64, 256, 0), 1 << 30),
            ("offset inside the header", HeaderWith(SchemaDescriptor.Marker, 64, 0, 0), 4096),
            ("offset not aligned", HeaderWith(SchemaDescriptor.Marker, 64, 100, 0), 4096),
            ("offset past the region", HeaderWith(SchemaDescriptor.Marker, 64, 8192, 0), 4096),
            ("block ending past the region", HeaderWith(SchemaDescriptor.Marker, 128, 4032, 0), 4096),
            ("offset that overflows", HeaderWith(SchemaDescriptor.Marker, 64, ulong.MaxValue - 63, 0), 4096),
        };

        foreach (var (what, header, capacity) in rejected)
        {
            Assert.Throws<InvalidDataException>(
                () => SchemaDescriptor.TryReadLocation(header, capacity, out _), what);
        }
    }

    // ── MemoryRegion with a range of acceptable capacities ───────────────────

    [Test]
    public void FlexibleRegion_AcceptsALargerExistingRegion_AndReportsItsCapacity()
    {
        string name = N("FlexLarger");
        using var creator = MemoryRegion.CreateOrOpen(name, 8192, 4096, null, RegionKind.RawMemory);
        Assert.That(creator.IsOwner, Is.True);
        Assert.That(creator.Capacity, Is.EqualTo(8192));

        creator.Write(new byte[] { 1, 2, 3, 4 }, 8000);

        // Asking for less than there is, and for exactly what there is, both find the existing region.
        foreach (long requested in new long[] { 4096, 8192 })
        {
            using var other = MemoryRegion.CreateOrOpen(name, requested, requested, null, RegionKind.RawMemory);
            Assert.That(other.IsOwner, Is.False);
            Assert.That(other.Capacity, Is.EqualTo(8192));
            var seen = new byte[4];
            other.Read(seen, 8000);
            Assert.That(seen.SequenceEqual(new byte[] { 1, 2, 3, 4 }), Is.True);
        }

        // Asking for more than there is, with a minimum that fits, works as well.
        using var wide = MemoryRegion.CreateOrOpen(name, 16384, 8192, null, RegionKind.RawMemory);
        Assert.That(wide.IsOwner, Is.False);
        Assert.That(wide.Capacity, Is.EqualTo(8192));
    }

    [Test]
    public void FlexibleRegion_RefusesASmallerExistingRegion()
    {
        string name = N("FlexSmaller");
        using var creator = MemoryRegion.CreateOrOpen(name, 4096, 4096, null, RegionKind.RawMemory);

        // The wording depends on the platform (a file of another size on Linux, the header on Windows).
        Assert.Throws<InvalidOperationException>(
            () => MemoryRegion.CreateOrOpen(name, 8192, 4097, null, RegionKind.RawMemory));

        // The failed open left the region as it was.
        using var again = MemoryRegion.OpenExisting(name, null, RegionKind.RawMemory);
        Assert.That(again.Capacity, Is.EqualTo(4096));
    }

    [Test]
    public void FlexibleRegion_DoesNotChangeTheExactMatchOfTheOrdinaryOverloads()
    {
        string name = N("FlexExact");
        using var creator = MemoryRegion.CreateOrOpen(name, 8192, 4096, null, RegionKind.RawMemory);

        Assert.Throws<InvalidOperationException>(() => MemoryRegion.CreateOrOpen(name, 4096));
        Assert.Throws<InvalidOperationException>(() => MemoryRegion.CreateOrOpen(name, 16384));
        using var exact = MemoryRegion.CreateOrOpen(name, 8192);
        Assert.That(exact.IsOwner, Is.False);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => MemoryRegion.CreateOrOpen(N("FlexBad"), 4096, 8192, null, RegionKind.RawMemory));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MemoryRegion.CreateOrOpen(N("FlexBad"), 4096, 0, null, RegionKind.RawMemory));
    }

    // A flexible process whose request is not the size of the mapping must not initialize it: the creator of the
    // mapping asked for that size, and a header with another capacity would send the writes of this process past the
    // end of the mapping. The tests prepare a file that has the size but no header yet, which is what a second
    // process sees in the moment between the creator sizing the file and writing the header.

    [Test, Timeout(60000)]
    public void FlexibleRegion_WhoseRequestDoesNotFitTheMapping_LeavesTheInitializationToItsCreator()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Ignore("Prepares a file in /dev/shm.");

        string name = N("NoInit");
        string path = "/dev/shm/" + name;
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite))
            file.SetLength(128 + 1024);

        try
        {
            // Asks for 4096 bytes but accepts 512 or more: the file is acceptable, yet not what was asked for.
            Assert.Throws<InvalidDataException>(
                () => MemoryRegion.CreateOrOpen(name, 4096, 512, null, RegionKind.RawMemory));

            byte[] content = File.ReadAllBytes(path);
            Assert.That(content.Length, Is.EqualTo(128 + 1024));
            Assert.That(content.All(b => b == 0), Is.True, "the header must not have been written by the opener");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test, Timeout(60000)]
    public void FlexibleRegion_WhoseRequestDoesNotFitTheMapping_AdoptsTheCapacityOfTheCreator()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Ignore("Prepares a file in /dev/shm.");

        string name = N("Adopt");
        string path = "/dev/shm/" + name;
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite))
            file.SetLength(128 + 1024);

        try
        {
            var opener = Task.Run(() => MemoryRegion.CreateOrOpen(name, 4096, 512, null, RegionKind.RawMemory));
            Thread.Sleep(300);
            Assert.That(opener.IsCompleted, Is.False, "it has to wait for the header");

            // A process that asks for exactly the size of the file is the one that initializes it.
            using var creator = MemoryRegion.CreateOrOpen(name, 1024, 1024, null, RegionKind.RawMemory);
            using var adopted = opener.GetAwaiter().GetResult();

            Assert.That(creator.IsOwner, Is.True);
            Assert.That(adopted.IsOwner, Is.False);
            Assert.That(adopted.Capacity, Is.EqualTo(1024));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── StructuredMemory ─────────────────────────────────────────────────────

    private sealed record RawRegion(byte[] Header, byte[]? Block, long Capacity);

    /// <summary>Reads the schema header, and the descriptor block when the header has one, as another tool would.</summary>
    private static RawRegion ReadRaw(string name)
    {
        using var raw = MemoryRegion.OpenExisting(name, null, RegionKind.StructuredMemory);
        var header = new byte[64];
        raw.Read(header, 0);

        byte[]? block = null;
        if (SchemaDescriptor.TryReadLocation(header, raw.Capacity, out DescriptorLocation location))
        {
            block = new byte[location.Length];
            raw.Read(block, location.Offset);
        }

        return new RawRegion(header, block, raw.Capacity);
    }

    private static long DataEnd(IReadOnlyList<DescriptorField> fields) =>
        fields.Max(f => f.Offset + (long)f.ElementSize * f.ArrayLength);

    [Test]
    public void StoredDescriptor_IsBehindTheFieldData_AndDescribesEveryField()
    {
        string name = N("Stored");
        using var memory = StructuredMemory<DescribedSchema>.CreateOrOpen(name, new DescribedSchema(), Stored);

        Assert.That(memory.IsOwner, Is.True);
        Assert.That(memory.HasStoredDescriptor, Is.True);

        RawRegion raw = ReadRaw(name);
        Assert.That(raw.Block, Is.Not.Null);
        Assert.That(SchemaDescriptor.TryReadLocation(raw.Header, raw.Capacity, out DescriptorLocation location), Is.True);
        DecodedDescriptor decoded = SchemaDescriptor.Decode(raw.Block!, location.Crc32);

        // The same things the header says about the schema.
        Assert.That(decoded.SchemaVersion, Is.EqualTo(BitConverter.ToInt32(raw.Header, 4)));
        Assert.That(decoded.SchemaHash, Is.EqualTo(BitConverter.ToInt32(raw.Header, 12)));
        Assert.That(decoded.Fields.Count, Is.EqualTo(BitConverter.ToInt32(raw.Header, 8)));

        // Every field, in the order of the schema, with the layout the schema gets.
        string[] expectedNames = { "Sequence", "Temperature", "Status", "Samples", "Origin", "Path", "Payload", "Label" };
        Assert.That(decoded.Fields.Select(f => f.Name).SequenceEqual(expectedNames), Is.True);
        FieldTypeCode[] expectedCodes =
        {
            FieldTypeCode.Int32, FieldTypeCode.Double, FieldTypeCode.Char, FieldTypeCode.Single,
            FieldTypeCode.Struct, FieldTypeCode.Struct, FieldTypeCode.Blob, FieldTypeCode.Utf8String
        };
        Assert.That(decoded.Fields.Select(f => f.TypeCode).SequenceEqual(expectedCodes), Is.True);

        long end = 0;
        foreach (DescriptorField f in decoded.Fields)
        {
            Assert.That(f.Offset % f.Alignment, Is.EqualTo(0L), f.Name);
            Assert.That(f.Offset, Is.GreaterThanOrEqualTo(end), f.Name);
            end = f.Offset + (long)f.ElementSize * f.ArrayLength;
        }

        // The block starts where the fields end (the data is padded to 64 bytes behind a 64 byte header) and ends
        // with the region.
        long fieldsEnd = 64 + ((DataEnd(decoded.Fields) + 63) & ~63L);
        Assert.That(location.Offset, Is.EqualTo(fieldsEnd));
        Assert.That(raw.Capacity, Is.EqualTo(fieldsEnd + location.Length));
        Assert.That(location.Length % 64, Is.EqualTo(0));
    }

    [Test]
    public void StoredDescriptor_NamesTheTypeOfStructFields()
    {
        string name = N("StructInfo");
        using var memory = StructuredMemory<DescribedSchema>.CreateOrOpen(name, new DescribedSchema(), Stored);

        RawRegion raw = ReadRaw(name);
        SchemaDescriptor.TryReadLocation(raw.Header, raw.Capacity, out DescriptorLocation location);
        DecodedDescriptor decoded = SchemaDescriptor.Decode(raw.Block!, location.Crc32);

        foreach (string structField in new[] { "Origin", "Path" })
        {
            DescriptorField f = decoded.Fields.Single(x => x.Name == structField);
            Assert.That(f.StructTypeName, Is.EqualTo(typeof(DescPoint).FullName));
            Assert.That(f.StructFingerprint, Is.EqualTo((TypeLayoutFingerprint?)TypeLayoutFingerprint.Create<DescPoint>()));
        }

        foreach (DescriptorField f in decoded.Fields.Where(x => x.TypeCode != FieldTypeCode.Struct))
        {
            Assert.That(f.StructTypeName, Is.Null, f.Name);
            Assert.That(f.StructFingerprint.HasValue, Is.False, f.Name);
        }
    }

    [Test]
    public void WithoutTheOption_TheRegionIsExactlyWhatItWasBefore()
    {
        string name = N("Plain");
        using var memory = StructuredMemory<DescribedSchema>.CreateOrOpen(name, new DescribedSchema());

        Assert.That(memory.HasStoredDescriptor, Is.False);

        RawRegion raw = ReadRaw(name);
        Assert.That(raw.Block, Is.Null);
        // Nothing but the four values of 4.0 and 4.1 in the schema header; the part that holds the location of a
        // descriptor is zero, and the region is 64 bytes of header plus the fields rounded up to 64.
        Assert.That(BitConverter.ToUInt32(raw.Header, 0), Is.EqualTo(0x53504D49u));
        Assert.That(raw.Header.Skip(16).All(b => b == 0), Is.True);
        Assert.That(raw.Capacity % 64, Is.EqualTo(0L));

        string storedName = N("PlainVsStored");
        using var stored = StructuredMemory<DescribedSchema>.CreateOrOpen(storedName, new DescribedSchema(), Stored);
        RawRegion storedRaw = ReadRaw(storedName);
        Assert.That(storedRaw.Capacity, Is.GreaterThan(raw.Capacity));

        // The schema hash does not depend on the option: it is the same value 4.0 and 4.1 computed.
        Assert.That(storedRaw.Header.AsSpan(0, 16).SequenceEqual(raw.Header.AsSpan(0, 16)), Is.True);
    }

    [Test]
    public void ProcessesWithAndWithoutTheOption_ShareOneRegion()
    {
        string name = N("Share");
        using var creator = StructuredMemory<DescribedSchema>.CreateOrOpen(name, new DescribedSchema(), Stored);
        creator.Write("Sequence", 42);
        creator.WriteString("Status", "Ready");

        // Every way of opening finds the region, whether the opener asks for the descriptor or not.
        using var plain = StructuredMemory<DescribedSchema>.CreateOrOpen(name, new DescribedSchema());
        using var withOption = StructuredMemory<DescribedSchema>.CreateOrOpen(name, new DescribedSchema(), Stored);
        using var existing = StructuredMemory<DescribedSchema>.OpenExisting(name, new DescribedSchema());

        foreach (var opener in new[] { plain, withOption, existing })
        {
            Assert.That(opener.IsOwner, Is.False);
            Assert.That(opener.HasStoredDescriptor, Is.True);
            Assert.That(opener.Read<int>("Sequence"), Is.EqualTo(42));
            Assert.That(opener.ReadString("Status"), Is.EqualTo("Ready"));
        }

        existing.Write("Temperature", 21.5);
        Assert.That(creator.Read<double>("Temperature"), Is.EqualTo(21.5));
    }

    [Test]
    public void TheOption_DoesNothingForARegionThatAlreadyExists()
    {
        string name = N("AlreadyThere");
        using var creator = StructuredMemory<DescribedSchema>.CreateOrOpen(name, new DescribedSchema());
        long capacity = ReadRaw(name).Capacity;

        using var asked = StructuredMemory<DescribedSchema>.CreateOrOpen(name, new DescribedSchema(), Stored);

        Assert.That(asked.IsOwner, Is.False);
        Assert.That(asked.HasStoredDescriptor, Is.False);
        Assert.That(ReadRaw(name).Capacity, Is.EqualTo(capacity));
    }

    [Test, Timeout(120000)]
    public void CreatorsRacingWithDifferentOptions_AgreeOnOneRegion()
    {
        for (int round = 0; round < 200; round++)
        {
            string name = N("Race" + round);
            using var gate = new Barrier(2);
            StructuredMemory<DescribedSchema>? first = null, second = null;
            Exception? failure = null;

            var a = Task.Run(() =>
            {
                try
                {
                    gate.SignalAndWait();
                    first = StructuredMemory<DescribedSchema>.CreateOrOpen(name, new DescribedSchema(), Stored);
                }
                catch (Exception ex) { failure = ex; }
            });
            var b = Task.Run(() =>
            {
                try
                {
                    gate.SignalAndWait();
                    second = StructuredMemory<DescribedSchema>.CreateOrOpen(name, new DescribedSchema());
                }
                catch (Exception ex) { failure = ex; }
            });
            Task.WaitAll(a, b);

            try
            {
                Assert.That(failure, Is.Null, failure?.ToString());
                Assert.That((first!.IsOwner ? 1 : 0) + (second!.IsOwner ? 1 : 0), Is.EqualTo(1));
                Assert.That(first.HasStoredDescriptor, Is.EqualTo(second.HasStoredDescriptor));
                // The creator that asked for the descriptor stored it; the other one created a region without.
                Assert.That(first.HasStoredDescriptor, Is.EqualTo(first.IsOwner));

                first.Write("Sequence", round);
                Assert.That(second.Read<int>("Sequence"), Is.EqualTo(round));
            }
            finally
            {
                first?.Dispose();
                second?.Dispose();
            }
        }
    }

    [Test]
    public void ASchemaOfAnotherSize_IsStillRefusedByCreateOrOpen_AsBefore()
    {
        // Both directions, with and without a descriptor: the exact size has always been required.
        foreach (bool stored in new[] { false, true })
        {
            var options = new StructuredMemoryOptions { StoreSchemaDescriptor = stored };

            // The region is smaller than the schema needs: the wording depends on the platform.
            string smallFirst = N("SmallFirst" + stored);
            using (StructuredMemory<SmallSchema>.CreateOrOpen(smallFirst, new SmallSchema(), options))
            {
                Assert.Throws<InvalidOperationException>(
                    () => StructuredMemory<LargeSchema>.CreateOrOpen(smallFirst, new LargeSchema(), options));
            }

            // The region is larger: StructuredMemory itself says so.

            string largeFirst = N("LargeFirst" + stored);
            using (StructuredMemory<LargeSchema>.CreateOrOpen(largeFirst, new LargeSchema(), options))
            {
                var error = Assert.Throws<InvalidOperationException>(
                    () => StructuredMemory<SmallSchema>.CreateOrOpen(largeFirst, new SmallSchema(), options));
                Assert.That(error!.Message, Does.Contain("Capacity mismatch"));
            }
        }
    }

    [Test]
    public void SchemaVersions_WorkAsBefore_ForARegionThatStoresItsDescriptor()
    {
        string name = N("Versions");
        using var writer = StructuredMemory<VersionTwo>.CreateOrOpen(name, new VersionTwo(), Stored);
        writer.Write("Sequence", 5);

        // An older schema reads a region of a newer one that only appended fields (Forward), and a strict one
        // reports the versions, not the size.
        using var reader = StructuredMemory<VersionOne>.OpenExisting(name, new VersionOne(), SchemaCompatibility.Forward);
        Assert.That(reader.StoredSchemaVersion, Is.EqualTo(2));
        Assert.That(reader.HasStoredDescriptor, Is.True);
        Assert.That(reader.Read<int>("Sequence"), Is.EqualTo(5));

        var strict = Assert.Throws<InvalidOperationException>(
            () => StructuredMemory<VersionOne>.OpenExisting(name, new VersionOne()));
        Assert.That(strict!.Message, Does.Contain("version mismatch"));

        // The same version with another size is reported as a size mismatch, with the descriptor in the message.
        string other = N("VersionsSize");
        using var created = StructuredMemory<LargeSchema>.CreateOrOpen(other, new LargeSchema(), Stored);
        var size = Assert.Throws<InvalidDataException>(
            () => StructuredMemory<SmallSchema>.OpenExisting(other, new SmallSchema()));
        Assert.That(size!.Message, Does.Contain("size mismatch"));
        Assert.That(size.Message, Does.Contain("descriptor"));
    }

    [Test]
    public void ANewerSchema_CannotUseTheDescriptorOfAnOlderRegionAsFieldData()
    {
        string name = N("GrownIntoIt");
        using var writer = StructuredMemory<VersionOne>.CreateOrOpen(name, new VersionOne(), Stored);
        RawRegion raw = ReadRaw(name);
        long fieldsEnd = 64 + 64;   // one int field, padded to 64 bytes
        SchemaDescriptor.TryReadLocation(raw.Header, raw.Capacity, out DescriptorLocation location);
        Assert.That(location.Offset, Is.EqualTo(fieldsEnd));

        // The region is as large as the newer schema (192 bytes) but 64 of them are the descriptor.
        Assert.That(raw.Capacity, Is.EqualTo(192L));
        var error = Assert.Throws<InvalidDataException>(() => StructuredMemory<VersionTwoSmallGrowth>.OpenExisting(
            name, new VersionTwoSmallGrowth(), SchemaCompatibility.Backward));
        Assert.That(error!.Message, Does.Contain("holds only 128 bytes of fields"));

        // The same schema is fine for a region without a descriptor of the same size.
        string plain = N("GrownPlain");
        using var older = StructuredMemory<VersionOne>.CreateOrOpen(plain, new VersionOne());
        Assert.Throws<InvalidDataException>(() => StructuredMemory<VersionTwoSmallGrowth>.OpenExisting(
            plain, new VersionTwoSmallGrowth(), SchemaCompatibility.Backward));
    }

    [Test]
    public void TheHeaderOfAStoredDescriptor_IsCheckedWhenOpening_ItsContentIsNot()
    {
        string name = N("Damage");
        using var memory = StructuredMemory<DescribedSchema>.CreateOrOpen(name, new DescribedSchema(), Stored);
        RawRegion raw = ReadRaw(name);
        SchemaDescriptor.TryReadLocation(raw.Header, raw.Capacity, out DescriptorLocation location);

        using var region = MemoryRegion.OpenExisting(name, null, RegionKind.StructuredMemory);

        // A flipped byte in the block: the typed path never reads it, so it keeps working.
        region.Write(new byte[] { 0xFF }, location.Offset + 40);
        using (var typed = StructuredMemory<DescribedSchema>.OpenExisting(name, new DescribedSchema()))
        {
            Assert.That(typed.HasStoredDescriptor, Is.True);
        }

        // A location that does not fit the region is refused, before anything is read from it.
        var badLength = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(badLength, (uint)raw.Capacity + 64);
        region.Write(badLength, SchemaDescriptor.HeaderLengthOffset);
        var error = Assert.Throws<InvalidDataException>(
            () => StructuredMemory<DescribedSchema>.OpenExisting(name, new DescribedSchema()));
        Assert.That(error!.Message, Does.Contain("stored schema descriptor"));
    }

    [Test]
    public void ADescriptorThatDoesNotFit_FailsBeforeAnyRegionIsCreated()
    {
        string name = N("TooLong");

        var error = Assert.Throws<ArgumentException>(
            () => StructuredMemory<LongNameSchema>.CreateOrOpen(name, new LongNameSchema(), Stored));
        Assert.That(error!.Message, Does.Contain("at most"));

        // Without the option the same schema is fine, and nothing was left behind by the failed attempt.
        Assert.Throws<FileNotFoundException>(
            () => StructuredMemory<LongNameSchema>.OpenExisting(name, new LongNameSchema()));
        using var plain = StructuredMemory<LongNameSchema>.CreateOrOpen(name, new LongNameSchema());
        Assert.That(plain.HasStoredDescriptor, Is.False);
    }

    [Test]
    public void OptionsAreRequired_WhenTheOverloadTakesThem()
    {
        Assert.Throws<ArgumentNullException>(() => StructuredMemory<SmallSchema>.CreateOrOpen(
            N("NullOptions"), new SmallSchema(), SchemaCompatibility.Strict, null!));
        Assert.Throws<ArgumentNullException>(() => StructuredMemory<SmallSchema>.CreateOrOpen(
            N("NullOptions"), new SmallSchema(), null!));
    }
}
