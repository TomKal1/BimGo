using System.Collections.Generic;

namespace WWTools.Calcs
{
    /// <summary>
    /// The parts of a saved calc set that every calc shares: which zones it covers, which plan
    /// views get its output, and what it placed last time. Lighting and airflow calc sets each
    /// keep their own data contract and implement this, so the shared zone reader and run
    /// helpers never change what is stored in a model.
    /// </summary>
    public interface ICalcSet
    {
        string Name { get; }

        /// <summary>"Spaces", "Rooms" or "LinkedRooms".</summary>
        string ZoneSource { get; }

        /// <summary>The link instance for "LinkedRooms".</summary>
        string LinkInstanceUniqueId { get; }

        /// <summary>The zones to calculate; empty = every placed zone in the source.</summary>
        List<string> ZoneUniqueIds { get; }

        /// <summary>Plan views that get the calc's output (each shows the zones on its level).</summary>
        List<string> OutputViewUniqueIds { get; }

        /// <summary>Elements the last run placed on views (removed before the next run).</summary>
        List<string> CreatedElementUniqueIds { get; set; }
    }
}
