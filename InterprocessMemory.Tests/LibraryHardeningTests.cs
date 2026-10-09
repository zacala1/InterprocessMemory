using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using InterprocessMemory;

namespace InterprocessMemory.Tests;

[TestFixture]
public class LibraryHardeningTests
{
    private static string N(string prefix) => $"Hardening_{prefix}_{Guid.NewGuid():N}";

    [Test]
    public void CreateFalse_MissingRegions_ShouldThrow()
    {
        Assert.Throws<FileNotFoundException>(() =>
            new MemoryRegion(N("MissingRaw"),
                new MemoryRegionOptions { Capacity = 256, CreateOrOpen = false }));

        Assert.Throws<FileNotFoundException>(() =>
            new SharedArray<int>(N("MissingArray"), 4, create: false));

        Assert.Throws<FileNotFoundException>(() =>
            new SingleProducerByteStream(N("MissingSpsc"), 1024, create: false));

        Assert.Throws<FileNotFoundException>(() =>
            new ConcurrentMessageQueue(N("MissingMpmc"), 4, 64, create: false));

        Assert.Throws<FileNotFoundException>(() =>
            new StructuredMemory<SimpleSchema>(N("MissingStrict"), new SimpleSchema(), create: false));
    }

    [Test]
    public void SingleProducerByteStream_DefaultSecondOpener_DoesNotResetExistingQueue()
    {
        string name = N("SpscNoReset");
        using var writer = new SingleProducerByteStream(name, 1024);
        Assert.That(writer.TryWrite(new byte[] { 1, 2, 3, 4 }), Is.True);

        using var secondOpener = new SingleProducerByteStream(name, 1024);

        Span<byte> read = stackalloc byte[4];
        Assert.That(writer.TryRead(read), Is.EqualTo(4));
        Assert.That(read.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
    }

    [Test]
    public void InfiniteWriteLockTimeout_WaitsUntilReaderReleases()
    {
        using var buffer = new MemoryRegion(
            N("InfiniteLock"),
            new MemoryRegionOptions { Capacity = 256 });

        Assert.That(buffer.TryAcquireReadLock(TimeSpan.FromSeconds(1)), Is.True);
        using var writerStarted = new ManualResetEventSlim(false);

        var writer = Task.Run(() =>
        {
            writerStarted.Set();
            bool acquired = buffer.TryAcquireWriteLock(Timeout.InfiniteTimeSpan);
            if (acquired)
                buffer.ReleaseWriteLock();
            return acquired;
        });

        Assert.That(writerStarted.Wait(TimeSpan.FromSeconds(1)), Is.True);
        Assert.That(writer.Wait(TimeSpan.FromMilliseconds(100)), Is.False);

        buffer.ReleaseReadLock();
        Assert.That(writer.Wait(TimeSpan.FromSeconds(1)), Is.True);
        Assert.That(writer.Result, Is.True);
    }

    [Test]
    public void WriteLock_CannotBeReleasedByDifferentThread()
    {
        using var buffer = new MemoryRegion(
            N("ThreadOwner"),
            new MemoryRegionOptions { Capacity = 256 });

        Assert.That(buffer.TryAcquireWriteLock(TimeSpan.FromSeconds(1)), Is.True);

        // An unhandled exception on a raw Thread would take the test host down, so capture it.
        Exception? releaseError = null;
        var invalidRelease = new Thread(() =>
        {
            try
            { buffer.ReleaseWriteLock(); }
            catch (Exception ex) { releaseError = ex; }
        });
        invalidRelease.Start();
        invalidRelease.Join();

        // The misuse is reported instead of being silently ignored (a silently ignored release is
        // what turned `await` inside a lock scope into a lock nobody could ever release).
        Assert.That(releaseError, Is.InstanceOf<SynchronizationLockException>());

        var contender = Task.Run(() => buffer.TryAcquireWriteLock(TimeSpan.FromMilliseconds(50)));
        Assert.That(contender.Result, Is.False);

        buffer.ReleaseWriteLock();
        Assert.That(buffer.TryAcquireWriteLock(TimeSpan.FromSeconds(1)), Is.True);
        buffer.ReleaseWriteLock();
    }

    [Test]
    public void Dispose_WhileThreadWaitsForWriteLock_ReleasesWaiterInsteadOfCrashing()
    {
        // Dispose used to unmap the view while a thread was still spinning on the header, which is an
        // AccessViolationException: the whole process dies and the exception cannot be caught.
        var buffer = new MemoryRegion(
            N("DisposeWriteWaiter"),
            new MemoryRegionOptions { Capacity = 256 });

        Assert.That(buffer.TryAcquireWriteLock(TimeSpan.FromSeconds(1)), Is.True);

        using var started = new ManualResetEventSlim(false);
        var waiter = Task.Run(() =>
        {
            started.Set();
            return buffer.TryAcquireWriteLock(Timeout.InfiniteTimeSpan);
        });
        Assert.That(started.Wait(TimeSpan.FromSeconds(5)), Is.True);
        Thread.Sleep(100); // let the waiter reach its spin loop

        buffer.Dispose();

        var error = Assert.Throws<AggregateException>(() => waiter.Wait(TimeSpan.FromSeconds(10)));
        Assert.That(error!.InnerException, Is.InstanceOf<ObjectDisposedException>());
    }

    [Test]
    public void Dispose_WhileThreadWaitsForReadLock_ReleasesWaiterInsteadOfCrashing()
    {
        var buffer = new MemoryRegion(
            N("DisposeReadWaiter"),
            new MemoryRegionOptions { Capacity = 256 });

        Assert.That(buffer.TryAcquireWriteLock(TimeSpan.FromSeconds(1)), Is.True);

        using var started = new ManualResetEventSlim(false);
        var waiter = Task.Run(() =>
        {
            started.Set();
            return buffer.TryAcquireReadLock(Timeout.InfiniteTimeSpan);
        });
        Assert.That(started.Wait(TimeSpan.FromSeconds(5)), Is.True);
        Thread.Sleep(100);

        buffer.Dispose();

        var error = Assert.Throws<AggregateException>(() => waiter.Wait(TimeSpan.FromSeconds(10)));
        Assert.That(error!.InnerException, Is.InstanceOf<ObjectDisposedException>());
    }

    [Test]
    public void StructuredMemory_WriteLockGuard_DisposedOnAnotherThread_ThrowsAndStaysHeld()
    {
        string name = N("WriteGuardThread");
        using var memory = StructuredMemory<SimpleSchema>.CreateOrOpen(name, new SimpleSchema());
        using var peer = StructuredMemory<SimpleSchema>.OpenExisting(name, new SimpleSchema());

        // What `await` inside a lock scope does: the guard is disposed on a different thread.
        var guard = memory.AcquireWriteLock();
        Exception? error = null;
        Task.Run(() =>
        {
            try
            { guard.Dispose(); }
            catch (Exception ex) { error = ex; }
        }).Wait();

        Assert.That(error, Is.InstanceOf<SynchronizationLockException>());

        // Nothing was released or unbalanced: the lock still belongs to the acquiring thread...
        Assert.Throws<TimeoutException>(() => peer.AcquireWriteLock(TimeSpan.FromMilliseconds(100)));

        // ...which can still release it properly, after which other holders get in.
        guard.Dispose();
        using (peer.AcquireWriteLock(TimeSpan.FromSeconds(1)))
        {
        }
    }

    [Test]
    public void StructuredMemory_ReadLockGuard_DisposedOnAnotherThread_ThrowsAndStaysHeld()
    {
        string name = N("ReadGuardThread");
        using var memory = StructuredMemory<SimpleSchema>.CreateOrOpen(name, new SimpleSchema());
        using var peer = StructuredMemory<SimpleSchema>.OpenExisting(name, new SimpleSchema());

        var guard = memory.AcquireReadLock();
        Exception? error = null;
        Task.Run(() =>
        {
            try
            { guard.Dispose(); }
            catch (Exception ex) { error = ex; }
        }).Wait();

        Assert.That(error, Is.InstanceOf<SynchronizationLockException>());
        Assert.Throws<TimeoutException>(() => peer.AcquireWriteLock(TimeSpan.FromMilliseconds(100)));

        guard.Dispose();
        using (peer.AcquireWriteLock(TimeSpan.FromSeconds(1)))
        {
        }
    }

    [Test]
    public void DisposeGracePeriod_DefaultsToTenMilliseconds_AndRejectsNegativeValues()
    {
        Assert.That(MemoryRegion.DisposeGracePeriod, Is.EqualTo(TimeSpan.FromMilliseconds(10)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MemoryRegion.DisposeGracePeriod = TimeSpan.FromMilliseconds(-1));
    }

    [Test]
    public void Dispose_KeepsTheMappingForTheGracePeriod()
    {
        TimeSpan original = MemoryRegion.DisposeGracePeriod;
        try
        {
            MemoryRegion.DisposeGracePeriod = TimeSpan.FromMilliseconds(100);
            var withGrace = new MemoryRegion(N("Grace"), new MemoryRegionOptions { Capacity = 256 });
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            withGrace.Dispose();
            Assert.That(stopwatch.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(90));

            MemoryRegion.DisposeGracePeriod = TimeSpan.Zero;
            var withoutGrace = new MemoryRegion(N("NoGrace"), new MemoryRegionOptions { Capacity = 256 });
            stopwatch.Restart();
            withoutGrace.Dispose();
            Assert.That(stopwatch.ElapsedMilliseconds, Is.LessThan(90));
        }
        finally
        {
            MemoryRegion.DisposeGracePeriod = original;
        }
    }

    private static int CountOpenFilesMatching(string name)
    {
        return Directory.GetFiles("/proc/self/fd").Count(path =>
        {
            try
            { return new FileInfo(path).LinkTarget?.Contains(name) == true; }
            catch (IOException) { return false; }
        });
    }

    [Test]
    public void SharedArray_FailedOpen_ReleasesTheMappingImmediately()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Ignore("Counts the process's open file descriptors through /proc.");

        // Opening with another element type fails the header check after the region is mapped.
        // Without an explicit dispose the file descriptor stayed open until the finalizer ran.
        string name = N("ArrayLeak");
        using var owner = SharedArray<int>.CreateOrOpen(name, 4);
        int before = CountOpenFilesMatching(name);

        Assert.Throws<InvalidDataException>(() => SharedArray<long>.OpenExisting(name));

        Assert.That(CountOpenFilesMatching(name), Is.EqualTo(before));
    }

    public struct WideSchema : IMemorySchema
    {
        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<Guid>("Id");
            yield return FieldDefinition.String("Name", 8);
        }
    }

