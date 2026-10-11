using System.Numerics;

// The class belongs to the Scene namespace
namespace BimGo.Scene
{
    /// <summary>
    /// How an air terminal family is placed in Revit.
    /// </summary>
    public enum AirTerminalHosting
    {
        /// <summary>Level-based (or work-plane on a level): not attached to a ceiling.</summary>
        Unhosted,

        /// <summary>Face-based, on a ceiling (or other) face, possibly in a link.</summary>
        FaceHosted,

        /// <summary>Ceiling-hosted family (needs a ceiling element in the same model).</summary>
        CeilingHosted
    }

    /// <summary>
    /// How well BimGo knows an air terminal family (the family checker's badge).
    /// </summary>
    public enum FamilyRating
    {
        /// <summary>Not a 3-cone square supply diffuser (slots, louvers, returns…): shown, not modelled.</summary>
        NotModelled,

        /// <summary>Modelled, but some of its data was guessed (face offset, size, catalog by keyword…).</summary>
        Guessed,

        /// <summary>Meets the BimGo family spec, or is covered by a profile: nothing guessed.</summary>
        Ready
    }

    /// <summary>
    /// One air terminal (Revit Air Terminals category) as extracted. Positions are scene-local metres; flows L/s.
    /// </summary>
    public sealed class AirTerminal
    {
        /// <summary>Bits of <see cref="BlankedSides"/>: the family's +X, +Y, −X, −Y sides.</summary>
        public const byte SIDE_POS_X = 1, SIDE_POS_Y = 2, SIDE_NEG_X = 4, SIDE_NEG_Y = 8, ALL_SIDES = 15;

        /// <summary>The element (index into SceneData.Elements).</summary>
        public int Element { get; init; }

        /// <summary>Centre of the face (scene-local, m).</summary>
        public Vector3 Position { get; set; }

        /// <summary>The way the face points (unit; −Z for a ceiling diffuser).</summary>
        public Vector3 Normal { get; init; } = -Vector3.UnitZ;

        /// <summary>The family's X axis in plan (unit; orients the 4-way pattern and the blanked sides).</summary>
        public Vector3 Forward { get; init; } = Vector3.UnitX;

        /// <summary>Face size (m; square).</summary>
        public float FaceSize { get; init; }

        /// <summary>Round neck diameter (m).</summary>
        public float NeckDiameter { get; init; }

        /// <summary>Flow (L/s): Revit's value, taken as the cooling design flow. Edited by SetFlow.</summary>
        public float FlowLps { get; set; }

        /// <summary>True when the flow parameter can be written (instance, not read-only, not calculated).</summary>
        public bool FlowEditable { get; init; }

        /// <summary>The parameter that holds the flow (for SetFlow), e.g. "RBS_DUCT_FLOW_PARAM" or a family parameter name.</summary>
        public string FlowParameter { get; init; }

        /// <summary>Blanked sides (<see cref="SIDE_POS_X"/>…); 0 = all four open.</summary>
        public byte BlankedSides { get; init; }

        /// <summary>How the family is placed.</summary>
        public AirTerminalHosting Hosting { get; init; }

        /// <summary>True when any of its duct connectors is connected (a move may drag ductwork).</summary>
        public bool Connected { get; init; }

        /// <summary>True for supply terminals (only supply diffusers are modelled).</summary>
        public bool Supply { get; init; } = true;

        /// <summary>The catalog family key used (the generic key when matched by keyword or not at all).</summary>
        public string CatalogKey { get; init; } = DiffuserCatalog.GENERIC_KEY;

        /// <summary>The family checker's badge.</summary>
        public FamilyRating Rating { get; init; }

        /// <summary>Why not <see cref="FamilyRating.Ready"/> (shown in the readout), or null.</summary>
        public string RatingReason { get; init; }

        /// <summary>Distance from the family origin to its face along <see cref="Normal"/> (m); 0 for spec families.</summary>
        public float FaceOffset { get; init; }

        /// <summary>True when the trace models it: a supply diffuser rated Guessed or Ready with at least one open side.</summary>
        public bool Modelled => Supply && Rating != FamilyRating.NotModelled && OpenSides > 0 && NeckDiameter > 0f;

        /// <summary>Number of open sides (0–4).</summary>
        public int OpenSides => 4 - BitOperations.PopCount((uint)(BlankedSides & ALL_SIDES));

        /// <summary>
        /// Flow multiplier for evaluating one side's throw: catalogs give throws for all four sides open, so with
        /// sides blanked each open side carries 4 / open of its share. 0 when every side is blanked.
        /// </summary>
        public float SideFlowFactor => OpenSides == 0 ? 0f : 4f / OpenSides;

        /// <summary>True when the side along a plan direction (family axes: 0 = +X, 1 = +Y, 2 = −X, 3 = −Y) is open.</summary>
        public bool IsSideOpen(int side) => (BlankedSides & (1 << (side & 3))) == 0;
    }

    /// <summary>
    /// The model's air terminals and the catalog families they use (so a file is self-contained). Optional: older files
    /// and models without air terminals have none (<see cref="Empty"/>).
    /// </summary>
    public sealed class AirData
    {
        /// <summary>No air terminals.</summary>
        public static readonly AirData Empty = new();

        /// <summary>The Revit project's units at extraction (readouts default to them).</summary>
        public MeasurementSystem ProjectUnits { get; init; } = MeasurementSystem.SI;

        /// <summary>Every extracted air terminal, modelled or not.</summary>
        public AirTerminal[] Terminals { get; init; } = Array.Empty<AirTerminal>();

        /// <summary>The catalog families the terminals use (SI rows).</summary>
        public CatalogFamily[] Catalog { get; init; } = Array.Empty<CatalogFamily>();

        /// <summary>True when there are no terminals.</summary>
        public bool IsEmpty => Terminals.Length == 0;

        /// <summary>The terminal of an element, or null.</summary>
        public AirTerminal ForElement(int element)
        {
            foreach (AirTerminal t in Terminals)
            {
                if (t.Element == element) { return t; }
            }
            return null;
        }
    }
}
