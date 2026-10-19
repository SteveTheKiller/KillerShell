using System;
using System.IO;
using Xunit;

namespace KillerShell.Tests.Services
{
    public sealed class CrashLogTests
    {
        [Fact]
        public void CrashLog_RecordsTheExceptionAndKeepsOnlyTheNewestHalfWhenLong()
        {
            string root = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", Guid.NewGuid().ToString("N"));
            string log = Path.Combine(root, "crash.log");
            try
            {
                App.WriteCrashLog("dispatcher", new InvalidOperationException("first failure"), log);
                string text = File.ReadAllText(log);
                Assert.Contains("dispatcher", text);
                Assert.Contains("first failure", text);

                for (int i = 0; i < 400; i++)
                    App.WriteCrashLog("appdomain", new Exception("filler " + i + new string('x', 1000)), log);
                App.WriteCrashLog("dispatcher", new Exception("latest failure"), log);

                text = File.ReadAllText(log);
                Assert.True(new FileInfo(log).Length < 400 * 1024);
                Assert.DoesNotContain("first failure", text);
                Assert.Contains("latest failure", text);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
    }
}
