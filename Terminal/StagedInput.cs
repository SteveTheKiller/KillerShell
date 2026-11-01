using System;
using System.Threading;

namespace KillerShell.Terminal
{
    /// <summary>
    /// A command typed into a fresh shell once it is listening, without running it. Input sent
    /// earlier is eaten by the shell's startup. A shell that reports its directory is listening at
    /// its first report; the title it sets while starting up says nothing about that. Any other
    /// shell is taken to be listening a short settle after its first title, or at the timeout.
    /// </summary>
    internal sealed class StagedInput : IDisposable
    {
        private readonly TerminalBuffer _buffer;
        private readonly Action<string> _send;
        private readonly string _command;
        private readonly TimeSpan _settle;
        private Timer? _timer;
        private Timer? _settleTimer;
        private int _done;
        private int _titled;

        public StagedInput(TerminalBuffer buffer, string command, Action<string> send, TimeSpan timeout,
            bool reportsDirectory = false, TimeSpan? settle = null)
        {
            _settle = settle ?? TimeSpan.FromMilliseconds(400);
            _buffer = buffer;
            _command = command;
            _send = send;
            _buffer.DirectoryChanged += OnReport;
            // cmd and a shell started without the prompt script report no directory, but they do
            // set a title, and failing that the timeout sends anyway.
            if (!reportsDirectory) _buffer.TitleChanged += OnTitle;
            _timer = new Timer(_ => Fire(), null, timeout, Timeout.InfiniteTimeSpan);
        }

        private void OnReport(string _) => Fire();

        private void OnTitle(string _)
        {
            if (Interlocked.Exchange(ref _titled, 1) != 0 || Volatile.Read(ref _done) != 0) return;
            var timer = new Timer(__ => Fire(), null, _settle, Timeout.InfiniteTimeSpan);
            _settleTimer = timer;
            if (Volatile.Read(ref _done) != 0) timer.Dispose();
        }

        private void Fire()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            Release();
            _send(_command);
        }

        private void Release()
        {
            _buffer.DirectoryChanged -= OnReport;
            _buffer.TitleChanged -= OnTitle;
            Interlocked.Exchange(ref _timer, null)?.Dispose();
            Interlocked.Exchange(ref _settleTimer, null)?.Dispose();
        }

        /// <summary>Stops waiting without sending, as when the terminal closes first.</summary>
        public void Dispose()
        {
            Interlocked.Exchange(ref _done, 1);
            Release();
        }
    }
}
