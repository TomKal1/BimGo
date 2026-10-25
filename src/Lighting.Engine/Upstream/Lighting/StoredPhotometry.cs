using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace WWTools.Lighting
{
    /// <summary>
    /// A fixture type's IES data as found by Check IES Data, stored in the model so calc runs
    /// (and other computers, e.g. Model Delta's print farm) never have to open the family or
    /// reach an IES folder.
    /// </summary>
    [DataContract]
    public class StoredPhotometry
    {
        [DataMember(Order = 1)]
        public string TypeKey { get; set; } = string.Empty;

        [DataMember(Order = 2)]
        public string TypeName { get; set; } = string.Empty;

        /// <summary>The IES file text; empty when none was found.</summary>
        [DataMember(Order = 3)]
        public string IesText { get; set; } = string.Empty;

        /// <summary>Where the data came from when checked ("Embedded IES (family): x.ies", "IES file: …").</summary>
        [DataMember(Order = 4)]
        public string Source { get; set; } = string.Empty;

        [DataMember(Order = 5)]
        public string WebFileName { get; set; } = string.Empty;

        /// <summary>The type's light-source settings when checked; a different value means the type changed since.</summary>
        [DataMember(Order = 6)]
        public string Fingerprint { get; set; } = string.Empty;

        /// <summary>When it was checked (local time, yyyy-MM-dd HH:mm).</summary>
        [DataMember(Order = 7)]
        public string CheckedAt { get; set; } = string.Empty;

        /// <summary>Why no IES data was found, when it wasn't.</summary>
        [DataMember(Order = 8)]
        public string Problem { get; set; } = string.Empty;

        public bool HasIes => !string.IsNullOrEmpty(IesText);

        public string ToJson()
        {
            var serializer = new DataContractJsonSerializer(typeof(StoredPhotometry));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, this);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        public static StoredPhotometry? FromJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var serializer = new DataContractJsonSerializer(typeof(StoredPhotometry));
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    var record = serializer.ReadObject(stream) as StoredPhotometry;
                    if (record == null) return null;
                    record.TypeKey ??= string.Empty;
                    record.TypeName ??= string.Empty;
                    record.IesText ??= string.Empty;
                    record.Source ??= string.Empty;
                    record.WebFileName ??= string.Empty;
                    record.Fingerprint ??= string.Empty;
                    record.CheckedAt ??= string.Empty;
                    record.Problem ??= string.Empty;
                    return record;
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>A stable text fingerprint of a type's light-source settings.</summary>
        public static string MakeFingerprint(IEnumerable<string> parts) =>
            string.Join("|", parts ?? Array.Empty<string>());
    }
}
