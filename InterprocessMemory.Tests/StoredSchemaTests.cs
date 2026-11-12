using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using InterprocessMemory;

namespace InterprocessMemory.Tests;

/// <summary>
/// Opening a structured-memory region without its schema, from the field list its creator stored
/// (<see cref="StructuredMemory.OpenStored"/>), and the report of how a schema differs from the stored one.
/// </summary>
[TestFixture]
public class StoredSchemaTests
{
    private static string N(string prefix) => $"Stored_{prefix}_{Guid.NewGuid():N}";

    private static readonly StructuredMemoryOptions Stored = new() { StoreSchemaDescriptor = true };

    private static StructuredMemory<SchemaDescriptorTests.DescribedSchema> CreateDescribed(string name) =>
        StructuredMemory<SchemaDescriptorTests.DescribedSchema>.CreateOrOpen(
            name, new SchemaDescriptorTests.DescribedSchema(), Stored);

    public struct NumbersSchema : IMemorySchema
    {
        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<int>("Sequence");
            yield return FieldDefinition.Scalar<double>("Temperature");
        }
    }

    // Another type of the same size: the layout is the same, the schema hash is not.
    public struct NumbersWithALongSchema : IMemorySchema
    {
        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<int>("Sequence");
            yield return FieldDefinition.Scalar<long>("Temperature");
        }
    }

    public struct NumbersRenamedSchema : IMemorySchema
    {
        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<int>("Sequence");
            yield return FieldDefinition.Scalar<double>("Temp");
        }
    }

    // One more field that fits in the padding of the region, and one that does not.
    public struct NumbersWithModeSchema : IMemorySchema
    {
        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<int>("Sequence");
            yield return FieldDefinition.Scalar<double>("Temperature");
            yield return FieldDefinition.Scalar<int>("Mode");
        }
    }

    public struct NumbersWithBufferSchema : IMemorySchema
    {
        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<int>("Sequence");
            yield return FieldDefinition.Scalar<double>("Temperature");
            yield return FieldDefinition.Array<byte>("Big", 200);
        }
    }

    // ── Opening by the stored fields ─────────────────────────────────────────

    [Test]
    public void OpenStored_ListsTheFieldsAsTheCreatorLaidThemOut()
    {
        string name = N("Fields");
        using var creator = CreateDescribed(name);
        using var stored = StructuredMemory.OpenStored(name);

        Assert.That(stored.IsOwner, Is.False);
        Assert.That(stored.HasStoredDescriptor, Is.True);
        Assert.That(stored.SchemaVersion, Is.EqualTo(1));
        Assert.That(stored.StoredSchemaVersion, Is.EqualTo(1));
        Assert.That(stored.Schema.Version, Is.EqualTo(1));

        IReadOnlyList<StoredField> fields = stored.Schema.Fields;
        Assert.That(fields.Select(f => f.Name), Is.EqualTo(new[]
            { "Sequence", "Temperature", "Status", "Samples", "Origin", "Path", "Payload", "Label" }));
        Assert.That(fields.Select(f => f.TypeCode), Is.EqualTo(new[]
        {
            FieldTypeCode.Int32, FieldTypeCode.Double, FieldTypeCode.Char, FieldTypeCode.Single,
            FieldTypeCode.Struct, FieldTypeCode.Struct, FieldTypeCode.Blob, FieldTypeCode.Utf8String
        }));
        // On a 64 bit process, where a struct is aligned to 8 bytes.
        Assert.That(fields.Select(f => f.Offset), Is.EqualTo(new long[] { 0, 8, 16, 80, 112, 120, 152, 256 }));
        Assert.That(fields.Select(f => f.ArrayLength), Is.EqualTo(new[] { 1, 1, 32, 8, 1, 4, 104, 44 }));
        Assert.That(fields.Select(f => f.Size), Is.EqualTo(new long[] { 4, 8, 64, 32, 8, 32, 104, 44 }));

        // The type of the struct fields, as the creator recorded it.
        StoredField origin = fields.Single(f => f.Name == "Origin");
        Assert.That(origin.StructTypeName, Is.EqualTo(typeof(SchemaDescriptorTests.DescPoint).FullName));
        TypeLayoutFingerprint fingerprint = TypeLayoutFingerprint.Create<SchemaDescriptorTests.DescPoint>();
        Assert.That(origin.StructFingerprint, Is.EqualTo($"{fingerprint.High:x16}{fingerprint.Low:x16}"));
        Assert.That(fields.Single(f => f.Name == "Sequence").StructFingerprint, Is.Null);

        // What the creator reads back through the typed instance is the same list.
        Assert.That(creator.ReadStoredFields()!.SequenceEqual(fields), Is.True);
        Assert.That(stored.ReadStoredFields()!.SequenceEqual(fields), Is.True);
    }

    [Test]
    public void OpenStored_ReadsAndWritesEveryKindOfField()
    {
        string name = N("Values");
        using var creator = CreateDescribed(name);
        using (creator.AcquireWriteLock())
        {
            creator.Write("Sequence", 7);
            creator.Write("Temperature", 21.5);
            creator.WriteString("Status", "Ready");
            creator.WriteArray("Samples", new float[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            creator.Write("Origin", new SchemaDescriptorTests.DescPoint { X = 3, Y = 4 });
            creator.WriteArray("Path", new[]
            {
                new SchemaDescriptorTests.DescPoint { X = 1, Y = 1 }, new SchemaDescriptorTests.DescPoint { X = 2, Y = 2 },
                new SchemaDescriptorTests.DescPoint { X = 3, Y = 3 }, new SchemaDescriptorTests.DescPoint { X = 4, Y = 4 }
            });
            creator.WriteBlob("Payload", new byte[] { 9, 8, 7 });
            creator.WriteUtf8String("Label", "grüße");
        }

        using var stored = StructuredMemory.OpenStored(name);
        using (stored.AcquireReadLock())
        {
            Assert.That(stored.Read<int>("Sequence"), Is.EqualTo(7));
            Assert.That(stored.Read<double>("Temperature"), Is.EqualTo(21.5));
            Assert.That(stored.ReadString("Status"), Is.EqualTo("Ready"));
            var samples = new float[8];
            stored.ReadArray("Samples", samples);
            Assert.That(samples, Is.EqualTo(new float[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
            Assert.That(stored.Read<SchemaDescriptorTests.DescPoint>("Origin").Y, Is.EqualTo(4));
            var path = new SchemaDescriptorTests.DescPoint[4];
            stored.ReadArray("Path", path);
            Assert.That(path.Select(p => p.X), Is.EqualTo(new[] { 1, 2, 3, 4 }));
            Assert.That(stored.ReadBlob("Payload"), Is.EqualTo(new byte[] { 9, 8, 7 }));
            Assert.That(stored.ReadUtf8String("Label"), Is.EqualTo("grüße"));
        }

        // And the other way round, by the names the list gave.
        using (stored.AcquireWriteLock())
        {
            stored.Write("Sequence", 8);
            stored.WriteString("Status", "Done");
        }

        Assert.That(creator.Read<int>("Sequence"), Is.EqualTo(8));
        Assert.That(creator.ReadString("Status"), Is.EqualTo("Done"));

        // A type that is not the one of the field is refused, as it is with the schema.
        Assert.Throws<InvalidOperationException>(() => stored.Read<long>("Sequence"));
        Assert.Throws<ArgumentException>(() => stored.Read<int>("Nope"));
    }

    [Test]
    public void OpenStored_FindsTheFieldsOfARegionThatAnotherInstanceOpenedFromTheSchema()
    {
        string name = N("ManyOpeners");
        using var creator = CreateDescribed(name);
        using var typed = StructuredMemory<SchemaDescriptorTests.DescribedSchema>.OpenExisting(
            name, new SchemaDescriptorTests.DescribedSchema());
        using var first = StructuredMemory.OpenStored(name);
        using var second = StructuredMemory.OpenStored(name);

        Assert.That(first.Schema.Fields.SequenceEqual(second.Schema.Fields), Is.True);
        Assert.That(typed.ReadStoredFields()!.SequenceEqual(first.Schema.Fields), Is.True);
    }

    [Test]
    public void OpenStored_OfARegionWithoutAStoredList_SaysSo_AndLeavesItAlone()
    {
        string name = N("Plain");
        using var creator = StructuredMemory<SchemaDescriptorTests.DescribedSchema>.CreateOrOpen(
            name, new SchemaDescriptorTests.DescribedSchema());
        creator.Write("Sequence", 5);

        var error = Assert.Throws<InvalidOperationException>(() => StructuredMemory.OpenStored(name));
        Assert.That(error!.Message, Does.Contain("does not store a schema descriptor"));
        Assert.That(error.Message, Does.Contain(name));

        // The failed attempt released its mapping and changed nothing.
        using var typed = StructuredMemory<SchemaDescriptorTests.DescribedSchema>.OpenExisting(
            name, new SchemaDescriptorTests.DescribedSchema());
        Assert.That(typed.Read<int>("Sequence"), Is.EqualTo(5));
        Assert.That(typed.HasStoredDescriptor, Is.False);
        Assert.That(typed.ReadStoredFields(), Is.Null);
    }

    [Test]
    public void OpenStored_RefusesAMissingRegion_AndAnEmptyName()
    {
        Assert.Throws<FileNotFoundException>(() => StructuredMemory.OpenStored(N("Missing")));
        Assert.Throws<ArgumentException>(() => StructuredMemory.OpenStored(""));
        Assert.Throws<ArgumentException>(() => StructuredMemory.OpenStored("   "));
    }

    [Test]
    public void OpenStored_RefusesARegionOfAnotherKind()
    {
        string name = N("OtherKind");
        using var array = SharedArray<int>.CreateOrOpen(name, 16);

        Assert.Throws<InvalidDataException>(() => StructuredMemory.OpenStored(name));
    }

    [Test]
    public void AStoredSchema_ThatWasNeverFilledIn_IsEmpty()
    {
        StoredSchema empty = default;

        Assert.That(empty.Fields, Is.Empty);
        Assert.That(empty.GetFields(), Is.Empty);
        Assert.That(empty.IsCompatibleWith(0), Is.True);
        Assert.That(empty.IsCompatibleWith(1), Is.False);
    }

    // ── A descriptor that cannot be trusted ──────────────────────────────────

    private sealed record Raw(byte[] Header, byte[] Block, DescriptorLocation Location, DecodedDescriptor Decoded);

    private static Raw ReadRaw(MemoryRegion region)
    {
        var header = new byte[64];
        region.Read(header, 0);
        Assert.That(SchemaDescriptor.TryReadLocation(header, region.Capacity, out DescriptorLocation location), Is.True);
        var block = new byte[location.Length];
        region.Read(block, location.Offset);
        return new Raw(header, block, location, SchemaDescriptor.Decode(block, location.Crc32));
    }

    /// <summary>Writes a block of the same length as the one in the region, and the checksum that goes with it.</summary>
    private static void WriteBlock(MemoryRegion region, Raw original, byte[] block)
    {
        Assert.That(block.Length, Is.EqualTo(original.Block.Length));
        region.Write(block, original.Location.Offset);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(crc, SchemaDescriptor.ComputeCrc32(block));
        region.Write(crc, SchemaDescriptor.HeaderCrcOffset);
    }

    private static byte[] Reencode(Raw raw, Func<List<DescriptorField>, List<DescriptorField>> change,
        int? version = null, int? hash = null) =>
        SchemaDescriptor.Encode(
            version ?? raw.Decoded.SchemaVersion, hash ?? raw.Decoded.SchemaHash, change(raw.Decoded.Fields.ToList()));

    [Test]
    public void OpenStored_RefusesADescriptorThatDoesNotDescribeTheRegion()
    {
        string name = N("Lies");
        using var creator = CreateDescribed(name);
        using var region = MemoryRegion.OpenExisting(name, null, RegionKind.StructuredMemory);
        Raw raw = ReadRaw(region);

        var damage = new List<(string What, string Message, Action Apply)>
        {
            ("a flipped byte", "checksum", () => region.Write(new byte[] { 0xFF }, raw.Location.Offset + 40)),
            ("a length that does not fit", "stored schema descriptor", () =>
            {
                var length = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)region.Capacity + 64);
                region.Write(length, SchemaDescriptor.HeaderLengthOffset);
            }),
            ("another schema hash", "does not match the schema header", () =>
                WriteBlock(region, raw, Reencode(raw, f => f, hash: raw.Decoded.SchemaHash + 1))),
            ("another schema version", "does not match the schema header", () =>
                WriteBlock(region, raw, Reencode(raw, f => f, version: raw.Decoded.SchemaVersion + 1))),
            ("another field type of the same size", "does not match the schema header", () =>
                WriteBlock(region, raw, Reencode(raw, f =>
                {
                    f[0] = f[0] with { TypeCode = FieldTypeCode.Single };
                    return f;
                }))),
            ("another offset", "offset", () =>
                WriteBlock(region, raw, Reencode(raw, f =>
                {
                    f[1] = f[1] with { Offset = f[1].Offset + 8 };
                    return f;
                }))),
            ("a field that cannot be laid out", "cannot be used", () =>
                WriteBlock(region, raw, Reencode(raw, f =>
                {
                    // Characters of one byte: a string field would write over the next field.
                    f[2] = f[2] with { ElementSize = 1 };
                    return f;
                }))),
            ("a field of a size that does not fit the region", "stored schema descriptor", () =>
                WriteBlock(region, raw, Reencode(raw, f =>
                {
                    f[3] = f[3] with { ArrayLength = 1_000_000 };
                    return f;
                }))),
            ("a format of a newer version", "newer version", () =>
            {
                byte[] block = (byte[])raw.Block.Clone();
                BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(4), 2);
                WriteBlock(region, raw, block);
            }),
        };

        foreach (var (what, message, apply) in damage)
        {
            // Every case starts from the descriptor that the creator wrote.
            region.Write(raw.Block, raw.Location.Offset);
            region.Write(raw.Header, 0);

            apply();
            var error = Assert.Throws<InvalidDataException>(() => StructuredMemory.OpenStored(name), what);
            Assert.That(error!.Message, Does.Contain(message), what);
        }

        // The refusals did not harm the region: with the schema in the code it opens.
        region.Write(raw.Block, raw.Location.Offset);
        region.Write(raw.Header, 0);
        using var typed = StructuredMemory<SchemaDescriptorTests.DescribedSchema>.OpenExisting(
            name, new SchemaDescriptorTests.DescribedSchema());
        using var stored = StructuredMemory.OpenStored(name);
        Assert.That(stored.Schema.Fields.Count, Is.EqualTo(8));
    }

    [Test, Timeout(120000)]
    public void OpenStored_OfRandomlyDamagedDescriptors_OnlyEverThrowsInvalidDataException()
    {
        string name = N("Fuzz");
        using var creator = CreateDescribed(name);
        using var region = MemoryRegion.OpenExisting(name, null, RegionKind.StructuredMemory);
        Raw raw = ReadRaw(region);
        var random = new Random(20261112);

        int opened = 0;
        for (int i = 0; i < 3000; i++)
        {
            byte[] block = (byte[])raw.Block.Clone();
            int changes = random.Next(1, 5);
            for (int c = 0; c < changes; c++)
                block[random.Next(4) == 0 ? random.Next(block.Length) : random.Next(Math.Min(block.Length, 160))] =
                    (byte)random.Next(256);
            WriteBlock(region, raw, block);

            try
            {
                using var stored = StructuredMemory.OpenStored(name);
                // What is accepted describes the region: the same fields at the same places.
                opened++;
                Assert.That(stored.Schema.Fields.Select(f => (f.Name, f.TypeCode, f.ElementSize, f.ArrayLength, f.Offset))
                    .SequenceEqual(raw.Decoded.Fields.Select(f => (f.Name, f.TypeCode, f.ElementSize, f.ArrayLength, f.Offset))),
                    Is.True, $"iteration {i}");
            }
            catch (InvalidDataException)
            {
            }
        }

        // Reserved bytes and fingerprints can change without consequence, so some blocks are accepted.
        Assert.That(opened, Is.LessThan(3000));
        WriteBlock(region, raw, raw.Block);
        using var intact = StructuredMemory.OpenStored(name);
        Assert.That(intact.Schema.Fields.Count, Is.EqualTo(8));
    }

    // ── ReadStoredFields ─────────────────────────────────────────────────────

    [Test]
    public void ReadStoredFields_IsNullWithoutAList_AndReadOnceWithOne()
    {
        string plainName = N("RsfPlain");
        using var plain = StructuredMemory<NumbersSchema>.CreateOrOpen(plainName, new NumbersSchema());
        Assert.That(plain.ReadStoredFields(), Is.Null);

        string name = N("RsfStored");
        using var memory = StructuredMemory<NumbersSchema>.CreateOrOpen(name, new NumbersSchema(), Stored);
        IReadOnlyList<StoredField>? fields = memory.ReadStoredFields();
        Assert.That(fields, Is.Not.Null);
        Assert.That(fields!.Select(f => f.Name), Is.EqualTo(new[] { "Sequence", "Temperature" }));
        Assert.That(memory.ReadStoredFields(), Is.SameAs(fields));
    }

    [Test]
    public void ReadStoredFields_ReportsADescriptorThatWasDamagedAfterTheRegionWasOpened()
    {
        string name = N("RsfDamage");
        using var memory = StructuredMemory<NumbersSchema>.CreateOrOpen(name, new NumbersSchema(), Stored);
        using var region = MemoryRegion.OpenExisting(name, null, RegionKind.StructuredMemory);
        Raw raw = ReadRaw(region);

        region.Write(new byte[] { 0xFF }, raw.Location.Offset + 36);

        Assert.Throws<InvalidDataException>(() => memory.ReadStoredFields());
        // The typed access to the fields does not depend on it.
        memory.Write("Sequence", 3);
        Assert.That(memory.Read<int>("Sequence"), Is.EqualTo(3));
    }

    [Test]
    public void ReadStoredFields_AfterDispose_Throws()
    {
        string name = N("RsfDisposed");
        var memory = StructuredMemory<NumbersSchema>.CreateOrOpen(name, new NumbersSchema(), Stored);
        memory.Dispose();

        Assert.Throws<ObjectDisposedException>(() => memory.ReadStoredFields());
    }

    // ── What a mismatch says ─────────────────────────────────────────────────

    [Test]
    public void ASchemaOfAnotherType_IsReportedFieldByField()
    {
        string name = N("MismatchType");
        using var creator = StructuredMemory<NumbersSchema>.CreateOrOpen(name, new NumbersSchema(), Stored);

        var error = Assert.Throws<InvalidOperationException>(() =>
            StructuredMemory<NumbersWithALongSchema>.OpenExisting(name, new NumbersWithALongSchema()));

        Assert.That(error!.Message, Does.StartWith("Schema hash mismatch - the schema structure has changed"));
        Assert.That(error.Message, Does.Contain("field 'Temperature': stored Double at offset 8, schema Int64 at offset 8"));
        Assert.That(error.Message, Does.Not.Contain("'Sequence'"));
    }

    [Test]
    public void ARenamedField_IsReportedAsOneThatIsMissingOnEachSide()
    {
        string name = N("MismatchName");
        using var creator = StructuredMemory<NumbersSchema>.CreateOrOpen(name, new NumbersSchema(), Stored);

        var error = Assert.Throws<InvalidOperationException>(() =>
            StructuredMemory<NumbersRenamedSchema>.OpenExisting(name, new NumbersRenamedSchema()));

        Assert.That(error!.Message, Does.Contain("field 'Temperature': stored in the region only (Double at offset 8)"));
        Assert.That(error.Message, Does.Contain("field 'Temp': in the schema only (Double at offset 8)"));
    }

    [Test]
    public void AFieldThatIsNotInTheRegion_IsNamed_WhetherItFitsThePaddingOrNot()
    {
        string name = N("MismatchExtra");
        using var creator = StructuredMemory<NumbersSchema>.CreateOrOpen(name, new NumbersSchema(), Stored);

        // Fits in the padding of the 128 byte region: the number of fields is what differs.
        var count = Assert.Throws<InvalidOperationException>(() =>
            StructuredMemory<NumbersWithModeSchema>.OpenExisting(name, new NumbersWithModeSchema()));
        Assert.That(count!.Message, Does.StartWith("Schema field count mismatch: expected 3, found 2"));
        Assert.That(count.Message, Does.Contain("field 'Mode': in the schema only (Int32 at offset 16)"));

        // Does not fit: the size of the region differs.
        var size = Assert.Throws<InvalidDataException>(() =>
            StructuredMemory<NumbersWithBufferSchema>.OpenExisting(name, new NumbersWithBufferSchema()));
        Assert.That(size!.Message, Does.StartWith("Structured-memory size mismatch"));
        Assert.That(size.Message, Does.Contain("field 'Big': in the schema only (Byte[200] at offset 16)"));
    }

    [Test]
    public void WithoutAStoredList_TheMessagesAreTheOnesTheyAlwaysWere()
    {
        string name = N("MismatchPlain");
        using var creator = StructuredMemory<NumbersSchema>.CreateOrOpen(name, new NumbersSchema());

        var hash = Assert.Throws<InvalidOperationException>(() =>
            StructuredMemory<NumbersWithALongSchema>.OpenExisting(name, new NumbersWithALongSchema()));
        Assert.That(hash!.Message, Is.EqualTo("Schema hash mismatch - the schema structure has changed"));

        var count = Assert.Throws<InvalidOperationException>(() =>
            StructuredMemory<NumbersWithModeSchema>.OpenExisting(name, new NumbersWithModeSchema()));
        Assert.That(count!.Message, Is.EqualTo("Schema field count mismatch: expected 3, found 2"));

        var size = Assert.Throws<InvalidDataException>(() =>
            StructuredMemory<NumbersWithBufferSchema>.OpenExisting(name, new NumbersWithBufferSchema()));
        Assert.That(size!.Message, Does.Not.Contain("field '"));
    }

    [Test]
    public void ADamagedList_IsMentionedInsteadOfBeingReportedAsTheMismatch()
    {
        string name = N("MismatchDamaged");
        using var creator = StructuredMemory<NumbersSchema>.CreateOrOpen(name, new NumbersSchema(), Stored);
        using var region = MemoryRegion.OpenExisting(name, null, RegionKind.StructuredMemory);
        Raw raw = ReadRaw(region);
        region.Write(new byte[] { 0xFF }, raw.Location.Offset + 36);

        var error = Assert.Throws<InvalidOperationException>(() =>
            StructuredMemory<NumbersWithALongSchema>.OpenExisting(name, new NumbersWithALongSchema()));

        Assert.That(error!.Message, Does.StartWith("Schema hash mismatch - the schema structure has changed"));
        Assert.That(error.Message, Does.Contain("cannot be compared"));
    }

    [Test]
    public void TheDifferencesOfAVeryDifferentSchema_AreCappedAtSixteenLines()
    {
        var stored = Enumerable.Range(0, 40)
            .Select(i => new DescriptorField("Old" + i, FieldTypeCode.Int32, 4, 1, 4, i * 4L)).ToList();
        var current = new List<DescriptorField> { new("New", FieldTypeCode.Int32, 4, 1, 4, 0) };

        string[] lines = SchemaDescriptor.DescribeDifferences(stored, current).Split('\n');

        Assert.That(lines.Length, Is.EqualTo(17));
        Assert.That(lines[16], Is.EqualTo("  ... and 25 more"));
        Assert.That(lines[0], Is.EqualTo("  field 'Old0': stored in the region only (Int32 at offset 0)"));
        Assert.That(SchemaDescriptor.DescribeDifferences(stored, stored), Is.Empty);
    }

    [Test]
    public void OnlyTheAlignmentAndTheElementSize_AreNamed_WhenTheyAreWhatDiffers()
    {
        var stored = new List<DescriptorField> { new("A", FieldTypeCode.Struct, 8, 1, 8, 0) };
        var current = new List<DescriptorField> { new("A", FieldTypeCode.Struct, 12, 1, 4, 0) };

        string text = SchemaDescriptor.DescribeDifferences(stored, current);

        Assert.That(text, Does.Contain("stored Struct at offset 0, element size 8, alignment 8, schema Struct at offset 0, element size 12, alignment 4"));
    }
}
