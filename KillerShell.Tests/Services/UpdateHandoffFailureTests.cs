using System;
using System.ComponentModel;
using System.IO;
using System.Security;
using KillerShell.Features.About;
using Xunit;

namespace KillerShell.Tests.Services;

public sealed class UpdateHandoffFailureTests
{
    [Fact]
    public void FailedHandoffOffersTheManualDownloadButDeclinedUacDoesNot()
    {
        Assert.True(AboutController.OffersManualDownload(new SecurityException("portable copy in a protected folder")));
        Assert.True(AboutController.OffersManualDownload(new IOException("The update helper did not start.")));
        Assert.True(AboutController.OffersManualDownload(new Win32Exception(5)));
        Assert.False(AboutController.OffersManualDownload(new Win32Exception(1223)));
        Assert.True(AboutController.OffersManualDownload(KillerShell.Services.TrustedUpdate.FailureFor(1625)));
    }

    // The elevated helper reports why it stopped only as an installer exit code. The dialog says
    // what that code means in the interface language, never Windows' text for the number, which
    // read "This installation is forbidden by system policy" for a refused update and
    // "Incorrect function." for any other failure.
    [Theory]
    [InlineData(1625, "Str_About_UpdateHelperRefused")]
    [InlineData(1618, "Str_About_UpdateHelperBusy")]
    [InlineData(112, "Str_About_UpdateHelperDiskFull")]
    [InlineData(5, "Str_About_UpdateHelperDenied")]
    [InlineData(1, "Str_About_UpdateHelperFailed")]
    [InlineData(1603, "Str_About_UpdateHelperFailed")]
    public void AHelperExitCodeIsExplainedInTheInterfaceLanguage(int code, string key)
    {
        Exception failure = KillerShell.Services.TrustedUpdate.FailureFor(code);
        Assert.IsNotType<Win32Exception>(failure);
        string reason = AboutController.HandoffReason(failure, k => "<" + k + ">");
        Assert.StartsWith("<" + key + ">", reason);
    }

    [Fact]
    public void AnyOtherFailureKeepsItsOwnMessage()
        => Assert.Equal("The update helper did not start.",
            AboutController.HandoffReason(new IOException("The update helper did not start."), k => "<" + k + ">"));
}
