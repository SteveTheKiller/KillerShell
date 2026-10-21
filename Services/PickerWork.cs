using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace KillerShell.Services
{
    internal sealed class PickerWork(Dispatcher dispatcher) : IDisposable
    {
        private readonly SemaphoreSlim _workers = new(2);
        private CancellationTokenSource? _pending;
        private bool _closed;

        internal void Cancel()
        {
            _pending?.Cancel();
            _pending = null;
        }

        internal Task RunAsync<T>(Func<CancellationToken, T> read,
            Func<T, CancellationToken, Task> publish, Action<Exception>? failed = null)
        {
            if (_closed) return Task.CompletedTask;
            Cancel();
            var request = _pending = new CancellationTokenSource();
            return CompleteAsync(request, read, publish, failed);
        }

        private async Task CompleteAsync<T>(CancellationTokenSource request, Func<CancellationToken, T> read,
            Func<T, CancellationToken, Task> publish, Action<Exception>? failed)
        {
            var token = request.Token;
            try
            {
                await _workers.WaitAsync(token).ConfigureAwait(false);
                T result;
                try { result = await Task.Run(() => read(token), token).ConfigureAwait(false); }
                finally { _workers.Release(); }
                token.ThrowIfCancellationRequested();
                await dispatcher.InvokeAsync(() =>
                    _closed || token.IsCancellationRequested ? Task.CompletedTask : publish(result, token),
                    DispatcherPriority.Background).Task.Unwrap().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (!token.IsCancellationRequested && !dispatcher.HasShutdownStarted)
                    await dispatcher.InvokeAsync(() =>
                    {
                        if (!_closed && !token.IsCancellationRequested) failed?.Invoke(error);
                    }, DispatcherPriority.Background).Task.ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            _closed = true;
            Cancel();
        }

        internal static async Task PublishAsync<T>(IReadOnlyList<T> rows, ObservableCollection<T> target,
            CancellationToken token, Action<Action> mutate)
        {
            token.ThrowIfCancellationRequested();
            mutate(target.Clear);
            for (int offset = 0; offset < rows.Count; offset += 128)
            {
                token.ThrowIfCancellationRequested();
                int end = Math.Min(offset + 128, rows.Count);
                mutate(() =>
                {
                    for (int index = offset; index < end; index++)
                    {
                        token.ThrowIfCancellationRequested();
                        target.Add(rows[index]);
                    }
                });
                if (end < rows.Count) await Dispatcher.Yield(DispatcherPriority.Background);
            }
        }
    }
}
