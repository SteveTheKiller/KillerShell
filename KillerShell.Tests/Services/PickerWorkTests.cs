using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

public sealed class PickerWorkTests
{
    [Fact]
    public async Task HungReadsThatWereSupersededDoNotStarveTheNextNavigation()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();

        using var unreachable = new ManualResetEventSlim(false);
        try
        {
            using var work = new PickerWork(dispatcher!);
            // Two folders on a dead share: each read blocks, ignoring its token, as a native
            // listing on an unreachable share does. Each new navigation supersedes the last.
            _ = work.RunAsync(_ => { unreachable.Wait(); return 1; }, (_, _) => Task.CompletedTask);
            _ = work.RunAsync(_ => { unreachable.Wait(); return 2; }, (_, _) => Task.CompletedTask);

            var published = new TaskCompletionSource<int>();
            var third = work.RunAsync(_ => 3, (value, _) => { published.TrySetResult(value); return Task.CompletedTask; });

            var winner = await Task.WhenAny(published.Task, Task.Delay(5000));
            Assert.True(winner == published.Task, "The third navigation waited behind two abandoned reads.");
            Assert.Equal(3, await published.Task);
            await third;
        }
        finally
        {
            unreachable.Set();
            dispatcher!.InvokeShutdown();
        }
    }
}
