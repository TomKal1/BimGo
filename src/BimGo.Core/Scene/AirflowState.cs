// The class belongs to the Scene namespace
namespace BimGo.Scene
{
    /// <summary>Cooling or heating: which flow and temperatures the airflow study uses.</summary>
    public enum AirflowMode
    {
        /// <summary>Each diffuser at its Revit flow, cool supply air.</summary>
        Cooling,

        /// <summary>Each diffuser at <see cref="AirflowState.HeatingShare"/> of its flow, warm supply air.</summary>
        Heating
    }

    /// <summary>The zone the draft check applies in.</summary>
    public enum OccupiedZonePreset
    {
        /// <summary>ASHRAE 62.1 breathing zone: 75–1800 mm (3–72 in) above the floor, 600 mm (2 ft) from walls.</summary>
        Ashrae621,

        /// <summary>The user's heights and wall setback.</summary>
        Custom
    }

    /// <summary>How the Place gun puts down an air terminal.</summary>
    public enum DiffuserPlacementMode
    {
        /// <summary>Not attached to the ceiling, at the aimed ceiling's bottom elevation (default).</summary>
        UnhostedAtCeiling,

        /// <summary>Hosted on the aimed ceiling face (face-based families).</summary>
        HostedToCeiling,

        /// <summary>Unhosted at a typed elevation (open ceilings, diffusers hung off the duct).</summary>
        FreeAtElevation
    }

    /// <summary>How the ceiling X-ray shows ceilings.</summary>
    public enum CeilingXRay
    {
        /// <summary>Ceilings draw normally.</summary>
        Off,

        /// <summary>Ceilings draw see-through (~20 %).</summary>
        SeeThrough,

        /// <summary>Ceilings are not drawn (still aimed at).</summary>
        Hidden
    }

    /// <summary>
    /// The user's airflow study state for one model (<c>airflow.json</c> in a .bimgo file or the live model folder).
    /// All values SI (°C, m). <see cref="Clean"/> clamps everything so a hand-edited file can't break the study.
    /// </summary>
    public sealed class AirflowState
    {
        /// <summary>Entry layout version.</summary>
        public int Version { get; set; } = 1;

        /// <summary>Airflow overlay on.</summary>
        public bool On { get; set; }

        /// <summary>Cooling or heating.</summary>
        public AirflowMode Mode { get; set; } = AirflowMode.Cooling;

        /// <summary>Units for readouts and inputs; null = the Revit project's units.</summary>
        public MeasurementSystem? UnitsOverride { get; set; }

        /// <summary>Heating flow as a share of each diffuser's (cooling) flow, 0.1–1 (default 0.5).</summary>
        public float HeatingShare { get; set; } = 0.5f;

        /// <summary>Cooling supply air (°C): 55 °F.</summary>
        public float CoolingSupplyC { get; set; } = 12.8f;

        /// <summary>Cooling room setpoint (°C): 75 °F.</summary>
        public float CoolingRoomC { get; set; } = 23.9f;

        /// <summary>Heating supply air (°C): 90 °F.</summary>
        public float HeatingSupplyC { get; set; } = 32.2f;

        /// <summary>Heating room setpoint (°C): 70 °F.</summary>
        public float HeatingRoomC { get; set; } = 21.1f;

        /// <summary>The zone the draft check applies in.</summary>
        public OccupiedZonePreset ZonePreset { get; set; } = OccupiedZonePreset.Ashrae621;

        /// <summary>Custom zone: bottom above the floor (m).</summary>
        public float CustomZoneBottom { get; set; } = 0.075f;

        /// <summary>Custom zone: top above the floor (m).</summary>
        public float CustomZoneTop { get; set; } = 1.8f;

        /// <summary>Custom zone: distance kept from walls (m).</summary>
        public float CustomZoneSetback { get; set; } = 0.6f;

        /// <summary>How the Place gun places air terminals.</summary>
        public DiffuserPlacementMode PlacementMode { get; set; } = DiffuserPlacementMode.UnhostedAtCeiling;

        /// <summary>Ceiling X-ray.</summary>
        public CeilingXRay XRay { get; set; } = CeilingXRay.SeeThrough;

        /// <summary>Free-at-elevation mode: face height above the level (m).</summary>
        public float TargetElevation { get; set; } = 3.0f;

        /// <summary>Snap terminals to a ceiling grid while placing.</summary>
        public bool GridSnap { get; set; }

        /// <summary>Per family type (type UniqueId) forced catalog keys, from the Scan readout.</summary>
        public Dictionary<string, string> TypeCatalogOverrides { get; set; } = new(StringComparer.Ordinal);

        #region Zone

