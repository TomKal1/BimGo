using System.Text;
using BimGo.Platform;
using BimGo.Audio;
using BimGo.Format;
using BimGo.Game.Guns;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// Round Y-A: the Measure gun's measurements are kept. Revit sessions: <c>measurements.json</c> in the model folder,
    /// written on every change (mirrored beside the model when sharing is on, embedded by Revit's Export). .bimgo files:
    /// inside the file, saved with it (a change marks the file dirty). E (Measure gun) exports a CSV.
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Fields

        private MeasureGun _measureGun;
        private string _measurementsPath; // null: embedded in the .bimgo
        private int _measurementRevision, _savedMeasurementRevision;

        #endregion

        #region Setup

        /// <summary>
        /// Loads the saved measurements into the Measure gun (after the guns are made): the .bimgo's, else the model
        /// folder's.
        /// </summary>
        private void InitialiseMeasurements()
        {
            if (_measureGun == null) { return; }
            MeasurementDocument document;
            if (IsFileMode)
            {
                _measurementsPath = null;
                document = _options.Document?.Measurements;
            }
            else
            {
                _measurementsPath = MeasurementFiles.SidecarFor(Scene.CommentsPath);
                document = _measurementsPath == null ? null : MeasurementFiles.Read(_measurementsPath, out _);
            }
            _measureGun.LoadFrom(document, Scene.OriginOffset);
            if (_measureGun.Count > 0) { Utilities.Log_Utils.Write($"Measurements: {_measureGun.Count} loaded{(document?.Hidden == true ? " (hidden)" : string.Empty)}."); }
        }

        #endregion

        #region Changes

        /// <summary>
        /// The Measure gun changed its measurements: written at once in a Revit session, else the file is marked dirty.
        /// </summary>
        public void MeasurementsChanged()
        {
            _measurementRevision++;
            if (_measurementsPath != null)
            {
                MeasurementDocument document = _measureGun?.ToDocument(Scene.OriginOffset, Scene.ModelTitle) ?? new MeasurementDocument { Model = Scene.ModelTitle };
                if (!MeasurementFiles.Write(_measurementsPath, document, out string error)) { Toast(error ?? "Measurements could not be saved", 4f, important: true); }
            }
            UpdateTitle();
        }

        /// <summary>True when a .bimgo's measurements changed since it was saved.</summary>
        private bool MeasurementsDirty => _measurementsPath == null && _measurementRevision != _savedMeasurementRevision;

        /// <summary>The measurements to write into a .bimgo, or null.</summary>
        private MeasurementDocument MeasurementsForSave() => _measureGun?.ToDocument(Scene.OriginOffset, Scene.ModelTitle);

        #endregion

        #region Export

        /// <summary>
        /// Measure gun, E: every measurement to a CSV file (metres, m², degrees; points in Revit internal metres).
        /// </summary>
        public void ExportMeasurements()
        {
            MeasurementDocument document = MeasurementsForSave();
            if (document == null || document.Measurements.Count == 0)
            {
                Toast("No measurements to export");
                return;
            }

            _window.SetCaptured(false);
            _window.Input.ReleaseAll();
            string name = Path.GetFileNameWithoutExtension(DocumentName) + " measurements.csv";
            string path = FileDialogs.ShowSave(_window.Handle, "Export measurements", "CSV file (*.csv)|*.csv|All files (*.*)|*.*", SuggestedFolder(), name, ".csv");
            if (path == null) { return; }

            try
            {
                File.WriteAllText(path, MeasurementFiles.ToCsv(document, Scene.ModelTitle), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                Sound.Play(SoundId.Commit);
                Toast($"Exported {document.Measurements.Count} measurement{(document.Measurements.Count == 1 ? string.Empty : "s")} to {Path.GetFileName(path)}", 3f);
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Measurement export failed: {ex}");
                Sound.Play(SoundId.Error);
                Toast("Export failed: " + ex.Message, 4f, important: true);
            }
        }

        #endregion
    }
}
