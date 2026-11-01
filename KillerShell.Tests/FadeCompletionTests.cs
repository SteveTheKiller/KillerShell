using System;
using System.Threading;
using System.Windows.Threading;
using KillerShell.Controls;
using Xunit;

namespace KillerShell.Tests;

// The dialog's fade completion, run on a bare dispatcher with no window shown.
public sealed class FadeCompletionTests
{
    private static void OnDispatcherThread(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception error) { failure = error; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "The dispatcher check timed out.");
        if (failure != null) throw failure;
    }

    private static void Pump(Func<bool> until, TimeSpan limit)
    {
        var end = DateTime.UtcNow + limit;
        while (!until() && DateTime.UtcNow < end)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
    }

    [Fact]
    public void TheFallbackFiresAfterTheFadeHasHadTimeToEnd()
        => Assert.True(FadeCompletion.Fallback > TimeSpan.FromMilliseconds(Anim.FadeMs));

    [Fact]
    public void CompletionRunsOnceHoweverOftenItIsReached()
    {
        int runs = 0;
        var completion = new FadeCompletion(() => runs++);
        completion.Complete();
        completion.Complete();
        Assert.Equal(1, runs);
    }

    [Fact]
    public void ALostCompletedCallbackStillFinishesOnceAndALateOneChangesNothing()
    {
        OnDispatcherThread(() =>
        {
            int runs = 0;
            var completion = new FadeCompletion(() => runs++);
            var started = DateTime.UtcNow;
            completion.ArmFallback(Dispatcher.CurrentDispatcher);
            Pump(() => runs > 0, TimeSpan.FromSeconds(5));
            Assert.Equal(1, runs);
            Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(Anim.FadeMs));
            completion.Complete();
            Pump(() => false, TimeSpan.FromMilliseconds(FadeCompletion.Fallback.TotalMilliseconds + 200));
            Assert.Equal(1, runs);
        });
    }

    [Fact]
    public void TheCallbackArrivingFirstStopsTheTimer()
    {
        OnDispatcherThread(() =>
        {
            int runs = 0;
            var completion = new FadeCompletion(() => runs++);
            completion.ArmFallback(Dispatcher.CurrentDispatcher);
            completion.Complete();
            Pump(() => false, TimeSpan.FromMilliseconds(FadeCompletion.Fallback.TotalMilliseconds + 200));
            Assert.Equal(1, runs);
        });
    }
}
