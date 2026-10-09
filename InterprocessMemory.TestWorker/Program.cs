using System;
using System.Collections.Generic;
using InterprocessMemory;

/// <summary>
/// Lightweight child-process helper for cross-process integration tests.
/// Usage: InterprocessMemory.TestWorker &lt;role&gt; &lt;regionName&gt;
///
/// Roles:
///   hpbuf_writer   &lt;name&gt;  — open existing buffer, write known byte pattern
///   hpbuf_reader   &lt;name&gt;  — open existing buffer, verify known byte pattern
///   spsc_producer  &lt;name&gt;  — open existing SPSC queue, write 100 int messages
///   spsc_consumer  &lt;name&gt;  — open existing SPSC queue, read and verify 100 int messages
///   strict_writer  &lt;name&gt;  — open existing StructuredMemory, write Counter=42, Label="hello"
///   strict_reader  &lt;name&gt;  — open existing StructuredMemory, verify Counter=42, Label="hello"
///   typed_producer &lt;name&gt;  — enqueue integers 0..99 to a typed SPSC queue
///   typed_consumer &lt;name&gt;  — dequeue and verify integers 0..99
///   concurrent_producer &lt;name&gt; &lt;producerId&gt; — enqueue 1000 unique integers
///   try_write_lock &lt;name&gt; — try the cross-process write lock for 250 ms
///   orphan_write_lock &lt;name&gt; — acquire a write lock and exit without releasing it
///   dispose_busy_poll &lt;prefix&gt; — 200 rounds of: pollers spin on a ConcurrentQueue while it is disposed
///   hold_write_lock &lt;name&gt; — acquire a write lock, print "holding", and keep it until killed or until
///                            the parent closes our standard input (the parent died)
///   hold_read_lock &lt;name&gt; — acquire a read lock, print "holding", and keep it until killed or until
///                           the parent closes our standard input (the parent died)
/// </summary>
if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: InterprocessMemory.TestWorker <role> <regionName>");
    return 1;
}

string role = args[0];
string bufferName = args[1];

return role switch
{
    "hpbuf_writer"  => HpBufWriter(bufferName),
    "hpbuf_reader"  => HpBufReader(bufferName),
    "spsc_producer" => SpscProducer(bufferName),
    "spsc_consumer" => SpscConsumer(bufferName),
    "strict_writer" => StrictWriter(bufferName),
    "strict_reader" => StrictReader(bufferName),
    "typed_producer" => TypedProducer(bufferName),
    "typed_consumer" => TypedConsumer(bufferName),
    "concurrent_producer" => ConcurrentProducer(
        bufferName,
        args.Length >= 3 ? int.Parse(args[2]) : 0),
    "try_write_lock" => TryWriteLock(bufferName),
    "orphan_write_lock" => OrphanWriteLock(bufferName),
    "dispose_busy_poll" => DisposeBusyPoll(bufferName),
    "hold_write_lock" => HoldWriteLock(bufferName),
    "hold_read_lock" => HoldReadLock(bufferName),
    _               => Error($"Unknown role: {role}")
};

// ── helpers ─────────────────────────────────────────────────────────────────

static int HpBufWriter(string name)
{
    using var buf = MemoryRegion.OpenExisting(name);
    var data = new byte[64];
    for (int i = 0; i < data.Length; i++) data[i] = (byte)(i + 1);
    buf.Write(data, 0);
    Console.WriteLine("written");
    return 0;
}

static int HpBufReader(string name)
{
    using var buf = MemoryRegion.OpenExisting(name);
    var result = new byte[64];
    buf.Read(result, 0);
    for (int i = 0; i < result.Length; i++)
    {
        if (result[i] != (byte)(i + 1))
        {
            Console.Error.WriteLine($"Mismatch at index {i}: expected {i + 1}, got {result[i]}");
            return 3;
        }
    }
    Console.WriteLine("verified");
    return 0;
}

static int SpscProducer(string name)
{
    using var buf = SingleProducerByteStream.OpenExisting(name);
    for (int i = 0; i < 100; i++)
    {
        if (!buf.WaitWrite(BitConverter.GetBytes(i), TimeSpan.FromSeconds(10)))
        {
            Console.Error.WriteLine($"WaitWrite timed out at message {i}");
            return 4;
        }
    }
    Console.WriteLine("produced:100");
    return 0;
}

static int SpscConsumer(string name)
{
    using var buf = SingleProducerByteStream.OpenExisting(name);
    var dst = new byte[4];
    for (int i = 0; i < 100; i++)
    {
        int read = buf.WaitRead(dst, TimeSpan.FromSeconds(10));
        if (read != 4) { Console.Error.WriteLine($"Short read at {i}: {read}"); return 5; }
        int val = BitConverter.ToInt32(dst);
        if (val != i) { Console.Error.WriteLine($"Value mismatch at {i}: got {val}"); return 6; }
    }
    Console.WriteLine("consumed:100");
    return 0;
}

static int StrictWriter(string name)
{
    var schema = new IpcSchema();
    using var mem = StructuredMemory<IpcSchema>.OpenExisting(name, schema);
    using (mem.AcquireWriteLock())
    {
        mem.Write(IpcSchema.Counter, 42);
        mem.WriteString(IpcSchema.Label, "hello");
    }
    Console.WriteLine("strict_written");
    return 0;
}

static int StrictReader(string name)
{
    var schema = new IpcSchema();
    using var mem = StructuredMemory<IpcSchema>.OpenExisting(name, schema);
    int counter;
    string label;
    using (mem.AcquireReadLock())
    {
        counter = mem.Read<int>(IpcSchema.Counter);
        label = mem.ReadString(IpcSchema.Label);
    }
    if (counter != 42) { Console.Error.WriteLine($"Counter mismatch: {counter}"); return 7; }
    if (label != "hello") { Console.Error.WriteLine($"Label mismatch: {label}"); return 8; }
    Console.WriteLine("strict_verified");
    return 0;
}

