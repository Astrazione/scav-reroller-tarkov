namespace ScavReroller.Client
{
    internal sealed class RerollRecoveryState
    {
        private bool _neededBeforeRequest;
        internal bool Busy { get; private set; }
        internal bool NeedsRefresh { get; private set; }

        internal bool TryBegin()
        {
            if (Busy) return false;
            _neededBeforeRequest = NeedsRefresh;
            Busy = true;
            return true;
        }

        internal void GenerationRequested() { NeedsRefresh = true; }
        internal void GenerationRejected() { NeedsRefresh = _neededBeforeRequest; }
        internal void ProfileApplied() { NeedsRefresh = false; }
        internal void End() { Busy = false; }
        internal void ResetForSession() { NeedsRefresh = false; _neededBeforeRequest = false; }
        internal bool BlocksNext(bool scavSelected) { return Busy || (NeedsRefresh && scavSelected); }
    }
}
