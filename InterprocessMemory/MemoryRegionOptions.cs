using System;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace InterprocessMemory
{
    /// <summary>
    /// Configuration options for an interprocess memory region.
    /// </summary>
    public sealed class MemoryRegionOptions
    {
        // Internal migration hooks used only by the repository's 2.x regression suite.
        internal long Capacity { get; set; } = 64L * 1024 * 1024;

        internal bool CreateOrOpen { get; set; } = true;

        /// <summary>
        /// Default lock timeout: 5 seconds
        /// </summary>
        public static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(5);

        /// <summary>
        /// A reasonable value to pass as <see cref="OrphanLockTimeout"/> when you opt in to
        /// time-based lock takeover (30 seconds). It is NOT the default: <see cref="OrphanLockTimeout"/>
        /// is <see cref="TimeSpan.Zero"/> (disabled) unless you set it.
        /// </summary>
        public static readonly TimeSpan DefaultOrphanLockTimeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Gets or sets the default lock acquisition timeout
        /// </summary>
        public TimeSpan LockTimeout { get; set; } = DefaultLockTimeout;

        /// <summary>
        /// Gets or sets whether to enable SIMD optimizations
        /// </summary>
        public bool EnableSimd { get; set; } = true;

        /// <summary>
        /// Gets or sets the memory alignment (must be power of 2)
        /// </summary>
        public int Alignment { get; set; } = 64; // Cache line size

        /// <summary>
        /// Gets or sets the file path for persistent storage (null for anonymous)
        /// </summary>
        public string? FilePath { get; set; }

        /// <summary>
        /// Gets or sets whether a waiting process recovers a write lock whose owner process has exited
        /// (or whose PID was reused by another process)
        /// </summary>
        public bool EnableOrphanLockDetection { get; set; } = true;

        /// <summary>
        /// Gets or sets the time after which a write lock held by a process that is still ALIVE is
        /// also treated as orphaned, so that a waiter may take it over. <see cref="TimeSpan.Zero"/>
        /// (the default) disables this.
        /// <para>
        /// A lock whose owner process has exited is always recovered while
        /// <see cref="EnableOrphanLockDetection"/> is on. A time limit additionally breaks mutual
        /// exclusion for a healthy owner that simply holds the lock for longer than the limit (a long
        /// transaction, a paused debugger), so enable it only when every critical section is known to
        /// be shorter than the value you choose.
        /// </para>
        /// </summary>
        public TimeSpan OrphanLockTimeout { get; set; } = TimeSpan.Zero;

        /// <summary>
        /// Gets or sets whether to enable checksum verification
        /// </summary>
        public bool EnableChecksumVerification { get; set; } = false;

        /// <summary>
        /// Gets or sets whether to enable events
        /// </summary>
        public bool EnableEvents { get; set; } = false;

        /// <summary>
        /// When true (default), tracks read/write counts and byte totals via Interlocked operations
        /// on every <see cref="MemoryRegion.Read"/>/<see cref="MemoryRegion.Write"/>.
        /// The cost is roughly a single LOCK-prefixed instruction per call (~10ns uncontended,
        /// 20-40ns under heavy reader contention).
        ///
        /// Set to <c>false</c> for read-heavy workloads with multiple concurrent readers where
        /// the per-buffer statistics are not consumed: hot-path Interlocked overhead drops to
        /// zero and <see cref="MemoryRegion.GetStatistics"/> returns all zeros.
        /// External observability (request-level metrics, sampling, etc.) is recommended in
        /// that mode.
        /// </summary>
        public bool EnableStatistics { get; set; } = true;

        /// <summary>
        /// Gets or sets the logger instance
        /// </summary>
        public ILogger? Logger { get; set; }

        /// <summary>
        /// Validates the options
        /// </summary>
        public void Validate()
        {
            if (Capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(Capacity), "Capacity must be positive");

            if (LockTimeout < TimeSpan.Zero && LockTimeout != Timeout.InfiniteTimeSpan)
                throw new ArgumentOutOfRangeException(nameof(LockTimeout), "LockTimeout must be non-negative or infinite");

            if (Alignment <= 0 || (Alignment & (Alignment - 1)) != 0)
                throw new ArgumentException("Alignment must be a power of 2", nameof(Alignment));

            if (OrphanLockTimeout < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(OrphanLockTimeout), "OrphanLockTimeout must be non-negative");
        }
    }
}
