using BimGo.Audio;
using BimGo.Edits;
using BimGo.Live;
using BimGo.Physics;
using BimGo.Platform;
using BimGo.Rendering;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// Pushing a standalone file's edits into the live Revit model it came from (pause menu → PUSH TO REVIT).
    ///
    /// Flow: find a live session whose model key matches the file → dry run (Revit applies everything inside a
    /// transaction group, reports, rolls back) → preview panel → push for real (one undo step in Revit) → report.
    /// Applied entries are marked in the journal (the file becomes dirty; Save keeps it), so they are never sent
    /// again. Conflicts (moved in Revit since the file was made) are skipped unless "apply anyway" is ticked.
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Types and fields

        /// <summary>Where the push panel is.</summary>
        private enum PushState
        {
            Closed,
            Error,
            Checking,
            Preview,
            Applying,
            Done
        }

        /// <summary>One report row (strings built once when the answer arrives).</summary>
        private sealed class PushRow
        {
            public int Seq;
            public string Number;
            public string Label;
            public string Status;
            public string StatusText;
            public string Message;
            public uint Colour;
        }

        private PushState _pushState = PushState.Closed;
        private JournalPush _push;
        private JournalResultPayload _pushResult;
        private readonly List<PushRow> _pushRows = new();
        private string _pushTitle;
        private string _pushMessage;
        private string _pushSummary;
        private string _pushButtonLabel;
        private string _pushPhaseLine;
        private bool _pushApplyConflicts;
        private readonly ScrollRegion _pushScroll = new(); // Round Y-B: the report list (shell scroll region)
        private int _pushSent;

        #endregion

        #region State

        /// <summary>True while the push panel is showing (it replaces the pause menu).</summary>
        private bool IsPushPanelOpen => _pushState != PushState.Closed;

        /// <summary>
        /// True if this is a file with edits not yet in Revit that came from the given model (the app uses it to
        /// word the "switch model?" prompt when that model's Go arrives).
        /// </summary>
        public bool HasPushableEditsFor(string modelKey)
        {
            if (!IsFileMode || string.IsNullOrWhiteSpace(modelKey)) { return false; }
            string fileKey = Scene.Provenance?.ModelKey;
            return !string.IsNullOrWhiteSpace(fileKey)
                && string.Equals(fileKey, modelKey, StringComparison.OrdinalIgnoreCase)
                && _journal.CountNotInRevit() > 0;
        }

        #endregion

        #region Flow

        /// <summary>
        /// Opens the push panel and starts the dry run (or explains why it can't).
        /// </summary>
        private void OpenPush()
        {
            _pushScroll.Reset();
            _pushRows.Clear();
            _pushResult = null;
            _pushTitle = "PUSH TO REVIT";
            _pushPhaseLine = PhaseLine(null);

            int pending = _journal.CountNotInRevit();
            if (pending == 0)
            {
                ShowPushError(_journal.Count == 0 ? "This file has no edits to push." : "Every edit in this file is already in Revit.");
                return;
            }

            string modelKey = Scene.Provenance?.ModelKey;
            if (string.IsNullOrWhiteSpace(modelKey))
            {
                ShowPushError("This file doesn't record which Revit model it came from (it was made by an older add-in). Export it again from Revit to push edits.");
                return;
            }

            List<SessionInfo> sessions = JournalPush.FindSessions(modelKey);
            if (sessions.Count == 0)
            {
                string title = string.IsNullOrWhiteSpace(Scene.Provenance?.ModelTitle) ? Scene.ModelTitle : Scene.Provenance.ModelTitle;
                ShowPushError($"Open {title} in Revit and press Go (answer No when BimGo asks to switch), then push again.\n\n" +
                    "Only the model this file was exported from can take its edits.");
                return;
            }

            // Several sessions of the same model (unlikely: two Revit instances) → the newest
            _push?.Dispose();
            _push = new JournalPush(sessions[0]);
            SendPush(dryRun: true);
        }

        /// <summary>
        /// Sends the dry run or the real push.
        /// </summary>
        private void SendPush(bool dryRun)
        {
            if (_push == null) { return; }

            JournalApplyPayload request = JournalPush.BuildRequest(_journal, dryRun, _pushApplyConflicts, Scene.Provenance?.ModelKey,
                Scene.ExistingPhaseName, Scene.PhaseName, DocumentName);
            _pushSent = request.Entries.Count;

            string error = _push.Send(request);
            if (error != null)
            {
                ShowPushError($"Could not reach Revit: {error}");
                return;
            }

            _pushState = dryRun ? PushState.Checking : PushState.Applying;
            _pushMessage = null;
        }

        /// <summary>
        /// Per frame: takes Revit's answer when it arrives.
        /// </summary>
        private void UpdatePush(float dt)
        {
            if (_push == null || !_push.Waiting) { return; }
            if (!_push.Poll(dt, out JournalResultPayload result, out string error)) { return; }

            if (error != null)
            {
                ShowPushError(_pushState == PushState.Applying
                    ? $"{error}\n\nThe push may still have reached Revit: check the model there (and its undo list) before pushing again."
                    : error);
                return;
            }

            _pushResult = result;
            if (!result.Success)
            {
                ShowPushError($"Revit refused the push: {result.Message}");
                return;
            }

            BuildPushRows(result);
            if (result.DryRun)
            {
                _pushState = PushState.Preview;
                _pushTitle = "PUSH TO REVIT · PREVIEW";
                return;
            }

            MarkPushed(result);
            _pushState = PushState.Done;
            _pushTitle = "PUSH TO REVIT · DONE";
            Sound.Play(result.Applied > 0 ? SoundId.Commit : SoundId.Error);
        }

        /// <summary>
        /// Records a real push in the journal: applied (and already-present) entries are marked, clones get their
        /// Revit ids. The file is now dirty.
        /// </summary>
        private void MarkPushed(JournalResultPayload result)
        {
            int marked = 0;
            foreach (JournalEntryResult entryResult in result.Results)
            {
                if (entryResult.Status != JournalStatus.APPLIED && entryResult.Status != JournalStatus.ALREADY_APPLIED) { continue; }
                if (!_journal.MarkApplied(entryResult.Seq, entryResult.NewElementId)) { continue; }
                marked++;

                // The walkthrough's clone now has a Revit element
                if (entryResult.NewElementId <= 0) { continue; }
                JournalEntry entry = _journal.Entries[entryResult.Seq - 1];
                foreach (DynamicInstance instance in Dynamics.Instances)
                {
                    if (instance.IsClone && instance.CloneKey == entry.NewCloneKey) { instance.RevitId = entryResult.NewElementId; }
                }
            }
            UpdateTitle();
            Utilities.Log_Utils.Write($"Push: {marked} journal entries marked as in Revit.");
        }

        /// <summary>
        /// Shows a message in the panel instead of a report.
        /// </summary>
        private void ShowPushError(string message)
        {
            _push?.Abandon();
            _pushState = PushState.Error;
            _pushMessage = message;
            _pushRows.Clear();
            Sound.Play(SoundId.Error);
        }

        /// <summary>
        /// Closes the panel (not while a real push is in flight: its answer must be recorded).
        /// </summary>
        /// <returns>True if the panel was open.</returns>
        private bool ClosePush()
        {
            if (_pushState == PushState.Closed) { return false; }
            if (_pushState == PushState.Applying) { return true; }

            _push?.Dispose();
            _push = null;
            _pushState = PushState.Closed;
            _pushRows.Clear();
            _pushResult = null;
            return true;
        }

        #endregion

        #region Report

        /// <summary>
        /// Builds the report rows and the summary line from an answer.
        /// </summary>
        private void BuildPushRows(JournalResultPayload result)
        {
            _pushRows.Clear();
            _pushScroll.Reset();
            foreach (JournalEntryResult entryResult in result.Results)
            {
                JournalEntry entry = entryResult.Seq >= 1 && entryResult.Seq <= _journal.Count ? _journal.Entries[entryResult.Seq - 1] : null;
                _pushRows.Add(new PushRow
                {
                    Seq = entryResult.Seq,
                    Number = "#" + entryResult.Seq,
                    Label = string.IsNullOrWhiteSpace(entry?.Label) ? entry?.Op ?? "edit" : entry.Label,
                    Status = entryResult.Status,
                    StatusText = PushStatusText(entryResult.Status, result.DryRun),
                    Message = entryResult.Message ?? string.Empty,
                    Colour = PushStatusColour(entryResult.Status)
                });
            }

            string verb = result.DryRun ? "will apply" : "applied";
            var parts = new List<string> { $"{result.Applied} {verb}" };
            if (result.Conflicts > 0) { parts.Add($"{result.Conflicts} conflict{(result.Conflicts == 1 ? string.Empty : "s")}"); }
            if (result.Skipped > 0) { parts.Add($"{result.Skipped} skipped"); }
            if (result.Failed > 0) { parts.Add($"{result.Failed} failed"); }
            if (result.AlreadyApplied > 0) { parts.Add($"{result.AlreadyApplied} already in Revit"); }
            _pushSummary = string.Join(" · ", parts);
            _pushMessage = result.Message;
            _pushButtonLabel = result.Applied == 0 ? "NOTHING TO PUSH" : result.Applied == 1 ? "PUSH 1 EDIT" : $"PUSH {result.Applied} EDITS";
            _pushPhaseLine = PhaseLine(result);
        }

        private static string PushStatusText(string status, bool dryRun) => status switch
        {
            JournalStatus.APPLIED => dryRun ? "WILL APPLY" : "APPLIED",
            JournalStatus.CONFLICT => "CONFLICT",
            JournalStatus.FAILED => dryRun ? "WILL FAIL" : "FAILED",
            JournalStatus.ALREADY_APPLIED => "IN REVIT",
            _ => "SKIPPED"
        };

        private static uint PushStatusColour(string status) => status switch
        {
            JournalStatus.APPLIED => UiTheme.GOOD,
            JournalStatus.CONFLICT => UiTheme.MEASURE,
            JournalStatus.FAILED => UiTheme.DANGER,
            JournalStatus.ALREADY_APPLIED => UiTheme.TEXT_MUTED,
            _ => UiTheme.HAMMER
        };

        /// <summary>
        /// Writes the report as CSV (Excel-friendly, UTF-8 with BOM).
        /// </summary>
        private void ExportPushReport()
        {
            _window.SetCaptured(false);
            _window.Input.ReleaseAll();
            string name = Path.GetFileNameWithoutExtension(DocumentName) + " push report.csv";
            string path = FileDialogs.ShowSave(_window.Handle, "Save push report", "CSV file (*.csv)|*.csv|All files (*.*)|*.*", SuggestedFolder(), name, ".csv");
            if (path == null) { return; }

            try
            {
                var lines = new List<string> { "Edit,Description,Status,Message,New element id" };
                foreach (PushRow row in _pushRows)
                {
                    long newId = _pushResult?.Results.FirstOrDefault(r => r.Seq == row.Seq)?.NewElementId ?? 0;
                    lines.Add(string.Join(",", row.Seq.ToString(), Csv(row.Label), Csv(row.StatusText), Csv(row.Message), newId > 0 ? newId.ToString() : string.Empty));
                }
                File.WriteAllLines(path, lines, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                _pushMessage = $"Report saved: {Path.GetFileName(path)}";
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Push report failed: {ex}");
                _pushMessage = $"The report could not be saved: {ex.Message}";
            }
        }

        /// <summary>
        /// Quotes a CSV field when needed.
        /// </summary>
        private static string Csv(string value)
        {
            value ??= string.Empty;
            bool quote = value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
            return quote ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }

        #endregion

        #region Panel

        /// <summary>
        /// Draws and handles the push panel (in place of the pause menu). Round Y-B: on the panel shell (standard width):
        /// the target in the sub-line, the report in a clipped scroll region, the state's actions in the footer.
        /// </summary>
        private void BuildPushPanel()
        {
            FontAtlas f = _ui.Atlas;
            InputState input = _window.Input;

            // Sub-line: model · Revit version · phases
            Text.Clear();
            if (_push != null)
            {
                SessionInfo session = _push.Session;
                Text.Append(string.IsNullOrWhiteSpace(session.DocTitle) ? "Revit model" : session.DocTitle)
                    .Append(" · Revit ").Append(session.RevitVersion ?? string.Empty);
                if (_pushPhaseLine != null) { Text.Append(" · ").Append(_pushPhaseLine); }
            }
            else
            {
                Text.Append("This file's edits → the Revit model it came from");
            }
            PanelFrame frame = BeginPanel(PanelSize.Standard, _pushTitle ?? "PUSH TO REVIT", UiTheme.ACCENT, Text.Span);
            float ix = frame.Ix, iw = frame.Iw;
            float cy = frame.BodyTop;

            int tryAgain = -1, cancel = -1, push = -1, save = -1, export = -1;
            string hint = null;
            switch (_pushState)
            {
                case PushState.Error:
                    _ui.TextWrapped(f.Body, ix, cy + S(4), iw, _pushMessage ?? "Something went wrong.", UiTheme.TEXT_SOFT, maxLines: 12);
                    tryAgain = FooterAction("TRY AGAIN", ShellStyle.Primary);
                    hint = "TRY AGAIN checks again · Esc back";
                    break;

                case PushState.Checking:
                case PushState.Applying:
                    bool applying = _pushState == PushState.Applying;
                    Text.Clear().Append(applying ? "Pushing " : "Checking ").Append(_pushSent).Append(_pushSent == 1 ? " edit" : " edits")
                        .Append(applying ? " into Revit… " : " against the model (nothing changes yet)… ").Append(_push?.Waited ?? 0f, 0).Append(" s");
                    cy += _ui.TextWrapped(f.Body, ix, cy + S(4), iw, Text.Span, UiTheme.TEXT_SOFT, maxLines: 2) + S(12);
                    _ui.TextWrapped(f.Body, ix, cy, iw, "If Revit is in the middle of a command, finish or cancel it there.", UiTheme.TEXT_MUTED, maxLines: 2);
                    if (!applying) { cancel = FooterAction("CANCEL"); }
                    hint = applying ? "Waiting for Revit's answer: the panel stays open until it arrives" : "Nothing in Revit changes during the check";
                    break;

                case PushState.Preview:
                    {
                        // The conflicts tick sits under the list (it re-checks)
                        int conflicts = _pushResult?.Conflicts ?? 0;
                        bool tick = conflicts > 0 || _pushApplyConflicts;
                        float tickH = tick ? S(36) : 0f;
                        BuildPushReport(f, input, ix, cy, iw, frame.BodyBottom - tickH);
                        if (tick)
                        {
                            bool applyConflicts = Checkbox(f, input, ix, frame.BodyBottom - S(28), iw,
                                "Apply conflicting edits anyway, from where the elements are in Revit now", _pushApplyConflicts);
                            if (applyConflicts != _pushApplyConflicts)
                            {
                                // The preview must show what the real push will do: check again with the new choice
                                _pushApplyConflicts = applyConflicts;
                                SendPush(dryRun: true);
                            }
                        }
                        push = FooterAction(_pushButtonLabel ?? "PUSH", ShellStyle.Primary, (_pushResult?.Applied ?? 0) > 0 && _pushState == PushState.Preview);
                        cancel = FooterAction("CANCEL");
                        hint = "Nothing has changed in Revit yet · the push is one undo step there";
                        break;
                    }

                case PushState.Done:
                    BuildPushReport(f, input, ix, cy, iw, frame.BodyBottom);
                    if (IsDirty) { save = FooterAction("SAVE", ShellStyle.Primary); }
                    export = FooterAction("EXPORT REPORT…");
                    hint = IsDirty ? "Save this file to remember what was pushed" : "Pushed edits are marked in the file";
                    break;
            }

            int pressed = EndPanel(frame, null, hint);
            if (pressed == FOOTER_LEFT || pressed == FOOTER_NONE) { return; }
            if (pressed == tryAgain) { OpenPush(); }
            else if (pressed == cancel) { ClosePush(); }
            else if (pressed == push) { SendPush(dryRun: false); }
            else if (pressed == save) { Save(saveAs: false); }
            else if (pressed == export) { ExportPushReport(); }
        }

        /// <summary>
        /// "Existing → New" for the target line (Revit's phases once known, else the file's), or null.
        /// </summary>
        private string PhaseLine(JournalResultPayload result)
        {
            string newPhase = result?.PhaseName ?? Scene.PhaseName;
            string existing = result?.PhaseName != null ? result.ExistingPhaseName : Scene.ExistingPhaseName;
            if (string.IsNullOrEmpty(newPhase)) { return null; }
            return string.IsNullOrEmpty(existing) ? newPhase : existing + " → " + newPhase;
        }

        /// <summary>
        /// Summary, note and the list of entries (a clipped, pixel-scrolled region down to <paramref name="bottom"/>).
        /// </summary>
        private void BuildPushReport(FontAtlas f, InputState input, float x, float y, float w, float bottom)
        {
            _ui.Text(f.Bold, x, y, _pushSummary ?? string.Empty, UiTheme.ACCENT, S(0.6f));
            y += S(26);

            if (_pushState == PushState.Done && _pushResult?.UndoLabel != null)
            {
                Text.Clear().Append("One undo in Revit takes the whole push back: “").Append(_pushResult.UndoLabel).Append("”. Save this file to remember what was pushed.");
                y += _ui.TextWrapped(f.Body, x, y, w, Text.Span, UiTheme.TEXT_SOFT, maxLines: 2) + S(10);
            }
            if (!string.IsNullOrEmpty(_pushMessage))
            {
                y += _ui.TextWrapped(f.Body, x, y, w, _pushMessage, UiTheme.MEASURE_TEXT, maxLines: 2) + S(10);
            }
            if (bottom - y < S(40)) { return; }

            // Rows (only the visible ones are drawn)
            float rowH = S(UiTheme.ROW_HEIGHT);
            float top = BeginScroll(_pushScroll, input, x, y, w, bottom - y, out float cw);
            VisibleRows(_pushScroll, rowH, _pushRows.Count, out int first, out int last);
            float chipW = S(96);
            for (int i = first; i < last; i++)
            {
                PushRow row = _pushRows[i];
                float ry = top + i * rowH;
                if ((i & 1) == 0) { _ui.Rect(x, ry, cw, rowH - S(4), UiTheme.ROW_STRIPE); }

                float rx = x + S(8);
                _ui.Panel(rx, ry + S(6), chipW, S(20), Rgba.WithAlpha(row.Colour, 0.16f), Rgba.WithAlpha(row.Colour, 0.6f));
                _ui.TextCentred(f.Small, rx + chipW * 0.5f, ry + S(10), row.StatusText, row.Colour, S(0.8f));

                _ui.Text(f.Mono, rx + chipW + S(12), ry + S(7), row.Number, UiTheme.TEXT_FAINT);
                float tx = rx + chipW + S(60), tw = cw - (tx - x) - S(8);
                _ui.TextWrapped(f.Body, tx, ry + S(4), tw, row.Label, UiTheme.TEXT, maxLines: 1);
                _ui.TextWrapped(f.Small, tx, ry + S(23), tw, row.Message, UiTheme.TEXT_MUTED, maxLines: 1);
            }
            EndScroll(_pushScroll, input, top + _pushRows.Count * rowH);
        }

        #endregion
    }
}
