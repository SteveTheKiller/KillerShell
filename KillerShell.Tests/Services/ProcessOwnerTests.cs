using System.Collections.Generic;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services
{
    public sealed class ProcessOwnerTests
    {
        [Theory]
        [InlineData(2000L)]
        [InlineData(null)]
        public void ChangedOrMissingCreationIdentityDoesNotQueryOwner(long? actual)
        {
            bool queried = false;
            string owner = ProcessListQueryService.ReadOwnerForIdentity(1000, () => actual,
                () => { queried = true; return "DOMAIN\\replacement"; });
            Assert.Equal("-", owner);
            Assert.False(queried);
        }

        [Theory]
        [InlineData(2000L)]
        [InlineData(null)]
        public void ProcessReplacementDuringQueryDoesNotPublishOwner(long? after)
        {
            var identities = new Queue<long?>(new long?[] { 1000, after });
            Assert.Equal("-", ProcessListQueryService.ReadOwnerForIdentity(1000, () => identities.Dequeue(),
                () => "DOMAIN\\replacement"));
        }

        [Fact]
        public void StableCreationIdentityPublishesOwner()
            => Assert.Equal("DOMAIN\\original", ProcessListQueryService.ReadOwnerForIdentity(1009,
                () => 1000, () => "DOMAIN\\original"));
    }
}
