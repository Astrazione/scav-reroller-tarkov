$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$clientSource = Join-Path $PSScriptRoot '../src/ScavReroller.Client'
$stateSource = Get-Content -LiteralPath (Join-Path $clientSource 'RerollRecoveryState.cs') -Raw
$deadlineSource = Get-Content -LiteralPath (Join-Path $clientSource 'OperationDeadline.cs') -Raw
# Combine real production classes in one compilation without loading EFT.
$source = $deadlineSource + [Environment]::NewLine + ($stateSource -replace '^using System;\s*', '')
$checks = @'
namespace ScavReroller.Client
{
    public static class RecoveryChecks
    {
        private static void Require(bool value, string message)
        {
            if (!value) throw new System.Exception(message);
        }

        public static async System.Threading.Tasks.Task<string> Run()
        {
            var state = new RerollRecoveryState();
            Require(state.TryBegin(), "Initial generation blocked.");
            Require(!state.TryBegin(), "Concurrent operation accepted.");
            state.GenerationRequested();
            state.End(); // HTTP timeout or profile failure.
            Require(!state.Busy && state.NeedsRefresh, "Failure did not release busy state.");
            Require(state.BlocksNext(true) && !state.BlocksNext(false), "Stale scav incorrectly blocks PMC.");

            for (var i = 0; i < 20; i++)
            {
                Require(state.TryBegin(), "Recovery permanently blocked fresh operations.");
                state.GenerationRequested(); // Explicit new reroll must be possible while stale.
                state.End();
                Require(!state.Busy, "Repeated failure kept busy state.");
            }
            Require(state.TryBegin(), "Reroll unavailable after repeated failures.");
            state.GenerationRequested();
            state.ProfileApplied();
            state.End(); // Preview fails after session was already updated.
            Require(!state.NeedsRefresh && !state.BlocksNext(true), "Model failure invalidated a synchronized profile.");

            Require(state.TryBegin(), "New request unavailable after recovery.");
            state.GenerationRequested();
            state.GenerationRejected();
            state.End();
            Require(!state.NeedsRefresh, "Rejected request dirtied an initially synchronized profile.");
            state.TryBegin(); state.GenerationRequested(); state.End();
            state.TryBegin(); state.GenerationRequested(); state.GenerationRejected(); state.End();
            Require(state.NeedsRefresh, "Rejected new reroll erased an earlier uncertain result.");
            state.TryBegin(); state.ResetForSession(); state.GenerationRequested(); state.GenerationRejected(); state.End();
            Require(!state.NeedsRefresh, "Old account recovery leaked into another session.");
            var other = new RerollRecoveryState();
            state.TryBegin(); state.GenerationRequested(); state.End();
            Require(other.TryBegin() && !other.NeedsRefresh, "One client state blocked another client.");
            other.End();

            var none = System.Threading.CancellationToken.None;
            var value = await OperationDeadline.Wait(System.Threading.Tasks.Task.FromResult(42),
                System.TimeSpan.FromSeconds(1), none);
            Require(value == 42, "Successful request result was lost.");

            var failed = new System.Threading.Tasks.TaskCompletionSource<int>();
            failed.SetException(new System.InvalidOperationException("injected failure"));
            var failedObserved = false;
            try { await OperationDeadline.Wait(failed.Task, System.TimeSpan.FromSeconds(1), none); }
            catch (System.InvalidOperationException) { failedObserved = true; }
            Require(failedObserved, "Transport exception was swallowed.");

            var late = new System.Threading.Tasks.TaskCompletionSource<int>();
            var bounded = OperationDeadline.Wait(late.Task, System.TimeSpan.FromMilliseconds(20), none);
            var timedOut = false;
            try { await bounded; } catch (System.TimeoutException) { timedOut = true; }
            Require(timedOut, "Never-completing request did not time out.");
            late.SetResult(17);
            Require(bounded.IsFaulted, "Late HTTP result revived a timed-out operation.");

            using (var cancellation = new System.Threading.CancellationTokenSource())
            {
                var pending = new System.Threading.Tasks.TaskCompletionSource<int>();
                var cancelled = OperationDeadline.Wait(pending.Task, System.TimeSpan.FromSeconds(5), cancellation.Token);
                cancellation.Cancel();
                var observed = false;
                try { await cancelled; } catch (System.OperationCanceledException) { observed = true; }
                Require(observed, "Leaving selection did not cancel local waiting.");
                pending.SetException(new System.InvalidOperationException("late fault"));
                Require(cancelled.IsCanceled, "Late fault revived a cancelled operation.");
            }
            return "Passed recovery, explicit reroll after repeated failure, preview-only failure, account/client isolation, transport failure, deadline, late result and cancellation checks.";
        }
    }
}
'@
Add-Type -TypeDefinition ($source + [Environment]::NewLine + $checks) -Language CSharp
[ScavReroller.Client.RecoveryChecks]::Run().GetAwaiter().GetResult()

$navigation = Get-Content -LiteralPath (Join-Path $clientSource 'SelectionNavigationGuard.cs') -Raw
if ($navigation -match '_backButton') { throw 'Recovery guard must not take ownership of the Back button.' }
'Back button has no recovery lock owner. In-game Escape and UI recovery still require validation.'
