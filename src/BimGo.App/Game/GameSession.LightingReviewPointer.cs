using System.Numerics;
using BimGo.Platform;
using BimGo.Rendering;
using BimGo.Scene;

namespace BimGo.Game
{
    /// <summary>Model navigation gestures are owned from press to release; a pan can never select/reframe a room.</summary>
    internal sealed partial class GameSession
    {
        private readonly ReviewPointerDrag _reviewPan = new();
        private bool _reviewOrbitHeld, _reviewLookHeld;
        private Vector2 _reviewOrbitLast, _reviewLookLast;
        private float _reviewPanScale, _reviewRightPanScale;

        private void ResetReviewPointer()
        {
            _reviewPan.Cancel();
            _reviewOrbitHeld = _reviewLookHeld = _reviewCubeDrag = false;
        }

        /// <summary>Perspective pans use a stable grabbed depth, captured once; parallel pans use metres per pixel.</summary>
        private float ReviewPanUnits(Vector2 mouse)
        {
            if (Camera.IsOrthographic) { return Camera.PixelScale; }
            ScreenRay(mouse, out Vector3 origin, out Vector3 direction);
            float depth = Pick(origin, direction, FpsCamera.FAR, out var hit)
                ? Vector3.Dot(hit.Point - Camera.Position, Camera.Forward)
                : Vector3.Dot(_reviewBounds.Center - Camera.Position, Camera.Forward);
            return Camera.PixelScale * Math.Clamp(MathF.Abs(depth), 0.5f, 1000f);
        }

        private void ReviewPanPixels(Vector2 delta, float unitsPerPixel)
        {
            Vector3 move = ReviewNavigation.Pan(delta, Camera.Right, Camera.Forward, unitsPerPixel);
            if (_reviewView == 3) { _reviewEye += move; }
            else { _reviewTarget += move; }
        }

        /// <summary>Middle drag orbits parallel views and looks in place in fly mode; it never changes the fly eye.</summary>
        private void ReviewRotatePixels(Vector2 delta)
        {
            if (_reviewView is 1 or 2)
            {
                (_reviewYaw, _reviewPitch) = ReviewNavigation.Angles(-Camera.Forward);
                _reviewView = 0;
            }
            _reviewYaw -= delta.X * 0.005f * _sensitivity;
            _reviewPitch = Math.Clamp(_reviewPitch - delta.Y * 0.005f * _sensitivity, -1.553f, 1.553f);
        }

        /// <summary>Returns whether model keyboard navigation may continue this frame.</summary>
        private bool UpdateReviewPointer(InputState input)
        {
            Vector2 mouse = input.MousePosition;
            bool overDock = mouse.X >= _reviewPanelRect.X && mouse.X < _reviewPanelRect.X + _reviewPanelRect.Z
                && mouse.Y >= _reviewPanelRect.Y && mouse.Y < _reviewPanelRect.Y + _reviewPanelRect.W;
            if (OverlayOpen) { ResetReviewPointer(); return false; }

            // An existing drag owns release even outside the viewport, so no dock/cube action steals it.
            if (_reviewOrbitHeld)
            {
                if (input.Down(InputAction.ReviewOrbit)) { ReviewRotatePixels(mouse - _reviewOrbitLast); _reviewOrbitLast = mouse; }
                else { _reviewOrbitHeld = false; }
                input.ConsumeClicks();
                return true;
            }
            if (_reviewPan.Active)
            {
                (Vector2 delta, bool click) = _reviewPan.Step(mouse, input.LeftDown);
                ReviewPanPixels(delta, _reviewPanScale);
                if (click && !overDock && !OverReviewCube(mouse)) { ReviewPickAt(mouse); }
                input.ConsumeClicks();
                return true;
            }
            if (_reviewLookHeld && input.Down(InputAction.DragLook))
            {
                Vector2 delta = mouse - _reviewLookLast;
                if (input.Down(InputAction.Run) || _reviewView is 1 or 2) { ReviewPanPixels(delta, _reviewRightPanScale); }
                else { ReviewRotatePixels(delta); }
                _reviewLookLast = mouse;
                input.ConsumeClicks();
                return true;
            }
            _reviewLookHeld = false;
            if (_sectionDrag >= 0)
            {
                if (input.LeftDown) { DragHandle(mouse, input.Down(InputAction.Run)); }
                else { _sectionDrag = -1; }
                return false;
            }
            if (UpdateReviewCube(input)) { return false; }
            if (overDock) { _reviewLookHeld = false; return false; }

            if (input.Pressed(InputAction.ReviewOrbit))
            {
                _reviewOrbitHeld = input.Down(InputAction.ReviewOrbit);
                _reviewOrbitLast = mouse;
                _reviewLookHeld = false;
                input.ConsumeClicks();
                return true;
            }
            _sectionHover = _reviewEditBox ? HandleUnder(mouse) : -1;
            if (input.LeftPressed)
            {
                _reviewLookHeld = false;
                if (_sectionHover >= 0) { BeginDrag(_sectionHover, mouse); }
                else
                {
                    _reviewPanScale = ReviewPanUnits(input.LeftPressPosition);
                    _reviewPan.Begin(input.LeftPressPosition, S(4));
                    // A fast click may already have released before this frame was polled.
                    (Vector2 delta, bool click) = _reviewPan.Step(mouse, input.LeftDown);
                    ReviewPanPixels(delta, _reviewPanScale);
                    if (click) { ReviewPickAt(mouse); }
                }
                input.ConsumeClicks();
                return true;
            }

            // Keep the existing right-button gesture; seed its position on press to avoid a stale raw-delta jump.
            if (input.Down(InputAction.DragLook))
            {
                _reviewLookHeld = true;
                _reviewLookLast = mouse;
                _reviewRightPanScale = ReviewPanUnits(mouse);
            }
            return true;
        }

        /// <summary>Only a released, stationary click selects; fly destinations are chosen explicitly from the list.</summary>
        private void ReviewPickAt(Vector2 mouse)
        {
            if (TryNativeCalculationPick(mouse)) return;
            if (_reviewView == 3 && !_reviewPin) { return; }
            ScreenRay(mouse, out Vector3 origin, out Vector3 direction);
            if (!Pick(origin, direction, FpsCamera.FAR, out var hit)) { return; }
            if (_reviewPin)
            {
                _reviewPin = false;
                long id = hit.Element >= 0 && hit.Element < Scene.Elements.Length ? Scene.Elements[hit.Element].ElementId : 0;
                BeginCommentEdit(hit.Point, id, LevelNameAt(hit.Point.Z));
                return;
            }
            Vector3 p = hit.Point + hit.Normal * 0.06f;
            foreach (ReviewRoom room in _reviewRooms)
            {
                bool contains = room.Zone != null ? room.Zone.Contains(p)
                    : p.Z >= room.Bounds.Min.Z - 0.1f && p.Z <= room.Bounds.Max.Z + 0.1f && Scene.Rooms[room.Room].Contains(new Vector2(p.X, p.Y));
                if (contains) { ReviewSelectRoom(room); break; }
            }
        }
    }
}
