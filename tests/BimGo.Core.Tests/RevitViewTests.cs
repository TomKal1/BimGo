using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using BimGo.Live;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// The class belongs to the Tests namespace
namespace BimGo.Tests
{
    /// <summary>
    /// Round X-E: Revit views from BimGo's camera: the "BimGo NN" names, the no-roll up vector, the payload's checks and
    /// the new messages through a <see cref="FolderChannel"/> pair (temp folders).
    /// </summary>
    [TestClass]
    public sealed class RevitViewTests
    {
        private const string SESSION = "fedcba9876543210fedcba9876543210";

        [TestMethod]
        public void NextViewName_CountsOnFromTheHighest()
        {
            Assert.AreEqual("BimGo 01", RevitViews.NextViewName(null));
            Assert.AreEqual("BimGo 01", RevitViews.NextViewName(new[] { "{3D}", "BimGo view", "BimGo clash", "Level 1" }));
            Assert.AreEqual("BimGo 03", RevitViews.NextViewName(new[] { "BimGo 01", "bimgo 02" }));
            Assert.AreEqual("BimGo 08", RevitViews.NextViewName(new[] { "BimGo 01", "BimGo 07" }), "gaps aren't refilled");
            Assert.AreEqual("BimGo 100", RevitViews.NextViewName(new[] { "BimGo 99" }));
            Assert.AreEqual("BimGo 01", RevitViews.NextViewName(new[] { "BimGo -3", "BimGo 1.5", "BimGo 2b", "BimGo " }));
        }

        [TestMethod]
        public void LevelUp_HasNoRoll()
        {
            Assert.AreEqual(1f, RevitViews.LevelUp(new Vector3(1f, 1f, 0f)).Z, 1e-5f);

            Vector3 tilted = Vector3.Normalize(new Vector3(1f, 0f, -1f));
            Vector3 up = RevitViews.LevelUp(tilted);
            Assert.AreEqual(0f, Vector3.Dot(up, tilted), 1e-5f, "perpendicular to the view");
            Assert.AreEqual(0f, up.Y, 1e-5f, "no roll");
            Assert.IsTrue(up.Z > 0f);

            Assert.AreEqual(1f, RevitViews.LevelUp(-Vector3.UnitZ).Length(), 1e-5f, "straight down still has an up");
        }

        [TestMethod]
        public void Payload_RefusesNonFiniteValuesAndNoDirection()
        {
            Assert.IsTrue(new RevitViewPayload { ForwardX = 1 }.IsValid());
            Assert.IsFalse(new RevitViewPayload().IsValid(), "no direction");
            Assert.IsFalse(new RevitViewPayload { ForwardX = 1, EyeZ = double.NaN }.IsValid());
            Assert.IsFalse(new RevitViewPayload { ForwardX = double.PositiveInfinity }.IsValid());
            Assert.IsFalse(new RevitViewPayload { ForwardX = 1, HorizontalFov = double.NaN }.IsValid());
        }

        [TestMethod]
        public void Messages_TravelBothWays_AndOlderAddInsSayNothing()
        {
            using var folder = new TempFolder();
            string toRevit = Path.Combine(folder.Path, "to-revit"), toApp = Path.Combine(folder.Path, "to-app");
            using var app = new FolderChannel(SESSION, toRevit, toApp, "test-app");
            using var revit = new FolderChannel(SESSION, toApp, toRevit, "test-revit");

            revit.Send(MessageTypes.VIEW_REQUEST, new RevitViewRequestPayload { Action = RevitViewAction.Update });
            revit.Send(MessageTypes.HELLO_ACK, new HelloAckPayload { DocTitle = "Old add-in" });
            revit.Send(MessageTypes.HELLO_ACK, new HelloAckPayload { DocTitle = "X-E add-in", RevitViews = true });
            List<Envelope> atApp = Receive(app);
            Assert.AreEqual(3, atApp.Count);
            Assert.AreEqual(RevitViewAction.Update, atApp[0].Read<RevitViewRequestPayload>().Action);
            Assert.IsFalse(atApp[1].Read<HelloAckPayload>().RevitViews, "an older add-in leaves it out");
            Assert.IsTrue(atApp[2].Read<HelloAckPayload>().RevitViews);

            var sent = new RevitViewPayload
            {
                Action = RevitViewAction.Create, AskedInRevit = true, EyeX = 12.5, EyeY = -3, EyeZ = 1.65,
                ForwardX = 0.6, ForwardY = 0.8, UpZ = 1, HorizontalFov = 75, Aspect = 1.777
            };
            app.Send(MessageTypes.VIEW_MAKE, sent);
            List<Envelope> atRevit = Receive(revit);
            Assert.AreEqual(1, atRevit.Count);
            RevitViewPayload view = atRevit[0].Read<RevitViewPayload>();
            Assert.AreEqual(RevitViewAction.Create, view.Action);
            Assert.IsTrue(view.AskedInRevit);
            Assert.AreEqual(12.5, view.EyeX);
            Assert.AreEqual(0.8, view.ForwardY);
            Assert.AreEqual(75, view.HorizontalFov);
            Assert.AreEqual(1.777, view.Aspect);
            Assert.IsTrue(view.IsValid());
        }

        private static List<Envelope> Receive(FolderChannel channel)
        {
            channel.Scan();
            var received = new List<Envelope>();
            while (channel.TryReceive(out Envelope envelope)) { received.Add(envelope); }
            return received;
        }
    }
}
