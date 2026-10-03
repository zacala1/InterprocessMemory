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

    public struct BlobSchema : IMemorySchema
    {
        public const string Data = "Data";

        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Blob(Data, 8);
        }
    }
}
