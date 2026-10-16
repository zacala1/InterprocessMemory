# Migrating to InterprocessMemory 4.0

## From 3.0.0 to 4.0.0

The shared-memory format is unchanged (still format version 3), but the 3.0.0 lock-owner check on
Linux takes the write lock away from live owners, so **update every process that shares a region
together**; do not run 3.0.0 and 4.0.0 side by side on Linux. [CHANGELOG.md](CHANGELOG.md) lists every
change; these are the ones that can affect existing code.

**Source change.** `StructuredMemory<T>.WriteLock` and `ReadLock` are `ref struct`s. A guard can no longer be
held across an `await`, so move the locked work into a synchronous method:

```csharp
// 3.0.0: compiled, but the guard was disposed on another thread after the await, which could not
// release the lock, so it stayed held by the thread that took it
async Task UpdateAsync()
{
    using var guard = memory.AcquireWriteLock();
    await SomethingAsync();
    memory.Write("Count", 1);
}

// 4.0.0
async Task UpdateAsync()
{
    await SomethingAsync();
    Update();
}

void Update()
{
    using var guard = memory.AcquireWriteLock();
    memory.Write("Count", 1);
}
```

Code that kept a guard in a field, captured it in a lambda or converted it to `IDisposable` needs the same
change. Rebuild assemblies that were compiled against 3.0.0.

**Behavior changes.**

- `MemoryRegionOptions.OrphanLockTimeout` defaults to disabled. A lock whose owner process has exited is still
  recovered; a lock held by a live process is taken over only if you set a timeout.
- `MemoryRegion.ReleaseWriteLock` from a thread that does not own the lock throws
  `SynchronizationLockException`.
- `ConcurrentQueue<T>` and `ConcurrentMessageQueue` need at least two slots. A requested capacity of 1 becomes 2,
  and a region that was created with capacity 1 is rejected: remove it with `MemoryRegion.Remove` and create
  it again.
- `StructuredMemory<T>.OpenExisting` checks the schema version before the size. The same version needs the
  exact size; a different version needs a region at least as large as the schema.
- On Linux `CreateOrOpen` throws `IOException` when `/dev/shm` cannot hold the region (Docker's default is
  64 MB; raise it with `--shm-size`).
- `Dispose()` takes at least `MemoryRegion.DisposeGracePeriod` (10 ms) longer.

## From 2.x

Version 3 is intentionally source-, binary-, package-, and shared-memory-format incompatible
with 2.x. The rest of this document describes that move and applies to 4.0.0 unchanged.

## Package and namespace

```shell
dotnet remove package SharedMemory.HighPerformance
dotnet add package InterprocessMemory --version 4.0.0
```

Replace:

```csharp
using SharedMemory;
```

with:

```csharp
using InterprocessMemory;
```

## Type names

| 2.x | 3.0 |
|---|---|
| `HighPerformanceSharedBuffer` | `MemoryRegion` |
| `ISharedMemoryBuffer` | `IMemoryRegion` |
| `SharedMemoryBufferOptions` | `MemoryRegionOptions` |
| `StrictSharedMemory<TSchema>` | `StructuredMemory<TSchema>` |
| `ISharedMemorySchema` | `IMemorySchema` |
| `SharedTypeCode` | `FieldTypeCode` |
| `LockFreeCircularBuffer` | `SingleProducerByteStream` |
| `MpmcCircularBuffer` | `ConcurrentMessageQueue` |
| `BufferEventHandler/Args/Type` | `MemoryRegionEventHandler/Args/Type` |

`SharedArray<T>`, `FieldDefinition`, `IVersionedSchema`, `SchemaCompatibility`, and
`LockOwnerInfo` keep their names.

## Creation and opening

Constructors with `bool create` were removed from the public API.

```csharp
// 2.x
using var owner = new HighPerformanceSharedBuffer(
    name,
    new SharedMemoryBufferOptions { Capacity = 4096 });

using var reader = new HighPerformanceSharedBuffer(
    name,
    new SharedMemoryBufferOptions
    {
        Capacity = 4096,
        CreateOrOpen = false
    });

// 3.0
using var owner = MemoryRegion.CreateOrOpen(name, 4096);
using var reader = MemoryRegion.OpenExisting(name);
```

Openers no longer repeat capacity, element count, or maximum message size. The values are read
from the shared header.

## Queue migration

The old SPSC circular buffer was a byte stream, not a message queue. Its direct replacement is:

```csharp
SingleProducerByteStream.CreateOrOpen(name, capacityBytes);
SingleProducerByteStream.OpenExisting(name);
```

The old MPMC circular buffer preserved variable-length message boundaries. Its replacement is:

```csharp
ConcurrentMessageQueue.CreateOrOpen(
    name,
    capacity: oldSlotCount,
    maxMessageSize: oldSlotSize - 16);
```

The internal 16-byte slot header is no longer part of the public size argument.

For fixed-size values, migrate to `SingleProducerQueue<T>` or `ConcurrentQueue<T>`, where
`T : unmanaged`. Capacity is the number of items rather than bytes.

## Recreating shared regions

Version 3 rejects version 2 headers and never overwrites them.

1. Stop all processes using the 2.x region.
2. Preserve data externally if it must survive the upgrade.
3. Remove the explicit backing file or stale Linux `/dev/shm` entry when applicable. A version 3
   process can do this with `MemoryRegion.Remove(name)` (pass the same `MemoryRegionOptions` for a
   file-backed region).
4. Start the version 3 creating process.
5. Start remaining processes with `OpenExisting`.

There is no in-place migration or mixed 2.x/3.x operation.
