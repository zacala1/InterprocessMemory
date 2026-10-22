using System.Collections.Generic;
using System.Diagnostics;
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
}
