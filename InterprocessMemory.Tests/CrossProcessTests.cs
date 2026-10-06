using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;
using NUnit.Framework;
using InterprocessMemory;

namespace InterprocessMemory.Tests;

/// <summary>
/// Cross-process integration tests that validate actual IPC scenarios by spawning
/// a child process (dotnet InterprocessMemory.TestWorker.dll) to perform complementary read/write
/// operations on the same named shared memory region.
///
/// These tests are the only ones that exercise the true "two separate processes share
/// memory" path; all other tests operate within a single process.
/// </summary>
[TestFixture]
[Category("CrossProcess")]
public class CrossProcessTests
{
    private static readonly string HelperDll = Path.Combine(
        AppContext.BaseDirectory,
        "InterprocessMemory.TestWorker.dll");

    [OneTimeSetUp]
    public void EnsureHelperExists()
    {
        if (!File.Exists(HelperDll))
            Assert.Ignore($"Test worker binary not found at '{HelperDll}'. " +
                          "Build the InterprocessMemory.TestWorker project first.");
    }

    private static string GetUniqueName(string prefix) =>
        $"CP_{prefix}_{Guid.NewGuid():N}";

    /// <summary>Spawns a child IpcHelper process and waits for it to exit.</summary>
    private static ProcessStartInfo CreateHelperStartInfo(
        string role,
        string bufferName,
        string? extraArgument = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };
        psi.ArgumentList.Add(HelperDll);
        psi.ArgumentList.Add(role);
        psi.ArgumentList.Add(bufferName);
        if (extraArgument is not null)
            psi.ArgumentList.Add(extraArgument);
        return psi;
    }

    private (int exitCode, string stdout, string stderr) SpawnHelper(
        string role,
        string bufferName,
        int timeoutMs = 15000,
        string? extraArgument = null)
    {
        ProcessStartInfo psi = CreateHelperStartInfo(role, bufferName, extraArgument);

        using var proc = Process.Start(psi)!;
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        if (!proc.WaitForExit(timeoutMs))
        {
            proc.Kill();
            Assert.Fail($"Child process [{role}] timed out after {timeoutMs} ms");
        }

        return (proc.ExitCode, stdout.ToString().Trim(), stderr.ToString().Trim());
    }

    // ── MemoryRegion ──────────────────────────────────────────

    [Test, Timeout(30000)]
    public void CrossProcess_HPBuffer_ParentWrites_ChildReads()
    {
        string name = GetUniqueName("HPW");
        using var buf = MemoryRegion.CreateOrOpen(name, 256);

        var data = new byte[64];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i + 1);
        buf.Write(data, 0);

        var (exit, stdout, stderr) = SpawnHelper("hpbuf_reader", name);
        Assert.That(stderr, Is.Empty, $"stderr: {stderr}");
        Assert.That(exit,   Is.EqualTo(0), $"exit={exit} stdout={stdout}");
        Assert.That(stdout, Does.Contain("verified"));
    }

    [Test, Timeout(30000)]
    public void CrossProcess_HPBuffer_ChildWrites_ParentReads()
    {
        string name = GetUniqueName("HPR");
        using var buf = MemoryRegion.CreateOrOpen(name, 256);

        var (exit, stdout, stderr) = SpawnHelper("hpbuf_writer", name);
        Assert.That(exit, Is.EqualTo(0), $"exit={exit} stdout={stdout} stderr={stderr}");

        var result = new byte[64];
        buf.Read(result, 0);
        for (int i = 0; i < result.Length; i++)
            Assert.That(result[i], Is.EqualTo((byte)(i + 1)), $"index {i}");
    }

    // ── SingleProducerByteStream (SPSC) ────────────────────────────────────────

    [Test, Timeout(30000)]
    public void CrossProcess_SPSC_ParentProduces_ChildConsumes()
    {
        string name = GetUniqueName("SPSC_PC");
        using var buf = SingleProducerByteStream.CreateOrOpen(name, 4096);

        // Parent produces all 100 messages before child starts to avoid SPSC race
        for (int i = 0; i < 100; i++)
            Assert.That(buf.WaitWrite(BitConverter.GetBytes(i), TimeSpan.FromSeconds(5)), Is.True,
                $"WaitWrite failed at {i}");

        var (exit, stdout, stderr) = SpawnHelper("spsc_consumer", name);
        Assert.That(stderr, Is.Empty, $"stderr: {stderr}");
        Assert.That(exit,   Is.EqualTo(0), $"exit={exit} stdout={stdout}");
        Assert.That(stdout, Does.Contain("consumed:100"));
    }

    [Test, Timeout(30000)]
    public void CrossProcess_SPSC_ChildProduces_ParentConsumes()
    {
        string name = GetUniqueName("SPSC_CP");
        using var buf = SingleProducerByteStream.CreateOrOpen(name, 4096);

        // Child produces first, then parent consumes (avoids concurrent SPSC access)
        var (exit, stdout, stderr) = SpawnHelper("spsc_producer", name);
        Assert.That(exit, Is.EqualTo(0), $"exit={exit} stdout={stdout} stderr={stderr}");
        Assert.That(stdout, Does.Contain("produced:100"));

        var dst = new byte[4];
        for (int i = 0; i < 100; i++)
        {
            int read = buf.WaitRead(dst, TimeSpan.FromSeconds(5));
            Assert.That(read, Is.EqualTo(4),    $"Short read at {i}");
            Assert.That(BitConverter.ToInt32(dst), Is.EqualTo(i), $"Value at {i}");
        }
    }

    // ── StructuredMemory ───────────────────────────────────────────────────

    [Test, Timeout(30000)]
    public void CrossProcess_StrictMemory_ParentWrites_ChildReads()
    {
        string name = GetUniqueName("Strict_PW");
        var schema = new IpcTestSchema();
        using var mem = StructuredMemory<IpcTestSchema>.CreateOrOpen(name, schema);

        using (mem.AcquireWriteLock())
        {
            mem.Write(IpcTestSchema.Counter, 42);
            mem.WriteString(IpcTestSchema.Label, "hello");
        }

        var (exit, stdout, stderr) = SpawnHelper("strict_reader", name);
        Assert.That(stderr, Is.Empty, $"stderr: {stderr}");
        Assert.That(exit,   Is.EqualTo(0), $"exit={exit} stdout={stdout}");
        Assert.That(stdout, Does.Contain("strict_verified"));
    }

    [Test, Timeout(30000)]
    public void CrossProcess_StrictMemory_ChildWrites_ParentReads()
    {
        string name = GetUniqueName("Strict_CR");
        var schema = new IpcTestSchema();
        using var mem = StructuredMemory<IpcTestSchema>.CreateOrOpen(name, schema);

        var (exit, stdout, stderr) = SpawnHelper("strict_writer", name);
        Assert.That(exit, Is.EqualTo(0), $"exit={exit} stdout={stdout} stderr={stderr}");

        int counter;
        string label;
        using (mem.AcquireReadLock())
        {
            counter = mem.Read<int>(IpcTestSchema.Counter);
            label   = mem.ReadString(IpcTestSchema.Label);
        }
        Assert.That(counter, Is.EqualTo(42));
        Assert.That(label,   Is.EqualTo("hello"));
    }

    [Test, Timeout(30000)]
    public void CrossProcess_TypedSpsc_ParentProduces_ChildConsumes()
    {
        string name = GetUniqueName("TypedSpsc");
        using var queue = SingleProducerQueue<int>.CreateOrOpen(name, 128);
        for (int i = 0; i < 100; i++)
            Assert.That(queue.TryEnqueue(i), Is.True);

        var (exit, stdout, stderr) = SpawnHelper("typed_consumer", name);
        Assert.That(exit, Is.EqualTo(0), stderr);
        Assert.That(stdout, Does.Contain("typed_consumed:100"));
    }

    [Test, Timeout(60000)]
    public void CrossProcess_TypedMpmc_MultipleProcesses_DeliverExactlyOnce()
    {
        string name = GetUniqueName("TypedMpmc");
        using var queue = InterprocessMemory.ConcurrentQueue<int>.CreateOrOpen(name, 8192);
        const int producerCount = 4;
        const int perProducer = 1000;
        var processes = new List<Process>();

        try
        {
            for (int producerId = 0; producerId < producerCount; producerId++)
            {
                processes.Add(Process.Start(CreateHelperStartInfo(
                    "concurrent_producer",
                    name,
                    producerId.ToString()))!);
            }

            foreach (Process process in processes)
            {
                Assert.That(process.WaitForExit(30000), Is.True, "producer process timed out");
                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                Assert.That(process.ExitCode, Is.EqualTo(0), $"{stdout}\n{stderr}");
            }

            var seen = new int[producerCount * perProducer];
            for (int i = 0; i < seen.Length; i++)
            {
                Assert.That(
                    queue.TryDequeue(out int value, TimeSpan.FromSeconds(10)),
                    Is.True,
                    $"dequeue timed out at {i}");
                Assert.That(value, Is.InRange(0, seen.Length - 1));
                seen[value]++;
            }

            Assert.That(seen, Is.All.EqualTo(1));
        }
        finally
        {
            foreach (Process process in processes)
                process.Dispose();
        }
    }

    [Test, Timeout(30000)]
    public void CrossProcess_WriteLock_IsMutuallyExclusive()
    {
        string name = GetUniqueName("Lock");
        using var region = MemoryRegion.CreateOrOpen(name, 256);
        Assert.That(region.TryAcquireWriteLock(TimeSpan.FromSeconds(1)), Is.True);
        try
        {
            var blocked = SpawnHelper("try_write_lock", name);
            Assert.That(blocked.exitCode, Is.EqualTo(2));
            Assert.That(blocked.stdout, Does.Contain("lock_timeout"));
        }
        finally
        {
            region.ReleaseWriteLock();
        }

        var acquired = SpawnHelper("try_write_lock", name);
        Assert.That(acquired.exitCode, Is.EqualTo(0), acquired.stderr);
        Assert.That(acquired.stdout, Does.Contain("lock_acquired"));
    }

    [Test, Timeout(30000)]
    public void CrossProcess_OrphanWriteLock_IsRecovered()
    {
        string name = GetUniqueName("Orphan");
        using var region = MemoryRegion.CreateOrOpen(name, 256);
        var orphan = SpawnHelper("orphan_write_lock", name);
        Assert.That(orphan.exitCode, Is.EqualTo(0), orphan.stderr);
        Assert.That(orphan.stdout, Does.Contain("orphan_locked"));

        Assert.That(region.TryAcquireWriteLock(TimeSpan.FromSeconds(5)), Is.True);
        region.ReleaseWriteLock();
    }

    /// <summary>
    /// Starts a child that holds a lock until it is killed, and returns once the child has reported
    /// that it holds the lock. <paramref name="role"/> is <c>hold_write_lock</c> or <c>hold_read_lock</c>.
    /// </summary>
    private static Process StartLockHolder(string bufferName, string role = "hold_write_lock")
    {
        Process process = Process.Start(CreateHelperStartInfo(role, bufferName))!;
        string? line = process.StandardOutput.ReadLine();
        if (line != "holding")
        {
            try
            { process.Kill(); }
            catch (InvalidOperationException) { /* already exited */ }
            process.Dispose();
            Assert.Fail($"lock holder did not report 'holding' (got '{line}')");
        }

        return process;
    }

    /// <summary>Kills the process (a no-op when it has already exited) and waits for it to be gone.</summary>
    private static void KillAndWait(Process process)
    {
        try
        { process.Kill(); }
        catch (InvalidOperationException) { /* already exited */ }
        process.WaitForExit();
    }

    [Test, Timeout(30000)]
    public void CrossProcess_LiveWriteLockOwner_IsNotReportedAsOrphan()
    {
        // Process.StartTime is derived per observing process from a wall-clock boot-time snapshot on
        // Linux, so comparing the owner's recorded value with the observer's value reported every
        // live owner as an impostor and let waiters steal its lock.
        string name = GetUniqueName("LiveOwner");
        using var region = MemoryRegion.CreateOrOpen(name, 256);
        using Process holder = StartLockHolder(name);
        try
        {
            for (int i = 0; i < 50; i++)
            {
                Assert.That(region.IsWriteLockOrphaned(), Is.False, $"probe {i}");
                Thread.Sleep(10);
            }

            Assert.That(region.TryAcquireWriteLock(TimeSpan.FromMilliseconds(300)), Is.False,
                "a live owner's lock must not be taken over");
        }
        finally
        {
            KillAndWait(holder);
        }
    }

    [Test, Timeout(30000)]
    public void CrossProcess_InfiniteWait_RecoversWhenOwnerProcessDies()
    {
        string name = GetUniqueName("InfiniteOrphan");
        using var region = MemoryRegion.CreateOrOpen(name, 256);
        using Process holder = StartLockHolder(name);
        try
        {
            // Release on the acquiring thread: the write lock is thread-affine.
            Task<bool> waiter = Task.Run(() =>
            {
                bool acquired = region.TryAcquireWriteLock(Timeout.InfiniteTimeSpan);
                if (acquired)
                    region.ReleaseWriteLock();
                return acquired;
            });

            // Give the waiter time to run its first orphan probe while the owner is still alive.
            Thread.Sleep(500);
            Assert.That(waiter.IsCompleted, Is.False, "the owner is alive, so the waiter must still be waiting");

            KillAndWait(holder);

            Assert.That(waiter.Wait(TimeSpan.FromSeconds(10)), Is.True,
                "an infinite wait must keep probing the owner and recover once it has died");
            Assert.That(waiter.Result, Is.True);
        }
        finally
        {
            KillAndWait(holder);
        }
    }

    [Test, Timeout(40000)]
    public void CrossProcess_FiniteWait_RecoversPromptlyWhenOwnerDies()
    {
        // The orphan probe used to run only at the start of the wait and at 75% of the timeout, so
        // with a 30 s timeout an owner that died after one second was noticed after ~22 s.
        string name = GetUniqueName("FiniteOrphan");
        using var region = MemoryRegion.CreateOrOpen(name, 256);
        using Process holder = StartLockHolder(name);
        try
        {
            Task<bool> waiter = Task.Run(() =>
            {
                bool acquired = region.TryAcquireWriteLock(TimeSpan.FromSeconds(30));
                if (acquired)
                    region.ReleaseWriteLock();
                return acquired;
            });

            Thread.Sleep(500);
            KillAndWait(holder);

            Assert.That(waiter.Wait(TimeSpan.FromSeconds(5)), Is.True,
                "recovery must not wait for most of the lock timeout");
            Assert.That(waiter.Result, Is.True);
        }
        finally
        {
            KillAndWait(holder);
        }
    }

    [Test, Timeout(40000)]
    public void CrossProcess_ReadWait_RecoversWhenWriterProcessDies()
    {
        // Only a waiting writer used to recover a lock whose owner had died; a reader (a dashboard that
        // only reads) waited out its whole timeout behind a writer that no longer existed.
        string name = GetUniqueName("ReaderBehindDeadWriter");
        using var region = MemoryRegion.CreateOrOpen(name, 256);
        using Process holder = StartLockHolder(name);
        try
        {
            Task<bool> reader = Task.Run(() =>
            {
                bool acquired = region.TryAcquireReadLock(TimeSpan.FromSeconds(30));
                if (acquired)
                    region.ReleaseReadLock();
                return acquired;
            });

            Thread.Sleep(500);
            KillAndWait(holder);

            Assert.That(reader.Wait(TimeSpan.FromSeconds(5)), Is.True,
                "a reader must notice that the writer is gone, not wait for its whole timeout");
            Assert.That(reader.Result, Is.True);
        }
        finally
        {
            KillAndWait(holder);
        }
    }

    /// <summary>
    /// Leaves the lock the way a process killed between "state = 1" and "owner = pid" does: held, but
    /// with no owner recorded. Writes the region header through a second mapping of the /dev/shm file.
    /// </summary>
    private static void HoldLockWithNoOwner(string name)
    {
        if (!OperatingSystem.IsLinux())
            Assert.Ignore("Edits the header through the /dev/shm file, which only Linux has.");

        using var file = new FileStream("/dev/shm/" + name, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        using var map = MemoryMappedFile.CreateFromFile(file, null, 0, MemoryMappedFileAccess.ReadWrite,
            HandleInheritability.None, leaveOpen: true);
        using var view = map.CreateViewAccessor(0, 128);
        view.Write(28, 0);      // LockOwnerProcessId
        view.Write(32, 0L);     // LockOwnerThreadId
        view.Write(24, 1);      // WriterLockState
        view.Flush();
    }

    [Test, Timeout(40000)]
    public void CrossProcess_LockHeldWithNoOwner_IsReleasedByAWritingWaiter()
    {
        // A kill between the CAS that takes the lock and the store of the owner's pid (or between
        // clearing the owner and clearing the state) leaves WriterLockState = 1 and pid = 0. The owner
        // checks cannot see that, so the lock stayed held forever.
        string name = GetUniqueName("NoOwnerWriter");
        using var region = MemoryRegion.CreateOrOpen(name, 256);
        HoldLockWithNoOwner(name);
        Assert.That(region.TryAcquireWriteLock(TimeSpan.FromMilliseconds(300)), Is.False,
            "the lock is held and the owner check does not recognise it as an orphan");

        var sw = Stopwatch.StartNew();
        Assert.That(region.TryAcquireWriteLock(TimeSpan.FromSeconds(10)), Is.True);
        sw.Stop();
        region.ReleaseWriteLock();

        Assert.That(sw.ElapsedMilliseconds, Is.GreaterThan(1500),
            "a lock that merely looks ownerless for an instant must not be taken at once");
    }

    [Test, Timeout(40000)]
    public void CrossProcess_LockHeldWithNoOwner_IsReleasedByAReadingWaiter()
    {
        string name = GetUniqueName("NoOwnerReader");
        using var region = MemoryRegion.CreateOrOpen(name, 256);
        HoldLockWithNoOwner(name);

        var sw = Stopwatch.StartNew();
        Assert.That(region.TryAcquireReadLock(TimeSpan.FromSeconds(10)), Is.True);
        sw.Stop();
        region.ReleaseReadLock();

        Assert.That(sw.ElapsedMilliseconds, Is.GreaterThan(1500));
    }

    /// <summary>
    /// Makes the write lock look as if a process with <paramref name="pid"/> in the PID namespace
    /// <paramref name="pidNamespace"/> held it (the pid must not exist here), by editing the region header
    /// through a second mapping of the /dev/shm file.
    /// </summary>
    private static void HoldLockAs(string name, int pid, long pidNamespace)
    {
        if (!OperatingSystem.IsLinux())
            Assert.Ignore("Edits the header through the /dev/shm file, which only Linux has.");
        if (MemoryRegion.CurrentPidNamespace == 0)
            Assert.Ignore("The PID namespace of this process cannot be read (no /proc).");

        using var file = new FileStream("/dev/shm/" + name, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        using var map = MemoryMappedFile.CreateFromFile(file, null, 0, MemoryMappedFileAccess.ReadWrite,
            HandleInheritability.None, leaveOpen: true);
        using var view = map.CreateViewAccessor(0, 128);
        view.Write(88, pidNamespace);   // LockOwnerPidNamespace
        view.Write(48, 0L);             // LockOwnerProcessStartTime: unknown, so the pid decides
        view.Write(32, 1L);             // LockOwnerThreadId
        view.Write(28, pid);            // LockOwnerProcessId
        view.Write(24, 1);              // WriterLockState
        view.Flush();
    }

    private const int PidThatIsNotRunning = 4_000_001;

    [Test, Timeout(30000)]
    public void CrossProcess_OwnerInAnotherPidNamespace_IsNotTakenOver()
    {
        // Two containers that share /dev/shm have different PID namespaces. The owner's pid is looked up
        // in the waiter's namespace, where it does not exist (or is somebody else), so the waiter used to
        // decide that a live owner was dead and took its lock.
        string name = GetUniqueName("OtherPidNamespace");
        using var region = MemoryRegion.CreateOrOpen(name, 256);
        HoldLockAs(name, PidThatIsNotRunning, MemoryRegion.CurrentPidNamespace + 1);

        Assert.That(region.IsWriteLockOrphaned(), Is.False);
        Assert.That(region.GetLockOwnerInfo().IsOrphan, Is.False);
        Assert.That(region.TryAcquireWriteLock(TimeSpan.FromMilliseconds(1500)), Is.False,
            "the owner lives in another PID namespace, its lock is not ours to take");
        Assert.That(region.TryAcquireReadLock(TimeSpan.FromMilliseconds(600)), Is.False);
    }

    [Test, Timeout(30000)]
    public void CrossProcess_OwnerInTheSamePidNamespace_IsStillRecovered()
    {
        string name = GetUniqueName("SamePidNamespace");
        using var region = MemoryRegion.CreateOrOpen(name, 256);
        HoldLockAs(name, PidThatIsNotRunning, MemoryRegion.CurrentPidNamespace);

        Assert.That(region.IsWriteLockOrphaned(), Is.True);
        Assert.That(region.TryAcquireWriteLock(TimeSpan.FromSeconds(5)), Is.True);
        region.ReleaseWriteLock();
    }

    [Test, Timeout(30000)]
    public void CrossProcess_OwnerWithoutRecordedPidNamespace_IsStillRecovered()
    {
        // Locks taken by earlier versions record no namespace; they keep the pid-only decision.
        string name = GetUniqueName("UnknownPidNamespace");
        using var region = MemoryRegion.CreateOrOpen(name, 256);
        HoldLockAs(name, PidThatIsNotRunning, 0);

        Assert.That(region.IsWriteLockOrphaned(), Is.True);
        Assert.That(region.TryAcquireWriteLock(TimeSpan.FromSeconds(5)), Is.True);
        region.ReleaseWriteLock();
    }

    [Test, Timeout(30000)]
    public void CrossProcess_LiveOwner_RecordsItsPidNamespaceAndClearsItOnRelease()
    {
        if (!OperatingSystem.IsLinux() || MemoryRegion.CurrentPidNamespace == 0)
            Assert.Ignore("Needs the /dev/shm file and a readable /proc/self/ns/pid.");

        string name = GetUniqueName("RecordedNamespace");
        using var region = MemoryRegion.CreateOrOpen(name, 256);
        using var file = new FileStream("/dev/shm/" + name, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        using var map = MemoryMappedFile.CreateFromFile(file, null, 0, MemoryMappedFileAccess.Read,
            HandleInheritability.None, leaveOpen: true);
        using var view = map.CreateViewAccessor(0, 128, MemoryMappedFileAccess.Read);

        Assert.That(region.TryAcquireWriteLock(TimeSpan.FromSeconds(1)), Is.True);
        Assert.That(view.ReadInt64(88), Is.EqualTo(MemoryRegion.CurrentPidNamespace));
        region.ReleaseWriteLock();
        Assert.That(view.ReadInt64(88), Is.EqualTo(0L), "a stale namespace would mislead the next owner's waiters");
    }

    [Test, Timeout(30000)]
    public void CrossProcess_DeadReaderProcess_NeedsForceResetLocks()
    {
        // Read locks are not attributed to an owner, so a reader that dies leaves the shared count
        // above zero and nothing can tell that it is stale. ForceResetLocks is the operator's way out.
        string name = GetUniqueName("DeadReader");
        using var region = MemoryRegion.CreateOrOpen(name, 256);
        using Process holder = StartLockHolder(name, "hold_read_lock");
        Assert.That(region.GetLockOwnerInfo().ReaderCount, Is.EqualTo(1));
        KillAndWait(holder);

        Assert.That(region.TryAcquireWriteLock(TimeSpan.FromMilliseconds(500)), Is.False,
            "the dead reader's lock is still counted");
        Assert.That(region.GetLockOwnerInfo().ReaderCount, Is.EqualTo(1));

        region.ForceResetLocks();

        Assert.That(region.GetLockOwnerInfo().ReaderCount, Is.EqualTo(0));
        Assert.That(region.TryAcquireWriteLock(TimeSpan.FromSeconds(1)), Is.True);
        region.ReleaseWriteLock();
    }

    // ── Schema ───────────────────────────────────────────────────────────────

    public struct IpcTestSchema : IMemorySchema
    {
        public const string Counter = "Counter";
        public const string Label   = "Label";

        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<int>(Counter);
            yield return FieldDefinition.String(Label, 32);
        }
    }
}
