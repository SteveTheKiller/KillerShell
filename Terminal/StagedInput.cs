using System;
using System.Threading;

namespace KillerShell.Terminal
{
    /// <summary>
    /// A command typed into a fresh shell once it is listening, without running it. Input sent
    /// earlier is eaten by the shell's startup.
    /// </summary>
    internal sealed class StagedInput : IDisposable
    {
        private readonly TerminalBuffer _buffer;
        private readonly Action<string> _send;
        private readonly string _command;
        private Timer? _timer;
        private int _done;

        public StagedInput(TerminalBuffer buffer, string command, Action<string> send, TimeSpan timeout)
        {
            _buffer = buffer;
            _command = command;
            _send = send;
            _buffer.DirectoryChanged += OnReport;
            // cmd and a shell started without the prompt script report no directory, but they do
            // set a title, and failing that the timeout sends anyway.
            _buffer.TitleChanged += OnReport;
            _timer = new Timer(_ => Fire(), null, timeout, Timeout.InfiniteTimeSpan);
        }

        private void OnReport(string _) => Fire();

        private void Fire()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            Release();
            _send(_command);
        }

        private void Release()
        {
            _buffer.DirectoryChanged -= OnReport;
            _buffer.TitleChanged -= OnReport;
            Interlocked.Exchange(ref _timer, null)?.Dispose();
        }

        /// <summary>Stops waiting without sending, as when the terminal closes first.</summary>
        public void Dispose()
        {
            Interlocked.Exchange(ref _done, 1);
            Release();
        }
    }
}
