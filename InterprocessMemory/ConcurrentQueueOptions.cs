using System;

namespace InterprocessMemory
{
    /// <summary>Process-local tuning options for concurrent queues.</summary>
    public sealed class ConcurrentQueueOptions
    {
        /// <summary>
        /// How many times one <c>TryEnqueue</c>/<c>TryDequeue</c>/<c>TryWrite</c>/<c>TryRead</c> call looks at the
        /// slot it wants again after losing a race with another thread or finding the slot still being
        /// prepared, before it gives up and returns <c>false</c>. Every retry backs off a little longer. It is
        /// a bound on the work of one call, not a time: a full or an empty queue is reported at once, without
        /// using it up. Raise it for many contending producers or consumers. A call that gives up because of
        /// it is not a full or an empty queue: <see cref="ConcurrentMessageQueue"/> counts it separately
        /// (<c>SpinExhaustedWrites</c>/<c>SpinExhaustedReads</c>), <see cref="ConcurrentQueue{T}"/> does not
        /// count it at all.
        /// </summary>
        public int MaxSpins { get; set; } = 100;

        /// <summary>
        /// Whether the shared counters (total and failed operations) are maintained. Each counter is an
        /// interlocked update on a cache line that all processes share, which costs throughput under
        /// contention; turn it off when the numbers are not read.
        /// </summary>
        public bool EnableStatistics { get; set; } = true;

        internal void Validate()
        {
            if (MaxSpins <= 0)
                throw new ArgumentOutOfRangeException(nameof(MaxSpins), "MaxSpins must be positive.");
        }
    }
}