        /// <summary>The zone's bottom above the floor (m) for the current preset.</summary>
        public float ZoneBottom => ZonePreset == OccupiedZonePreset.Custom ? CustomZoneBottom : 0.075f;

        /// <summary>The zone's top above the floor (m) for the current preset.</summary>
        public float ZoneTop => ZonePreset == OccupiedZonePreset.Custom ? CustomZoneTop : 1.8f;

        /// <summary>The zone's distance from walls (m) for the current preset.</summary>
        public float ZoneSetback => ZonePreset == OccupiedZonePreset.Custom ? CustomZoneSetback : 0.6f;

        #endregion

        #region Mode

        /// <summary>The share of each diffuser's flow used in the current mode.</summary>
        public float FlowShare => Mode == AirflowMode.Heating ? HeatingShare : 1f;

        /// <summary>Supply air temperature (°C) in the current mode.</summary>
        public float SupplyC => Mode == AirflowMode.Heating ? HeatingSupplyC : CoolingSupplyC;

        /// <summary>Room temperature (°C) in the current mode.</summary>
        public float RoomC => Mode == AirflowMode.Heating ? HeatingRoomC : CoolingRoomC;

        /// <summary>The units readouts use: the override, else the project's.</summary>
        public MeasurementSystem UnitsFor(MeasurementSystem projectUnits) => UnitsOverride ?? projectUnits;

        #endregion

        /// <summary>
        /// Clamps every value into range (unknown enum values fall back to defaults; the custom zone keeps top above
        /// bottom). Returns this.
        /// </summary>
        public AirflowState Clean()
        {
            Version = 1;
            if (!Enum.IsDefined(Mode)) { Mode = AirflowMode.Cooling; }
            if (UnitsOverride.HasValue && !Enum.IsDefined(UnitsOverride.Value)) { UnitsOverride = null; }
            if (!Enum.IsDefined(ZonePreset)) { ZonePreset = OccupiedZonePreset.Ashrae621; }
            if (!Enum.IsDefined(PlacementMode)) { PlacementMode = DiffuserPlacementMode.UnhostedAtCeiling; }
            if (!Enum.IsDefined(XRay)) { XRay = CeilingXRay.SeeThrough; }

            HeatingShare = Clamp(HeatingShare, 0.1f, 1f, 0.5f);
            CoolingSupplyC = Clamp(CoolingSupplyC, 5f, 25f, 12.8f);
            CoolingRoomC = Clamp(CoolingRoomC, 15f, 32f, 23.9f);
            HeatingSupplyC = Clamp(HeatingSupplyC, 15f, 60f, 32.2f);
            HeatingRoomC = Clamp(HeatingRoomC, 10f, 30f, 21.1f);

            CustomZoneBottom = Clamp(CustomZoneBottom, 0f, 1f, 0.075f);
            CustomZoneTop = Clamp(CustomZoneTop, CustomZoneBottom + 0.1f, 3f, 1.8f);
            CustomZoneSetback = Clamp(CustomZoneSetback, 0f, 2f, 0.6f);
            TargetElevation = Clamp(TargetElevation, 0.5f, 30f, 3.0f);

            TypeCatalogOverrides = TypeCatalogOverrides == null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : TypeCatalogOverrides
                    .Where(p => !string.IsNullOrWhiteSpace(p.Key) && !string.IsNullOrWhiteSpace(p.Value))
                    .Take(500)
                    .ToDictionary(p => p.Key.Trim(), p => p.Value.Trim(), StringComparer.Ordinal);
            return this;
        }

        /// <summary>True when nothing differs from a fresh state (then the file entry isn't written).</summary>
        public bool IsDefault()
        {
            var d = new AirflowState();
            return On == d.On && Mode == d.Mode && UnitsOverride == d.UnitsOverride && HeatingShare == d.HeatingShare
                && CoolingSupplyC == d.CoolingSupplyC && CoolingRoomC == d.CoolingRoomC && HeatingSupplyC == d.HeatingSupplyC
                && HeatingRoomC == d.HeatingRoomC && ZonePreset == d.ZonePreset && CustomZoneBottom == d.CustomZoneBottom
                && CustomZoneTop == d.CustomZoneTop && CustomZoneSetback == d.CustomZoneSetback && PlacementMode == d.PlacementMode
                && XRay == d.XRay && TargetElevation == d.TargetElevation && GridSnap == d.GridSnap
                && (TypeCatalogOverrides == null || TypeCatalogOverrides.Count == 0);
        }

        /// <summary>A finite value clamped to a range (else the default).</summary>
        private static float Clamp(float value, float min, float max, float fallback) =>
            float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
    }
}
