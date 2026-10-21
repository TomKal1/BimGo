using BimGo.Scene;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// The class belongs to the Tests namespace
namespace BimGo.Tests
{
    /// <summary>
    /// Round X-D0: the frame rate limit defaults to 30 and a hand-edited value snaps to a choice.
    /// </summary>
    [TestClass]
    public sealed class FrameRateLimitTests
    {
        [TestMethod]
        public void Default_Is30()
        {
            Assert.AreEqual(30, new LaunchSettings().FrameRateLimit);
        }

        [DataTestMethod]
        [DataRow(0, 0)]
        [DataRow(-5, 0)]
        [DataRow(1, 30)]
        [DataRow(30, 30)]
        [DataRow(40, 45)]
        [DataRow(45, 45)]
        [DataRow(144, 60)]
        public void Nearest_SnapsToAChoice(int value, int expected)
        {
            Assert.AreEqual(expected, LaunchSettings.NearestFrameRateLimit(value));
        }
    }
}
