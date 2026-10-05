# Changelog

Changes since the 3.0.0 release. Migrating from 2.x: see [MIGRATION.md](MIGRATION.md).

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
- Disposing a region while another thread waits for one of its locks no longer crashes the process;
  the waiter fails with `ObjectDisposedException`.
- Generic unmanaged structs (`ValueTuple`, `KeyValuePair<,>`) work in all typed containers.
  Fingerprints of types that already worked are unchanged, so existing regions stay compatible.
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
