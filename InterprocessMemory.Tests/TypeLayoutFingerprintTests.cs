using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using NUnit.Framework;
using InterprocessMemory;

namespace InterprocessMemory.Tests;

[TestFixture]
public class TypeLayoutFingerprintTests
{
    private static string N(string prefix) => $"Fingerprint_{prefix}_{Guid.NewGuid():N}";

    [Test]
    public void MarshallableTypes_KeepTheirRelease300Fingerprint()
    {
        // These values were produced by 3.0.0. A different value would make a process built from
        // this version reject regions created by a 3.0.0 process (and vice versa) with
        // "different format or element type", so they must only ever change together with the
        // shared-memory format version.
        Assert.That(TypeLayoutFingerprint.Create<int>(),
            Is.EqualTo(new TypeLayoutFingerprint(0xD4616E4290E3AB20, 0xEB0DEB962578DCD5)));
        Assert.That(TypeLayoutFingerprint.Create<long>(),
            Is.EqualTo(new TypeLayoutFingerprint(0xD52D441606CE7C3F, 0x4D50B1F77C44F34C)));
        Assert.That(TypeLayoutFingerprint.Create<Guid>(),
            Is.EqualTo(new TypeLayoutFingerprint(0x7EE1FD251B406A46, 0x9427C9B56B8CC942)));
        Assert.That(TypeLayoutFingerprint.Create<decimal>(),
            Is.EqualTo(new TypeLayoutFingerprint(0xB7337E9F0D12BB73, 0x2C0F045AB00D96C8)));
    }

    [Test]
    public void GenericUnmanagedStructs_HaveStableDistinctFingerprints()
    {
        // Marshal.SizeOf rejects generic types, so (int, int) used to fail with ArgumentException
        // in every typed container even though it satisfies the unmanaged constraint.
        var tupleA = TypeLayoutFingerprint.Create<(int, int)>();
        var tupleB = TypeLayoutFingerprint.Create<(int, int)>();
        var differentArgument = TypeLayoutFingerprint.Create<(int, uint)>();
        var differentShape = TypeLayoutFingerprint.Create<(int, int, int)>();
        var pair = TypeLayoutFingerprint.Create<KeyValuePair<int, int>>();

        Assert.That(tupleA, Is.EqualTo(tupleB));
        Assert.That(tupleA, Is.Not.EqualTo(differentArgument));
        Assert.That(tupleA, Is.Not.EqualTo(differentShape));
        Assert.That(tupleA, Is.Not.EqualTo(pair));
    }

    [StructLayout(LayoutKind.Auto)]
    private struct AutoMixed
    {
        public byte A;
        public long B;
        public short C;
        public int D;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct ExplicitFields
    {
        [FieldOffset(0)] public int First;
        [FieldOffset(16)] public long Second;
    }

    private static int[] OffsetsOf<T>() where T : unmanaged
    {
        var fields = typeof(T)
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .OrderBy(f => f.MetadataToken)
            .ToArray();
        return TypeLayoutFingerprint.ManagedFieldOffsets(typeof(T), fields);
    }

    [Test]
    public void ManagedFieldOffsets_AreTheOffsetsTheRuntimeChose()
    {
        // (int, int): two 4 byte fields, whatever order the runtime picks.
        int[] pair = OffsetsOf<(int, int)>();
        Assert.That(pair.OrderBy(x => x).ToArray(), Is.EqualTo(new[] { 0, 4 }));

        // Explicit layout: the offsets of the attribute.
        Assert.That(OffsetsOf<ExplicitFields>(), Is.EqualTo(new[] { 0, 16 }));

        // Auto layout: every field has its own bytes inside the struct (no overlap, nothing past the end).
        int[] offsets = OffsetsOf<AutoMixed>();
        int[] sizes = { 1, 8, 2, 4 };
        int total = System.Runtime.CompilerServices.Unsafe.SizeOf<AutoMixed>();
        var spans = offsets.Zip(sizes, (offset, size) => (offset, size)).OrderBy(x => x.offset).ToArray();
        Assert.That(spans[0].offset, Is.GreaterThanOrEqualTo(0));
        for (int i = 1; i < spans.Length; i++)
            Assert.That(spans[i].offset, Is.GreaterThanOrEqualTo(spans[i - 1].offset + spans[i - 1].size));
        Assert.That(spans[^1].offset + spans[^1].size, Is.LessThanOrEqualTo(total));
    }

    [Test]
    public void AutoLayoutStructs_AreDescribedByTheirOffsets()
    {
        // The descriptor of a type that the marshaller rejects contains the measured offsets. A type that the
        // marshaller accepts keeps its 3.0.0 descriptor (see MarshallableTypes_KeepTheirRelease300Fingerprint),
        // so only the generic fallback changed, and it has never been released.
        var first = TypeLayoutFingerprint.Create<(byte, long)>();
        Assert.That(TypeLayoutFingerprint.Create<(byte, long)>(), Is.EqualTo(first), "stable within a process");
        Assert.That(TypeLayoutFingerprint.Create<(long, byte)>(), Is.Not.EqualTo(first));
    }

    [Test]
    public void SingleProducerQueue_AcceptsGenericStruct()
    {
        string name = N("SpscTuple");
        using var producer = SingleProducerQueue<(int, long)>.CreateOrOpen(name, 8);
        Assert.That(producer.TryEnqueue((7, 9L)), Is.True);

        using var consumer = SingleProducerQueue<(int, long)>.OpenExisting(name);
        Assert.That(consumer.TryDequeue(out (int, long) item), Is.True);
        Assert.That(item, Is.EqualTo((7, 9L)));
    }

    [Test]
    public void ConcurrentQueue_AcceptsGenericStruct()
    {
        string name = N("MpmcPair");
        using var queue = InterprocessMemory.ConcurrentQueue<KeyValuePair<int, int>>.CreateOrOpen(name, 8);
        Assert.That(queue.TryEnqueue(new KeyValuePair<int, int>(3, 4)), Is.True);

        using var reader = InterprocessMemory.ConcurrentQueue<KeyValuePair<int, int>>.OpenExisting(name);
        Assert.That(reader.TryDequeue(out KeyValuePair<int, int> item), Is.True);
        Assert.That(item.Key, Is.EqualTo(3));
        Assert.That(item.Value, Is.EqualTo(4));
    }

    [Test]
    public void SharedArray_AcceptsGenericStruct()
    {
        string name = N("ArrayTuple");
        using var owner = SharedArray<(int, int)>.CreateOrOpen(name, 4);
        owner[2] = (5, 6);

        using var reader = SharedArray<(int, int)>.OpenExisting(name);
        Assert.That(reader[2], Is.EqualTo((5, 6)));
    }

    [Test]
    public void OpeningGenericStructQueueWithDifferentLayout_Throws()
    {
        // Same size (8 bytes), different element type: only the fingerprint tells them apart.
        string name = N("Mismatch");
        using var owner = SingleProducerQueue<(int, int)>.CreateOrOpen(name, 4);

        Assert.Throws<InvalidDataException>(() => SingleProducerQueue<(int, uint)>.OpenExisting(name));
    }
}
