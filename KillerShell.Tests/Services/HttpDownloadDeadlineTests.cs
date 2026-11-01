using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

public sealed class HttpDownloadDeadlineTests
{
    // A body whose reads never finish and ignore the token, as the .NET Framework response stream does.
    private sealed class StalledStream : Stream
    {
        private readonly TaskCompletionSource<int> never = new TaskCompletionSource<int>();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => never.Task.GetAwaiter().GetResult();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => never.Task;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StalledHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) });
    }

    [Fact]
    public async Task StalledBodyEndsAtTheDeadline()
    {
        using var http = new HttpClient(new StalledHandler());
        var watch = Stopwatch.StartNew();
        var copy = HttpDownload.CopyToAsync(http, new Uri("https://example.invalid/update.exe"), new MemoryStream(), TimeSpan.FromMilliseconds(300));
        var finished = await Task.WhenAny(copy, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(copy, finished);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => copy);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }
}
