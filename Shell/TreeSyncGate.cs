using System;

namespace KillerShell.Shell
{
    /// <summary>
    /// The folder tree's two-way sync guard. Selecting a node navigates and navigating selects
    /// a node, so code that drives the tree holds this open to keep the two from echoing.
    /// </summary>
    /// <remarks>
    /// A depth count, not a saved-and-restored flag. Two overlapping holders (two quick
    /// hidden-file toggles, each awaiting its refresh) used to restore each other's snapshot:
    /// the first to finish reopened the guard under the second, and the second then restored
    /// "syncing" forever, so tree clicks stopped navigating.
    /// </remarks>
    internal sealed class TreeSyncGate
    {
        private int _depth;

        internal bool IsActive => _depth > 0;

        internal IDisposable Enter()
        {
            _depth++;
            return new Scope(this);
        }

        private sealed class Scope(TreeSyncGate gate) : IDisposable
        {
            private bool _done;

            public void Dispose()
            {
                if (_done) return;
                _done = true;
                gate._depth--;
            }
        }
    }
}
