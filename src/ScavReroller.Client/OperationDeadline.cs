using System;
using System.Threading;
using System.Threading.Tasks;

namespace ScavReroller.Client
{
    internal static class OperationDeadline
    {
        internal static async Task<T> Wait<T>(Task<T> operation, TimeSpan timeout, CancellationToken cancellation)
        {
            using (var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                var delay = Task.Delay(timeout, delayCancellation.Token);
                var completed = await Task.WhenAny(operation, delay);
                if (completed != operation)
                {
                    // The transport cannot always be cancelled. Observe a late
                    // error, but never resume the caller or apply a late result.
                    _ = operation.ContinueWith(t => { var ignored = t.Exception; },
                        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted |
                        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    cancellation.ThrowIfCancellationRequested();
                    throw new TimeoutException("Operation timed out after " + timeout.TotalSeconds + " seconds.");
                }
                delayCancellation.Cancel();
                cancellation.ThrowIfCancellationRequested();
                return await operation;
            }
        }

        internal static Task Wait(Task operation, TimeSpan timeout, CancellationToken cancellation) =>
            Wait(Complete(operation), timeout, cancellation);

        private static async Task<bool> Complete(Task operation)
        {
            await operation;
            return true;
        }
    }
}
