using System;
using BimGo.Live;
using BimGo.Scene;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// The class belongs to the Tests namespace
namespace BimGo.Tests
{
    /// <summary>
    /// Round Z-E (installer and first run): the add-in / app version check and safe start.
    /// </summary>
    [TestClass]
    public sealed class ReleaseTests
    {
        [TestMethod]
        public void VersionCheck_ComparesReleasesOnly()
        {
            Assert.IsTrue(VersionCheck.SameRelease("1.0.0", "1.0.0.0"), "the fourth part is ignored");
            Assert.IsTrue(VersionCheck.SameRelease("1.2.3+abc123", "1.2.3"), "a build suffix is ignored");
            Assert.IsFalse(VersionCheck.SameRelease("1.0.0", "1.0.1"));
            Assert.IsFalse(VersionCheck.SameRelease("1.1.0", "1.0.0"));
            Assert.IsTrue(VersionCheck.SameRelease("", "1.0.0"), "unknown: nothing to warn about");
            Assert.IsTrue(VersionCheck.SameRelease(null, "nonsense"));
            Assert.AreEqual("1.0.0", VersionCheck.Release("1.0.0.0"));
            StringAssert.Contains(VersionCheck.Mismatch("1.0.1", "1.0.0.0"), "BimGo for Revit is version 1.0.1 and the BimGo app is version 1.0.0");
        }

        [TestMethod]
        public void SafeStart_GivesBasicDefaults_AndSavesNothing()
        {
            try
            {
                LaunchSettings.SafeStart = true;
                LaunchSettings settings = LaunchSettings.LoadOrDefault();
                Assert.AreEqual(QualityProfile.Basic, QualityProfiles.Detect(settings));
                Assert.IsFalse(settings.WelcomeShown);
                settings.Save(); // a no-op: nothing is written (logged only)
            }
            finally
            {
                LaunchSettings.SafeStart = false;
            }
        }
    }
}
