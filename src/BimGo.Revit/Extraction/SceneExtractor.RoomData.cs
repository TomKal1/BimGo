using BimGo.Scene;

// The class belongs to the Extraction namespace
namespace BimGo.Extraction
{
    /// <summary>
    /// Round Z-A: every non-empty instance parameter of a room for the walkthrough's room data overlay (tags, the
    /// parameter picker and the target check): the display text as Revit shows it, and a number in the model's display
    /// units where the value is numeric (doubles converted from internal units, integers as they are, text read for a
    /// leading number), so "Area" and a "Required area" compare in the same units. Read-only; unreadable parameters are
    /// left out.
    /// </summary>
    internal sealed partial class SceneExtractor
    {
        #region Fields

        // Display unit per spec for the source being read (reset per source: links have their own units)
        private readonly Dictionary<string, ForgeTypeId> _roomUnits = new(StringComparer.Ordinal);
        private Document _roomUnitsDoc;

        #endregion

        #region Room parameters

        /// <summary>
        /// The room's parameters (name order as Revit lists them, the first of a repeated name kept), never null.
        /// </summary>
        private RoomParameter[] ReadRoomParameters(Autodesk.Revit.DB.Architecture.Room room)
        {
            if (!ReferenceEquals(_roomUnitsDoc, _src.Doc))
            {
                _roomUnits.Clear();
                _roomUnitsDoc = _src.Doc;
            }

            var list = new List<RoomParameter>(64);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ParameterSet set;
            try { set = room.Parameters; }
            catch { return Array.Empty<RoomParameter>(); }

            foreach (Parameter parameter in set)
            {
                try
                {
                    if (parameter?.Definition == null || !parameter.HasValue) { continue; }
                    string name = parameter.Definition.Name;
                    if (string.IsNullOrWhiteSpace(name) || seen.Contains(name)) { continue; }

                    string text = ParameterText(parameter);
                    if (string.IsNullOrWhiteSpace(text)) { continue; }
                    double number = RoomParameterNumber(parameter, text);
                    seen.Add(name);
                    list.Add(new RoomParameter(name, text, number));
                }
                catch
                {
                    // Unreadable parameter: leave it out for this room
                }
            }
            return list.ToArray();
        }

        /// <summary>
        /// A parameter's value as a number in the model's display units (NaN when it isn't numeric).
        /// </summary>
        private double RoomParameterNumber(Parameter parameter, string text)
        {
            switch (parameter.StorageType)
            {
                case StorageType.Double:
                {
                    double raw = parameter.AsDouble();
                    ForgeTypeId unit = RoomDisplayUnit(parameter);
                    return unit == null ? raw : UnitUtils.ConvertFromInternalUnits(raw, unit);
                }
                case StorageType.Integer:
                    return parameter.AsInteger();
                case StorageType.String:
                    return RoomValues.ParseNumber(text);
                default:
                    return double.NaN;
            }
        }

        /// <summary>The display unit of a measurable double parameter in the source model, or null (use the raw value).</summary>
        private ForgeTypeId RoomDisplayUnit(Parameter parameter)
        {
            ForgeTypeId spec;
            try { spec = parameter.Definition.GetDataType(); }
            catch { return null; }
            if (spec == null || string.IsNullOrEmpty(spec.TypeId)) { return null; }
            if (_roomUnits.TryGetValue(spec.TypeId, out ForgeTypeId cached)) { return cached; }

            ForgeTypeId unit = null;
            try
            {
                if (UnitUtils.IsMeasurableSpec(spec)) { unit = _src.Doc.GetUnits().GetFormatOptions(spec).GetUnitTypeId(); }
            }
            catch
            {
                unit = null;
            }
            _roomUnits[spec.TypeId] = unit;
            return unit;
        }

        #endregion
    }
}
