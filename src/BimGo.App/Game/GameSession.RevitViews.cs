using System.Numerics;
using BimGo.Audio;
using BimGo.Live;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// Round X-E: Revit views from where the player stands (live sessions). NEW REVIT VIEW (Shift+F5, the pause menu,
    /// or Revit's "New view from BimGo") makes a perspective 3D view "BimGo NN" with this camera and opens it in Revit;
    /// UPDATE REVIT VIEW (Alt+F5, the pause menu, or Revit's "Update view from BimGo") puts this camera on Revit's
    /// active perspective view. Each is one Revit transaction (one undo entry). Revit's answer arrives as a toast.
    /// (Build r1's live camera sync put every move on Revit's undo list and was dropped.)
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Fields

        private readonly RevitViewPayload _revitView = new();

        #endregion

        #region Actions

        /// <summary>
        /// Sends the walkthrough camera to Revit: a new view, or the active view updated.
        /// </summary>
        /// <param name="action">New or update.</param>
        /// <param name="askedInRevit">True when Revit's ribbon asked for it.</param>
        /// <param name="sound">False when a menu button already clicked.</param>
        private void SendRevitView(RevitViewAction action, bool askedInRevit = false, bool sound = true)
        {
            if (_live == null)
            {
                Toast("Revit views need a live Revit session (press Go in Revit)");
                return;
            }
            if (!_live.Connected)
            {
                Toast("Not connected to Revit", important: true);
                return;
            }
            if (!_live.RevitViewsSupported)
            {
                Toast("Revit views from BimGo need the BimGo add-in from build X-E or later in Revit", 4f, important: true);
                return;
            }

            FillRevitView(_revitView, action, askedInRevit);
            if (!_live.MakeRevitView(_revitView))
            {
                Toast("Not connected to Revit", important: true);
                return;
            }
            if (sound) { Sound.Play(SoundId.UiClick); }
            Toast(action == RevitViewAction.Update ? "Updating Revit's active view to here…" : "Making a Revit view from here…");
        }

        /// <summary>
        /// Per frame (with the other host work): a view Revit's ribbon asked for is answered with the current camera.
        /// </summary>
        private void UpdateRevitViews()
        {
            if (_live == null) { return; }
            while (_live.TryTakeViewRequest(out RevitViewAction action))
            {
                SendRevitView(action, askedInRevit: true, sound: false);
            }
        }

        /// <summary>
        /// The walkthrough camera as a view request (Revit internal metres; the rendered camera, so a playing tour or
        /// path counts; no roll).
        /// </summary>
        private void FillRevitView(RevitViewPayload payload, RevitViewAction action, bool askedInRevit)
        {
            Vector3 origin = Scene.OriginOffset;
            Vector3 forward = Camera.Forward;
            Vector3 up = RevitViews.LevelUp(forward);
            payload.Action = action;
            payload.AskedInRevit = askedInRevit;
            payload.EyeX = (double)Camera.Position.X + origin.X;
            payload.EyeY = (double)Camera.Position.Y + origin.Y;
            payload.EyeZ = (double)Camera.Position.Z + origin.Z;
            payload.ForwardX = forward.X;
            payload.ForwardY = forward.Y;
            payload.ForwardZ = forward.Z;
            payload.UpX = up.X;
            payload.UpY = up.Y;
            payload.UpZ = up.Z;
            payload.HorizontalFov = Camera.HorizontalFovDegrees;
            payload.Aspect = Camera.Aspect;
        }

        #endregion
    }
}
