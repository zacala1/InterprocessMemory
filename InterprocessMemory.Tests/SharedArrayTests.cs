using NUnit.Framework;
using InterprocessMemory;

namespace InterprocessMemory.Tests;

[TestFixture]
public class SharedArrayTests
{
    private const string TestBufferName = "TestBuffer_Array";

    [TearDown]
    public void Cleanup()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    [Test]
    public void Create_WithValidLength_ShouldSucceed()
    {
        using var array = new SharedArray<int>(TestBufferName + "_Create", 100);

        Assert.That(array.Length, Is.EqualTo(100));
    }

    [Test]
    public void Indexer_SetGet_ShouldRoundTrip()
    {
        using var array = new SharedArray<int>(TestBufferName + "_Indexer", 10);

        array[0] = 123;
        array[5] = 456;
        array[9] = 789;

        Assert.That(array[0], Is.EqualTo(123));
        Assert.That(array[5], Is.EqualTo(456));
        Assert.That(array[9], Is.EqualTo(789));
    }

    [Test]
    public void Indexer_NegativeIndex_ShouldThrow()
    {
        using var array = new SharedArray<int>(TestBufferName + "_NegativeIndex", 10);

        // Note: uint cast causes -1 to become a large positive number, which triggers out of range
        Assert.Throws<IndexOutOfRangeException>(() => array[-1] = 100);
        Assert.Throws<IndexOutOfRangeException>(() => { _ = array[-1]; });
    }

    [Test]
    public void Indexer_IndexOutOfRange_ShouldThrow()
    {
        using var array = new SharedArray<int>(TestBufferName + "_OutOfRange", 10);

        Assert.Throws<IndexOutOfRangeException>(() => array[10] = 100);
        Assert.Throws<IndexOutOfRangeException>(() => { _ = array[10]; });
    }

    [Test]
    public void DoubleArray_ShouldWork()
    {
        using var array = new SharedArray<double>(TestBufferName + "_Double", 5);

        array[0] = 1.1;
        array[1] = 2.2;
        array[2] = 3.3;
        array[3] = 4.4;
        array[4] = 5.5;

        Assert.That(array[0], Is.EqualTo(1.1).Within(0.001));
        Assert.That(array[1], Is.EqualTo(2.2).Within(0.001));
        Assert.That(array[2], Is.EqualTo(3.3).Within(0.001));
        Assert.That(array[3], Is.EqualTo(4.4).Within(0.001));
        Assert.That(array[4], Is.EqualTo(5.5).Within(0.001));
    }

    [Test]
    public void LongArray_ShouldWork()
    {
        using var array = new SharedArray<long>(TestBufferName + "_Long", 5);

        array[0] = long.MaxValue;
        array[1] = long.MinValue;
        array[2] = 0;
        array[3] = 123456789012345L;
        array[4] = -987654321098765L;

        Assert.That(array[0], Is.EqualTo(long.MaxValue));
        Assert.That(array[1], Is.EqualTo(long.MinValue));
        Assert.That(array[2], Is.EqualTo(0));
        Assert.That(array[3], Is.EqualTo(123456789012345L));
        Assert.That(array[4], Is.EqualTo(-987654321098765L));
    }

    [Test]
    public void StructArray_ShouldWork()
    {
        using var array = new SharedArray<TestPoint>(TestBufferName + "_Struct", 3);

        array[0] = new TestPoint { X = 10, Y = 20 };
        array[1] = new TestPoint { X = 30, Y = 40 };
        array[2] = new TestPoint { X = 50, Y = 60 };

        Assert.That(array[0].X, Is.EqualTo(10));
        Assert.That(array[0].Y, Is.EqualTo(20));
        Assert.That(array[1].X, Is.EqualTo(30));
        Assert.That(array[1].Y, Is.EqualTo(40));
        Assert.That(array[2].X, Is.EqualTo(50));
        Assert.That(array[2].Y, Is.EqualTo(60));
    }

    [Test]
    public void CopyFrom_ShouldPopulateArray()
    {
        using var array = new SharedArray<int>(TestBufferName + "_Fill", 10);

        var data = new int[] { 1, 2, 3, 4, 5 };
        array.CopyFrom(0, data);

        for (int i = 0; i < data.Length; i++)
        {
            Assert.That(array[i], Is.EqualTo(data[i]));
        }
    }

