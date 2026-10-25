using Autodesk.Revit.DB.ExtensibleStorage;
using BimGo.Extraction;
using BimGo.Live;
using BimGo.Scene;

// The class belongs to the Bridge namespace
namespace BimGo.Bridge
{
    /// <summary>
    /// Round Z-B: keeps one comment marker per BimGo comment in a Revit model (Gavin's "BimGo Comment Marker" family:
    /// adaptive, one point; its "Default" type). Live sessions call it for markers.sync; the COMMENT MARKERS button calls
    /// it with the model folder's (or a .bimgo file's) comments.
    /// <list type="bullet">
    /// <item>The family is loaded when the model doesn't have it: Options' path, else the copy shipped inside the add-in
    /// (written to %LocalAppData%\BimGo\Families).</item>
    /// <item>Markers are matched by BimGo_Id; shared parameters are found by GUID (<see cref="MarkerFamily"/>).</item>
    /// <item>The ids BimGo has placed are kept in the model (extensible storage on a DataStorage element), so a marker
    /// deleted in Revit stays gone until RE-PLACE ALL, for everyone working on the model.</item>
    /// <item>One "BimGo: comment markers" transaction, only when something differs (no undo entry otherwise); warnings
    /// swallowed, errors roll back. Elements another user owns (workshared) are skipped.</item>
    /// </list>
    /// Revit API thread only; never throws.
    /// </summary>
    internal static class MarkerSync
    {
        #region Fields

        private static readonly Guid SCHEMA_GUID = new("b1a9c0de-2026-4d5a-9e0b-0c0a2b5e1d7a");

        // r6: each marker's comment id, kept on the instance itself (matching doesn't depend on reading BimGo_Id back)
        private static readonly Guid TAG_SCHEMA_GUID = new("b1a9c0de-2026-4d5a-9e0b-0c0a2b5e1d7b");
        private const string TAG_SCHEMA_NAME = "BimGoCommentMarker";
        private const string FIELD_COMMENT_ID = "CommentId";
        private const string SCHEMA_NAME = "BimGoCommentMarkers";
        private const string FIELD_PLACED = "Placed";

        #endregion

        #region Sync

