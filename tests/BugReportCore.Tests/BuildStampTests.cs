using System;
using System.IO;
using NUnit.Framework;
using UnturnedGodot.BugReport;

namespace BugReportCore.Tests
{
    /// <summary>A relocatable package can sit inside an unrelated checkout (bevy/dist under the repo, a launcher
    /// folder in a git tree). Its bug reports must carry the commit it was packaged from, never the HEAD of
    /// whatever repository happens to enclose it.</summary>
    [TestFixture, NonParallelizable]
    public class BuildStampTests
    {
        string root;

        [SetUp]
        public void Setup()
        {
            BuildStamp.Reset();
            root = Path.Combine(Path.GetTempPath(), "buildstamp-" + Guid.NewGuid().ToString("N"));
            // An enclosing checkout at a detached HEAD, with the package two folders below it.
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            File.WriteAllText(Path.Combine(root, ".git", "HEAD"), "0123456789abcdef0123456789abcdef01234567\n");
            Directory.CreateDirectory(Path.Combine(root, "dist", "unturned-bevy"));
        }

        [TearDown]
        public void Cleanup()
        {
            BuildStamp.Reset();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }

        [Test]
        public void ACheckoutRunReadsTheEnclosingHead()
        {
            BuildStamp.ProjectDir = Path.Combine(root, "dist", "unturned-bevy");
            Assert.That(BuildStamp.Commit, Is.EqualTo("0123456789ab"));
        }

        [Test]
        public void APackageReportsItsRecordedCommitNotTheEnclosingCheckout()
        {
            BuildStamp.ProjectDir = Path.Combine(root, "dist", "unturned-bevy");
            BuildStamp.UsePackagedCommit("5707bbba9760+dirty");
            Assert.That(BuildStamp.Commit, Is.EqualTo("5707bbba9760+dirty"));
        }

        [Test]
        public void APackageWithoutARecordedCommitIsUnknownNotTheCheckoutHead()
        {
            BuildStamp.ProjectDir = Path.Combine(root, "dist", "unturned-bevy");
            _ = BuildStamp.Commit;   // a stamp cached before the host configured it must not survive
            BuildStamp.UsePackagedCommit(null);
            Assert.That(BuildStamp.Commit, Is.EqualTo("unknown"));
        }
    }
}