    [Test]
    public void CopyFrom_PartialData_ShouldWork()
    {
        using var array = new SharedArray<int>(TestBufferName + "_FillPartial", 10);

        // Initialize all to -1
        array.Fill(-1);

        var data = new int[] { 100, 200, 300 };
        array.CopyFrom(0, data);

        Assert.That(array[0], Is.EqualTo(100));
        Assert.That(array[1], Is.EqualTo(200));
        Assert.That(array[2], Is.EqualTo(300));
        Assert.That(array[3], Is.EqualTo(-1)); // Unchanged
    }

    [Test]
    public void Fill_SingleValue_ShouldPopulateArray()
    {
        using var array = new SharedArray<int>(TestBufferName + "_FillValue", 10);

        array.Fill(42);

        for (int i = 0; i < 10; i++)
        {
            Assert.That(array[i], Is.EqualTo(42));
        }
    }

    [Test]
    public void CopyTo_ShouldExtractData()
    {
        using var array = new SharedArray<int>(TestBufferName + "_CopyTo", 10);

        for (int i = 0; i < 10; i++)
        {
            array[i] = i * 10;
        }

        var buffer = new int[10];
        array.CopyTo(0, buffer);

        for (int i = 0; i < 10; i++)
        {
            Assert.That(buffer[i], Is.EqualTo(i * 10));
        }
    }

    [Test]
    public void CopyTo_WithOffset_ShouldWork()
    {
        using var array = new SharedArray<int>(TestBufferName + "_CopyToOffset", 10);

        for (int i = 0; i < 10; i++)
        {
            array[i] = i * 10;
        }

        var buffer = new int[5];
        array.CopyTo(5, buffer);

        for (int i = 0; i < 5; i++)
        {
            Assert.That(buffer[i], Is.EqualTo((i + 5) * 10));
        }
    }

    [Test]
    public void Clear_ShouldZeroArray()
    {
        using var array = new SharedArray<int>(TestBufferName + "_Clear", 10);

        for (int i = 0; i < 10; i++)
        {
            array[i] = i + 100;
        }

        array.Clear();

        for (int i = 0; i < 10; i++)
        {
            Assert.That(array[i], Is.EqualTo(0));
        }
    }

    [Test]
    public void LargeArray_ShouldWork()
    {
        using var array = new SharedArray<long>(TestBufferName + "_Large", 10000);

        array[0] = long.MaxValue;
        array[5000] = long.MinValue;
        array[9999] = 0;

        Assert.That(array[0], Is.EqualTo(long.MaxValue));
        Assert.That(array[5000], Is.EqualTo(long.MinValue));
        Assert.That(array[9999], Is.EqualTo(0));
    }

    [Test]
    public void AllElements_ShouldBeAccessible()
    {
        using var array = new SharedArray<int>(TestBufferName + "_AllElements", 1000);

        // Write pattern
        for (int i = 0; i < 1000; i++)
        {
            array[i] = i * 3 + 7;
        }

        // Verify
        for (int i = 0; i < 1000; i++)
        {
            Assert.That(array[i], Is.EqualTo(i * 3 + 7));
        }
    }

    [Test]
    public void ByteArray_ShouldWork()
    {
        using var array = new SharedArray<byte>(TestBufferName + "_Byte", 256);

        for (int i = 0; i < 256; i++)
        {
            array[i] = (byte)i;
        }

        for (int i = 0; i < 256; i++)
        {
            Assert.That(array[i], Is.EqualTo((byte)i));
        }
    }

    [Test]
    public void Dispose_ShouldCleanupResources()
    {
        var array = new SharedArray<int>(TestBufferName + "_Dispose", 100);
        array.Dispose();

        // Should not throw on second dispose
        Assert.DoesNotThrow(() => array.Dispose());
    }

    private struct TestPoint
    {
        public int X;
        public int Y;
    }

    // ── Cross-process locking ────────────────────────────────────────────────

    public struct Wide64 { public long A, B, C, D, E, F, G, H; }

    // Three bytes: not a power of two, so a single aligned move cannot copy it atomically.
    public struct Odd3 { public byte X, Y, Z; }

    private static string LockName(string tag) => $"SharedArrayLock_{tag}_{Guid.NewGuid():N}";

    private static Wide64 Wide(long v) => new() { A = v, B = v, C = v, D = v, E = v, F = v, G = v, H = v };

    private static bool IsWhole(Wide64 w) =>
        w.A == w.B && w.B == w.C && w.C == w.D && w.D == w.E && w.E == w.F && w.F == w.G && w.G == w.H;