static int TypedProducer(string name)
{
    using var queue = SingleProducerQueue<int>.OpenExisting(name);
    for (int i = 0; i < 100; i++)
    {
        if (!queue.TryEnqueue(i, TimeSpan.FromSeconds(10)))
            return Error($"typed enqueue timed out at {i}");
    }
    Console.WriteLine("typed_produced:100");
    return 0;
}

static int TypedConsumer(string name)
{
    using var queue = SingleProducerQueue<int>.OpenExisting(name);
    for (int expected = 0; expected < 100; expected++)
    {
        if (!queue.TryDequeue(out int actual, TimeSpan.FromSeconds(10)))
            return Error($"typed dequeue timed out at {expected}");
        if (actual != expected)
            return Error($"typed mismatch: expected {expected}, got {actual}");
    }
    Console.WriteLine("typed_consumed:100");
    return 0;
}

static int ConcurrentProducer(string name, int producerId)
{
    using var queue = InterprocessMemory.ConcurrentQueue<int>.OpenExisting(name);
    for (int i = 0; i < 1000; i++)
    {
        int value = checked(producerId * 1000 + i);
        if (!queue.TryEnqueue(value, TimeSpan.FromSeconds(20)))
            return Error($"concurrent enqueue timed out at {producerId}:{i}");
    }
    Console.WriteLine($"concurrent_produced:{producerId}");
    return 0;
}

static int TryWriteLock(string name)
{
    using var region = MemoryRegion.OpenExisting(name);
    bool acquired = region.TryAcquireWriteLock(TimeSpan.FromMilliseconds(250));
    if (acquired)
    {
        region.ReleaseWriteLock();
        Console.WriteLine("lock_acquired");
        return 0;
    }
    Console.WriteLine("lock_timeout");
    return 2;
}

static int OrphanWriteLock(string name)
{
    var region = MemoryRegion.OpenExisting(name);
    if (!region.TryAcquireWriteLock(TimeSpan.FromSeconds(5)))
        return Error("failed to acquire orphan test lock");
    Console.WriteLine("orphan_locked");
    return 0; // Deliberately skip Dispose/Release; process teardown closes only the mapping handle.
}

// The common shutdown pattern: consumers spin on TryDequeue while another thread disposes the queue. Without
// MemoryRegion.DisposeGracePeriod this killed the process with an AccessViolationException within a few hundred
// rounds. It is a mitigation, not a proof: a thread that is descheduled for longer than the grace period at the
// wrong moment can still fail, and that kills this process, which is why it runs here and not in the test host.
static int DisposeBusyPoll(string namePrefix)
{
    var random = new Random(42);
    int pollerCount = Math.Max(3, Environment.ProcessorCount - 1);

    for (int round = 0; round < 200; round++)
    {
        string name = $"{namePrefix}_{round}";
        var queue = InterprocessMemory.ConcurrentQueue<long>.CreateOrOpen(name, 64);
        var pollers = new Thread[pollerCount];
        for (int i = 0; i < pollers.Length; i++)
        {
            pollers[i] = new Thread(() =>
            {
                try
                {
                    while (true)
                    {
                        queue.TryDequeue(out _);
                        queue.TryEnqueue(1);
                    }
                }
                catch (ObjectDisposedException)
                {
                    // Expected: the queue was disposed under the poller.
                }
            }) { IsBackground = true };
            pollers[i].Start();
        }

        Thread.Sleep(random.Next(0, 3));
        queue.Dispose();

        foreach (Thread poller in pollers)
        {
            if (!poller.Join(TimeSpan.FromSeconds(10)))
                return Error($"round {round}: a poller did not stop");
        }

        MemoryRegion.Remove(name);
    }

    Console.WriteLine("ok");
    return 0;
}

static int HoldWriteLock(string name)
{
    using var region = MemoryRegion.OpenExisting(name);
    if (!region.TryAcquireWriteLock(TimeSpan.FromSeconds(5)))
        return Error("failed to acquire held test lock");

    // The parent reads this line, probes the lock while we are alive, and then kills us
    // to simulate a crash while the lock is held.
    Console.WriteLine("holding");
    Console.Out.Flush();
    WaitUntilTheParentIsGone();
    return 0;
}

static int HoldReadLock(string name)
{
    using var region = MemoryRegion.OpenExisting(name);
    if (!region.TryAcquireReadLock(TimeSpan.FromSeconds(5)))
        return Error("failed to acquire held test read lock");

    Console.WriteLine("holding");
    Console.Out.Flush();
    WaitUntilTheParentIsGone();
    return 0;
}

// The test kills us in the normal case. If it dies first (crash, hang timeout, Ctrl-C) the pipe that is our
// standard input closes, which ends this wait instead of holding the lock, and the process, for ever.
// The upper bound covers a parent that hangs without dying.
static void WaitUntilTheParentIsGone()
{
    try
    {
        System.Threading.Tasks.Task.Run(() => Console.In.ReadToEnd()).Wait(TimeSpan.FromMinutes(2));
    }
    catch (Exception)
    {
        // Nothing to wait on (no stdin): exit.
    }
}

static int Error(string msg)
{
    Console.Error.WriteLine(msg);
    return 1;
}

// ── schema ───────────────────────────────────────────────────────────────────

public struct IpcSchema : IMemorySchema
{
    public const string Counter = "Counter";
    public const string Label   = "Label";

    public IEnumerable<FieldDefinition> GetFields()
    {
        yield return FieldDefinition.Scalar<int>(Counter);
        yield return FieldDefinition.String(Label, 32);
    }
}
