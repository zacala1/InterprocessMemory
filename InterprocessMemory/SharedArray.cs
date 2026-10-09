using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace InterprocessMemory
{
    /// <summary>
    /// High-performance generic shared array with type safety and zero-allocation indexer.
    /// Provides array-like access to shared memory with compile-time type checking.
    /// Cross-platform: backed by <see cref="MemoryRegion"/> which supports Windows and Linux.
    /// <para>
    /// <b>Atomicity.</b> An element whose size is 1, 2, 4 or 8 bytes is read and written with one
    /// aligned load or store (the indexer, and <see cref="CopyTo"/>/<see cref="CopyFrom"/> of a single
    /// element), so another process never observes half of one, and no lock is taken. A range of
    /// several elements is a plain memory copy: each element is intact in practice, but the range as
    /// a whole is not a snapshot, so take a lock for that. Every other element size (a 16-byte
    /// <see cref="Guid"/>, a 64-byte struct, ...) could be read torn while another process writes it,
    /// so the indexer, <see cref="CopyTo"/>, <see cref="CopyFrom"/> and <see cref="Fill"/> take the
    /// shared region lock for those types.
    /// </para>
    /// <para>
    /// Use <see cref="AcquireReadLock()"/> / <see cref="AcquireWriteLock()"/> when several elements
    /// must be observed or changed together, for any element type. The guards belong to the calling
    /// thread, may be nested, and cannot be held across an <c>await</c> (they are ref structs).
    /// </para>
    /// </summary>
    /// <typeparam name="T">Unmanaged value type</typeparam>
    public sealed unsafe class SharedArray<T> : IDisposable where T : unmanaged
    {
        private readonly IMemoryRegion _buffer;
        private const int ArrayHeaderSize = 64;
        private const uint ArrayMagic = 0x59415249; // "IRAY"
        private const int FormatVersion = 3;

        // Address of element 0. Valid until Dispose (the mapping does not move); used for the
        // lock-free single-element path, see AtomicAccess.
        private readonly byte* _elements;

        private int _length;
        private readonly int _elementSize;
        private readonly TypeLayoutFingerprint _fingerprint;
        private volatile int _disposed;

        // True for element sizes that one aligned move cannot copy atomically. Per-T constant, so the
        // JIT removes the locking branch from the hot path of 1/2/4/8-byte element types.
        private static readonly bool s_needsLock = !(Unsafe.SizeOf<T>() is 1 or 2 or 4 or 8);

        // Reentrancy bookkeeping, per thread: a thread that holds the lock must not take it again.
        private readonly ThreadLocal<int> _writeLockDepth = new(() => 0);
        private readonly ThreadLocal<int> _readLockDepth = new(() => 0);

        /// <summary>
        /// Gets the number of elements in the array
        /// </summary>
        public int Length => _length;

        /// <summary>
        /// Creates or opens a shared array.
        /// </summary>
        public static SharedArray<T> CreateOrOpen(string name, int length) =>
            new(name, length, createOrOpen: true);

        /// <summary>Opens an existing shared array and loads its length from shared metadata.</summary>
        public static SharedArray<T> OpenExisting(string name) =>
            new(name, length: null, createOrOpen: false);

        internal SharedArray(string name, int length, bool create = true)
            : this(name, create ? length : null, create)
        {
        }

        private SharedArray(string name, int? length, bool createOrOpen)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Name cannot be empty", nameof(name));
            if (length is <= 0)
                throw new ArgumentOutOfRangeException(nameof(length));

            _elementSize = Unsafe.SizeOf<T>();
            _fingerprint = TypeLayoutFingerprint.Create<T>();

            if (createOrOpen)
            {
                _length = length!.Value;
                long dataSize = checked((long)_length * _elementSize);
                _buffer = MemoryRegion.CreateOrOpen(
                    name,
                    checked(ArrayHeaderSize + dataSize),
                    CreateRegionOptions(),
                    RegionKind.SharedArray);
            }
            else
            {
                _buffer = MemoryRegion.OpenExisting(
                    name,
                    CreateRegionOptions(),
                    RegionKind.SharedArray);
            }

            // The region is already mapped here. Opening an array of another element type or length
            // throws from the header check, and without this the mapping and (on Linux) its file
            // descriptor would stay open until the finalizer runs.
            try
            {
                if (createOrOpen && _buffer.IsOwner)
                    InitializeHeader();
                else
                    ValidateAndLoadHeader(expectedLength: createOrOpen ? _length : null);
            }
            catch
            {
                _buffer.Dispose();
                throw;
            }

            _elements = AtomicAccess.GetPointer(_buffer, ArrayHeaderSize);
        }

        // The array exposes no statistics, so the region's per-call counters would only cost an
        // interlocked operation on every element access.
        private static MemoryRegionOptions CreateRegionOptions() =>
            new() { EnableStatistics = false };

        private void InitializeHeader()
        {
            Span<byte> header = stackalloc byte[ArrayHeaderSize];
            header.Clear();
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(4), FormatVersion);
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(8), _length);
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(12), _elementSize);
            BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(16), _fingerprint.Low);
            BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(24), _fingerprint.High);
            _buffer.Write(header, 0);

            // Publish the magic last. Region.Write does no fencing, so on a weakly ordered CPU another
            // process could otherwise see the magic before the fields it announces.
            Thread.MemoryBarrier();
            BinaryPrimitives.WriteUInt32LittleEndian(header, ArrayMagic);
            _buffer.Write(header.Slice(0, sizeof(uint)), 0);
        }

        private void ValidateAndLoadHeader(int? expectedLength)
        {
            Span<byte> header = stackalloc byte[ArrayHeaderSize];
            Span<byte> magic = stackalloc byte[sizeof(uint)];
            var sw = Stopwatch.StartNew();
            while (true)
            {
                _buffer.Read(magic, 0);
                if (BinaryPrimitives.ReadUInt32LittleEndian(magic) == ArrayMagic)
                    break;
                if (sw.Elapsed > TimeSpan.FromSeconds(5))
                    throw new InvalidDataException("Timed out waiting for the shared-array header.");
                Thread.SpinWait(100);
            }

            // Read the fields only after the magic has been seen, never in the same copy: a single
            // copy gives no ordering between the magic and the bytes that follow it.
            Thread.MemoryBarrier();
            _buffer.Read(header, 0);

            int version = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(4));
            int storedLength = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(8));
            int storedElementSize = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(12));
            var storedFingerprint = new TypeLayoutFingerprint(
                BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(16)),
                BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(24)));

            if (version != FormatVersion || storedLength <= 0 ||
                storedElementSize != _elementSize ||
                storedFingerprint != _fingerprint)
                throw new InvalidDataException(
                    "The existing shared array has a different format or element type.");
            if (expectedLength.HasValue && expectedLength.Value != storedLength)
                throw new InvalidOperationException(
                    $"Length mismatch: expected {expectedLength.Value}, found {storedLength}.");

            long expectedCapacity = checked(ArrayHeaderSize + (long)storedLength * storedElementSize);
            if (_buffer.Capacity != expectedCapacity)
                throw new InvalidDataException("The shared-array capacity does not match its header.");

            _length = storedLength;
        }

        /// <summary>
        /// Gets or sets the element at the specified index.
        /// Zero-allocation accessor using direct memory access. Element types other than 1, 2, 4 or 8
        /// bytes wide are read and written under the shared region lock (see the class remarks).
        /// </summary>
        /// <exception cref="TimeoutException">The automatic lock could not be taken within 5 seconds.</exception>
        /// <exception cref="InvalidOperationException">
        /// A wide element is written while this thread holds only a read lock.
        /// </exception>
        public T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                ThrowIfDisposed();
                if ((uint)index >= (uint)_length)
                    throw new IndexOutOfRangeException();

                return s_needsLock && !IsHoldingAnyLock() ? ReadElementLocked(index) : ReadElement(index);
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                ThrowIfDisposed();
                if ((uint)index >= (uint)_length)
                    throw new IndexOutOfRangeException();

                if (s_needsLock && !IsHoldingWriteLock())
                    WriteElementLocked(index, value);
                else
                    WriteElement(index, value);
            }
        }

        private T ReadElement(int index)
        {
            // 1, 2, 4 and 8 byte elements: one typed load. Going through Span.CopyTo is not enough,
            // a two byte copy is a one byte store plus a two byte store and can be seen half done.
            if (!s_needsLock)
                return AtomicAccess.Read<T>(_elements + (long)index * _elementSize);

            T value = default;
            _buffer.Read(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref value, 1)),
                ArrayHeaderSize + (long)index * _elementSize);
            return value;
        }

        private void WriteElement(int index, T value)
        {
            if (!s_needsLock)
            {
                AtomicAccess.Write(_elements + (long)index * _elementSize, value);
                return;
            }

            _buffer.Write(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref value, 1)),
                ArrayHeaderSize + (long)index * _elementSize);
        }

        private T ReadElementLocked(int index)
        {
            LockTicket ticket = EnterRead(MemoryRegionOptions.DefaultLockTimeout);
            try
            {
                return ReadElement(index);
            }
            finally
            {
                ExitRead(ticket);
            }
        }

        private void WriteElementLocked(int index, T value)
        {
            LockTicket ticket = EnterWrite(MemoryRegionOptions.DefaultLockTimeout);
            try
            {
                WriteElement(index, value);
            }
            finally
            {
                ExitWrite(ticket);
            }
        }

        /// <summary>
        /// Copies a range of elements to a span.
        /// High-performance batch operation with SIMD optimization. For element types other than
        /// 1, 2, 4 or 8 bytes wide the range is read under the shared region lock.
        /// </summary>
        /// <param name="startIndex">Starting index in the array</param>
        /// <param name="destination">Destination span to copy elements to</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when range exceeds array bounds</exception>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void CopyTo(int startIndex, Span<T> destination)
        {
            ThrowIfDisposed();
            // Use long arithmetic for the bound check: (uint)+(uint) wraps mod 2^32, so a
            // hostile/buggy startIndex≈2 000 000 000 with a comparable length would silently
            // pass the test and then read past the array. Span.Length is non-negative by
            // contract, but startIndex isn't, so we explicitly reject negatives first.
            if (startIndex < 0 || (long)startIndex + destination.Length > _length)
                throw new ArgumentOutOfRangeException(nameof(startIndex));

            if (!s_needsLock || IsHoldingAnyLock())
            {
                CopyToCore(startIndex, destination);
                return;
            }

            LockTicket ticket = EnterRead(MemoryRegionOptions.DefaultLockTimeout);
            try
            {
                CopyToCore(startIndex, destination);
            }
            finally
            {
                ExitRead(ticket);
            }
        }

        private void CopyToCore(int startIndex, Span<T> destination)
        {
            // A single element must stay one load (see ReadElement); longer ranges are a plain copy
            // and are not atomic across elements.
            if (!s_needsLock && destination.Length == 1)
            {
                destination[0] = AtomicAccess.Read<T>(_elements + (long)startIndex * _elementSize);
                return;
            }

            _buffer.Read(MemoryMarshal.AsBytes(destination), ArrayHeaderSize + (long)startIndex * _elementSize);
        }

        /// <summary>
        /// Copies a span of elements to the array.
        /// High-performance batch operation with SIMD optimization. For element types other than
        /// 1, 2, 4 or 8 bytes wide the range is written under the shared region lock.
        /// </summary>
        /// <param name="startIndex">Starting index in the array</param>
        /// <param name="source">Source span to copy elements from</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when range exceeds array bounds</exception>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void CopyFrom(int startIndex, ReadOnlySpan<T> source)
        {
            ThrowIfDisposed();
            // Long arithmetic — see CopyTo for the same overflow rationale.
            if (startIndex < 0 || (long)startIndex + source.Length > _length)
                throw new ArgumentOutOfRangeException(nameof(startIndex));

            if (!s_needsLock || IsHoldingWriteLock())
            {
                CopyFromCore(startIndex, source);
                return;
            }

            LockTicket ticket = EnterWrite(MemoryRegionOptions.DefaultLockTimeout);
            try
            {
                CopyFromCore(startIndex, source);
            }
            finally
            {
                ExitWrite(ticket);
            }
        }

        private void CopyFromCore(int startIndex, ReadOnlySpan<T> source)
        {
            if (!s_needsLock && source.Length == 1)
            {
                AtomicAccess.Write(_elements + (long)startIndex * _elementSize, source[0]);
                return;
            }

            _buffer.Write(MemoryMarshal.AsBytes(source), ArrayHeaderSize + (long)startIndex * _elementSize);
        }

        /// <summary>
        /// Fills a range with a value.
        /// Optimized for large ranges using vectorization. For element types other than 1, 2, 4 or
        /// 8 bytes wide the whole range is written under one shared region lock.
        /// </summary>
        /// <param name="value">Value to fill with</param>
        /// <param name="startIndex">Starting index (default: 0)</param>
        /// <param name="count">Number of elements to fill (-1 for remaining elements)</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when range exceeds array bounds</exception>
        public void Fill(T value, int startIndex = 0, int count = -1)
        {
            ThrowIfDisposed();
            if (count == -1)
                count = _length - startIndex;

            // Long arithmetic — see CopyTo. Plus explicit negative-count rejection because
            // Fill(value, 0, -2) would compute -2 in long and pass the upper-bound check.
            if (startIndex < 0 || count < 0 || (long)startIndex + count > _length)
                throw new ArgumentOutOfRangeException();

            if (!s_needsLock || IsHoldingWriteLock())
            {
                FillCore(in value, startIndex, count);
                return;
            }

            LockTicket ticket = EnterWrite(MemoryRegionOptions.DefaultLockTimeout);
            try
            {
                FillCore(in value, startIndex, count);
            }
            finally
            {
                ExitWrite(ticket);
            }
        }

        // A batch of this many bytes is the most that is staged at a time.
        private const int FillBatchBytes = 64 * 1024;

        // The value is passed by reference down to the copy: a by-value parameter of an element of tens of KiB
        // puts a copy of it on the stack in every frame, and a thread of 1 MiB (Windows) does not have many.
        private void FillCore(in T value, int startIndex, int count)
        {
            // A managed array cannot hold elements of 64 KiB or more, and a method that so much as mentions
            // T[] or ArrayPool<T> for such a T does not even load (TypeLoadException when it is compiled,
            // before a single element is written). A batch of such elements would be one or two elements
            // anyway, so they are copied one by one from a method that has no array in it.
            if (_elementSize >= FillBatchBytes / 2)
                FillOneByOne(in value, startIndex, count);
            else
                FillInBatches(in value, startIndex, count);
        }

        private void FillOneByOne(in T value, int startIndex, int count)
        {
            ReadOnlySpan<T> one = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in value), 1);
            for (int i = 0; i < count; i++)
                CopyFromCore(startIndex + i, one);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void FillInBatches(in T value, int startIndex, int count)
        {
            // Batch fill: create a filled buffer and write in chunks. The batch is bounded in bytes as well as
            // in elements: 4096 elements of 32 KiB would be a 128 MiB temporary buffer on every call.
            int batchCount = Math.Min(count, Math.Min(4096, Math.Max(1, FillBatchBytes / _elementSize)));
            int batchBytes = batchCount * _elementSize;

            // Use stackalloc for small batches, ArrayPool for large.
            // ArrayPool (vs GC.AllocateUninitializedArray) eliminates GC pressure when Fill is
            // called repeatedly — common in init/reset patterns for shared arrays — and the
            // rented buffer is short-lived, exactly the workload ArrayPool is tuned for.
            if (batchBytes <= 1024)
            {
                Span<T> temp = stackalloc T[batchCount];
                temp.Fill(value);
                FillBatched(startIndex, count, temp);
            }
            else
            {
                T[] rented = ArrayPool<T>.Shared.Rent(batchCount);
                try
                {
                    var span = rented.AsSpan(0, batchCount);
                    span.Fill(value);
                    FillBatched(startIndex, count, span);
                }
                finally
                {
                    ArrayPool<T>.Shared.Return(rented);
                }
            }
        }

        /// <summary>
        /// Clears the entire array to default(T)
        /// </summary>
        public void Clear()
        {
            ThrowIfDisposed();
            Fill(default, 0, _length);
        }

        private void FillBatched(int startIndex, int count, Span<T> batch)
        {
            int offset = 0;
            while (offset < count)
            {
                int batchSize = Math.Min(batch.Length, count - offset);
                CopyFromCore(startIndex + offset, batch.Slice(0, batchSize));
                offset += batchSize;
            }
        }

        /// <summary>
        /// Acquires the shared write lock (all processes) with the default timeout of 5 seconds.
        /// See <see cref="AcquireWriteLock(TimeSpan)"/>.
        /// </summary>
        public WriteLock AcquireWriteLock() => AcquireWriteLock(MemoryRegionOptions.DefaultLockTimeout);

        /// <summary>
        /// Acquires the shared write lock, which excludes every other reader and writer that uses a lock,
        /// in all processes, until the returned guard is disposed. Use it to change several elements as one
        /// unit. The lock is reentrant for the calling thread; the indexer and the range operations inside
        /// the scope do not take it again.
        /// </summary>
        /// <param name="timeout">How long to wait; <see cref="Timeout.InfiniteTimeSpan"/> waits indefinitely.</param>
        /// <exception cref="TimeoutException">The lock could not be acquired within the timeout.</exception>
        /// <exception cref="InvalidOperationException">
        /// The calling thread holds only a read lock; upgrading it would deadlock the thread against itself.
        /// </exception>
        public WriteLock AcquireWriteLock(TimeSpan timeout) => new(this, EnterWrite(timeout));

        /// <summary>
        /// Acquires the shared read lock (all processes) with the default timeout of 5 seconds.
        /// See <see cref="AcquireReadLock(TimeSpan)"/>.
        /// </summary>
        public ReadLock AcquireReadLock() => AcquireReadLock(MemoryRegionOptions.DefaultLockTimeout);

        /// <summary>
        /// Acquires the shared read lock, which excludes writers but not other readers, in all processes,
        /// until the returned guard is disposed. Use it to observe several elements as one consistent
        /// snapshot. It is reentrant for the calling thread, also inside a write lock.
        /// </summary>
        /// <param name="timeout">How long to wait; <see cref="Timeout.InfiniteTimeSpan"/> waits indefinitely.</param>
        /// <exception cref="TimeoutException">The lock could not be acquired within the timeout.</exception>
        public ReadLock AcquireReadLock(TimeSpan timeout) => new(this, EnterRead(timeout));

        /// <summary>
        /// Unconditionally clears the cross-process write lock and read-lock count of this array's region.
        /// A process that dies while holding a lock can leave the reader count above zero forever. Call this
        /// only when no process is inside a critical section of the array. See
        /// <see cref="MemoryRegion.ForceResetLocks"/>.
        /// </summary>
        public void ForceResetLocks()
        {
            ThrowIfDisposed();
            ((MemoryRegion)_buffer).ForceResetLocks();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsHoldingAnyLock() => _writeLockDepth.Value > 0 || _readLockDepth.Value > 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsHoldingWriteLock() => _writeLockDepth.Value > 0;

        /// <summary>Returns true when the region lock was taken, false for a reentrant acquisition.</summary>
        /// <summary>
        /// Proof of one acquisition: the depth the thread was at right afterwards, and whether this
        /// acquisition took the region lock (false for a reentrant one). Releasing checks the depth, so a copy
        /// of a guard, a guard released out of order or one released on another thread is refused instead of
        /// corrupting the bookkeeping of the thread that really holds the lock.
        /// </summary>
        internal readonly struct LockTicket
        {
            public readonly bool TookRegionLock;
            public readonly int Depth;

            public LockTicket(bool tookRegionLock, int depth)
            {
                TookRegionLock = tookRegionLock;
                Depth = depth;
            }
        }

        private LockTicket EnterWrite(TimeSpan timeout)
        {
            ThrowIfDisposed();
            TimeoutHelper.Validate(timeout, nameof(timeout));

            if (_writeLockDepth.Value == 0 && _readLockDepth.Value > 0)
                throw new InvalidOperationException(
                    "Cannot take the write lock while holding only a read lock. Release the read lock first, " +
                    "or take the write lock before reading and writing together.");

            bool tookRegionLock = false;
            if (_writeLockDepth.Value == 0)
            {
                if (!_buffer.TryAcquireWriteLock(timeout))
                    throw new TimeoutException($"Failed to acquire write lock within {timeout}");
                tookRegionLock = true;
            }

            return new LockTicket(tookRegionLock, ++_writeLockDepth.Value);
        }

        private void ExitWrite(LockTicket ticket)
        {
            // Dispose() already released what this thread held.
            if (_disposed != 0)
                return;

            if (_writeLockDepth.Value != ticket.Depth)
                throw new SynchronizationLockException(
                    "A write lock guard was released twice, out of order or on another thread. Nothing was released.");

            // A read guard taken inside the write lock holds no region lock of its own; releasing the write
            // lock under it would leave that guard reading without any protection.
            if (ticket.TookRegionLock && _readLockDepth.Value > 0)
                throw new SynchronizationLockException(
                    "The write lock cannot be released while a read lock guard taken inside it is still open. Nothing was released.");

            try
            {
                if (ticket.TookRegionLock)
                    _buffer.ReleaseWriteLock();
            }
            finally
            {
                _writeLockDepth.Value = ticket.Depth - 1;
            }
        }

        private LockTicket EnterRead(TimeSpan timeout)
        {
            ThrowIfDisposed();
            TimeoutHelper.Validate(timeout, nameof(timeout));

            bool tookRegionLock = false;
            if (_readLockDepth.Value == 0 && _writeLockDepth.Value == 0)
            {
                if (!_buffer.TryAcquireReadLock(timeout))
                    throw new TimeoutException($"Failed to acquire read lock within {timeout}");
                tookRegionLock = true;
            }

            return new LockTicket(tookRegionLock, ++_readLockDepth.Value);
        }

        private void ExitRead(LockTicket ticket)
        {
            if (_disposed != 0)
                return;

            if (_readLockDepth.Value != ticket.Depth)
                throw new SynchronizationLockException(
                    "A read lock guard was released twice, out of order or on another thread. Nothing was released.");

            try
            {
                if (ticket.TookRegionLock)
                    _buffer.ReleaseReadLock();
            }
            finally
            {
                _readLockDepth.Value = ticket.Depth - 1;
            }
        }

        /// <summary>
        /// Guard returned by <see cref="AcquireWriteLock()"/>; disposing it releases the lock. It is a ref
        /// struct on purpose: the lock belongs to one thread, and the compiler therefore rejects holding the
        /// guard across an <c>await</c> or handing it to another thread.
        /// </summary>
        public ref struct WriteLock
        {
            private SharedArray<T>? _owner;
            private readonly LockTicket _ticket;

            internal WriteLock(SharedArray<T> owner, LockTicket ticket)
            {
                _owner = owner;
                _ticket = ticket;
            }

            /// <summary>Releases the write lock; disposing more than once has no further effect.</summary>
            public void Dispose()
            {
                SharedArray<T>? owner = _owner;
                if (owner is null)
                    return;

                // A refused release (a copy, or an order that would leave a read guard unprotected) throws
                // before anything changes, and the guard stays valid so that it can be released properly.
                owner.ExitWrite(_ticket);
                _owner = null;
            }
        }

        /// <summary>
        /// Guard returned by <see cref="AcquireReadLock()"/>; disposing it releases the lock. A ref struct
        /// for the same reason as <see cref="WriteLock"/>.
        /// </summary>
        public ref struct ReadLock
        {
            private SharedArray<T>? _owner;
            private readonly LockTicket _ticket;

            internal ReadLock(SharedArray<T> owner, LockTicket ticket)
            {
                _owner = owner;
                _ticket = ticket;
            }

            /// <summary>Releases the read lock; disposing more than once has no further effect.</summary>
            public void Dispose()
            {
                SharedArray<T>? owner = _owner;
                if (owner is null)
                    return;

                owner.ExitRead(_ticket);
                _owner = null;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ThrowIfDisposed()
        {
            if (_disposed != 0)
                throw new ObjectDisposedException(nameof(SharedArray<T>));
        }

        /// <summary>
        /// Releases the underlying memory region. Stop and join every thread that uses this instance first:
        /// calls that do not take a lock are not tracked, so one that is still running while the memory is
        /// unmapped terminates the process (see <see cref="MemoryRegion.DisposeGracePeriod"/>).
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            // A guard that is still open on this thread would otherwise leave the cross-process lock held
            // until the process ends (its owner is alive, so no waiter would recover it). Guards held by
            // other threads cannot be reached from here: stop and join those threads before disposing.
            try
            {
                if (_writeLockDepth.Value > 0)
                    _buffer.ReleaseWriteLock();
                else if (_readLockDepth.Value > 0)
                    _buffer.ReleaseReadLock();
            }
            catch (Exception ex) when (ex is SynchronizationLockException or ObjectDisposedException)
            {
                // The lock was taken over (orphan recovery or ForceResetLocks) or the region is gone.
            }

            // No finalizer: if Dispose is never called, the MemoryRegion's own finalizer unmaps the memory.
            _buffer?.Dispose();
            _writeLockDepth.Dispose();
            _readLockDepth.Dispose();
        }
    }
}
