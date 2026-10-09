# Changelog

Changes since the 3.0.0 release. Migrating from 2.x: see [MIGRATION.md](MIGRATION.md).

**Do not mix 3.0.0 with a later version in processes that share a region on Linux.** 3.0.0 compares
`Process.StartTime`, which differs between observers, so it takes the write lock away from every live owner,
including one that runs a later version. Update all processes that use a region together.

## Unreleased

### Behavior changes

- `MemoryRegion.ReleaseWriteLock` throws `SynchronizationLockException` when the calling thread does
  not own the lock (it used to be ignored). The `StructuredMemory<T>` lock guards do the same when they
  are disposed on another thread, which is what an `await` inside a lock scope causes.
- `MemoryRegionOptions.OrphanLockTimeout` defaults to `TimeSpan.Zero` (disabled) instead of 30 seconds.
  A lock whose owner process has exited is still recovered by default; a lock held by a live process is
  taken over only if you set a timeout. `DefaultOrphanLockTimeout` keeps its value as a suggestion.
- `Dispose()` of a `MemoryRegion`, and of every container that owns one, takes at least
  `MemoryRegion.DisposeGracePeriod` (10 ms) longer.
- `SharedArray<T>` takes the shared region lock per element access for element sizes other than
  1, 2, 4 and 8 bytes, so values are no longer torn. These accesses are slower; take the lock once with
  `AcquireReadLock()` / `AcquireWriteLock()` for bulk work.
- An out-of-range queue `capacity` is reported with the parameter name `capacity`.

### Fixed

- Linux: a live write-lock owner was reported as an orphan and its lock was taken over, because
  `Process.StartTime` differs between observers. The owner is now identified by its `/proc/<pid>/stat`
  start tick count.
- Waiting for a lock with `Timeout.InfiniteTimeSpan` now recovers from an owner that dies later
  (the owner is probed every 250 ms).
- A write lock whose owner was killed between taking the lock and recording its pid (or between clearing the
  pid and releasing the lock) stayed held forever, because the orphan check needs a pid. A waiter now
  clears a lock that has had no owner for two seconds.
- Linux: processes in different PID namespaces that share `/dev/shm` (containers) took each other's locks, because a
  waiter looked the owner's pid up in its own namespace and did not find it. The owner now records its PID
  namespace (the inode of `/proc/self/ns/pid`, in the reserved part of the header) and a waiter in another
  namespace no longer declares the owner dead from its pid.
- `StructuredMemory<T>.OpenExisting` checked the size of the region before the schema version, so no
  `SchemaCompatibility` mode could open a region of another size (`Forward` and `Full` only worked when an
  appended field fitted in the padding) and `Strict` reported a size mismatch instead of the version. The
  version is checked first now; for different versions the region must only be at least as large as the
  schema, so an older schema can read a larger region written by a newer one. The same version still needs
  the exact size. See "Schema versions" in the README.
- `SharedArray<T>.Fill` and `Clear` threw `TypeLoadException` for elements of 64 KiB or more (a managed array
  cannot hold them, and the staging buffer was a `T[]`) and staged up to 4096 elements per batch whatever their
  size, 128 MiB for 32 KiB elements. Large elements are written one by one, and a batch is limited to 64 KiB.
- The lock guards of `StructuredMemory<T>` and `SharedArray<T>` now remember the depth at which they were taken.
  Disposing a copy of a guard a second time used to decrement the thread's depth again, so a thread inside an
  outer lock believed it held none and tried to take the lock it already held; releasing a write guard before
  a read guard taken inside it removed the protection under that read guard. Both throw
  `SynchronizationLockException` now, before anything changes, and the guard stays valid.
- Disposing a `StructuredMemory<T>` or `SharedArray<T>` while one of its guards was open on the calling thread
  left the cross-process lock held until the process ended (its owner was alive, so nobody recovered it). It
  releases that lock now; a guard that outlives its instance can still be disposed.
- `StructuredMemory<T>.AcquireWriteLock()` called while the thread holds a read guard set the writer flag and
  waited for that thread's own read lock, blocking every other process until the timeout. It now throws
  `InvalidOperationException` at once, like the automatic write lock and `SharedArray<T>` do.
- `SingleProducerByteStream.Available` and `.Used` read the unmapped header after `Dispose()`; they throw
  `ObjectDisposedException` like the other members.
