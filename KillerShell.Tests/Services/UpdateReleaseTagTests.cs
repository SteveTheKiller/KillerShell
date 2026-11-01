using System;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

public sealed class UpdateReleaseTagTests
{
    private static readonly Version Current = new(1, 5, 0);

    [Fact]
    public void StableReleaseNewerThanCurrentIsOffered()
        => Assert.Equal("v1.5.1", UpdateService.NewerReleaseTag("{\"tag_name\":\"v1.5.1\",\"draft\":false,\"prerelease\":false}", Current));

    [Fact]
    public void TagIsReadWhateverTheFieldOrder()
        => Assert.Equal("v1.6.0", UpdateService.NewerReleaseTag("{\"body\":\"notes\",\"assets\":[{\"name\":\"a\"}],\"prerelease\":false,\"tag_name\":\"v1.6.0\"}", Current));

    [Fact]
    public void ReleaseNotesQuotingFieldNamesDoNotFoolTheRead()
    {
        string body = "Notes quote \\\"tag_name\\\": \\\"v9.9.9\\\" and \\\"prerelease\\\": true and a brace } and [ here.";
        Assert.Equal("v1.5.1", UpdateService.NewerReleaseTag("{\"body\":\"" + body + "\",\"tag_name\":\"v1.5.1\"}", Current));
        Assert.Equal("v1.5.1", UpdateService.NewerReleaseTag("{\"tag_name\":\"v1.5.1\",\"body\":\"" + body + "\"}", Current));
    }

    [Fact]
    public void FieldsOfNestedObjectsAreNotTheReleasesOwn()
    {
        string json = "{\"author\":{\"tag_name\":\"v9.9.9\",\"draft\":true,\"prerelease\":true},\"assets\":[{\"draft\":true}],\"tag_name\":\"v1.5.1\"}";
        Assert.Equal("v1.5.1", UpdateService.NewerReleaseTag(json, Current));
    }

    [Fact]
    public void EscapedTagNameIsDecoded()
        => Assert.Equal("v1.5.1", UpdateService.NewerReleaseTag("{\"tag_name\":\"\\u0076\\u0031.5.1\"}", Current));

    [Theory]
    [InlineData("{\"tag_name\":\"v1.5.1\",\"draft\":true}")]
    [InlineData("{\"prerelease\":true,\"tag_name\":\"v1.5.1\"}")]
    [InlineData("{\"tag_name\":\"v1.5.1\",\"prerelease\": true }")]
    public void DraftAndPrereleaseAreNeverOffered(string json)
        => Assert.Null(UpdateService.NewerReleaseTag(json, Current));

    [Theory]
    [InlineData("{\"tag_name\":\"v1.5.0\"}")]
    [InlineData("{\"tag_name\":\"v1.4.9\"}")]
    [InlineData("{\"tag_name\":\"v1.6.0-beta\"}")]
    [InlineData("{\"tag_name\":\"1.6\"}")]
    [InlineData("{\"tag_name\":1.6}")]
    [InlineData("{\"name\":\"v1.6.0\"}")]
    [InlineData("{\"tag_name\":\"v1.6.0\"")]
    [InlineData("{\"tag_name\":\"v1.6.0\"} trailing")]
    [InlineData("[\"tag_name\",\"v1.6.0\"]")]
    [InlineData("")]
    public void NothingNewerOrUnreadableIsNotOffered(string json)
        => Assert.Null(UpdateService.NewerReleaseTag(json, Current));
}
