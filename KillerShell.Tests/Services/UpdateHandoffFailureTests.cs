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
    }
}