- The timeout overloads of `ConcurrentQueue<T>` and `ConcurrentMessageQueue` counted a failed enqueue or
  dequeue on every poll (about 1,800 for 4 s of waiting). A call now counts once, when it gives up, and
  not at all when it succeeds after waiting. They also no longer allocate a `Stopwatch` per call.
- `OpenExisting` racing the process that creates the region reported "invalid header" or "empty" about once in
  eleven races instead of waiting the few microseconds until the creator had written the header. It waits up
  to two seconds for a region that is still being created.
- Linux: two processes calling `CreateOrOpen` for a new name at the same moment both became the creator, and
  the one that then failed (another capacity or region kind) deleted the file the other was using, so later
  openers got a second, separate region. `FileMode.CreateNew` decides who creates and sizes the file, and
  only that process removes it when construction fails.
- `MemoryRegionOptions.FilePath`: an existing file of another size was grown to the requested capacity before
  anything checked it, which left it permanently resized and could never be undone. It is now rejected
  without being modified (an older format or a foreign file is reported as such), and the file is opened
  with sharing so that a second process can map it.
- `MemoryRegion.GetMemory` returned a `Memory<byte>` that did not keep the region reachable; a caller that
  dropped the region and kept the memory let the finalizer unmap it, which ended the process.
- Linux: a lock owner that had been killed but not yet reaped by its parent (a zombie) counted as alive. It is
  recognised as gone now.
- A waiting reader now recovers a write lock whose owner process died, like a waiting writer does. It used
  to wait for its whole timeout.
- Disposing a region while another thread waits for one of its locks no longer crashes the process;
  the waiter fails with `ObjectDisposedException`.
- `ConcurrentQueue<T>` and `ConcurrentMessageQueue` with a capacity of 1 overwrote the stored item when a second
  one was enqueued and then never delivered anything again. They need two slots, so a requested capacity
  of 1 is now raised to 2, and an existing region that stored a capacity of 1 is rejected as invalid
  (remove it with `MemoryRegion.Remove`).
- Linux: creating a region larger than the free space of `/dev/shm` (Docker's default is 64 MB) succeeded and
  the process was killed with an uncatchable `SIGBUS` at the first write that did not fit. `CreateOrOpen`
  now throws `IOException` up front; a file-backed region (`MemoryRegionOptions.FilePath`) is checked too.
- Generic unmanaged structs (`ValueTuple`, `KeyValuePair<,>`) work in all typed containers.
  Fingerprints of types that already worked are unchanged, so existing regions stay compatible.
- Two byte `SharedArray<T>` elements could be read half written by another process (a two byte copy is a
  one byte store plus a two byte store). Elements of 1, 2, 4 and 8 bytes are now read and written with one
  typed load/store. `StructuredMemory<T>` had the same defect for 2 byte scalars and, because it only locked
  values wider than 8 bytes, also for 3, 5, 6 and 7 byte values and for small arrays: only 1, 2, 4 and 8
  byte scalars are lock-free now, everything else (including every array) takes the shared lock.
- `SharedArray<T>` disposes its region when opening fails because of a different element type or length.
- `SharedArray<T>` and `StructuredMemory<T>` publish their header magic after the other fields, which
  prevents a spurious format error on weakly ordered CPUs.
- `Dispose()` racing a call that is still running on another thread is mitigated by
  `DisposeGracePeriod`. This is best effort; stop and join threads before disposing.

### Added

- `MemoryRegion.Remove(name, options)` deletes the backing storage of a region left unusable by a crash.
- `MemoryRegion.ForceResetLocks()` and `StructuredMemory<T>.ForceResetLocks()` /
  `SharedArray<T>.ForceResetLocks()` clear lock state left behind by a crashed process.
- `LockOwnerInfo.ReaderCount` for diagnosing a stale reader count.
- `MemoryRegion.DisposeGracePeriod` (static, process-wide).
- `SharedArray<T>.AcquireReadLock()` / `AcquireWriteLock()` with timeout overloads, returning
  reentrant `ref struct` guards.
- GitHub Actions workflow that builds and tests on Linux and Windows.

### Removed

- Finalizers on `ConcurrentMessageQueue`, `SingleProducerByteStream`, `SharedArray<T>` and
  `StructuredMemory<T>`. The `MemoryRegion` finalizer still unmaps the memory when `Dispose` is never
  called.
