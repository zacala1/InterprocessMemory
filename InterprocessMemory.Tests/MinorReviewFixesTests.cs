using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using InterprocessMemory;

namespace InterprocessMemory.Tests;

/// <summary>Regression tests for the smaller findings of the third code review.</summary>
[TestFixture]
public unsafe class MinorReviewFixesTests
{
    private static string N(string prefix) => $"Minor_{prefix}_{Guid.NewGuid():N}";

    // ── Queues ───────────────────────────────────────────────────────────────

    [Test, Timeout(30000)]
    public void SingleProducerByteStream_WaitReadIntoAnEmptyBuffer_ReturnsAtOnce()
    {
        using var stream = new SingleProducerByteStream(N("EmptyDest"), 1024);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        // Nothing fits into an empty buffer, so the wait used to last until the timeout, for ever with an
        // infinite one. The token only ends the old behavior; the fixed code never looks at it.
        var sw = Stopwatch.StartNew();
        int read = stream.WaitRead(new byte[0], Timeout.InfiniteTimeSpan, cancel.Token);

        Assert.That(read, Is.EqualTo(0));
        Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(1)));
    }

    [Test]
    public void ConcurrentMessageQueue_SlotStillHolds_OnlyWhileTheSequenceIsTheExpectedOne()
    {
        // The length of a message is read after the slot sequence. Another reader may take the message and
        // a writer reuse the slot in between, so an unexpected length is only trusted (and reported as an
        // error) while the slot still carries the sequence that was read first.
        var slot = new ConcurrentMessageQueue.Slot { Sequence = 5, DataLength = 100 };

        Assert.That(ConcurrentMessageQueue.SlotStillHolds(&slot, 5), Is.True);

        slot.Sequence = 5 + 8;   // consumed: the free sequence is read + slotCount
        Assert.That(ConcurrentMessageQueue.SlotStillHolds(&slot, 5), Is.False);

        slot.Sequence = 6;       // consumed and published again for the next lap
        Assert.That(ConcurrentMessageQueue.SlotStillHolds(&slot, 5), Is.False);
    }

    [Test, Timeout(60000)]
    public void ConcurrentMessageQueue_ManyReadersAndWriters_NeverReportAWrongLength()
    {
        // Messages of every size up to the maximum; readers use a buffer of exactly that size, so neither the
        // "destination too small" nor the "invalid length" error can be legitimate.
        const int maxSize = 48;
        using var queue = new ConcurrentMessageQueue(N("WrongLength"), slotCount: 2, slotSize: maxSize + 16);   // the slot size includes the 16 byte slot header

        long written = 0;
        long read = 0;
        const int perWriter = 20000;
        Exception? failure = null;
        using var cts = new CancellationTokenSource();

        var writers = new Thread[2];
        for (int w = 0; w < writers.Length; w++)
        {
            int seed = w;
            writers[w] = new Thread(() =>
            {
                var data = new byte[maxSize];
                var rng = new Random(seed);
                for (int i = 0; i < perWriter && failure == null; i++)
                {
                    int length = rng.Next(1, maxSize + 1);
                    while (!queue.TryWrite(data.AsSpan(0, length)) && failure == null)
                        Thread.Yield();
                    Interlocked.Increment(ref written);
                }
            })
            { IsBackground = true };
        }

        var readers = new Thread[2];
        for (int r = 0; r < readers.Length; r++)
        {
            readers[r] = new Thread(() =>
            {
                var buffer = new byte[maxSize];
                try
                {
                    while (!cts.IsCancellationRequested)
                    {
                        if (queue.TryRead(buffer) > 0)
                            Interlocked.Increment(ref read);
                        else
                            Thread.Yield();
                    }
                }
                catch (Exception ex)
                {
                    failure ??= ex;
                }
            })
            { IsBackground = true };
        }

        foreach (var t in readers) t.Start();
        foreach (var t in writers) t.Start();
        foreach (var t in writers) Assert.That(t.Join(TimeSpan.FromSeconds(50)), Is.True, "writers did not finish");

        var drain = Stopwatch.StartNew();
        while (Interlocked.Read(ref read) < Interlocked.Read(ref written) && drain.Elapsed < TimeSpan.FromSeconds(5))
            Thread.Sleep(5);
        cts.Cancel();
        foreach (var t in readers) t.Join(TimeSpan.FromSeconds(5));

        Assert.That(failure, Is.Null, failure?.ToString());
        Assert.That(Interlocked.Read(ref read), Is.EqualTo(Interlocked.Read(ref written)));
    }

    // ── StructuredMemory ─────────────────────────────────────────────────────

    public struct Utf8Schema : IMemorySchema
    {
        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Utf8String("Token", 64);
            yield return FieldDefinition.Scalar<int>("After");
        }
    }

    public struct BigBlobSchema : IMemorySchema
    {
        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Blob("Data", 64 * 1024 * 1024);
        }
    }

    public struct InconsistentCharSchema : IMemorySchema
    {
        public IEnumerable<FieldDefinition> GetFields()
        {
            // A string of 8 chars that claims 1 byte per element: the layout reserves 8 bytes while
            // WriteString writes 16, over the field that follows.
            yield return new FieldDefinition
            {
                Name = "Text", TypeCode = FieldTypeCode.Char, ElementSize = 1, ArrayLength = 8, Alignment = 2
            };
            yield return FieldDefinition.Scalar<long>("Next");
        }
    }

    public struct TinyBlobSchema : IMemorySchema
    {
        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return new FieldDefinition
            {
                Name = "Data", TypeCode = FieldTypeCode.Blob, ElementSize = 1, ArrayLength = 3, Alignment = 4
            };
        }
    }

    private static byte[] ReadWholeRegion(string name)
    {
        using var raw = MemoryRegion.OpenExisting(name, null, RegionKind.StructuredMemory);
        var bytes = new byte[raw.Capacity];
        raw.Read(bytes, 0);
        return bytes;
    }

    private static bool Contains(byte[] haystack, string ascii) =>
        haystack.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(ascii)) >= 0;

    [Test]
    public void WriteUtf8String_Empty_ClearsTheBytesOfThePreviousValue()
    {
        string name = N("Utf8Clear");
        using var memory = StructuredMemory<Utf8Schema>.CreateOrOpen(name, new Utf8Schema());

        memory.WriteUtf8String("Token", "secret-token-1234");
        Assert.That(Contains(ReadWholeRegion(name), "secret-token-1234"), Is.True, "setup: the value is stored");

        memory.WriteUtf8String("Token", "");

        Assert.That(memory.ReadUtf8String("Token"), Is.EqualTo(""));
        Assert.That(Contains(ReadWholeRegion(name), "secret-token"), Is.False,
            "the old value must not stay in the shared memory");
    }

    [Test]
    public void WriteUtf8String_ShorterValue_ClearsTheTailOfALongerOne()
    {
        string name = N("Utf8Shorter");
        using var memory = StructuredMemory<Utf8Schema>.CreateOrOpen(name, new Utf8Schema());
        memory.Write("After", 0x41414141);

        memory.WriteUtf8String("Token", "0123456789abcdefghij");
        memory.WriteUtf8String("Token", "012");

        Assert.That(memory.ReadUtf8String("Token"), Is.EqualTo("012"));
        Assert.That(Contains(ReadWholeRegion(name), "3456789abcdefghij"), Is.False);
        Assert.That(memory.Read<int>("After"), Is.EqualTo(0x41414141), "the next field is untouched");
    }

    [Test]
    public void CreateOrOpen_OfALargeRegion_DoesNotMakeItResident()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Ignore("Measures the free space of /dev/shm.");

        long before = new DriveInfo("/dev/shm").AvailableFreeSpace;
        string name = N("Sparse");
        using var memory = StructuredMemory<BigBlobSchema>.CreateOrOpen(name, new BigBlobSchema());
        long used = before - new DriveInfo("/dev/shm").AvailableFreeSpace;

        // Zero-filling the new region used to commit all 64 MiB at once.
        Assert.That(used, Is.LessThan(16L * 1024 * 1024), $"{used} bytes of /dev/shm are in use");
    }

    [Test]
    public void FieldDefinition_WhoseElementSizeContradictsItsTypeCode_IsRejected()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            StructuredMemory<InconsistentCharSchema>.CreateOrOpen(N("BadChar"), new InconsistentCharSchema()));
        Assert.That(ex!.Message, Does.Contain("Text"));
    }

    [Test]
    public void FieldDefinition_OfABlobWithoutRoomForData_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StructuredMemory<TinyBlobSchema>.CreateOrOpen(N("TinyBlob"), new TinyBlobSchema()));
    }

    // ── MemoryRegion ─────────────────────────────────────────────────────────

    [Test]
    public void DisposeGracePeriod_LongerThanASleepCanLast_IsRejected()
    {
        TimeSpan original = MemoryRegion.DisposeGracePeriod;
        try
        {
            // Thread.Sleep takes an int number of milliseconds; a longer period made Dispose throw after the
            // region had already been marked disposed, leaving the mapping behind.
            Assert.Throws<ArgumentOutOfRangeException>(() => MemoryRegion.DisposeGracePeriod = TimeSpan.FromDays(30));
            Assert.That(MemoryRegion.DisposeGracePeriod, Is.EqualTo(original), "a rejected value changes nothing");

            Assert.DoesNotThrow(() => MemoryRegion.DisposeGracePeriod = TimeSpan.FromMilliseconds(int.MaxValue));
        }
        finally
        {
            MemoryRegion.DisposeGracePeriod = original;
        }
    }

    [Test]
    public void RollBackReaderIncrement_NeverDrivesTheReaderCountBelowZero()
    {
        // A reader that backs out because a writer got in decrements the count it had incremented. If
        // ForceResetLocks cleared the count in between, a plain decrement left -1 behind for good.
        var header = new MemoryRegion.SharedHeader { ReaderCount = 0 };
        MemoryRegion.RollBackReaderIncrement(&header);
        Assert.That(header.ReaderCount, Is.EqualTo(0));

        header.ReaderCount = 2;
        MemoryRegion.RollBackReaderIncrement(&header);
        Assert.That(header.ReaderCount, Is.EqualTo(1));
    }

    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void UnixCreateMode_SetsThePermissionsOfTheBackingFile()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Ignore("Unix file permissions.");

        string name = N("Mode");
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var region = MemoryRegion.CreateOrOpen(name, 4096, new MemoryRegionOptions { UnixCreateMode = mode });

        Assert.That(File.GetUnixFileMode("/dev/shm/" + name), Is.EqualTo(mode));
    }

    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void UnixCreateMode_AppliesToAFilePathRegionToo()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Ignore("Unix file permissions.");

        string path = Path.Combine(Path.GetTempPath(), N("ModeFile") + ".bin");
        try
        {
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
            using (var region = MemoryRegion.CreateOrOpen(N("ModeFileRegion"), 4096,
                new MemoryRegionOptions { FilePath = path, UnixCreateMode = mode }))
            {
                // The umask only removes bits, and these are ones that no usual umask removes.
                Assert.That(File.GetUnixFileMode(path) & (UnixFileMode.UserRead | UnixFileMode.UserWrite),
                    Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));
                Assert.That(File.GetUnixFileMode(path) & (UnixFileMode.OtherRead | UnixFileMode.OtherWrite
                    | UnixFileMode.GroupWrite), Is.EqualTo((UnixFileMode)0));
            }
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    // ── Resource leaks ───────────────────────────────────────────────────────

    private static int CountOpenFilesMatching(string namePart)
    {
        return Directory.GetFiles("/proc/self/fd").Count(path =>
        {
            try
            { return new FileInfo(path).LinkTarget?.Contains(namePart) == true; }
            catch (IOException) { return false; }
        });
    }

    // Runs a construction that is bound to fail after it has mapped the region, and compares the open file
    // descriptors before and after. File.Delete cannot show a leak on Linux (it succeeds on open files), so
    // the older tests that rely on it only detect a leak on Windows.
    private static void AssertFailedConstructionReleasesItsFile(
        string name, string fileNamePart, Func<IDisposable> keeper, Action failing)
    {
        using var keep = keeper();
        int before = CountOpenFilesMatching(fileNamePart);
        Assert.That(before, Is.GreaterThan(0), $"{name}: setup should hold the region open");

        for (int i = 0; i < 5; i++)
        {
            bool failed = false;
            try
            { failing(); }
            catch (Exception) { failed = true; }
            Assert.That(failed, Is.True, $"{name}: the second construction should fail");
        }

        Assert.That(CountOpenFilesMatching(fileNamePart), Is.EqualTo(before),
            $"{name}: a failed construction left a file descriptor open");
    }

    // Overwrites bytes of the container header of a live region so that the next open fails in the
    // container's own validation, after the region itself has been mapped successfully.
    private static void Corrupt(string name, RegionKind kind, long offset, byte[] bytes)
    {
        using var raw = MemoryRegion.OpenExisting(name, null, kind);
        raw.Write(bytes, offset);
    }

    [Test, Timeout(60000)]
    public void FailedConstruction_ReleasesTheMappedFileAtOnce()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Ignore("Counts the process's open file descriptors through /proc.");

        byte[] badLongVersion = BitConverter.GetBytes(99L);   // a version no release uses
        byte[] badIntVersion = BitConverter.GetBytes(99);

        string spsc = N("LeakSpsc");
        AssertFailedConstructionReleasesItsFile("SingleProducerByteStream", spsc,
            () =>
            {
                var keep = new SingleProducerByteStream(spsc, 1024);
                Corrupt(spsc, RegionKind.SingleProducerByteStream, 16, badLongVersion);   // Header.Version
                return keep;
            },
            () => new SingleProducerByteStream(spsc, 1024).Dispose());

        string mpmc = N("LeakMpmc");
        AssertFailedConstructionReleasesItsFile("ConcurrentMessageQueue", mpmc,
            () =>
            {
                var keep = new ConcurrentMessageQueue(mpmc, 4, 64);
                Corrupt(mpmc, RegionKind.ConcurrentMessageQueue, 140, badIntVersion);    // Header.Version
                return keep;
            },
            () => new ConcurrentMessageQueue(mpmc, 4, 64).Dispose());

        string cq = N("LeakCq");
        AssertFailedConstructionReleasesItsFile("ConcurrentQueue", cq,
            () =>
            {
                var keep = ConcurrentQueue<int>.CreateOrOpen(cq, 4);
                Corrupt(cq, RegionKind.ConcurrentQueue, 136, badIntVersion);             // Header.Version
                return keep;
            },
            () => ConcurrentQueue<int>.CreateOrOpen(cq, 4).Dispose());

        string sq = N("LeakSq");
        AssertFailedConstructionReleasesItsFile("SingleProducerQueue", sq,
            () =>
            {
                var keep = SingleProducerQueue<int>.CreateOrOpen(sq, 4);
                Corrupt(sq, RegionKind.SingleProducerQueue, 136, badIntVersion);         // Header.Version
                return keep;
            },
            () => SingleProducerQueue<int>.CreateOrOpen(sq, 4).Dispose());

        string st = N("LeakStructured");
        AssertFailedConstructionReleasesItsFile("StructuredMemory", st,
            () =>
            {
                var keep = StructuredMemory<Utf8Schema>.CreateOrOpen(st, new Utf8Schema());
                Corrupt(st, RegionKind.StructuredMemory, 12, BitConverter.GetBytes(0x7EADBEEF));   // schema hash
                return keep;
            },
            () => StructuredMemory<Utf8Schema>.CreateOrOpen(st, new Utf8Schema()).Dispose());

        string file = Path.Combine(Path.GetTempPath(), N("LeakFile") + ".bin");
        try
        {
            AssertFailedConstructionReleasesItsFile("MemoryRegion (FilePath)", Path.GetFileName(file),
                () => MemoryRegion.CreateOrOpen(N("LeakFileA"), 4096, new MemoryRegionOptions { FilePath = file }),
                () => MemoryRegion.CreateOrOpen(N("LeakFileB"), 8192, new MemoryRegionOptions { FilePath = file }).Dispose());
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    public struct TinyIntSchema : IMemorySchema
    {
        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<int>("Only");
        }
    }
}