        /// <summary>
        /// Brings the model's markers in line with the comments (see <see cref="CommentMarkers.Plan"/>).
        /// </summary>
        public static MarkersResultPayload Sync(Document doc, MarkersSyncPayload payload, LaunchSettings settings)
        {
            var result = new MarkersResultPayload();
            if (doc == null || !doc.IsValidObject) { return Fail(result, "The model is no longer open in Revit"); }
            if (doc.IsFamilyDocument) { return Fail(result, "Comment markers go in a project, not a family"); }
            if (doc.IsReadOnly) { return Fail(result, "The model is read-only in Revit"); }
            payload ??= new MarkersSyncPayload();
            settings ??= LaunchSettings.LoadOrDefault();

            try
            {
                Family family = FindFamily(doc);
                List<ExistingMarker> existing = family == null ? new List<ExistingMarker>() : ReadExisting(doc, family);
                Schema schema = PlacedSchema();
                DataStorage storage = FindStorage(doc, schema);
                HashSet<string> placedBefore = ReadPlaced(storage, schema);

                MarkerPlan plan = CommentMarkers.Plan(payload.Markers ?? new List<MarkerRecord>(), existing, placedBefore,
                    payload.Full, payload.ReplaceAll, settings.CommentMarkersRemoveClosed);
                result.DeletedInRevit = plan.DeletedInRevit;
                bool memoryChanged = !plan.PlacedAfter.SetEquals(placedBefore);
                if (!plan.HasWork && !memoryChanged)
                {
                    result.Success = true;
                    result.Message = CommentMarkers.Summary(result);
                    return result;
                }

                // The family file, found before the transaction starts (nothing to roll back if it's missing)
                string familyPath = null;
                if (family == null && plan.Place.Count > 0)
                {
                    familyPath = FamilyFile(settings, out string why);
                    if (familyPath == null) { return Fail(result, why); }
                }

                var failures = new RevitEditor.SwallowFailures();
                using var transaction = new Transaction(doc, MarkerFamily.TRANSACTION_NAME);
                FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
                options.SetFailuresPreprocessor(failures);
                options.SetClearAfterRollback(true);
                transaction.SetFailureHandlingOptions(options);
                transaction.Start();

                string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
                string idProblem = null;
                string parameterWarning = null;
                (FamilyInstance Instance, string Id)? firstPlaced = null;
                try
                {
                    // Removed first (their ids are free again), then changes, then new markers
                    foreach (ExistingMarker marker in plan.Remove)
                    {
                        var id = new ElementId(marker.ElementId);
                        if (OwnedByOther(doc, id)) { result.Skipped++; continue; }
                        try
                        {
                            doc.Delete(id);
                            result.Removed++;
                        }
                        catch (Exception ex)
                        {
                            result.Skipped++;
                            Utilities.Log_Utils.Write($"Comment marker {marker.Values.Id}: not removed: {ex.Message}");
                        }
                    }

                    foreach ((ExistingMarker marker, MarkerRecord wanted, bool move, bool values) in plan.Change)
                    {
                        var id = new ElementId(marker.ElementId);
                        if (OwnedByOther(doc, id) || doc.GetElement(id) is not FamilyInstance instance) { result.Skipped++; continue; }
                        try
                        {
                            if (move)
                            {
                                SetPoint(doc, instance, ToFeet(wanted));
                                result.Moved++;
                            }
                            if (TagOf(instance) != wanted.Id) { Tag(instance, wanted.Id); }
                            if (values)
                            {
                                WriteValues(instance, wanted, stamp);
                                result.Updated++;
                            }
                            else if (move)
                            {
                                SetText(instance, MarkerFamily.SYNCED, stamp);
                            }
                        }
                        catch (Exception ex)
                        {
                            result.Skipped++;
                            Utilities.Log_Utils.Write($"Comment marker {wanted.Id}: not updated: {ex.Message}");
                        }
                    }

                    if (plan.Place.Count > 0)
                    {
                        if (family == null)
                        {
                            if (!doc.LoadFamily(familyPath, out family) || family == null)
                            {
                                family = FindFamily(doc);
                                if (family == null) { throw new InvalidOperationException($"Revit couldn't load {Path.GetFileName(familyPath)}"); }
                            }
                            Utilities.Log_Utils.Write($"Comment markers: loaded {familyPath}.");
                        }
                        FamilySymbol symbol = PickType(doc, family) ?? throw new InvalidOperationException($"“{MarkerFamily.FAMILY_NAME}” has no type");
                        if (!symbol.IsActive)
                        {
                            symbol.Activate();
                            doc.Regenerate();
                        }
                        bool adaptive = AdaptiveComponentFamilyUtils.IsAdaptiveComponentFamily(family);

                        foreach (MarkerRecord wanted in plan.Place)
                        {
                            FamilyInstance instance;
                            try
                            {
                                instance = adaptive
                                    ? AdaptiveComponentInstanceUtils.CreateAdaptiveComponentInstance(doc, symbol)
                                    : doc.Create.NewFamilyInstance(ToFeet(wanted), symbol, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                                if (adaptive) { SetPoint(doc, instance, ToFeet(wanted)); }
                                Tag(instance, wanted.Id);
                                WriteValues(instance, wanted, stamp);
                            }
                            catch (Exception ex)
                            {
                                result.Skipped++;
                                plan.PlacedAfter.Remove(wanted.Id);
                                Utilities.Log_Utils.Write($"Comment marker {wanted.Id}: not placed: {ex.Message}");
                                continue;
                            }

                            // Without its id a marker can't be found again (it would be placed again at every sync)
                            if (TagOf(instance) != wanted.Id)
                            {
                                idProblem = "the marker couldn't be tagged with its comment id (see the log)";
                                Utilities.Log_Utils.Write($"Comment markers: tag on {instance.Id.Value} reads “{TagOf(instance)}”, wanted {wanted.Id}.");
                                break;
                            }
                            firstPlaced ??= (instance, wanted.Id);
                            result.Placed++;
                        }

                        // r6: the parameters are checked after a regenerate (adaptive instances may only settle then)
                        if (idProblem == null && firstPlaced.HasValue)
                        {
                            (FamilyInstance placedInstance, string placedId) = firstPlaced.Value;
                            doc.Regenerate();
                            if (GetText(placedInstance, MarkerFamily.ID) != placedId)
                            {
                                parameterWarning = " · the marker parameters stayed empty (details in the log)";
                                DescribeIdProblem(placedInstance, placedId);
                            }
                        }
                    }

                    if (idProblem != null)
                    {
                        transaction.RollBack();
                        return Fail(result, idProblem);
                    }

                    // Remember what BimGo has placed (in the model, for everyone)
                    if (!plan.PlacedAfter.SetEquals(placedBefore))
                    {
                        if (storage != null && OwnedByOther(doc, storage.Id))
                        {
                            Utilities.Log_Utils.Write("Comment markers: the placed list is owned by someone else; not updated this time.");
                        }
                        else
                        {
                            storage ??= DataStorage.Create(doc);
                            var entity = new Entity(schema);
                            entity.Set(FIELD_PLACED, CommentMarkers.FormatPlaced(plan.PlacedAfter));
                            storage.SetEntity(entity);
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (transaction.HasStarted() && !transaction.HasEnded()) { transaction.RollBack(); }
                    Utilities.Log_Utils.Write($"Comment markers failed: {ex}");
                    return Fail(result, Explain(ex));
                }

                if (!result.Changed && transaction.GetStatus() == TransactionStatus.Started && plan.PlacedAfter.SetEquals(placedBefore))
                {
                    transaction.RollBack(); // nothing happened after all: no undo entry
                }
                else if (transaction.Commit() != TransactionStatus.Committed)
                {
                    return Fail(result, failures.FirstError ?? "Revit rolled the markers back");
                }

                result.Success = true;
                result.Message = CommentMarkers.Summary(result) + parameterWarning;
                Utilities.Log_Utils.Write(result.Message);
                return result;
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Comment markers failed: {ex}");
                return Fail(result, Explain(ex));
            }
        }

        #endregion

        #region Family

        /// <summary>The marker family in the model, or null.</summary>
        private static Family FindFamily(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .FirstOrDefault(f => string.Equals(f.Name, MarkerFamily.FAMILY_NAME, StringComparison.OrdinalIgnoreCase));

        /// <summary>Its "Default" type, else its first.</summary>
        private static FamilySymbol PickType(Document doc, Family family)
        {
            List<FamilySymbol> types = family.GetFamilySymbolIds().Select(doc.GetElement).OfType<FamilySymbol>().ToList();
            return types.FirstOrDefault(t => string.Equals(t.Name, MarkerFamily.TYPE_NAME, StringComparison.OrdinalIgnoreCase)) ?? types.FirstOrDefault();
        }

        /// <summary>
        /// The family file to load: Options' path when set, else the copy inside the add-in, written to
        /// %LocalAppData%\BimGo\Families (again when it differs).
        /// </summary>
        private static string FamilyFile(LaunchSettings settings, out string why)
        {
            why = null;
            string custom = settings.CommentMarkerFamily;
            if (!string.IsNullOrWhiteSpace(custom))
            {
                if (File.Exists(custom)) { return custom; }
                why = $"The marker family set in Options isn't there: {custom}";
                return null;
            }
            try
            {
                string name = Globals.ADDIN_ASSEMBLY.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("." + MarkerFamily.FILE_NAME, StringComparison.OrdinalIgnoreCase));
                if (name == null)
                {
                    why = "The add-in doesn't carry the marker family: set its path in Options → Player";
                    return null;
                }
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BimGo", "Families");
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, MarkerFamily.FILE_NAME);
                using Stream stream = Globals.ADDIN_ASSEMBLY.GetManifestResourceStream(name);
                if (stream == null)
                {
                    why = "The add-in's marker family couldn't be read";
                    return null;
                }
                // Compared byte for byte: Revit files grow in whole blocks, so a re-saved family is often the same length
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                byte[] shipped = memory.ToArray();
                if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(shipped))
                {
                    File.WriteAllBytes(path, shipped);
                    Utilities.Log_Utils.Write($"Comment markers: wrote the shipped family to {path}.");
                }
                return path;
            }
            catch (Exception ex)
            {
                why = $"The marker family couldn't be prepared: {ex.Message}";
                return null;
            }
        }

        #endregion

        #region Markers

        /// <summary>The model's markers (every instance of the family), with their values and points.</summary>
        private static List<ExistingMarker> ReadExisting(Document doc, Family family)
        {
            var markers = new List<ExistingMarker>();
            foreach (ElementId typeId in family.GetFamilySymbolIds())
            {
                foreach (FamilyInstance instance in new FilteredElementCollector(doc).WherePasses(new FamilyInstanceFilter(doc, typeId)).Cast<FamilyInstance>())
                {
                    XYZ point = GetPoint(doc, instance);
                    markers.Add(new ExistingMarker
                    {
                        ElementId = instance.Id.Value,
                        Values = new MarkerRecord
                        {
                            Id = TagOf(instance) is { Length: > 0 } tagged ? tagged : GetText(instance, MarkerFamily.ID),
                            X = point == null ? double.NaN : point.X * SceneExtractor.FT,
                            Y = point == null ? double.NaN : point.Y * SceneExtractor.FT,
                            Z = point == null ? double.NaN : point.Z * SceneExtractor.FT,
                            Title = GetText(instance, MarkerFamily.TITLE),
                            Comment = GetText(instance, MarkerFamily.COMMENT),
                            Status = GetText(instance, MarkerFamily.STATUS),
                            Priority = GetText(instance, MarkerFamily.PRIORITY),
                            Author = GetText(instance, MarkerFamily.AUTHOR),
                            AssignedTo = GetText(instance, MarkerFamily.ASSIGNED_TO),
                            Created = GetText(instance, MarkerFamily.CREATED),
                            Updated = GetText(instance, MarkerFamily.UPDATED),
                            Level = GetText(instance, MarkerFamily.LEVEL),
                            Element = GetText(instance, MarkerFamily.ELEMENT),
                            Replies = GetInt(instance, MarkerFamily.REPLIES),
                            Picture = GetText(instance, MarkerFamily.PICTURE),
                            IsOpen = GetInt(instance, MarkerFamily.IS_OPEN) != 0,
                            IsInProgress = GetInt(instance, MarkerFamily.IS_IN_PROGRESS) != 0,
                            IsClosed = GetInt(instance, MarkerFamily.IS_CLOSED) != 0,
                            IsHighPriority = GetInt(instance, MarkerFamily.IS_HIGH_PRIORITY) != 0
                        }
                    });
                }
            }
            return markers;
        }

        /// <summary>Writes every shared parameter (only values that differ are set).</summary>
        private static void WriteValues(FamilyInstance instance, MarkerRecord m, string stamp)
        {
            SetText(instance, MarkerFamily.ID, m.Id);
            SetText(instance, MarkerFamily.TITLE, m.Title);
            SetText(instance, MarkerFamily.COMMENT, m.Comment);
            SetText(instance, MarkerFamily.STATUS, m.Status);
            SetText(instance, MarkerFamily.PRIORITY, m.Priority);
            SetText(instance, MarkerFamily.AUTHOR, m.Author);
            SetText(instance, MarkerFamily.ASSIGNED_TO, m.AssignedTo);
            SetText(instance, MarkerFamily.CREATED, m.Created);
            SetText(instance, MarkerFamily.UPDATED, m.Updated);
            SetText(instance, MarkerFamily.LEVEL, m.Level);
            SetText(instance, MarkerFamily.ELEMENT, m.Element);
            SetInt(instance, MarkerFamily.REPLIES, m.Replies);
            SetText(instance, MarkerFamily.PICTURE, m.Picture);
            SetInt(instance, MarkerFamily.IS_OPEN, m.IsOpen ? 1 : 0);
            SetInt(instance, MarkerFamily.IS_IN_PROGRESS, m.IsInProgress ? 1 : 0);
            SetInt(instance, MarkerFamily.IS_CLOSED, m.IsClosed ? 1 : 0);
            SetInt(instance, MarkerFamily.IS_HIGH_PRIORITY, m.IsHighPriority ? 1 : 0);
            SetText(instance, MarkerFamily.SYNCED, stamp);
        }

        private static string GetText(Element element, Guid guid)
        {
            Parameter p = element.get_Parameter(guid);
            if (p == null || !p.HasValue) { return string.Empty; }
            return p.StorageType == StorageType.String ? p.AsString() ?? string.Empty : p.AsValueString() ?? string.Empty;
        }

        private static int GetInt(Element element, Guid guid)
        {
            Parameter p = element.get_Parameter(guid);
            return p != null && p.HasValue && p.StorageType == StorageType.Integer ? p.AsInteger() : 0;
        }

        private static void SetText(Element element, Guid guid, string value)
        {
            Parameter p = element.get_Parameter(guid);
            if (p == null || p.StorageType != StorageType.String) { return; }
            value ??= string.Empty;
            if ((p.AsString() ?? string.Empty) != value) { p.Set(value); }
        }

        private static void SetInt(Element element, Guid guid, int value)
        {
            Parameter p = element.get_Parameter(guid);
            if (p == null || p.StorageType != StorageType.Integer) { return; }
            if (!p.HasValue || p.AsInteger() != value) { p.Set(value); }
        }

        /// <summary>The marker's point (feet): the first adaptive placement point, else its location point.</summary>
        private static XYZ GetPoint(Document doc, FamilyInstance instance)
        {
            IList<ElementId> points = AdaptiveComponentInstanceUtils.IsAdaptiveComponentInstance(instance)
                ? AdaptiveComponentInstanceUtils.GetInstancePlacementPointElementRefIds(instance)
                : null;
            if (points != null && points.Count > 0 && doc.GetElement(points[0]) is ReferencePoint reference) { return reference.Position; }
            return (instance.Location as LocationPoint)?.Point;
        }

        private static void SetPoint(Document doc, FamilyInstance instance, XYZ feet)
        {
            IList<ElementId> points = AdaptiveComponentInstanceUtils.IsAdaptiveComponentInstance(instance)
                ? AdaptiveComponentInstanceUtils.GetInstancePlacementPointElementRefIds(instance)
                : null;
            if (points != null && points.Count > 0 && doc.GetElement(points[0]) is ReferencePoint reference)
            {
                reference.Position = feet;
                return;
            }
            if (instance.Location is LocationPoint location) { location.Point = feet; }
        }

        private static XYZ ToFeet(MarkerRecord m) => new(m.X / SceneExtractor.FT, m.Y / SceneExtractor.FT, m.Z / SceneExtractor.FT);

        /// <summary>True when another user owns the element (workshared models).</summary>
        private static bool OwnedByOther(Document doc, ElementId id)
        {
            if (!doc.IsWorkshared) { return false; }
            try { return WorksharingUtils.GetCheckoutStatus(doc, id) == CheckoutStatus.OwnedByOtherUser; }
            catch { return false; }
        }

        /// <summary>r6: the tag schema (made once per Revit session).</summary>
        private static Schema TagSchema()
        {
            Schema schema = Schema.Lookup(TAG_SCHEMA_GUID);
            if (schema != null) { return schema; }
            var builder = new SchemaBuilder(TAG_SCHEMA_GUID);
            builder.SetSchemaName(TAG_SCHEMA_NAME);
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.SetDocumentation("BimGo: the comment this marker shows.");
            builder.AddSimpleField(FIELD_COMMENT_ID, typeof(string));
            return builder.Finish();
        }

        /// <summary>The comment id kept on a marker, or empty.</summary>
        private static string TagOf(Element element)
        {
            Entity entity = element.GetEntity(TagSchema());
            return entity.IsValid() ? entity.Get<string>(FIELD_COMMENT_ID) ?? string.Empty : string.Empty;
        }

        private static void Tag(Element element, string id)
        {
            var entity = new Entity(TagSchema());
            entity.Set(FIELD_COMMENT_ID, id ?? string.Empty);
            element.SetEntity(entity);
        }

        /// <summary>
        /// Why BimGo_Id didn't take (r5: what Revit says about the parameter, rather than a guess): found by GUID or
        /// only by name, its storage type, read-only / user-modifiable flags, whether a formula drives it, what Set
        /// returned or threw, and what it reads back. Every shared parameter on the instance goes to the log.
        /// </summary>
        private static string DescribeIdProblem(FamilyInstance instance, string id)
        {
            var log = new System.Text.StringBuilder();
            log.AppendLine($"Comment markers: BimGo_Id didn't take on {instance.Id.Value} ({instance.Symbol?.Family?.Name} : {instance.Symbol?.Name}). Shared parameters on it:");
            foreach (Parameter q in instance.Parameters)
            {
                if (q?.Definition == null || !q.IsShared) { continue; }
                string guid;
                try { guid = q.GUID.ToString(); }
                catch { guid = "?"; }
                log.AppendLine($"  {q.Definition.Name} {guid} {q.StorageType} readOnly={q.IsReadOnly} userModifiable={q.UserModifiable} value=“{q.AsString() ?? q.AsValueString()}”");
            }

            Parameter p = instance.get_Parameter(MarkerFamily.ID);
            string found = "by GUID";
            if (p == null)
            {
                p = instance.LookupParameter("BimGo_Id");
                found = p == null ? null : "only by name (GUID " + (p.IsShared ? p.GUID.ToString() : "not shared") + ")";
            }
            if (p == null)
            {
                Utilities.Log_Utils.Write(log.ToString());
                return $"the model's “{MarkerFamily.FAMILY_NAME}” has no BimGo_Id (GUID {MarkerFamily.ID}). The log lists the parameters it does have.";
            }

            string set;
            try { set = p.Set(id) ? "Set returned true" : "Set returned false"; }
            catch (Exception ex) { set = "Set threw: " + ex.Message; }
            string back = p.AsString() ?? p.AsValueString() ?? "(nothing)";
            string summary = $"BimGo_Id found {found}: {p.StorageType}, read-only {p.IsReadOnly}, user-modifiable {p.UserModifiable}, {set}, reads back “{back}”";
            log.AppendLine("  " + summary);
            Utilities.Log_Utils.Write(log.ToString());
            return summary + ". Details in the log.";
        }

        #endregion

        #region Placed list

        /// <summary>The placed list's schema (made once per Revit session).</summary>
        private static Schema PlacedSchema()
        {
            Schema schema = Schema.Lookup(SCHEMA_GUID);
            if (schema != null) { return schema; }
            var builder = new SchemaBuilder(SCHEMA_GUID);
            builder.SetSchemaName(SCHEMA_NAME);
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.SetDocumentation("BimGo: the comment ids whose markers BimGo has placed in this model (one per line).");
            builder.AddSimpleField(FIELD_PLACED, typeof(string));
            return builder.Finish();
        }

        private static DataStorage FindStorage(Document doc, Schema schema) =>
            new FilteredElementCollector(doc).OfClass(typeof(DataStorage)).Cast<DataStorage>()
                .FirstOrDefault(d => d.GetEntity(schema)?.IsValid() == true);

        private static HashSet<string> ReadPlaced(DataStorage storage, Schema schema)
        {
            if (storage == null) { return new HashSet<string>(StringComparer.Ordinal); }
            Entity entity = storage.GetEntity(schema);
            return CommentMarkers.ParsePlaced(entity.IsValid() ? entity.Get<string>(FIELD_PLACED) : null);
        }

        #endregion

        #region Helpers

        private static MarkersResultPayload Fail(MarkersResultPayload result, string message)
        {
            Utilities.Log_Utils.Write($"Comment markers: {message}");
            result.Success = false;
            result.Message = "Revit markers: " + message;
            result.Placed = result.Updated = result.Moved = result.Removed = 0;
            return result;
        }

        /// <summary>A failure in words (a family saved in a newer Revit says so).</summary>
        private static string Explain(Exception ex)
        {
            string message = ex.Message ?? "unknown error";
            if (message.Contains("newer", StringComparison.OrdinalIgnoreCase))
            {
                return $"the marker family couldn't be loaded ({message}). Save it in the oldest Revit you use (2025).";
            }
            return message;
        }

        #endregion
    }
}
