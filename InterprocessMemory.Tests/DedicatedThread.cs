namespace InterprocessMemory.Tests;

/// <summary>
/// Runs a loop that spins until it is cancelled on a thread of its own.
///
/// <para>With <c>Task.Run</c> such loops occupy thread-pool workers. The pool starts with one worker per
/// core and adds one every half second or so, so on a small machine (a two core CI runner) the loops queued
/// behind the first few start late, or never before the test's own cancellation timer, which also needs a
/// pool thread, has fired. The test then fails with a writer that never ran, a consumer that saw nothing, or
/// its timeout, depending on the order in which the work items were queued, and the result differs between
/// running the test alone and running the whole suite.</para>
/// </summary>
internal static class DedicatedThread
{
    public static Task Run(Action action) =>
        Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
}
