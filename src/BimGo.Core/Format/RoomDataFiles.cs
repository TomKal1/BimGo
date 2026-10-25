using BimGo.Scene;

// The class belongs to the Format namespace
namespace BimGo.Format
{
    /// <summary>
    /// Round Z-A: rooms.json (every room's parameters, pooled) to and from the rooms' <see cref="RoomParameter"/> arrays.
    /// </summary>
    public static class RoomDataFiles
    {
        /// <summary>
        /// The pooled parameters of the rooms (rows in room order), or null when no room has any (the entry is left out).
        /// A value text is pooled with the number of its first use.
        /// </summary>
        public static RoomDataDto ToDto(IReadOnlyList<RoomInfo> rooms)
        {
            if (rooms == null || rooms.Count == 0 || !rooms.Any(r => r?.Parameters != null && r.Parameters.Length > 0)) { return null; }
            var names = new List<string>();
            var nameIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            var values = new List<string>();
            var numbers = new List<double?>();
            var valueIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            var rows = new int[rooms.Count][];
            for (int r = 0; r < rooms.Count; r++)
            {
                RoomParameter[] parameters = rooms[r]?.Parameters;
                if (parameters == null || parameters.Length == 0) { continue; }
                int[] row = new int[parameters.Length * 2];
                for (int i = 0; i < parameters.Length; i++)
                {
                    RoomParameter p = parameters[i];
                    if (!nameIndex.TryGetValue(p.Name, out int n))
                    {
                        n = names.Count;
                        names.Add(p.Name);
                        nameIndex[p.Name] = n;
                    }
                    if (!valueIndex.TryGetValue(p.Value, out int v))
                    {
                        v = values.Count;
                        values.Add(p.Value);
                        numbers.Add(p.IsNumeric ? Math.Round(p.Number, 6) : null);
                        valueIndex[p.Value] = v;
                    }
                    row[i * 2] = n;
                    row[i * 2 + 1] = v;
                }
                rows[r] = row;
            }
            return new RoomDataDto { Names = names.ToArray(), Values = values.ToArray(), Numbers = numbers.ToArray(), Rows = rows };
        }

        /// <summary>
        /// Reads rooms.json rows back (bad indices are skipped; a missing entry gives every room none).
        /// </summary>
        public sealed class Pools
        {
            private readonly RoomDataDto _dto;

            /// <summary>Wraps the entry (null: no entry).</summary>
            public Pools(RoomDataDto dto)
            {
                _dto = dto;
                if (_dto == null) { return; }
                _dto.Names ??= Array.Empty<string>();
                _dto.Values ??= Array.Empty<string>();
                _dto.Numbers ??= Array.Empty<double?>();
                _dto.Rows ??= Array.Empty<int[]>();
            }

            /// <summary>The parameters of the room at this position in model.json (never null).</summary>
            public RoomParameter[] Row(int room)
            {
                if (_dto == null || (uint)room >= (uint)_dto.Rows.Length || _dto.Rows[room] == null) { return Array.Empty<RoomParameter>(); }
                int[] row = _dto.Rows[room];
                var list = new List<RoomParameter>(row.Length / 2);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i + 1 < row.Length; i += 2)
                {
                    int n = row[i], v = row[i + 1];
                    if ((uint)n >= (uint)_dto.Names.Length || (uint)v >= (uint)_dto.Values.Length) { continue; }
                    string name = _dto.Names[n];
                    if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) { continue; }
                    double number = (uint)v < (uint)_dto.Numbers.Length && _dto.Numbers[v] is double d && double.IsFinite(d) ? d : double.NaN;
                    list.Add(new RoomParameter(name, _dto.Values[v], number));
                }
                return list.ToArray();
            }
        }
    }
}
