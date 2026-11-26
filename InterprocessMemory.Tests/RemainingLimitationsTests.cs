using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using InterprocessMemory;

namespace InterprocessMemory.Tests;

/// <summary>Limitations that were documented in 4.0.0 and are handled now.</summary>
[TestFixture]
public class RemainingLimitationsTests
{
    private static string N(string prefix) => $"Limits_{prefix}_{Guid.NewGuid():N}";

    public struct WideSchema : IMemorySchema
    {
        public IEnumerable<FieldDefinition> GetFields()
        {
            yield return FieldDefinition.Scalar<Guid>("Value");
        }
    }

    // ── Two instances of one region on one thread ────────────────────────────

    // Locks are reentrant only through the instance that took them. A second instance on the same thread used to
    // wait for the whole timeout for a lock that its own thread holds (for ever with an infinite timeout).

    [Test, Timeout(60000)]
    public void StructuredMemory_SecondInstanceOnTheSameThread_WriteLock_FailsAtOnce()
    {
        string name = N("SmWrite");
        using var a = StructuredMemory<WideSchema>.CreateOrOpen(name, new WideSchema());
        using var b = StructuredMemory<WideSchema>.OpenExisting(name, new WideSchema());

        using (a.AcquireWriteLock())
        {
            var sw = Stopwatch.StartNew();
            var ex = Assert.Throws<InvalidOperationException>(() => b.AcquireWriteLock(TimeSpan.FromSeconds(10)));
            Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(3)));
            Assert.That(ex!.Message, Does.Contain("another instance"));
        }

        // Once the first instance has let go, the second one can take the lock.
        using (b.AcquireWriteLock(TimeSpan.FromSeconds(2)))
        {
        }
    }

    [Test, Timeout(60000)]
    public void StructuredMemory_SecondInstanceOnTheSameThread_ReadLock_FailsAtOnce()
    {
        string name = N("SmRead");
        using var a = StructuredMemory<WideSchema>.CreateOrOpen(name, new WideSchema());
        using var b = StructuredMemory<WideSchema>.OpenExisting(name, new WideSchema());

        using (a.AcquireWriteLock())
        {
            var sw = Stopwatch.StartNew();
            Assert.Throws<InvalidOperationException>(() => b.AcquireReadLock(TimeSpan.FromSeconds(10)));
            Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(3)));
        }
    }

    [Test, Timeout(60000)]
    public void StructuredMemory_AutomaticLockOfASecondInstance_FailsAtOnce()
    {
        string name = N("SmAuto");
        using var a = StructuredMemory<WideSchema>.CreateOrOpen(name, new WideSchema());
        using var b = StructuredMemory<WideSchema>.OpenExisting(name, new WideSchema());

        using (a.AcquireWriteLock())
        {
            // A 16 byte value is written under the shared lock automatically.
            var sw = Stopwatch.StartNew();
            Assert.Throws<InvalidOperationException>(() => b.Write("Value", Guid.NewGuid()));
            Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(3)));
        }
    }

    [Test, Timeout(60000)]
    public void SharedArray_SecondInstanceOnTheSameThread_FailsAtOnce()
    {
        string name = N("Array");
        using var a = SharedArray<int>.CreateOrOpen(name, 8);
        using var b = SharedArray<int>.OpenExisting(name);

        using (a.AcquireWriteLock())
        {
            var sw = Stopwatch.StartNew();
            Assert.Throws<InvalidOperationException>(() => b.AcquireWriteLock(TimeSpan.FromSeconds(10)));
            Assert.Throws<InvalidOperationException>(() => b.AcquireReadLock(TimeSpan.FromSeconds(10)));
            Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(3)));
        }

        using (b.AcquireReadLock(TimeSpan.FromSeconds(2)))
        {
        }
    }

    [Test, Timeout(60000)]
    public void AnotherThreadStillTimesOutAsBefore()
    {
        string name = N("OtherThread");
        using var a = StructuredMemory<WideSchema>.CreateOrOpen(name, new WideSchema());
        using var b = StructuredMemory<WideSchema>.OpenExisting(name, new WideSchema());

        using (a.AcquireWriteLock())
        {
            // The lock belongs to this thread, not to the contender, so this is ordinary contention.
            Exception? error = null;
            DedicatedThread.Run(() =>
            {
                try
                { b.AcquireWriteLock(TimeSpan.FromMilliseconds(100)); }
                catch (Exception ex) { error = ex; }
            }).Wait();

            Assert.That(error, Is.InstanceOf<TimeoutException>());
        }
    }

    // ── Windows namespace prefixes in names ──────────────────────────────────

    [Test]
    public void Names_MayStartWithGlobalOrLocal_ButMustBeFlatAfterThat()
    {
        string unique = Guid.NewGuid().ToString("N");

        foreach (string bad in new[]
        {
            "Global\\",                 // nothing after the prefix
            "Local\\ ",                 // only white space after it
            "Global\\a\\b",           // not flat after the prefix
            "Global\\Local\\" + unique,
            "Other\\" + unique,         // only Global and Local are namespaces
            "Global\\..",
            "Local\\a/b",
        })
        {
            Assert.Throws<ArgumentException>(
                () => MemoryRegion.CreateOrOpen(bad, 1024),
                $"'{bad}' must be rejected");
        }
    }

    [Test]
    public void PrefixedAndPlainNames_AreOneRegionOnLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Ignore("Linux has no sessions: the prefix is dropped from the file name there.");

        string flat = N("Prefix");
        using var global = MemoryRegion.CreateOrOpen("Global\\" + flat, 4096);
        global.Write(new byte[] { 1, 2, 3, 4 }, 0);

        using var plain = MemoryRegion.OpenExisting(flat);
        using var local = MemoryRegion.OpenExisting("local\\" + flat);   // the prefix is not case sensitive

        var read = new byte[4];
        plain.Read(read, 0);
        Assert.That(read, Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
        local.Read(read, 0);
        Assert.That(read, Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
        Assert.That(global.Name, Is.EqualTo("Global\\" + flat), "Name keeps what the caller passed");

        Assert.That(MemoryRegion.Remove("Local\\" + flat), Is.True);
        Assert.That(File.Exists("/dev/shm/" + flat), Is.False);
    }

    [Test]
    public void LocalNamespace_WorksOnWindows()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Ignore("The kernel namespaces exist on Windows only.");

        string name = "Local\\" + N("WinLocal");
        using var creator = MemoryRegion.CreateOrOpen(name, 4096);
        creator.Write(new byte[] { 9, 8, 7 }, 0);

        using var opener = MemoryRegion.OpenExisting(name);
        var read = new byte[3];
        opener.Read(read, 0);
        Assert.That(read, Is.EqualTo(new byte[] { 9, 8, 7 }));
    }

    [Test]
    public void GlobalNamespace_WorksOnWindowsWithThePrivilege()
    {
        if (!RequireTheCreateGlobalPrivilege())
            return;

        string name = "Global\\" + N("WinGlobal");
        using var creator = MemoryRegion.CreateOrOpen(name, 4096);
        creator.Write(new byte[] { 5, 6 }, 0);

        using var opener = MemoryRegion.OpenExisting(name);
        var read = new byte[2];
        opener.Read(read, 0);
        Assert.That(read, Is.EqualTo(new byte[] { 5, 6 }));
    }

    // Without the privilege MemoryMappedFile.CreateOrOpen does not fail at once: it retries for about 80 seconds and
    // then throws an InvalidOperationException. A region reports the missing privilege at once instead, and still
    // opens a Global region that exists, because opening needs no privilege.

    [Test, Timeout(30000)]
    public void GlobalNamespace_CreatingWithoutThePrivilege_FailsAtOnce()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("The kernel namespaces exist on Windows only.");
            return;
        }

        // Acts as a process without the privilege, so that a CI runner, which has it, runs the test too.
        AssertCreatingFailsAtOnce(new MemoryRegionOptions { CanCreateGlobal = false });
    }

    [Test, Timeout(30000)]
    public void GlobalNamespace_CreatingWithoutThePrivilege_FailsAtOnce_ForTheTokenOfThisProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("The kernel namespaces exist on Windows only.");
            return;
        }

        if (WindowsPrivileges.IsEnabled(WindowsPrivileges.CreateGlobal))
            Assert.Ignore("This process has SeCreateGlobalPrivilege; " +
                          "GlobalNamespace_CreatingWithoutThePrivilege_FailsAtOnce covers a process without it.");

        AssertCreatingFailsAtOnce(options: null);
    }

    [Test, Timeout(30000)]
    public void GlobalNamespace_CreatingWithoutThePrivilege_OpensARegionThatExists()
    {
        if (!RequireTheCreateGlobalPrivilege())   // to create the region first
            return;

        string name = "Global\\" + N("WinOpenExisting");
        using var creator = MemoryRegion.CreateOrOpen(name, 4096);
        creator.Write(new byte[] { 7, 8 }, 0);

        using var opener = MemoryRegion.CreateOrOpen(name, 4096, new MemoryRegionOptions { CanCreateGlobal = false });
        Assert.That(opener.IsOwner, Is.False);
        var read = new byte[2];
        opener.Read(read, 0);
        Assert.That(read, Is.EqualTo(new byte[] { 7, 8 }));
    }

    private static void AssertCreatingFailsAtOnce(MemoryRegionOptions? options)
    {
        string name = "Global\\" + N("WinNoPrivilege");

        var sw = Stopwatch.StartNew();
        var ex = Assert.Throws<UnauthorizedAccessException>(() => MemoryRegion.CreateOrOpen(name, 4096, options));
        sw.Stop();

        Assert.That(ex!.Message, Does.Contain("SeCreateGlobalPrivilege").And.Contain(name));
        Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)), "it must not wait for anything");
    }

    /// <summary>
    /// True when this process may create a section in the Global namespace. Otherwise the test is skipped, or it
    /// fails on a CI runner: a runner has the privilege, and a skipped test there would go unnoticed.
    /// </summary>
    private static bool RequireTheCreateGlobalPrivilege()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("The kernel namespaces exist on Windows only.");
            return false;
        }

        if (WindowsPrivileges.IsEnabled(WindowsPrivileges.CreateGlobal))
            return true;

        const string message = "Creating a section in the Global namespace needs SeCreateGlobalPrivilege " +
                               "(a service or an elevated process has it).";
        if (Environment.GetEnvironmentVariable("CI") == "true" ||
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
            Assert.Fail(message);

        Assert.Ignore(message);
        return false;
    }
}