    [Test, Timeout(60000)]
    public void WideElements_AreNeverObservedTorn_AcrossInstances()
    {
        // Without the automatic lock about 1% of reads of a 64-byte element were torn in this setup.
        string name = LockName("Torn");
        using var writerArray = SharedArray<Wide64>.CreateOrOpen(name, 4);
        using var readerArray = SharedArray<Wide64>.OpenExisting(name);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
        long reads = 0;
        long torn = 0;

        var writer = Task.Factory.StartNew(() =>
        {
            long i = 0;
            while (!stop.IsCancellationRequested)
                writerArray[0] = Wide(++i);
        }, TaskCreationOptions.LongRunning);

        var reader = Task.Factory.StartNew(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (!IsWhole(readerArray[0]))
                    torn++;
                reads++;
            }
        }, TaskCreationOptions.LongRunning);

        Task.WaitAll(writer, reader);

        Assert.That(reads, Is.GreaterThan(0));
        Assert.That(torn, Is.EqualTo(0), $"{torn} of {reads} reads were torn");
    }

    // Two bytes: the width that used to be copied as a one byte store plus a two byte store.
    public struct Pair2 { public byte X, Y; }

    private enum ElementPath { Indexer, CopyRange, Fill }

    // The writer alternates two values whose halves differ, the reader (a second instance, as another
    // process would be) must only ever see one of the two. Before the element was written with a single
    // store, a reader saw 0x0000 or 0xFFFF between 0x00FF and 0xFF00 about once in 200 reads.
    private static void AssertNeverTorn<T>(string tag, T first, T second, ElementPath path)
        where T : unmanaged
    {
        string name = LockName($"Atomic{tag}{path}");
        using var writerArray = SharedArray<T>.CreateOrOpen(name, 4);
        using var readerArray = SharedArray<T>.OpenExisting(name);
        writerArray[0] = first;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(0.6));
        long reads = 0;
        long torn = 0;

        var writer = Task.Factory.StartNew(() =>
        {
            T[] one = new T[1];
            while (!stop.IsCancellationRequested)
            {
                switch (path)
                {
                    case ElementPath.Indexer:
                        writerArray[0] = second;
                        writerArray[0] = first;
                        break;
                    case ElementPath.CopyRange:
                        one[0] = second;
                        writerArray.CopyFrom(0, one);
                        one[0] = first;
                        writerArray.CopyFrom(0, one);
                        break;
                    default:
                        writerArray.Fill(second, 0, 1);
                        writerArray.Fill(first, 0, 1);
                        break;
                }
            }
        }, TaskCreationOptions.LongRunning);

        var reader = Task.Factory.StartNew(() =>
        {
            T[] one = new T[1];
            var expectedFirst = System.Runtime.InteropServices.MemoryMarshal.AsBytes(new[] { first }.AsSpan()).ToArray();
            var expectedSecond = System.Runtime.InteropServices.MemoryMarshal.AsBytes(new[] { second }.AsSpan()).ToArray();
            while (!stop.IsCancellationRequested)
            {
                T value;
                if (path == ElementPath.Indexer)
                {
                    value = readerArray[0];
                }
                else
                {
                    readerArray.CopyTo(0, one);
                    value = one[0];
                }

                var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(new[] { value }.AsSpan());
                if (!bytes.SequenceEqual(expectedFirst) && !bytes.SequenceEqual(expectedSecond))
                    torn++;
                reads++;
            }
        }, TaskCreationOptions.LongRunning);

        Task.WaitAll(writer, reader);

        Assert.That(reads, Is.GreaterThan(0));
        Assert.That(torn, Is.EqualTo(0), $"{typeof(T).Name} via {path}: {torn} of {reads} reads were torn");
    }

    [Test, Timeout(60000)]
    public void TwoByteElements_AreNeverObservedTorn()
    {
        foreach (ElementPath path in Enum.GetValues<ElementPath>())
        {
            AssertNeverTorn("U16", (ushort)0x00FF, (ushort)0xFF00, path);
            AssertNeverTorn("Pair2", new Pair2 { X = 0x00, Y = 0xFF }, new Pair2 { X = 0xFF, Y = 0x00 }, path);
        }
    }

    [Test, Timeout(60000)]
    public void OtherAtomicWidths_AreNeverObservedTorn()
    {
        foreach (ElementPath path in Enum.GetValues<ElementPath>())
        {
            AssertNeverTorn("U8", (byte)0x0F, (byte)0xF0, path);
            AssertNeverTorn("U32", 0x00FF00FFu, 0xFF00FF00u, path);
            AssertNeverTorn("U64", 0x00FF00FF00FF00FFul, 0xFF00FF00FF00FF00ul, path);
        }
    }

    [Test, Timeout(60000)]
    public void AtomicElements_BypassTheLock_WideElementsDoNot()
    {
        string atomicName = LockName("Atomic");
        using var atomic = SharedArray<long>.CreateOrOpen(atomicName, 4);
        using var atomicPeer = SharedArray<long>.OpenExisting(atomicName);

        string wideName = LockName("Wide");
        using var wide = SharedArray<Wide64>.CreateOrOpen(wideName, 4);
        using var widePeer = SharedArray<Wide64>.OpenExisting(wideName);

        Task<Wide64> blockedRead;
        using (atomic.AcquireWriteLock())
        using (wide.AcquireWriteLock())
        {
            // A long is copied with one aligned move, so reading it needs no lock and does not wait.
            Assert.That(Task.Run(() => atomicPeer[0]).Wait(TimeSpan.FromSeconds(2)), Is.True);

            // A 64-byte struct could be torn, so its reader waits for the writer to finish.
            blockedRead = Task.Run(() => widePeer[0]);
            Assert.That(blockedRead.Wait(TimeSpan.FromMilliseconds(300)), Is.False);
        }

        Assert.That(blockedRead.Wait(TimeSpan.FromSeconds(5)), Is.True);
    }

    [Test, Timeout(30000)]
    public void ExplicitWriteLock_ExcludesOtherInstances_ForAnyElementType()
    {
        string name = LockName("Exclude");
        using var owner = SharedArray<int>.CreateOrOpen(name, 4);
        using var other = SharedArray<int>.OpenExisting(name);

        using (owner.AcquireWriteLock())
        {
            Assert.Throws<TimeoutException>(() => other.AcquireWriteLock(TimeSpan.FromMilliseconds(100)));
            Assert.Throws<TimeoutException>(() => other.AcquireReadLock(TimeSpan.FromMilliseconds(100)));
        }

        using (other.AcquireWriteLock(TimeSpan.FromSeconds(1)))
        {
        }

        // Readers share the lock.
        using (owner.AcquireReadLock(TimeSpan.FromSeconds(1)))
        using (other.AcquireReadLock(TimeSpan.FromSeconds(1)))
        {
        }
    }

    [Test, Timeout(30000)]
    public void Locks_AreReentrant_AndTheIndexerDoesNotTakeThemAgain()
    {
        string name = LockName("Reentrant");
        using var array = SharedArray<Wide64>.CreateOrOpen(name, 8);
        using var peer = SharedArray<Wide64>.OpenExisting(name);

        using (array.AcquireWriteLock())
        {
            array[0] = Wide(1);
            Assert.That(IsWhole(array[0]), Is.True);
            array.CopyFrom(1, new[] { Wide(2), Wide(3) });
            array.Fill(Wide(9), 3, 2);

            using (array.AcquireWriteLock())
            using (array.AcquireReadLock())
            {
                array[7] = Wide(7);
                Assert.That(array[7].A, Is.EqualTo(7));
            }

            // The inner guards released only their own level: the outer lock is still exclusive.
            Assert.Throws<TimeoutException>(() => peer.AcquireReadLock(TimeSpan.FromMilliseconds(100)));
        }

        using (peer.AcquireWriteLock(TimeSpan.FromSeconds(1)))
        {
            Assert.That(peer[7].A, Is.EqualTo(7));
        }
    }

    [Test, Timeout(30000)]
    public void WritingWhileHoldingOnlyAReadLock_Throws()
    {
        using var narrow = SharedArray<int>.CreateOrOpen(LockName("Upgrade"), 4);
        using var wide = SharedArray<Wide64>.CreateOrOpen(LockName("UpgradeWide"), 4);

        using (narrow.AcquireReadLock())
        {
            // Upgrading would wait for this very thread to release its read lock.
            Assert.Throws<InvalidOperationException>(() => narrow.AcquireWriteLock());
        }

        using (wide.AcquireReadLock())
        {
            Assert.Throws<InvalidOperationException>(() => wide[0] = Wide(1));
            Assert.Throws<InvalidOperationException>(() => wide.CopyFrom(0, new[] { Wide(1) }));
            Assert.That(IsWhole(wide[0]), Is.True);
        }

        // Nothing was left behind by the rejected attempts.
        using (narrow.AcquireWriteLock(TimeSpan.FromSeconds(1)))
        {
        }

        wide[0] = Wide(5);
        Assert.That(wide[0].A, Is.EqualTo(5));
    }

    [Test, Timeout(60000)]
    public void ExplicitLocks_GiveConsistentSnapshotsOfSeveralElements()
    {
        // Two longs are each atomic, but a reader can still see one of them from before and the other
        // from after an update. The explicit locks make the pair one unit.
        string name = LockName("Snapshot");
        using var writerArray = SharedArray<long>.CreateOrOpen(name, 2);
        using var readerArray = SharedArray<long>.OpenExisting(name);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
        long snapshots = 0;
        long mismatches = 0;

        var writer = Task.Factory.StartNew(() =>
        {
            long i = 0;
            while (!stop.IsCancellationRequested)
            {
                i++;
                using var guard = writerArray.AcquireWriteLock();
                writerArray[0] = i;
                writerArray[1] = i;
            }
        }, TaskCreationOptions.LongRunning);

        var reader = Task.Factory.StartNew(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                long first;
                long second;
                using (readerArray.AcquireReadLock())
                {
                    first = readerArray[0];
                    second = readerArray[1];
                }

                if (first != second)
                    mismatches++;
                snapshots++;
            }
        }, TaskCreationOptions.LongRunning);

        Task.WaitAll(writer, reader);

        Assert.That(snapshots, Is.GreaterThan(0));
        Assert.That(mismatches, Is.EqualTo(0), $"{mismatches} of {snapshots} snapshots were inconsistent");
    }

    [Test]
    public void Guards_AreHarmlessWhenDisposedTwice()
    {
        string name = LockName("DoubleDispose");
        using var array = SharedArray<int>.CreateOrOpen(name, 4);
        using var peer = SharedArray<int>.OpenExisting(name);

        var guard = array.AcquireWriteLock();
        guard.Dispose();
        guard.Dispose();
        default(SharedArray<int>.WriteLock).Dispose();
        default(SharedArray<int>.ReadLock).Dispose();

        // A second release must not have freed anything it did not hold or unbalanced the bookkeeping.
        using (peer.AcquireWriteLock(TimeSpan.FromSeconds(1)))
        {
        }

        using (array.AcquireWriteLock(TimeSpan.FromSeconds(1)))
        {
            array[0] = 1;
        }
    }

    [Test]
    public void ForceResetLocks_UnblocksWritersAfterALostReader()
    {
        string name = LockName("Reset");
        using var array = SharedArray<int>.CreateOrOpen(name, 4);
        using var peer = SharedArray<int>.OpenExisting(name);

        // A reader that never comes back, as after a crash.
        var lostReader = peer.AcquireReadLock();
        Assert.Throws<TimeoutException>(() => array.AcquireWriteLock(TimeSpan.FromMilliseconds(100)));

        array.ForceResetLocks();

        using (array.AcquireWriteLock(TimeSpan.FromSeconds(1)))
        {
        }

        lostReader.Dispose();
    }

    [Test]
    public void WideAndOddSizedElements_RoundTripThroughEveryAccessPath()
    {
        using var wide = SharedArray<Wide64>.CreateOrOpen(LockName("RoundTripWide"), 10);
        wide[3] = Wide(33);
        Assert.That(wide[3].H, Is.EqualTo(33));

        wide.CopyFrom(4, new[] { Wide(4), Wide(5) });
        var copy = new Wide64[2];
        wide.CopyTo(4, copy);
        Assert.That(copy[0].A, Is.EqualTo(4));
        Assert.That(copy[1].A, Is.EqualTo(5));

        wide.Fill(Wide(8), 6, 4);
        Assert.That(wide[9].A, Is.EqualTo(8));
        Assert.That(wide[5].A, Is.EqualTo(5), "the range before the fill is untouched");
        Assert.That(wide[0].A, Is.EqualTo(0), "an element nobody wrote is still zero");

        wide.Clear();
        Assert.That(wide[3].A, Is.EqualTo(0));

        using var odd = SharedArray<Odd3>.CreateOrOpen(LockName("RoundTripOdd"), 5);
        odd[2] = new Odd3 { X = 1, Y = 2, Z = 3 };
        Assert.That(odd[2].Z, Is.EqualTo(3));
        odd.Fill(new Odd3 { X = 9, Y = 9, Z = 9 }, 0, 5);
        Assert.That(odd[4].Y, Is.EqualTo(9));
    }
}