    [Test]
    public void StructuredMemory_AutoLockedAccess_DoesNotAllocatePerCall()
    {
        // Values wider than eight bytes and strings take the region lock automatically. Each of those
        // calls used to allocate a delegate (about 104 bytes) for the lock guard.
        using var memory = StructuredMemory<WideSchema>.CreateOrOpen(N("NoAlloc"), new WideSchema());
        Guid id = Guid.NewGuid();

        for (int i = 0; i < 2000; i++)
        {
            memory.Write("Id", id);
            memory.Read<Guid>("Id");
            memory.WriteString("Name", "ab");
        }

        const int Rounds = 10_000;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Rounds; i++)
        {
            memory.Write("Id", id);
            memory.Read<Guid>("Id");
            memory.WriteString("Name", "ab");
        }
        long bytesPerCall = (GC.GetAllocatedBytesForCurrentThread() - before) / (Rounds * 3);

        Assert.That(bytesPerCall, Is.LessThan(8));
    }

    [Test]
    public void OrphanLockTimeout_IsDisabledByDefault()
    {
        // A time limit takes the lock away from a healthy owner that merely holds it for long
        // (a long transaction, a paused debugger), so it is opt-in. Dead owners are still recovered.
        var options = new MemoryRegionOptions();

        Assert.That(options.OrphanLockTimeout, Is.EqualTo(TimeSpan.Zero));
        Assert.That(options.EnableOrphanLockDetection, Is.True);
    }

    [Test]
    public void ForceResetLocks_ClearsReadersAndHeldWriteLock()
    {
        using var buffer = new MemoryRegion(
            N("ForceReset"),
            new MemoryRegionOptions { Capacity = 256 });

        Assert.That(buffer.TryAcquireReadLock(TimeSpan.FromSeconds(1)), Is.True);
        Assert.That(buffer.GetLockOwnerInfo().ReaderCount, Is.EqualTo(1));
        Assert.That(buffer.TryAcquireWriteLock(TimeSpan.FromMilliseconds(100)), Is.False);

        buffer.ForceResetLocks();

        Assert.That(buffer.GetLockOwnerInfo().ReaderCount, Is.EqualTo(0));
        Assert.That(buffer.TryAcquireWriteLock(TimeSpan.FromSeconds(1)), Is.True);

        // Resetting also frees a write lock that is still "held"; its holder learns that on release.
        buffer.ForceResetLocks();
        Assert.Throws<SynchronizationLockException>(() => buffer.ReleaseWriteLock());

        Assert.That(buffer.TryAcquireWriteLock(TimeSpan.FromSeconds(1)), Is.True);
        buffer.ReleaseWriteLock();
    }

    [Test]
    public void StructuredMemory_ForceResetLocks_UnblocksWriter()
    {
        string name = N("StructuredReset");
        using var memory = StructuredMemory<SimpleSchema>.CreateOrOpen(name, new SimpleSchema());
        using var peer = StructuredMemory<SimpleSchema>.OpenExisting(name, new SimpleSchema());

        // A reader that never comes back, as after a crash.
        var staleReader = peer.AcquireReadLock();
        Assert.Throws<TimeoutException>(() => memory.AcquireWriteLock(TimeSpan.FromMilliseconds(100)));

        memory.ForceResetLocks();

        using (memory.AcquireWriteLock(TimeSpan.FromSeconds(1)))
        {
        }

        staleReader.Dispose();
    }

    [Test]
    public void Remove_DeletesLinuxRegion_SoItCanBeRecreatedWithDifferentCapacity()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Ignore("Only Linux keeps regions in /dev/shm after their last user is gone.");

        string name = N("Remove");
        using (MemoryRegion.CreateOrOpen(name, 256))
        {
        }

        // The region outlives its users, so a different capacity is rejected...
        Assert.Throws<InvalidOperationException>(() => MemoryRegion.CreateOrOpen(name, 512));

        // ...until it is removed.
        Assert.That(MemoryRegion.Remove(name), Is.True);
        Assert.That(MemoryRegion.Remove(name), Is.False);

        using var fresh = MemoryRegion.CreateOrOpen(name, 512);
        Assert.That(fresh.IsOwner, Is.True);
        Assert.That(fresh.Capacity, Is.EqualTo(512));
    }

    [Test]
    public void Remove_ClearsRegionLeftInitializingByCrashedCreator()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Ignore("Only Linux keeps regions in /dev/shm after their last user is gone.");

        // "IPMI": the creator died after claiming initialization and before publishing the header,
        // which makes every opener wait and then time out.
        string name = N("StuckInit");
        using (var file = new FileStream("/dev/shm/" + name, FileMode.CreateNew))
        {
            file.SetLength(128 + 256);
            file.Write(BitConverter.GetBytes(0x494D5049u));
        }

        Assert.That(MemoryRegion.Remove(name), Is.True);

        using var region = MemoryRegion.CreateOrOpen(name, 256);
        Assert.That(region.IsOwner, Is.True);
    }

    [Test]
    public void Remove_FileBackedRegion_DeletesTheFile()
    {
        string name = N("RemoveFile");
        string path = Path.Combine(Path.GetTempPath(), name + ".bin");
        var options = new MemoryRegionOptions { FilePath = path };
        try
        {
            using (MemoryRegion.CreateOrOpen(name, 256, options))
            {
            }

            Assert.That(File.Exists(path), Is.True);
            Assert.That(MemoryRegion.Remove(name, options), Is.True);
            Assert.That(File.Exists(path), Is.False);
            Assert.That(MemoryRegion.Remove(name, options), Is.False);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void Remove_UnknownRegion_ReturnsFalse_AndBadNamesAreRejected()
    {
        Assert.That(MemoryRegion.Remove(N("Nothing")), Is.False);
        Assert.Throws<ArgumentException>(() => MemoryRegion.Remove("a/b"));
        Assert.Throws<ArgumentException>(() => MemoryRegion.Remove(""));
    }

    [Test]
    public void ReadLock_DoubleRelease_DoesNotBreakWriterExclusion()
    {
        using var buffer = new MemoryRegion(
            N("ReadUnderflow"),
            new MemoryRegionOptions { Capacity = 256 });

        Assert.That(buffer.TryAcquireReadLock(TimeSpan.FromSeconds(1)), Is.True);
        buffer.ReleaseReadLock();
        buffer.ReleaseReadLock();

        Assert.That(buffer.TryAcquireReadLock(TimeSpan.FromSeconds(1)), Is.True);
        try
        {
            var writer = Task.Run(() => buffer.TryAcquireWriteLock(TimeSpan.FromMilliseconds(50)));
            Assert.That(writer.Result, Is.False);
        }
        finally
        {
            buffer.ReleaseReadLock();
        }
    }

    [Test]
    public void StructuredMemory_ReorderedSameVersionSchema_ShouldThrow()
    {
        string name = N("SchemaOrder");
        using var owner = new StructuredMemory<OrderedSchema>(name, new OrderedSchema());

        Assert.Throws<InvalidOperationException>(() =>
            new StructuredMemory<ReorderedSchema>(name, new ReorderedSchema(), create: false));
    }

    [Test]
    public void StructuredMemory_InvalidFieldAlignment_ShouldThrow()
    {
        Assert.Throws<ArgumentException>(() =>
            new StructuredMemory<InvalidAlignmentSchema>(N("BadAlignment"), new InvalidAlignmentSchema()));
    }

    [Test]
    public void StructuredMemory_CreatorStoredSchemaVersion_ShouldMatchCurrentVersion()
    {
        using var memory = new StructuredMemory<VersionedSimpleSchema>(
            N("StoredVersion"),
            new VersionedSimpleSchema());

        Assert.That(memory.StoredSchemaVersion, Is.EqualTo(memory.SchemaVersion));
    }

    [Test]
    public void StructuredMemory_CorruptBlobLength_ShouldThrowInvalidData()
    {
        string name = N("BlobLength");
        using var memory = new StructuredMemory<BlobSchema>(name, new BlobSchema());
        memory.WriteBlob(BlobSchema.Data, new byte[] { 1, 2, 3 });

        using var raw = MemoryRegion.OpenExisting(
            name,
            options: null,
            RegionKind.StructuredMemory);
        raw.Write(BitConverter.GetBytes(999), 64);

        Assert.Throws<InvalidDataException>(() => memory.ReadBlob(BlobSchema.Data));
    }

    [Test]
    public void OrphanEvent_RespectsEnableEvents()
    {
        using var disabled = new MemoryRegion(
            N("OrphanEventOff"),
            new MemoryRegionOptions
            {
                Capacity = 256,
                EnableEvents = false,
                OrphanLockTimeout = TimeSpan.FromMilliseconds(1)
            });

        bool disabledRaised = false;
        disabled.OnOrphanLockDetected += (_, _) => disabledRaised = true;
        Assert.That(disabled.TryAcquireWriteLock(TimeSpan.FromSeconds(1)), Is.True);
        Assert.That(SpinWait.SpinUntil(
            () => disabled.IsWriteLockOrphaned(),
            TimeSpan.FromSeconds(1)), Is.True);
        Assert.That(disabled.TryForceReleaseWriteLock(), Is.True);
        Assert.That(disabledRaised, Is.False);

        using var enabled = new MemoryRegion(
            N("OrphanEventOn"),
            new MemoryRegionOptions
            {
                Capacity = 256,
                EnableEvents = true,
                OrphanLockTimeout = TimeSpan.FromMilliseconds(1)
            });

        bool enabledRaised = false;
        enabled.OnOrphanLockDetected += (_, _) => enabledRaised = true;
        Assert.That(enabled.TryAcquireWriteLock(TimeSpan.FromSeconds(1)), Is.True);
        Assert.That(SpinWait.SpinUntil(
            () => enabled.IsWriteLockOrphaned(),
            TimeSpan.FromSeconds(1)), Is.True);
        Assert.That(enabled.TryForceReleaseWriteLock(), Is.True);
        Assert.That(enabledRaised, Is.True);
    }

    [Test]
    public void ConcurrentMessageQueue_ZeroLengthMessage_ShouldThrow()
    {
        using var buffer = new ConcurrentMessageQueue(N("MpmcZero"), 4, 64);

        Assert.Throws<ArgumentException>(() => buffer.TryWrite(ReadOnlySpan<byte>.Empty));
    }

    public struct SimpleSchema : IMemorySchema
    {
        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<int>("Value");
        }
    }

    public struct OrderedSchema : IMemorySchema
    {
        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<int>("A");
            yield return FieldDefinition.Scalar<double>("B");
        }
    }

    public struct ReorderedSchema : IMemorySchema
    {
        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<double>("B");
            yield return FieldDefinition.Scalar<int>("A");
        }
    }

    public struct InvalidAlignmentSchema : IMemorySchema
    {
        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return new FieldDefinition
            {
                Name = "Bad",
                TypeCode = FieldTypeCode.Int32,
                ElementSize = 4,
                ArrayLength = 1,
                Alignment = 3
            };
        }
    }

    public struct VersionedSimpleSchema : IVersionedSchema
    {
        public int Version => 7;
        public bool IsCompatibleWith(int otherVersion) => otherVersion == Version;

        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<int>("Value");
        }
    }

    [Test, Timeout(30000)]
    public void ConcurrentQueue_CapacityOne_IsRaisedToTwoAndStillReportsFull()
    {
        // With one slot the sequence that marks a slot published (write + 1) equals the one that marks it
        // free again (read + capacity), so the second enqueue overwrote the first item and the queue then
        // never produced anything again.
        using var queue = ConcurrentQueue<int>.CreateOrOpen(N("MpmcCap1"), 1);

        Assert.That(queue.Capacity, Is.EqualTo(2));
        Assert.That(queue.TryEnqueue(10), Is.True);
        Assert.That(queue.TryEnqueue(20), Is.True);
        Assert.That(queue.TryEnqueue(30), Is.False, "the queue is full, it must not overwrite item 10");

        Assert.That(queue.TryDequeue(out int first), Is.True);
        Assert.That(first, Is.EqualTo(10));
        Assert.That(queue.TryDequeue(out int second), Is.True);
        Assert.That(second, Is.EqualTo(20));
        Assert.That(queue.TryDequeue(out _), Is.False);

        for (int i = 0; i < 10; i++)
        {
            Assert.That(queue.TryEnqueue(i), Is.True);
            Assert.That(queue.TryDequeue(out int value), Is.True);
            Assert.That(value, Is.EqualTo(i));
        }
    }

    [Test, Timeout(30000)]
    public void ConcurrentMessageQueue_CapacityOne_IsRaisedToTwoAndStillReportsFull()
    {
        using var queue = ConcurrentMessageQueue.CreateOrOpen(N("MqCap1"), 1, 16);
        var a = new byte[] { 1 };
        var b = new byte[] { 2 };
        var buffer = new byte[16];

        Assert.That(queue.Capacity, Is.EqualTo(2));
        Assert.That(queue.TryEnqueue(a), Is.True);
        Assert.That(queue.TryEnqueue(b), Is.True);
        Assert.That(queue.TryEnqueue(new byte[] { 3 }), Is.False, "the queue is full, it must not overwrite message 1");

        Assert.That(queue.TryDequeue(buffer, out int length), Is.True);
        Assert.That(length, Is.EqualTo(1));
        Assert.That(buffer[0], Is.EqualTo(1));
        Assert.That(queue.TryDequeue(buffer, out length), Is.True);
        Assert.That(buffer[0], Is.EqualTo(2));
        Assert.That(queue.TryDequeue(buffer, out _), Is.False);
    }

    [Test, Timeout(30000)]
    public void ConcurrentQueue_ReopenedWithCapacityOne_MatchesTheRaisedCapacity()
    {
        string name = N("MpmcCap1Reopen");
        using var owner = ConcurrentQueue<int>.CreateOrOpen(name, 1);
        using var reopened = ConcurrentQueue<int>.CreateOrOpen(name, 1);

        Assert.That(reopened.Capacity, Is.EqualTo(2));
    }

    [Test]
    public void EnsureFreeSpace_ThrowsWhenTheFilesystemCannotHoldTheRegion()
    {
        string directory = Path.GetTempPath();

        Assert.DoesNotThrow(() => MemoryRegion.EnsureFreeSpace(directory, 1024, "small"));
        var ex = Assert.Throws<IOException>(() => MemoryRegion.EnsureFreeSpace(directory, long.MaxValue, "huge"));
        Assert.That(ex!.Message, Does.Contain("huge"));
        Assert.That(ex.Message, Does.Contain("--shm-size"));

        // A path the OS cannot describe must not block creating the region.
        Assert.DoesNotThrow(() => MemoryRegion.EnsureFreeSpace("\0:/does-not-exist", long.MaxValue, "unknown"));
    }

    [Test, Timeout(30000)]
    public void CreateOrOpen_WithMoreThanTheDevShmCanHold_ThrowsInsteadOfCrashingLater()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Ignore("Only the Linux /dev/shm backing can be oversubscribed.");

        // A sparse tmpfs file of this size is created without error, and the process dies with SIGBUS
        // at the first write that does not fit. 4 TiB is far beyond any /dev/shm.
        string name = N("TooBig");
        Assert.Throws<IOException>(() => MemoryRegion.CreateOrOpen(name, 4L << 40));
        Assert.That(File.Exists("/dev/shm/" + name), Is.False, "the failed region must not leave a file behind");
    }

    [Test, Timeout(30000)]
    public void StructuredMemory_ExplicitWriteLock_WhileHoldingOnlyAReadLock_ThrowsInsteadOfBlockingEveryone()
    {
        // The automatic write lock already refused this. The explicit one set the writer flag and then
        // waited for this very thread's read lock to go away: every other reader and writer queued behind
        // it until the timeout, or for good with an infinite timeout.
        string name = N("ExplicitUpgrade");
        using var memory = StructuredMemory<SimpleSchema>.CreateOrOpen(name, new SimpleSchema());

        using (memory.AcquireReadLock())
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Assert.Throws<InvalidOperationException>(() => memory.AcquireWriteLock(TimeSpan.FromSeconds(10)));
            Assert.That(sw.ElapsedMilliseconds, Is.LessThan(2000), "it must fail at once, not wait for the timeout");
        }

        using (memory.AcquireWriteLock(TimeSpan.FromSeconds(1)))
        {
        }
    }

    [Test]
    public void SingleProducerByteStream_AvailableAndUsed_AfterDispose_Throw()
    {
        // Both properties read the unmapped header, which ends the process, instead of failing like the
        // other members do.
        var stream = SingleProducerByteStream.CreateOrOpen(N("SpscDisposedProps"), 1024);
        stream.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _ = stream.Available);
        Assert.Throws<ObjectDisposedException>(() => _ = stream.Used);
    }

    [Test, Timeout(30000)]
    public void ConcurrentQueue_AWaitingCall_CountsAsOneFailureNotOnePerPoll()
    {
        using var queue = ConcurrentQueue<int>.CreateOrOpen(N("MpmcFailCount"), 4);

        Assert.That(queue.TryDequeue(out _, TimeSpan.FromMilliseconds(300)), Is.False);
        Assert.That(queue.GetStatistics().FailedDequeues, Is.EqualTo(1));

        for (int i = 0; i < 4; i++)
            Assert.That(queue.TryEnqueue(i), Is.True);
        Assert.That(queue.TryEnqueue(99, TimeSpan.FromMilliseconds(300)), Is.False);
        Assert.That(queue.GetStatistics().FailedEnqueues, Is.EqualTo(1));

        // A call that waits and then succeeds is not a failure.
        for (int i = 0; i < 4; i++)
            Assert.That(queue.TryDequeue(out _), Is.True);
        Task<bool> waiter = Task.Run(() => queue.TryDequeue(out _, TimeSpan.FromSeconds(10)));
        Thread.Sleep(100);
        Assert.That(queue.TryEnqueue(7), Is.True);
        Assert.That(waiter.Wait(TimeSpan.FromSeconds(10)), Is.True);
        Assert.That(waiter.Result, Is.True);
        Assert.That(queue.GetStatistics().FailedDequeues, Is.EqualTo(1));
    }

    [Test, Timeout(30000)]
    public void ConcurrentMessageQueue_AWaitingCall_CountsAsOneFailureNotOnePerPoll()
    {
        using var queue = ConcurrentMessageQueue.CreateOrOpen(N("MqFailCount"), 2, 16);
        var buffer = new byte[16];

        Assert.That(queue.TryDequeue(buffer, out _, TimeSpan.FromMilliseconds(300)), Is.False);
        Assert.That(queue.GetStatistics().FailedReads, Is.EqualTo(1));

        Assert.That(queue.TryEnqueue(new byte[] { 1 }), Is.True);
        Assert.That(queue.TryEnqueue(new byte[] { 2 }), Is.True);
        Assert.That(queue.TryEnqueue(new byte[] { 3 }, TimeSpan.FromMilliseconds(300)), Is.False);
        Assert.That(queue.GetStatistics().FailedWrites, Is.EqualTo(1));
    }

    [Test, Timeout(120000)]
    public void OpenExisting_RacingTheCreator_WaitsInsteadOfReportingACorruptHeader()
    {
        // The creator makes the backing file, sizes it and writes the header one step after another.
        // An opener that arrived in between found an empty file or a zero magic number and reported
        // "invalid header" (about 9% of 300 races) instead of waiting the few microseconds.
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();

        for (int round = 0; round < 300; round++)
        {
            string name = N($"OpenRace{round}");
            using var go = new ManualResetEventSlim(false);
            MemoryRegion? created = null;

            var creator = new Thread(() =>
            {
                go.Wait();
                created = MemoryRegion.CreateOrOpen(name, 256);
            });
            var opener = new Thread(() =>
            {
                go.Wait();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.Elapsed < TimeSpan.FromSeconds(5))
                {
                    try
                    {
                        using var region = MemoryRegion.OpenExisting(name);
                        return;
                    }
                    catch (FileNotFoundException)
                    {
                        // Not created yet: the only error a caller is expected to retry.
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"round {round}: {ex.GetType().Name}: {ex.Message}");
                        return;
                    }
                }
            });

            creator.Start();
            opener.Start();
            go.Set();
            Assert.That(creator.Join(TimeSpan.FromSeconds(30)), Is.True);
            Assert.That(opener.Join(TimeSpan.FromSeconds(30)), Is.True);
            created?.Dispose();
            MemoryRegion.Remove(name);
        }

        Assert.That(failures, Is.Empty, $"{failures.Count} of 300 races failed, e.g. {failures.FirstOrDefault()}");
    }

    [Test, Timeout(120000)]
    public void CreateOrOpen_LosingTheCreationRace_DoesNotDeleteTheWinnersFile()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Ignore("The race is about the file in /dev/shm.");

        // Both processes saw an empty file, both took the creator's role, and the one whose capacity did
        // not match then deleted the file that the other was using: later openers got a second, separate
        // region under the same name (981 of 3000 races).
        int deleted = 0;
        int unexpected = 0;

        for (int round = 0; round < 300; round++)
        {
            string name = N($"CreateRace{round}");
            using var go = new Barrier(2);
            MemoryRegion?[] regions = new MemoryRegion?[2];
            Exception?[] errors = new Exception?[2];

            Thread Start(int index, long capacity) => new Thread(() =>
            {
                try
                {
                    go.SignalAndWait();
                    regions[index] = MemoryRegion.CreateOrOpen(name, capacity);
                }
                catch (Exception ex)
                {
                    errors[index] = ex;
                }
            });

            Thread a = Start(0, 1024);
            Thread b = Start(1, 2048);
            a.Start();
            b.Start();
            Assert.That(a.Join(TimeSpan.FromSeconds(30)), Is.True);
            Assert.That(b.Join(TimeSpan.FromSeconds(30)), Is.True);

            int winners = regions.Count(r => r != null);
            if (winners != 1 || errors.Count(e => e is InvalidOperationException) != 1)
                unexpected++;
            else if (!File.Exists("/dev/shm/" + name))
                deleted++;

            foreach (var region in regions)
                region?.Dispose();
            MemoryRegion.Remove(name);
        }

        Assert.That(unexpected, Is.EqualTo(0), "exactly one creator must win and the other must report the size mismatch");
        Assert.That(deleted, Is.EqualTo(0), "the loser must not unlink the winner's file");
    }

    [Test]
    public void FilePath_ExistingFileOfAnotherSize_IsRejectedWithoutChangingIt()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ipm_resize_{Guid.NewGuid():N}.bin");
        string name = N("FileResize");
        try
        {
            using (MemoryRegion.CreateOrOpen(name, 1000, new MemoryRegionOptions { FilePath = path }))
            {
            }

            long before = new FileInfo(path).Length;

            Assert.Throws<InvalidOperationException>(() =>
                MemoryRegion.CreateOrOpen(name, 2000, new MemoryRegionOptions { FilePath = path }));
            Assert.That(new FileInfo(path).Length, Is.EqualTo(before),
                "the file used to be grown to the requested size before anything was checked");

            using var again = MemoryRegion.CreateOrOpen(name, 1000, new MemoryRegionOptions { FilePath = path });
            Assert.That(again.Capacity, Is.EqualTo(1000));
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    [Test]
    public void FilePath_ExistingVersion2File_IsReportedAsOldFormatAndNotResized()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ipm_v2size_{Guid.NewGuid():N}.bin");
        try
        {
            byte[] bytes = new byte[128 + 4096];
            BitConverter.TryWriteBytes(bytes.AsSpan(0), 0x48504D53u);
            BitConverter.TryWriteBytes(bytes.AsSpan(4), 2u);
            File.WriteAllBytes(path, bytes);

            var error = Assert.Throws<InvalidDataException>(() =>
                MemoryRegion.CreateOrOpen(N("V2Size"), 8192, new MemoryRegionOptions { FilePath = path }));

            Assert.That(error!.Message, Does.Contain("2.x"));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes), "the old file must not be modified");
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    [Test]
    public void FilePath_TwoInstancesOfTheSameFile_ShareTheMemory()
    {
        if (OperatingSystem.IsWindows())
            Assert.Ignore("Windows names the mapping after the region; a second named mapping of one region is a separate case.");

        string path = Path.Combine(Path.GetTempPath(), $"ipm_shared_{Guid.NewGuid():N}.bin");
        string name = N("FileShared");
        var options = new MemoryRegionOptions { FilePath = path };
        try
        {
            using var first = MemoryRegion.CreateOrOpen(name, 256, options);
            using var second = MemoryRegion.OpenExisting(name, options);

            first.Write(new byte[] { 7, 8, 9 }, 0);
            var read = new byte[3];
            second.Read(read, 0);
            Assert.That(read, Is.EqualTo(new byte[] { 7, 8, 9 }));
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (Memory<byte> Memory, WeakReference Region) GetMemoryAndForgetTheRegion(string name)
    {
        var region = MemoryRegion.CreateOrOpen(name, 256);
        return (region.GetMemory(0, 64), new WeakReference(region));
    }

    [Test, Timeout(30000)]
    public void GetMemory_KeepsTheRegionAliveForAsLongAsTheMemoryIsUsed()
    {
        // The Memory<byte> wraps a raw pointer into the mapping. Nothing tied it to the MemoryRegion, so a
        // caller that dropped the region and kept the Memory let the finalizer unmap the view under it.
        var (memory, region) = GetMemoryAndForgetTheRegion(N("Rooted"));

        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.That(region.IsAlive, Is.True, "the region was finalized while its Memory<byte> was still in use");
        memory.Span[0] = 42;
        Assert.That(memory.Span[0], Is.EqualTo((byte)42));
        GC.KeepAlive(memory);
    }

    [Test, Timeout(30000)]
    public void StructuredMemory_LockGuardCopyDisposedTwice_IsRefusedWithoutDisturbingTheLockState()
    {
        string name = N("GuardCopy");
        using var memory = StructuredMemory<GuidSchema>.CreateOrOpen(name, new GuidSchema());
        using var peer = StructuredMemory<GuidSchema>.OpenExisting(name, new GuidSchema());

        using (memory.AcquireWriteLock())
        {
            var inner = memory.AcquireWriteLock();
            var copy = inner;
            inner.Dispose();

            Assert.Throws<SynchronizationLockException>(() => copy.Dispose());

            // Still inside the outer guard: an automatic lock must not try to take the region lock again.
            memory.Write("Value", Guid.NewGuid());
        }

        Assert.That(Task.Run(() => { using (peer.AcquireWriteLock(TimeSpan.FromSeconds(2))) { } }).Wait(TimeSpan.FromSeconds(5)), Is.True);
    }

    [Test, Timeout(30000)]
    public void StructuredMemory_WriteReleasedBeforeTheReadGuardInsideIt_IsRefusedAndTheLockStaysHeld()
    {
        string name = N("GuardOrder");
        using var memory = StructuredMemory<GuidSchema>.CreateOrOpen(name, new GuidSchema());
        using var peer = StructuredMemory<GuidSchema>.OpenExisting(name, new GuidSchema());

        var write = memory.AcquireWriteLock();
        var read = memory.AcquireReadLock();

        Assert.Throws<SynchronizationLockException>(() => write.Dispose());
        Assert.That(Task.Run(() => peer.Read<Guid>("Value")).Wait(TimeSpan.FromMilliseconds(300)), Is.False,
            "the write lock must still be held");

        read.Dispose();
        write.Dispose();
        Assert.That(Task.Run(() => peer.Read<Guid>("Value")).Wait(TimeSpan.FromSeconds(5)), Is.True);
    }

    [Test, Timeout(30000)]
    public void StructuredMemory_DisposeWithAnOpenGuardOnThisThread_DoesNotLeaveTheCrossProcessLockHeld()
    {
        string name = N("DisposeOpenGuard");
        var memory = StructuredMemory<GuidSchema>.CreateOrOpen(name, new GuidSchema());
        using var peer = StructuredMemory<GuidSchema>.OpenExisting(name, new GuidSchema());

        var guard = memory.AcquireWriteLock();
        memory.Dispose();

        Assert.That(Task.Run(() => { using (peer.AcquireWriteLock(TimeSpan.FromSeconds(2))) { } }).Wait(TimeSpan.FromSeconds(5)), Is.True,
            "the lock was still held after the instance was disposed");

        Assert.DoesNotThrow(() => guard.Dispose(), "a guard that outlives its instance is harmless");
    }

    // A 16 byte field: written and read under the shared lock, which is what the guard tests need.
    public struct GuidSchema : IMemorySchema
    {
        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<Guid>("Value");
        }
    }

    public struct EvolvingV1 : IVersionedSchema
    {
        public int Version => 1;
        public bool IsCompatibleWith(int otherVersion) => true;

        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<int>("A");
            yield return FieldDefinition.Scalar<double>("B");
        }
    }

    // The same fields as V1 plus an appended one that makes the region larger.
    public struct EvolvingV2 : IVersionedSchema
    {
        public int Version => 2;
        public bool IsCompatibleWith(int otherVersion) => true;

        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<int>("A");
            yield return FieldDefinition.Scalar<double>("B");
            yield return FieldDefinition.String("Label", 100);
        }
    }

    [Test]
    public void StructuredMemory_OlderSchema_CanOpenALargerRegionOfANewerVersion_WithForwardCompatibility()
    {
        // The size check ran before the version check, so no compatibility mode could ever open a region of
        // a different size: Forward and Full only worked when the appended field fitted in the padding.
        string name = N("EvolveForward");
        using var writer = StructuredMemory<EvolvingV2>.CreateOrOpen(name, new EvolvingV2());
        writer.Write("A", 42);
        writer.Write("B", 2.5);

        using var reader = StructuredMemory<EvolvingV1>.OpenExisting(name, new EvolvingV1(), SchemaCompatibility.Forward);
        Assert.That(reader.Read<int>("A"), Is.EqualTo(42));
        Assert.That(reader.Read<double>("B"), Is.EqualTo(2.5));

        using var full = StructuredMemory<EvolvingV1>.OpenExisting(name, new EvolvingV1(), SchemaCompatibility.Full);
        Assert.That(full.Read<int>("A"), Is.EqualTo(42));
    }

    [Test]
    public void StructuredMemory_StrictOrWrongDirection_ReportsTheVersionMismatchNotTheSize()
    {
        string name = N("EvolveStrict");
        using var writer = StructuredMemory<EvolvingV2>.CreateOrOpen(name, new EvolvingV2());

        var strict = Assert.Throws<InvalidOperationException>(() =>
            StructuredMemory<EvolvingV1>.OpenExisting(name, new EvolvingV1()));
        Assert.That(strict!.Message, Does.Contain("version mismatch"));

        // Backward means "the region is older than the schema"; this region is newer.
        Assert.Throws<InvalidOperationException>(() =>
            StructuredMemory<EvolvingV1>.OpenExisting(name, new EvolvingV1(), SchemaCompatibility.Backward));
    }

    [Test]
    public void StructuredMemory_NewerSchema_CannotOpenASmallerRegion()
    {
        // A region cannot be smaller than the schema that opens it, whatever the mode says: failing here is
        // better than failing at the first read of an appended field.
        string name = N("EvolveBackward");
        using var writer = StructuredMemory<EvolvingV1>.CreateOrOpen(name, new EvolvingV1());

        var error = Assert.Throws<InvalidDataException>(() =>
            StructuredMemory<EvolvingV2>.OpenExisting(name, new EvolvingV2(), SchemaCompatibility.Backward));
        Assert.That(error!.Message, Does.Contain("contains only"));

        Assert.Throws<InvalidDataException>(() =>
            StructuredMemory<EvolvingV2>.OpenExisting(name, new EvolvingV2(), SchemaCompatibility.Full));
    }

    [Test]
    public void StructuredMemory_SameVersionOfAnotherSize_IsStillRejectedAsASizeMismatch()
    {
        string name = N("EvolveSameVersion");
        using var writer = StructuredMemory<EvolvingV1>.CreateOrOpen(name, new EvolvingV1());

        Assert.Throws<InvalidDataException>(() =>
            StructuredMemory<SameVersionDifferentSize>.OpenExisting(name, new SameVersionDifferentSize(), SchemaCompatibility.Full));
    }

    public struct SameVersionDifferentSize : IVersionedSchema
    {
        public int Version => 1;
        public bool IsCompatibleWith(int otherVersion) => true;

        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<int>("A");
            yield return FieldDefinition.Scalar<double>("B");
            yield return FieldDefinition.String("Extra", 100);
        }
    }

    public struct BlobSchema : IMemorySchema
    {
        public const string Data = "Data";

        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Blob(Data, 8);
        }
    }
}
