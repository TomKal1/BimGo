using System.Numerics;
using BimGo.Scene;

// The class belongs to the Rendering namespace
namespace BimGo.Rendering
{
    /// <summary>
    /// This frame's artificial lighting as the shaders take it: up to <see cref="MAX_LIGHTS"/> point lights (picked
    /// and packed by the session each frame, shadow maps assigned by the renderer), the glow multiplier for emissive
    /// surfaces and the bloom strength.
    /// Reused every frame (no allocations).
    /// </summary>
    internal sealed class ArtificialLighting
    {
        /// <summary>Most lights the shaders take (the GLSL arrays in <see cref="Shaders.LIGHTS_GLSL"/> are this size).</summary>
        public const int MAX_LIGHTS = 32;

        /// <summary>xyz position (scene-local), w radius (m).</summary>
        public readonly Vector4[] Position = new Vector4[MAX_LIGHTS];

        /// <summary>rgb colour × intensity, w downward share (0 omni – 1 downlight).</summary>
        public readonly Vector4[] Colour = new Vector4[MAX_LIGHTS];

        /// <summary>x first shadow layer (-1 = unshadowed), y fade (0–1); set by the renderer from <see cref="Key"/>.</summary>
        public readonly Vector4[] Shadow = new Vector4[MAX_LIGHTS];

        /// <summary>Identity of each light across frames (its shadow map is cached under it).</summary>
        public readonly long[] Key = new long[MAX_LIGHTS];

        /// <summary>
        /// Round X-C1: most lights beyond the shadowed ones ("all fixtures": photos, videos, stills, and live when asked;
        /// the std140 block in <see cref="Shaders.LIGHTS_GLSL"/> is this size: 15 KB, within GL's 16 KB minimum).
        /// </summary>
        public const int MAX_FAR = 480;

        /// <summary>Round X-C1: the unshadowed lights (as <see cref="Position"/> / <see cref="Colour"/>, fade folded in).</summary>
        public readonly Vector4[] FarPosition = new Vector4[MAX_FAR], FarColour = new Vector4[MAX_FAR];

        /// <summary>Round X-C1: unshadowed lights in use.</summary>
        public int FarCount;

        /// <summary>Round X-C1: all fixtures this frame: a shadowed light that loses its map is lit unshadowed instead of dropped.</summary>
        public bool KeepUnshadowed;

        /// <summary>Round X-C1: the far lights changed since the renderer last uploaded them.</summary>
        public bool FarDirty = true;

        /// <summary>Round X-C1: adds an unshadowed light (ignored when full).</summary>
        public void AddFar(Vector4 position, Vector4 colour)
        {
            if (FarCount >= MAX_FAR) { return; }
            FarPosition[FarCount] = position;
            FarColour[FarCount] = colour;
            FarCount++;
            FarDirty = true;
        }

        /// <summary>
        /// Removes light k, keeping the order of the rest (with <see cref="KeepUnshadowed"/>, it moves to the unshadowed lights).
        /// </summary>
        public void RemoveAt(int k)
        {
            if (KeepUnshadowed)
            {
                Vector4 c = Colour[k];
                float fade = Shadow[k].Y;
                AddFar(Position[k], new Vector4(c.X * fade, c.Y * fade, c.Z * fade, c.W));
            }
            for (int i = k; i < Count - 1; i++)
            {
                Position[i] = Position[i + 1];
                Colour[i] = Colour[i + 1];
                Shadow[i] = Shadow[i + 1];
                Key[i] = Key[i + 1];
            }
            Count--;
        }

        /// <summary>Values per profile table (one layer of the shader's profile texture).</summary>
        public const int PROFILE_VALUES = LightProfile.FINE_SIZE;

        /// <summary>
        /// The light profile tables, 16-bit (slot s at s × <see cref="PROFILE_VALUES"/>: a layer of the renderer's R16
        /// texture array, filtered by the hardware). Set when the per-type choices change, not per frame.
        /// </summary>
        public readonly ushort[] ProfileValues = new ushort[LightProfiles.MAX_GPU_PROFILES * PROFILE_VALUES];

        /// <summary>Round X-D1: per slot, x = <see cref="LightProfile.FineScale"/>.</summary>
        public readonly Vector4[] ProfileInfo = new Vector4[LightProfiles.MAX_GPU_PROFILES];

        /// <summary>Round X-D1: the tables changed since the renderer last uploaded them.</summary>
        public bool ProfilesDirty = true;

        /// <summary>Round X-D1: puts a profile in a slot (ignored when out of range).</summary>
        public void SetProfile(int slot, LightProfile profile)
        {
            if (slot < 0 || slot >= LightProfiles.MAX_GPU_PROFILES || profile == null) { return; }
            Array.Copy(profile.Fine, 0, ProfileValues, slot * PROFILE_VALUES, PROFILE_VALUES);
            ProfileInfo[slot] = new Vector4(profile.FineScale, profile.BeamAngle, 0f, 0f);
            ProfilesDirty = true;
        }

        /// <summary>Lights in use this frame (0 = none).</summary>
        public int Count;

        /// <summary>Emissive surface multiplier (0 = glowing surfaces look like any other).</summary>
        public float Emissive;

        /// <summary>Bloom strength (0 = no bloom).</summary>
        public float Bloom;

        /// <summary>
        /// Clears the frame (no lights, no glow).
        /// </summary>
        public void Clear()
        {
            Count = 0;
            FarCount = 0; // X-C1
            KeepUnshadowed = false;
            FarDirty = true;
            Emissive = 0f;
            Bloom = 0f;
        }
    }
}
