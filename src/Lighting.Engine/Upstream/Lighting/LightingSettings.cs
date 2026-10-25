using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using WWTools.Calcs;
using WWTools.Lighting.Engine;

namespace WWTools.Lighting
{
    /// <summary>
    /// Everything the lighting tools remember in a model: per-type luminaire settings and the
    /// calculation sets. Saved as JSON in extensible storage (<see cref="LightingStorage"/>), so a
    /// batch run (Model Delta) can repeat exactly what someone set up interactively.
    /// </summary>
    [DataContract]
    public class LightingProjectSettings
    {
        public const int CurrentSchemaVersion = 1;

        [DataMember(Order = 1)]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        /// <summary>"Footcandles" or "Lux".</summary>
        [DataMember(Order = 2)]
        public string Units { get; set; } = "Footcandles";

        /// <summary>Folders searched (with subfolders) for IES files named by fixture types.</summary>
        [DataMember(Order = 3)]
        public List<string> IesFolders { get; set; } = new List<string>();

        [DataMember(Order = 4)]
        public List<LuminaireTypeSetting> Luminaires { get; set; } = new List<LuminaireTypeSetting>();

        [DataMember(Order = 5)]
        public List<CalcSetDefinition> CalcSets { get; set; } = new List<CalcSetDefinition>();

        /// <summary>Where reports are written; empty = Documents\WW Lighting Calcs\&lt;model&gt;.</summary>
        [DataMember(Order = 6)]
        public string ReportFolder { get; set; } = string.Empty;

        /// <summary>
        /// Write the results exchange file beside the report and into the model's BimGo folder, so a BimGo walkthrough
        /// shows the calc. Stored inverted so settings saved before the option existed get the file.
        /// </summary>
        public bool WriteBimGoResults
        {
            get => !NoBimGoResults;
            set => NoBimGoResults = !value;
        }

        [DataMember(Order = 7)]
        private bool NoBimGoResults { get; set; }

        /// <summary>Per-fixture overrides and last placements, keyed by instance key (link UniqueId + "|" for linked fixtures).</summary>
        [DataMember(Order = 8)]
        public List<LuminaireInstanceSetting> Instances { get; set; } = new List<LuminaireInstanceSetting>();

        public LuminaireTypeSetting? FindLuminaire(string typeKey) =>
            Luminaires.FirstOrDefault(l => string.Equals(l.TypeKey, typeKey, StringComparison.Ordinal));

        public LuminaireInstanceSetting? FindInstance(string instanceKey) =>
            Instances.FirstOrDefault(i => string.Equals(i.InstanceKey, instanceKey, StringComparison.Ordinal));

        public LuminaireInstanceSetting GetOrAddInstance(string instanceKey, string typeKey)
        {
            LuminaireInstanceSetting? existing = FindInstance(instanceKey);
            if (existing != null)
            {
                existing.TypeKey = typeKey;
                return existing;
            }
            var created = new LuminaireInstanceSetting { InstanceKey = instanceKey, TypeKey = typeKey };
            Instances.Add(created);
            return created;
        }

        /// <summary>Stores where a run placed each fixture's light centre; overrides are left alone.</summary>
        public void RecordPlacements(IEnumerable<(string InstanceKey, string TypeKey, MountingPlacement Placement)> placements, string runLabel)
        {
            foreach ((string instanceKey, string typeKey, MountingPlacement p) in placements)
            {
                LuminaireInstanceSetting s = GetOrAddInstance(instanceKey, typeKey);
                s.LastPlacement = LuminaireMounting.Word(p.Kind);
                s.LastSure = p.Sure;
                s.LastReason = p.Reason;
                s.LastRun = runLabel;
                s.LastAim = p.Aim.HasValue ? "out of the wall" : "down";
            }
        }

        public LuminaireTypeSetting GetOrAddLuminaire(string typeKey, string displayName)
        {
            LuminaireTypeSetting? existing = FindLuminaire(typeKey);
            if (existing != null)
            {
                existing.DisplayName = displayName;
                return existing;
            }
            var created = new LuminaireTypeSetting { TypeKey = typeKey, DisplayName = displayName };
            Luminaires.Add(created);
            return created;
        }

        public CalcSetDefinition? FindCalcSet(string name) =>
            CalcSets.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

