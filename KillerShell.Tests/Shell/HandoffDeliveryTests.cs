using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Interop;
using System.Windows.Threading;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Shell;

public sealed class HandoffDeliveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeliveryRequiresReceiverAcknowledgment(bool accepts)
    {
        using var receiver = new Receiver(accepts, 0);
        Assert.Equal(accepts, MainWindow.SendHandoffArgs("--processes", receiver.Handle));
    }

    [Fact]
    public void UnresponsiveReceiverHasBoundedDelivery()
    {
        using var receiver = new Receiver(true, 3000);
        var elapsed = Stopwatch.StartNew();
        Assert.False(MainWindow.SendHandoffArgs("--storage", receiver.Handle));
        Assert.True(elapsed.Elapsed < TimeSpan.FromMilliseconds(2500), elapsed.Elapsed.ToString());
    }

    [Fact]
    public void MissingReceiverIsNotSuccessfulDelivery() =>
        Assert.False(MainWindow.SendHandoffArgs("--registry", IntPtr.Zero));

    private sealed class Receiver : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new();
        private Dispatcher? _dispatcher;
        private Exception? _failure;
        internal IntPtr Handle { get; private set; }
        internal Receiver(bool accepts, int delay)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    _dispatcher = Dispatcher.CurrentDispatcher;
                    // Message-only windows never appear or take focus on the desktop.
                    using var source = new HwndSource(new HwndSourceParameters("KillerShell handoff fixture")
                    { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
                    source.AddHook((IntPtr hwnd, int message, IntPtr w, IntPtr l, ref bool handled) =>
                    {
                        if (message != 0x004A) return IntPtr.Zero;
                        if (delay > 0) Thread.Sleep(delay);
                        handled = true;
                        return accepts ? new IntPtr(1) : IntPtr.Zero;
                    });
                    Handle = source.Handle;
                    _ready.Set();
                    Dispatcher.Run();
                }
                catch (Exception ex) { _failure = ex; _ready.Set(); }
            }) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            Assert.True(_ready.Wait(10000), "Receiver did not start.");
            Assert.Null(_failure);
        }
        public void Dispose()
        {
            _dispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
            Assert.True(_thread.Join(10000), "Receiver did not stop.");
            _ready.Dispose();
        }
    }
}
