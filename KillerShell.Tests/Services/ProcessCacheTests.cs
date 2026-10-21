using System;
using System.Collections.Generic;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services
{
    public sealed class ProcessCacheTests
    {
        private static readonly DateTime SampleTime = new(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);
        [Fact]
        public void ReusedPidGetsFreshCpuOwnerAndReadability()
        {
            var cache = new ProcessListLogic.ProcessCache();
            var first = cache.Observe(42, 1000);
            first.SampleCpu(TimeSpan.FromSeconds(1), SampleTime, 1);
            first.Unreadable = true;
            Assert.True(cache.BeginOwnerLookup(first));
            cache.CompleteOwnerLookup(first, "DOMAIN\\first");

            var replacement = cache.Observe(42, 2000);

            Assert.False(replacement.Unreadable);
            Assert.Equal(0, replacement.SampleCpu(TimeSpan.FromSeconds(50), SampleTime.AddSeconds(1), 1));
            Assert.Equal("-", cache.Owner(replacement));
            Assert.True(cache.BeginOwnerLookup(replacement));
            cache.CompleteOwnerLookup(replacement, "DOMAIN\\replacement");
            Assert.Equal("DOMAIN\\replacement", cache.Owner(replacement));
        }

        [Fact]
        public void OldOwnerCompletionCannotChangeReplacementOrItsPendingRequest()
        {
            var cache = new ProcessListLogic.ProcessCache();
            var first = cache.Observe(42, 1000);
            Assert.True(cache.BeginOwnerLookup(first));
            var replacement = cache.Observe(42, 2000);
            Assert.True(cache.BeginOwnerLookup(replacement));

            Assert.False(cache.CompleteOwnerLookup(first, "DOMAIN\\first"));

            Assert.Equal("-", cache.Owner(replacement));
            Assert.False(cache.BeginOwnerLookup(replacement));
            Assert.True(cache.CompleteOwnerLookup(replacement, "DOMAIN\\replacement"));
            Assert.Equal("DOMAIN\\replacement", cache.Owner(replacement));
            Assert.False(cache.IsCurrent(first));
        }

        [Fact]
        public void UnknownIdentityDoesNotCarryCachedDataAcrossRefreshes()
        {
            var cache = new ProcessListLogic.ProcessCache();
            var first = cache.Observe(42, null);
            first.Unreadable = true;
            Assert.False(cache.BeginOwnerLookup(first));
            Assert.False(cache.Observe(42, null).Unreadable);
        }

        [Fact]
        public void StableIdentityKeepsItsCpuBaselineAndOwnerLookup()
        {
            var cache = new ProcessListLogic.ProcessCache();
            var first = cache.Observe(42, 1000);
            first.SampleCpu(TimeSpan.FromSeconds(1), SampleTime, 2);
            Assert.True(cache.BeginOwnerLookup(first));
            var second = cache.Observe(42, 1009);
            Assert.Same(first, second);
            Assert.Equal(25, second.SampleCpu(TimeSpan.FromSeconds(1.5), SampleTime.AddSeconds(1), 2));
            Assert.False(cache.BeginOwnerLookup(second));
            cache.CompleteOwnerLookup(first, "DOMAIN\\first");
            Assert.Equal("DOMAIN\\first", cache.Owner(second));
        }

        [Fact]
        public void RemovedProcessRejectsLateOwnerCompletion()
        {
            var cache = new ProcessListLogic.ProcessCache();
            var first = cache.Observe(42, 1000);
            cache.BeginOwnerLookup(first);
            cache.ForgetExcept(new HashSet<int>());
            Assert.False(cache.CompleteOwnerLookup(first, "DOMAIN\\first"));
            Assert.False(cache.IsCurrent(first));
        }
    }
}