        public string ToJson()
        {
            var serializer = new DataContractJsonSerializer(typeof(LightingProjectSettings));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, this);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        public static LightingProjectSettings FromJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new LightingProjectSettings();
            var serializer = new DataContractJsonSerializer(typeof(LightingProjectSettings));
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                var settings = serializer.ReadObject(stream) as LightingProjectSettings ?? new LightingProjectSettings();
                settings.Normalise();
                return settings;
            }
        }

        /// <summary>Fills lists DataContract left null (older or hand-edited JSON).</summary>
        public void Normalise()
        {
            IesFolders ??= new List<string>();
            Luminaires ??= new List<LuminaireTypeSetting>();
            CalcSets ??= new List<CalcSetDefinition>();
            Units ??= "Footcandles";
            ReportFolder ??= string.Empty;
            foreach (CalcSetDefinition set in CalcSets) set.Normalise();
            foreach (LuminaireTypeSetting lum in Luminaires) lum.Normalise();
            Instances ??= new List<LuminaireInstanceSetting>();
            Instances.RemoveAll(i => i == null || string.IsNullOrEmpty(i.InstanceKey));
            foreach (LuminaireInstanceSetting i in Instances) i.Normalise();
        }
    }

    /// <summary>
    /// One fixture's own overrides (null = follow its type) and where the last run placed its light
    /// centre. An Offset here nudges the placed light up or down, except on a Custom mounting, where it
    /// is the Custom offset.
    /// </summary>
    [DataContract]
    public class LuminaireInstanceSetting
    {
        [DataMember(Order = 1)] public string InstanceKey { get; set; } = string.Empty;
        /// <summary>The fixture's type when last seen, for display; an override stays with the fixture if its type changes.</summary>
        [DataMember(Order = 2)] public string TypeKey { get; set; } = string.Empty;
        [DataMember(Order = 3)] public string? Mounting { get; set; }
        [DataMember(Order = 4)] public string? Aim { get; set; }
        [DataMember(Order = 5)] public double? RotationDegrees { get; set; }
        [DataMember(Order = 6)] public double? CenterOffset { get; set; }
        /// <summary>The last run's placement word (e.g. "wall"), whether it was sure, why, and which run.</summary>
        [DataMember(Order = 7)] public string LastPlacement { get; set; } = string.Empty;
        [DataMember(Order = 8)] public bool LastSure { get; set; } = true;
        [DataMember(Order = 9)] public string LastReason { get; set; } = string.Empty;
        [DataMember(Order = 10)] public string LastRun { get; set; } = string.Empty;
        /// <summary>Which way an Auto aim pointed in the last run ("down" or "out of the wall").</summary>
        [DataMember(Order = 11)] public string LastAim { get; set; } = string.Empty;

        public bool HasOverride => Mounting != null || Aim != null || RotationDegrees.HasValue || CenterOffset.HasValue;

        public void ClearOverrides()
        {
            Mounting = null;
            Aim = null;
            RotationDegrees = null;
            CenterOffset = null;
        }

        public void Normalise()
        {
            TypeKey ??= string.Empty;
            LastPlacement ??= string.Empty;
            LastReason ??= string.Empty;
            LastRun ??= string.Empty;
            LastAim ??= string.Empty;
        }
    }

    /// <summary>The mounting, aim and light centre a calc uses for one fixture: its own overrides over its type's settings.</summary>
    public readonly struct FixtureSettings
    {
        public FixtureSettings(MountingKind mounting, string aim, double rotationDegrees, string center, double centerOffset, double nudge)
        {
            Mounting = mounting;
            Aim = aim;
            RotationDegrees = rotationDegrees;
            Center = center;
            CenterOffset = centerOffset;
            Nudge = nudge;
        }

        public MountingKind Mounting { get; }
        public string Aim { get; }
        public double RotationDegrees { get; }
        public string Center { get; }
        public double CenterOffset { get; }

        /// <summary>Vertical move (feet) added after the light centre is placed.</summary>
        public double Nudge { get; }

        public static FixtureSettings Resolve(LuminaireTypeSetting? type, LuminaireInstanceSetting? instance)
        {
            MountingKind mounting = LuminaireMounting.Parse(instance?.Mounting ?? type?.Mounting);
            string aim = instance?.Aim ?? type?.Aim ?? "Auto";
            double rotation = instance?.RotationDegrees ?? type?.RotationDegrees ?? 0;
            string center = type?.Center ?? "Origin";
            double offset = type?.CenterOffset ?? 0;
            if (instance?.Mounting != null && mounting != MountingKind.Custom)
                (center, offset) = LuminaireMounting.PresetCentre(mounting);
            if (mounting == MountingKind.Custom)
                return new FixtureSettings(mounting, aim, rotation, center, instance?.CenterOffset ?? offset, 0);
            return new FixtureSettings(mounting, aim, rotation, center, offset, instance?.CenterOffset ?? 0);
        }
    }

    /// <summary>Overrides for one luminaire type (keyed by the type's UniqueId, prefixed by the link for linked fixtures).</summary>
    [DataContract]
    public class LuminaireTypeSetting
    {
        [DataMember(Order = 1)]
        public string TypeKey { get; set; } = string.Empty;

        [DataMember(Order = 2)]
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>Leave this type out of calculations.</summary>
        [DataMember(Order = 3)]
        public bool Exclude { get; set; }

        /// <summary>Total light loss factor; 0 = use the type's Revit value.</summary>
        [DataMember(Order = 4)]
        public double LightLossFactor { get; set; }

        /// <summary>An IES file to use instead of the one Revit has.</summary>
        [DataMember(Order = 5)]
        public string IesPath { get; set; } = string.Empty;

        /// <summary>Extra rotation of the photometric web about its aiming axis (degrees).</summary>
        [DataMember(Order = 6)]
        public double RotationDegrees { get; set; }

        /// <summary>
        /// "Auto" (down, or out of the wall for a fixture set in a wall face), "Down",
        /// "FamilyNegativeZ" or "FamilyPositiveZ".
        /// </summary>
        [DataMember(Order = 7)]
        public string Aim { get; set; } = "Auto";

        /// <summary>"Auto", "Origin", "BoundingBoxBottom" or "BoundingBoxCenter".</summary>
        [DataMember(Order = 8)]
        public string Center { get; set; } = "Origin";

        /// <summary>Vertical offset added to the light centre (feet).</summary>
        [DataMember(Order = 9)]
        public double CenterOffset { get; set; }

        /// <summary>Extra multiplier on output (e.g. a dimmed scene); 1 = none.</summary>
        [DataMember(Order = 10)]
        public double Multiplier { get; set; } = 1.0;

        /// <summary>Input watts per luminaire; 0 = from the IES file or the family.</summary>
        [DataMember(Order = 11)]
        public double Watts { get; set; }

        /// <summary>
        /// How the fixture is mounted (a <see cref="WWTools.Lighting.Engine.MountingKind"/> name): Auto works it out per
        /// fixture; Custom uses <see cref="Center"/> and <see cref="CenterOffset"/>. Null in older settings.
        /// </summary>
        [DataMember(Order = 12)]
        public string? Mounting { get; set; }

        /// <summary>What the last run's Auto placement found for this type, for the Luminaire Manager.</summary>
        [DataMember(Order = 13)]
        public string LastPlacement { get; set; } = string.Empty;

        /// <summary>1 once <see cref="Aim"/> knows "Auto"; 0 in older settings, where "Down" was only the default.</summary>
        [DataMember(Order = 14)]
        public int AimVersion { get; set; } = 1;

        public void Normalise()
        {
            TypeKey ??= string.Empty;
            DisplayName ??= string.Empty;
            IesPath ??= string.Empty;
            Aim ??= "Auto";
            if (AimVersion < 1)
            {
                if (Aim == "Down") Aim = "Auto";
                AimVersion = 1;
            }
            Center ??= "Origin";
            LastPlacement ??= string.Empty;
            // Older settings had only a light centre: one someone chose stays theirs.
            if (string.IsNullOrEmpty(Mounting))
                Mounting = (Center == "Origin" || Center == "Auto") && CenterOffset == 0 ? "Auto" : "Custom";
            if (Multiplier <= 0) Multiplier = 1.0;
        }

        /// <summary>
        /// The LLF, multiplier and watts a calc uses for a type: the setting's value when it is
        /// positive, else Revit's light loss factor, 1, and the photometry's watts.
        /// </summary>
        public static (double LightLossFactor, double Multiplier, double Watts) Effective(
            LuminaireTypeSetting? setting, double revitLightLossFactor, double photometryWatts)
        {
            double llf = setting != null && setting.LightLossFactor > 0 ? setting.LightLossFactor : revitLightLossFactor;
            double multiplier = setting != null && setting.Multiplier > 0 ? setting.Multiplier : 1.0;
            double watts = setting != null && setting.Watts > 0 ? setting.Watts : photometryWatts;
            return (llf, multiplier, watts);
        }
    }

    /// <summary>A named, repeatable calculation: which zones, how the grid is laid, where results go.</summary>
    [DataContract]
    public class CalcSetDefinition : ICalcSet
    {
        [DataMember(Order = 1)]
        public string Name { get; set; } = "Lighting Calc";

        /// <summary>"Spaces", "Rooms" or "LinkedRooms".</summary>
        [DataMember(Order = 2)]
        public string ZoneSource { get; set; } = "Spaces";

        /// <summary>The link instance for "LinkedRooms".</summary>
        [DataMember(Order = 3)]
        public string LinkInstanceUniqueId { get; set; } = string.Empty;

        /// <summary>The zones to calculate; empty = every placed zone in the source.</summary>
        [DataMember(Order = 4)]
        public List<string> ZoneUniqueIds { get; set; } = new List<string>();

        /// <summary>
        /// The settings every zone of a run shares, as "name: value", saved with the results: a
        /// partial rerun under different ones would mix two calculations in one report.
        /// </summary>
        public List<string> SharedSettings()
        {
            string N(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            return new List<string>
            {
                $"grid: {N(GridSpacing)} ft",
                $"boundary offset: {N(BoundaryOffset)} ft",
                $"work plane: {(UseZoneWorkplane ? "space value, else " : string.Empty)}{N(WorkplaneHeight)} ft",
                $"geometry detail: {GeometryDetail}",
                $"quality: {Quality}",
                $"interreflection: {(Interreflection ? "on" : "off")}",
                $"reflectance source: {ReflectanceSource}",
                $"reflectances: ceiling {N(CeilingReflectance)}, walls {N(WallReflectance)}, floor {N(FloorReflectance)}, furniture {N(FurnitureReflectance)}",
            };
        }

        [DataMember(Order = 5)]
        public double GridSpacing { get; set; } = 2.0;

        [DataMember(Order = 6)]
        public double BoundaryOffset { get; set; } = 1.0;

        /// <summary>Use each space's Lighting Calculation Workplane when it has one.</summary>
        [DataMember(Order = 7)]
        public bool UseZoneWorkplane { get; set; } = true;

        [DataMember(Order = 8)]
        public double WorkplaneHeight { get; set; } = 2.5;

        /// <summary>"Zone" (space reflectance parameters, else the values below), "Defaults" or "Materials".</summary>
        [DataMember(Order = 9)]
        public string ReflectanceSource { get; set; } = "Zone";

        [DataMember(Order = 10)]
        public double CeilingReflectance { get; set; } = 0.8;

        [DataMember(Order = 11)]
        public double WallReflectance { get; set; } = 0.5;

        [DataMember(Order = 12)]
        public double FloorReflectance { get; set; } = 0.2;

        [DataMember(Order = 13)]
        public double FurnitureReflectance { get; set; } = 0.5;

        [DataMember(Order = 14)]
        public bool Interreflection { get; set; } = true;

        /// <summary>"Draft", "Normal" or "High".</summary>
        [DataMember(Order = 15)]
        public string Quality { get; set; } = "Normal";

        [DataMember(Order = 16)]
        public bool IncludeLinkedFixtures { get; set; }

        [DataMember(Order = 17)]
        public bool IncludeLinkedGeometry { get; set; } = true;

        /// <summary>Plan views that get point values (each shows the zones on its level).</summary>
        [DataMember(Order = 18)]
        public List<string> OutputViewUniqueIds { get; set; } = new List<string>();

        [DataMember(Order = 19)]
        public bool PlaceValues { get; set; } = true;

        /// <summary>Also place a statistics note in each zone on the plans.</summary>
        [DataMember(Order = 20)]
        public bool PlaceStatistics { get; set; }

        /// <summary>Create / refresh a drafting view with the summary table.</summary>
        [DataMember(Order = 21)]
        public bool CreateSummaryView { get; set; } = true;

        /// <summary>Target average in the report's units; 0 = none.</summary>
        [DataMember(Order = 22)]
        public double TargetAverage { get; set; }

        /// <summary>Text notes the last run placed on plans (removed before the next run).</summary>
        [DataMember(Order = 23)]
        public List<string> CreatedElementUniqueIds { get; set; } = new List<string>();

        [DataMember(Order = 24)]
        public string SummaryViewUniqueId { get; set; } = string.Empty;

        [DataMember(Order = 25)]
        public string LastRun { get; set; } = string.Empty;

        [DataMember(Order = 26)]
        public string LastSummary { get; set; } = string.Empty;

        [DataMember(Order = 27)]
        public string LastReportPath { get; set; } = string.Empty;

        /// <summary>Extra space around each zone for geometry and luminaires (feet).</summary>
        [DataMember(Order = 28)]
        public double RegionMargin { get; set; } = 15.0;

        /// <summary>
        /// "Simplified" (default): furniture, casework, equipment, plumbing fixtures, generic models,
        /// doors and any element over 1,500 triangles are replaced by their boxes. "Full": every triangle.
        /// </summary>
        [DataMember(Order = 29)]
        public string GeometryDetail { get; set; } = "Simplified";

        /// <summary>
        /// Place every value on plans. Off (default): values that would print over their
        /// neighbours at the view's scale are skipped (the report and CSV keep every point).
        /// </summary>
        [DataMember(Order = 30)]
        public bool ShowAllValues { get; set; }

        /// <summary>Put a colour-coded dot at each placed value (blue = low through red = high).</summary>
        [DataMember(Order = 31)]
        public bool ColorPoints { get; set; } = true;

        /// <summary>Printed height of the point values in inches (e.g. 0.0625 = 1/16"); 0 = 3/32".</summary>
        [DataMember(Order = 32)]
        public double ValueTextSize { get; set; } = 1.0 / 16.0;

        /// <summary>Top of the dot colour scale in the report's units; 0 = the set's highest value.</summary>
        [DataMember(Order = 33)]
        public double ColorScaleMax { get; set; }

        /// <summary>Place a results table (ElumTools style) at the top left of each sheet showing an output view.</summary>
        [DataMember(Order = 34)]
        public bool PlaceResultsTable { get; set; } = true;

        [DataMember(Order = 35)]
        public string ResultsTableTitle { get; set; } = ResultsTable.DefaultTitle;

        /// <summary>Default largest avg/min ratio (3 for 3:1); 0 = none. Pairs with <see cref="TargetAverage"/>.</summary>
        [DataMember(Order = 36)]
        public double TargetAverageToMinimum { get; set; }

        /// <summary>Per-zone targets that replace the set's default (typed as "20, 3" or "1-3, 5:1").</summary>
        [DataMember(Order = 37)]
        public List<ZoneTargetSetting> ZoneTargets { get; set; } = new List<ZoneTargetSetting>();

        /// <summary>The results-table drafting views the last run placed, one per sheet.</summary>
        [DataMember(Order = 38)]
        public List<ResultsTablePlacement> ResultsTables { get; set; } = new List<ResultsTablePlacement>();

        /// <summary>
        /// One detailed page per zone in the report (3D views, isolux lines, surfaces, luminaires).
        /// Stored inverted so sets saved before the option existed get the pages.
        /// </summary>
        public bool RoomPages
        {
            get => !CompactReport;
            set => CompactReport = !value;
        }

        [DataMember(Order = 39)]
        private bool CompactReport { get; set; }

        /// <summary>Also print the report to PDF with Edge (or Chrome). Stored inverted, like <see cref="RoomPages"/>.</summary>
        public bool SavePdf
        {
            get => !NoPdf;
            set => NoPdf = !value;
        }

        [DataMember(Order = 40)]
        private bool NoPdf { get; set; }

        [DataMember(Order = 41)] public bool EmergencyOnly { get; set; }
        [DataMember(Order = 42)] public List<EmergencyLuminaireSetting> EmergencyLuminaires { get; set; } = new List<EmergencyLuminaireSetting>();
        [DataMember(Order = 43)] public List<string> ExcludedElementKeys { get; set; } = new List<string>();
        [DataMember(Order = 44)] public List<string> ExcludedCategories { get; set; } = new List<string>();
        [DataMember(Order = 45)] public List<ExcludedCalcPoint> ExcludedPoints { get; set; } = new List<ExcludedCalcPoint>();
        // Inverted for old saved sets: furniture exclusion is enabled unless explicitly disabled.
        [DataMember(Order = 46)] private bool KeepFurniturePoints { get; set; }
        public bool AutoExcludeFurniture { get => !KeepFurniturePoints; set => KeepFurniturePoints = !value; }
        /// <summary>Old opt-in for moving recessed sources below ceilings; superseded by <see cref="AutoPlaceSources"/>.</summary>
        [DataMember(Order = 47)] public bool CorrectRecessedSources { get; set; }
        // Inverted so old saved sets get automatic placement too (it replaces the old recessed correction).
        [DataMember(Order = 50)] private bool ManualSourcePlacement { get; set; }
        /// <summary>Place light centres of types whose mounting is Auto from their geometry and host; off = family origin.</summary>
        public bool AutoPlaceSources { get => !ManualSourcePlacement; set => ManualSourcePlacement = !value; }
        [DataMember(Order = 48)] public List<ZoneAnnotation> ZoneAnnotations { get; set; } = new List<ZoneAnnotation>();
        [DataMember(Order = 49)] public List<CalcPointAnnotation> PointAnnotations { get; set; } = new List<CalcPointAnnotation>();
        /// <summary>The zones the last run's results hold (all of them after a partial rerun), for the calc window's zone list.</summary>
        [DataMember(Order = 51)] public List<string> CalculatedZoneKeys { get; set; } = new List<string>();

        /// <summary>
        /// Calculate elevator shafts too. Off by default: a shaft has no fixtures, reads zero and
        /// costs a full solve (see <see cref="IsElevatorShaft"/>).
        /// </summary>
        [DataMember(Order = 52)] public bool IncludeElevators { get; set; }

        /// <summary>
        /// A zone named as an elevator shaft ("E1 Elevator", "Elev 2", "Hoistway", "Lift Shaft"); not
        /// an elevator lobby, machine or equipment room, which are lit rooms.
        /// </summary>
        public static bool IsElevatorShaft(string? name)
        {
            string n = " " + (name ?? string.Empty).ToLowerInvariant() + " ";
            if (new[] { "lobby", "machine", "equipment", "control", "vestibule", "room", "corridor" }.Any(n.Contains)) return false;
            return n.Contains("elevator") || n.Contains("hoistway") || n.Contains("lift shaft") ||
                   System.Text.RegularExpressions.Regex.IsMatch(n, @"\belev\b");
        }

        public double EmergencyOutputFor(string key) => EmergencyLuminaires.FirstOrDefault(e => e.Key == key)?.Output ?? 0;
        public bool IncludesGeometry(string key, string category) => !ExcludedElementKeys.Contains(key) && !ExcludedCategories.Contains(category);

        /// <summary>The set's default target; null when none is set.</summary>
        public LightingTarget? DefaultTarget
        {
            get
            {
                var target = new LightingTarget { Average = TargetAverage, AverageToMinimum = TargetAverageToMinimum };
                return target.IsEmpty ? null : target;
            }
        }

        /// <summary>The zone's own target when it has one ("none" clears the default), else the set's default.</summary>
        public LightingTarget? TargetFor(string zoneKey)
        {
            ZoneTargetSetting? own = ZoneTargets.FirstOrDefault(z => string.Equals(z.ZoneUniqueId, zoneKey, StringComparison.Ordinal));
            if (own != null && LightingTarget.TryParse(own.Target, out LightingTarget parsed))
                return parsed.IsEmpty ? null : parsed;
            return DefaultTarget;
        }

        /// <summary>The printed height of the point values in feet.</summary>
        public double ValueTextHeightFeet => ValueTextSize / 12.0;

        public void Normalise()
        {
            Name ??= "Lighting Calc";
            ZoneSource ??= "Spaces";
            LinkInstanceUniqueId ??= string.Empty;
            ZoneUniqueIds ??= new List<string>();
            OutputViewUniqueIds ??= new List<string>();
            CreatedElementUniqueIds ??= new List<string>();
            ReflectanceSource ??= "Zone";
            Quality ??= "Normal";
            SummaryViewUniqueId ??= string.Empty;
            LastRun ??= string.Empty;
            LastSummary ??= string.Empty;
            LastReportPath ??= string.Empty;
            if (GridSpacing <= 0) GridSpacing = 2.0;
            if (BoundaryOffset < 0) BoundaryOffset = 0;
            if (RegionMargin <= 0) RegionMargin = 15.0;
            if (string.IsNullOrEmpty(GeometryDetail)) GeometryDetail = "Simplified";
            // Sets saved before the size was a setting used 3/32".
            if (ValueTextSize <= 0) ValueTextSize = 3.0 / 32.0;
            if (ColorScaleMax < 0) ColorScaleMax = 0;
            ResultsTableTitle ??= ResultsTable.DefaultTitle;
            EmergencyLuminaires ??= new List<EmergencyLuminaireSetting>();
            ExcludedElementKeys ??= new List<string>(); ExcludedCategories ??= new List<string>();
            ExcludedPoints ??= new List<ExcludedCalcPoint>(); ZoneAnnotations ??= new List<ZoneAnnotation>();
            PointAnnotations ??= new List<CalcPointAnnotation>();
            CalculatedZoneKeys ??= new List<string>();
            if (EmergencyLuminaires.Any(e => e == null || double.IsNaN(e.Output) || double.IsInfinity(e.Output) || e.Output < 0 || e.Output > 1))
                throw new InvalidDataException("Emergency output must be between 0 and 1 (0–100%).");
            ZoneTargets ??= new List<ZoneTargetSetting>();
            ResultsTables ??= new List<ResultsTablePlacement>();
            ZoneTargets.RemoveAll(z => z == null || string.IsNullOrWhiteSpace(z.Target));
            foreach (ZoneTargetSetting z in ZoneTargets) z.ZoneUniqueId ??= string.Empty;
            ResultsTables.RemoveAll(r => r == null);
        }

        public CalcSetDefinition CloneDefinition(string newName)
        {
            string json = new LightingProjectSettings { CalcSets = { this } }.ToJson();
            CalcSetDefinition copy = LightingProjectSettings.FromJson(json).CalcSets[0];
            copy.Name = newName;
            copy.CreatedElementUniqueIds = new List<string>();
            copy.ZoneAnnotations.Clear(); copy.PointAnnotations.Clear();
            copy.CalculatedZoneKeys.Clear();
            copy.SummaryViewUniqueId = string.Empty;
            copy.LastRun = string.Empty;
            copy.LastSummary = string.Empty;
            copy.LastReportPath = string.Empty;
            copy.ResultsTables = new List<ResultsTablePlacement>();
            return copy;
        }
    }

    [DataContract]
    public class EmergencyLuminaireSetting
    {
        [DataMember(Order = 1)] public string Key { get; set; } = string.Empty;
        [DataMember(Order = 2)] public double Output { get; set; } = 1;
    }
    [DataContract]
    public class ExcludedCalcPoint
    {
        [DataMember(Order = 1)] public string ZoneKey { get; set; } = string.Empty;
        [DataMember(Order = 2)] public double X { get; set; }
        [DataMember(Order = 3)] public double Y { get; set; }
        [DataMember(Order = 4)] public double Z { get; set; }
    }
    [DataContract]
    public class ZoneAnnotation
    {
        [DataMember(Order = 1)] public string ZoneKey { get; set; } = string.Empty;
        [DataMember(Order = 2)] public List<string> ElementIds { get; set; } = new List<string>();
    }
    [DataContract]
    public class CalcPointAnnotation : ExcludedCalcPoint
    {
        [DataMember(Order = 5)] public List<string> ElementIds { get; set; } = new List<string>();
    }

    /// <summary>A zone's own target, as typed (e.g. "20, 3" = 20 avg, 3:1 avg/min; "none" = no target).</summary>
    [DataContract]
    public class ZoneTargetSetting
    {
        [DataMember(Order = 1)]
        public string ZoneUniqueId { get; set; } = string.Empty;

        [DataMember(Order = 2)]
        public string Target { get; set; } = string.Empty;
    }

    /// <summary>One sheet's results table: the drafting view drawn for it.</summary>
    [DataContract]
    public class ResultsTablePlacement
    {
        [DataMember(Order = 1)]
        public string SheetUniqueId { get; set; } = string.Empty;

        [DataMember(Order = 2)]
        public string ViewUniqueId { get; set; } = string.Empty;
    }
}
