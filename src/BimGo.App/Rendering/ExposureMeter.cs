using BimGo.Native;

// The class belongs to the Rendering namespace
namespace BimGo.Rendering
{
    /// <summary>
    /// Auto exposure's light meter: the main view's scene colour (before the UI), resolved, shrunk to
    /// <see cref="WIDTH"/> × <see cref="HEIGHT"/> and copied into one of <see cref="RING"/> pixel buffers; the oldest buffer
    /// (a few frames back, long finished on the GPU) is read on the CPU, so the meter never waits for the frame being drawn.
    /// The reading is the mean luma (0–1) of those pixels as displayed, or NaN while there is none yet. It runs every
    /// <see cref="EVERY"/> frames (the exposure eases over half a second, so more often only costs time).
    /// </summary>
    internal sealed unsafe class ExposureMeter : IDisposable
    {
        /// <summary>The shrunk picture's size (pixels): enough samples for an average, a few KB to read.</summary>
        public const int WIDTH = 64, HEIGHT = 36;

        private const int RING = 4;

        /// <summary>Frames between readings.</summary>
        public const int EVERY = 4;

        private int _frame;
        private const uint PIXEL_PACK_BUFFER = 0x88EB, STREAM_READ = 0x88E1;

        private uint _resolveFbo, _resolveColour, _smallFbo, _smallColour;
        private int _width, _height;
        private readonly uint[] _buffers = new uint[RING];
        private readonly bool[] _filled = new bool[RING];
        private int _slot;
        private bool _failed;
        private readonly byte[] _pixels = new byte[WIDTH * HEIGHT * 4];

        /// <summary>
        /// Every <see cref="EVERY"/> frames, copies this frame's scene into the ring and returns the oldest copy's mean
        /// luma; NaN on the frames between, when there is no copy yet, or when the meter can't run on this GPU. Rebinds
        /// <paramref name="target"/> afterwards.
        /// </summary>
        public float Measure(RenderTarget target)
        {
            if (_failed || target.Width <= 0 || target.Height <= 0) { return float.NaN; }
            if (++_frame < EVERY) { return float.NaN; }
            _frame = 0;
            if (!Ensure(target.Width, target.Height))
            {
                target.Bind();
                return float.NaN;
            }

            // This frame: resolve (an MSAA target can't be scaled while it is resolved), shrink, start the copy
            target.ResolveColourTo(_resolveFbo, _width, _height);
            Gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, _resolveFbo);
            Gl.BindFramebuffer(Gl.DRAW_FRAMEBUFFER, _smallFbo);
            Gl.BlitFramebuffer(0, 0, _width, _height, 0, 0, WIDTH, HEIGHT, Gl.COLOR_BUFFER_BIT, Gl.LINEAR);
            Gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, _smallFbo);
            Gl.ReadBuffer(Gl.COLOR_ATTACHMENT0);
            Gl.PixelStore(Gl.PACK_ALIGNMENT, 1);
            Gl.BindBuffer(PIXEL_PACK_BUFFER, _buffers[_slot]);
            Gl.ReadPixels(0, 0, WIDTH, HEIGHT, Gl.BGRA, Gl.UNSIGNED_BYTE, null); // into the buffer, not CPU memory
            _filled[_slot] = true;
            _slot = (_slot + 1) % RING;

            // The oldest copy (the slot written next)
            float mean = float.NaN;
            if (_filled[_slot])
            {
                fixed (byte* p = _pixels) { Gl.GetBufferSubData(PIXEL_PACK_BUFFER, 0, _pixels.Length, p); }
                double sum = 0.0;
                for (int i = 0; i < _pixels.Length; i += 4)
                {
                    sum += 0.0722 * _pixels[i] + 0.7152 * _pixels[i + 1] + 0.2126 * _pixels[i + 2]; // BGRA
                }
                mean = (float)(sum / (WIDTH * HEIGHT * 255.0));
            }
            Gl.BindBuffer(PIXEL_PACK_BUFFER, 0);
            Gl.PixelStore(Gl.PACK_ALIGNMENT, 4);
            Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
            target.Bind();
            return mean;
        }

        /// <summary>Forgets the copies in flight (a jump the reading shouldn't carry over).</summary>
        public void Reset() => Array.Clear(_filled);

        private bool Ensure(int width, int height)
        {
            if (_resolveFbo != 0 && width == _width && height == _height) { return true; }
            Release();
            while (Gl.GetError() != Gl.NO_ERROR) { /* clear stale errors */ }

            _resolveColour = Gl.GenRenderbuffer();
            Gl.BindRenderbuffer(Gl.RENDERBUFFER, _resolveColour);
            Gl.RenderbufferStorageMultisample(Gl.RENDERBUFFER, 0, Gl.RGBA8, width, height);
            _resolveFbo = Gl.GenFramebuffer();
            Gl.BindFramebuffer(Gl.FRAMEBUFFER, _resolveFbo);
            Gl.FramebufferRenderbuffer(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.RENDERBUFFER, _resolveColour);
            bool complete = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER) == Gl.FRAMEBUFFER_COMPLETE;

            _smallColour = Gl.GenRenderbuffer();
            Gl.BindRenderbuffer(Gl.RENDERBUFFER, _smallColour);
            Gl.RenderbufferStorageMultisample(Gl.RENDERBUFFER, 0, Gl.RGBA8, WIDTH, HEIGHT);
            _smallFbo = Gl.GenFramebuffer();
            Gl.BindFramebuffer(Gl.FRAMEBUFFER, _smallFbo);
            Gl.FramebufferRenderbuffer(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.RENDERBUFFER, _smallColour);
            complete &= Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER) == Gl.FRAMEBUFFER_COMPLETE;
            Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
            Gl.BindRenderbuffer(Gl.RENDERBUFFER, 0);

            for (int i = 0; i < RING; i++)
            {
                _buffers[i] = Gl.GenBuffer();
                Gl.BindBuffer(PIXEL_PACK_BUFFER, _buffers[i]);
                Gl.BufferData(PIXEL_PACK_BUFFER, _pixels.Length, null, STREAM_READ);
            }
            Gl.BindBuffer(PIXEL_PACK_BUFFER, 0);
            Array.Clear(_filled);

            if (!complete || Gl.GetError() != Gl.NO_ERROR)
            {
                Utilities.Log_Utils.Write("Auto exposure: the light meter's targets could not be made; exposure stays fixed.");
                Release();
                _failed = true;
                return false;
            }
            _width = width;
            _height = height;
            return true;
        }

        private void Release()
        {
            if (_resolveFbo != 0) { Gl.DeleteFramebuffer(_resolveFbo); }
            if (_smallFbo != 0) { Gl.DeleteFramebuffer(_smallFbo); }
            if (_resolveColour != 0) { Gl.DeleteRenderbuffer(_resolveColour); }
            if (_smallColour != 0) { Gl.DeleteRenderbuffer(_smallColour); }
            for (int i = 0; i < RING; i++)
            {
                if (_buffers[i] != 0) { Gl.DeleteBuffer(_buffers[i]); }
                _buffers[i] = 0;
            }
            _resolveFbo = _smallFbo = _resolveColour = _smallColour = 0;
            _width = _height = 0;
        }

        public void Dispose() => Release();
    }
}
