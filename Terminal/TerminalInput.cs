using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace KillerShell.Terminal;

internal sealed class TerminalInput
{
    private const int PendingLimit = 4 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly Queue<byte[]> _pending = new();
    private int _pendingBytes, _activeBytes;
    private bool _stopping;

    internal TerminalInput(Stream stream)
    {
        var writer = new Thread(() => Write(stream)) { IsBackground = true, Name = "ConPTY input" };
        writer.Start();
    }

    internal bool TrySend(string text)
    {
        lock (_gate)
        {
            if (_stopping) return true;
            int available = PendingLimit - _pendingBytes;
            // UTF-8 needs at least one byte per UTF-16 code unit. Reject large
            // operations before counting or allocating their encoded payload.
            if (text.Length > available) return false;
            int count = Encoding.UTF8.GetByteCount(text);
            if (count > available) return false;
            _pending.Enqueue(Encoding.UTF8.GetBytes(text));
            _pendingBytes += count;
            Monitor.PulseAll(_gate);
            return true;
        }
    }

    internal void Stop()
    {
        lock (_gate)
        {
            _stopping = true;
            _pending.Clear();
            _pendingBytes = _activeBytes;
            Monitor.PulseAll(_gate);
        }
    }

    private void Write(Stream stream)
    {
        while (true)
        {
            byte[] bytes;
            lock (_gate)
            {
                while (!_stopping && _pending.Count == 0) Monitor.Wait(_gate);
                if (_stopping) return;
                bytes = _pending.Dequeue();
                _activeBytes = bytes.Length;
            }
            try
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
            catch { Stop(); return; }
            finally
            {
                lock (_gate)
                {
                    _pendingBytes -= bytes.Length;
                    _activeBytes = 0;
                }
            }
        }
    }
}
