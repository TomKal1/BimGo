using System.Numerics;
using System.Text;
using System.Threading;
using BimGo.Calculations;
using BimGo.Format;
using BimGo.Scene;

namespace BimGo.Game;

internal sealed partial class GameSession
{
    private NativeLightingDocument _nativeLighting = new();
    private Dictionary<string,byte[]> _nativeInputs = new();
    private string _nativeLightingPath, _nativeSceneFolder;
    private int _nativeRevision, _nativeSavedRevision, _nativeInputRevision;
    private bool _nativeLightingReady, _nativeShown, _nativeArmed, _nativeQueued;
    private float _nativeNextCheck, _nativeDue;
    private string _nativeStatus = "Create a set and run a BimGo calculation.", _nativeProgress;
    private readonly CalculationRevisionGate _nativeGate = new();
    private CancellationTokenSource _nativeCancel;
    private Task<NativeCalculationOutcome> _nativeJob;
    private long _nativeTicket;
    private string _nativeJobSet;
    private NativeCalculationRun _nativeRun, _nativePreviousRun;
    private string _nativePartialRoomKey;
    private byte[] _nativeOriginalSetup;
    private bool _nativeSetupUnreadable;
    private NativePhysicalState _nativeObserved, _nativeJobState;
    private sealed record NativePhysicalState(CalculationEdits Edits, int Profiles, int Setup)
    {
        public bool Same(NativePhysicalState other) => other != null && Profiles == other.Profiles && Setup == other.Setup
            && Edits.Removed.AsSpan().SequenceEqual(other.Edits.Removed) && Edits.Instances.SequenceEqual(other.Edits.Instances);
    }
    private NativeLightingSet NativeSet => _nativeLighting?.Sets?.FirstOrDefault(s => s.Id == _nativeLighting.Selected);
    private bool NativeLightingDirty => _nativeRevision != _nativeSavedRevision;
    private byte[] NativeDefinitionsForSave() => _nativeSetupUnreadable && _nativeRevision == 0 ? _nativeOriginalSetup : NativeLightingFiles.Serialize(_nativeLighting);

    private void InitialiseNativeLighting()
    {
        _nativeLightingPath = IsFileMode ? null : NativeLightingFiles.SidecarFor(Scene.CommentsPath);
        _nativeOriginalSetup = _options.Document?.NativeLighting;
        string error;
        _nativeLighting = IsFileMode ? NativeLightingFiles.Parse(_options.Document?.NativeLighting,out error) : NativeLightingFiles.Read(_nativeLightingPath,out error);
        if (_nativeLighting == null) { _nativeSetupUnreadable = true; _nativeLighting = new(); _nativeStatus = "Calculator setup could not be read: " + error; }
        _nativeLighting.Selected ??= _nativeLighting.Sets.FirstOrDefault()?.Id;
        if (NativeSet != null && !_nativeSetupUnreadable) _nativeStatus = "Ready · run the selected BimGo set, or view saved results in RESULTS.";
        _nativeInputs = _options.Document?.LightingScenes != null ? new(_options.Document.LightingScenes) : new();
        _nativeSceneFolder = _nativeLightingPath == null ? null : Path.Combine(Path.GetDirectoryName(_nativeLightingPath),"lighting-scenes");
        if (_nativeSceneFolder != null)
        {
            long total = 0;
            foreach (var set in _nativeLighting.Sets)
            {
                try
                {
                    string path = Path.Combine(_nativeSceneFolder,set.Id+".json");
                    if (!File.Exists(path)) continue;
                    long size = new FileInfo(path).Length; total += size;
                    if(size<=64L*1024*1024 && total<=256L*1024*1024) _nativeInputs[NativeLightingFiles.ScenePrefix+set.Id+".json"] = File.ReadAllBytes(path);
                }
                catch(Exception ex) { Utilities.Log_Utils.Write("Native input cache: "+ex.Message); }
            }
        }
        _nativeLightingReady = true;
        _nativeObserved = CaptureNativeState();
        _nativeSetupSnapshot = NativeLightingFiles.Serialize(_nativeLighting);
    }

    private NativePhysicalState CaptureNativeState() => new(new CalculationEdits
    {
        Removed = (bool[])_hidden.Clone(),
        // Ignore category/profile/user visibility. These are display choices, not physical removal.
        Instances = Dynamics?.Instances.Where(d => !d.Hidden).Select(d => new CalculationInstance(d.Element,d.IsClone?d.CloneKey:0,d.IsClone,d.Model)).ToArray()
            ?? Array.Empty<CalculationInstance>()
    },_lightProfileRevision,_nativeInputRevision);

    private void SaveNativeLightingSettings()
    {
        if (_nativeLightingPath != null && !NativeLightingFiles.Write(_nativeLightingPath,_nativeLighting,out string error)) _nativeStatus = error;
    }
    private void NativeSetupChanged(bool physical = true)
    {
        RememberNativeSetup();
        _nativeRevision++;
        if (physical)
        {
            _nativeInputRevision++;
            _nativeObserved = CaptureNativeState();
            InvalidateNativeCalculation();
        }
        SaveNativeLightingSettings(); UpdateTitle();
    }
    private void InvalidateNativeCalculation()
    {
        _nativeGate.Advance(); _nativeCancel?.Cancel(); _nativeRun = null;
        if (_calc?.IsNative == true) { _calc = null; _calcZone = null; _calcBuildKey = _calcTextKey = int.MinValue; }
        _nativeQueued = _nativeShown && _nativeArmed && NativeSet?.Automatic == true;
        _nativeDue = _clock + .35f;
        _nativeStatus = _nativeQueued ? "Inputs changed · recalculation queued" : "Inputs changed · run to calculate this revision";
    }
    private void SelectNativeSet(string id)
    {
        _nativePickMode = 0;
        _nativeLighting.Selected = id; _nativeArmed = _nativeQueued = false;
        _nativeGate.Advance(); _nativeCancel?.Cancel(); _nativeRun = null;
        if (_nativeShown) { _calc = null; _calcZone = null; }
        _nativeShown = false; _nativeStatus = "Ready · run the selected BimGo set";
        NativeSetupChanged(physical:false);
    }
    private void RunNativeCalculation(string zoneKey = null)
    {
        if (NativeSet == null) { CreateNativeSet(); if (NativeSet == null) return; }
        if (zoneKey != null && (_nativePreviousRun == null || _nativePreviousRun.Input.Set.Id != NativeSet.Id))
        { _nativeStatus = "Run this entire set once before rerunning a room."; return; }
        _nativePartialRoomKey = zoneKey;
        _nativeShown = _nativeArmed = true;
        // An in-flight imported-file read can never overwrite a newly selected native run.
        _calcLoad = null;
        InvalidateNativeCalculation(); _nativeQueued = true; _nativeDue = _clock;
        _nativeStatus = "BimGo calculation queued";
    }
    private void CancelNativeCalculation()
    {
        _nativeGate.Advance(); _nativeCancel?.Cancel(); _nativeQueued = _nativeArmed = false;
        _nativeRun = null; _nativeStatus = "Canceled · no result published";
    }
    private void LeaveNativeCalculation()
    {
        _nativeShown = _nativeQueued = _nativeArmed = false;
        _nativeGate.Advance(); _nativeCancel?.Cancel();
    }

    private void TickNativeLighting()
    {
        if (!_nativeLightingReady) return;
        if (_clock >= _nativeNextCheck)
        {
            _nativeNextCheck = _clock + (_nativeShown || _nativeJob != null || _calc?.IsNative == true ? 0f : .25f);
            var state = CaptureNativeState();
            if (!state.Same(_nativeObserved)) { _nativeObserved = state; InvalidateNativeCalculation(); }
        }
        if (_nativeJob?.IsCompleted == true)
        {
            var job = _nativeJob; _nativeJob = null;
            bool current = _nativeGate.Matches(_nativeTicket) && _nativeShown && NativeSet?.Id == _nativeJobSet
                && _nativeJobState.Same(CaptureNativeState());
            if (current)
            {
                if (job.IsCompletedSuccessfully)
                {
                    var outcome = job.Result;
                    if (outcome.Run != null) PublishNativeCalculation(outcome.Run);
                    else
                    {
                        _nativeQueued = false;
                        _nativeStatus = outcome.Canceled ? "Canceled · no result published"
                            : (outcome.NeedsInput ? "Inputs need attention: " : "Calculation incomplete: ") + outcome.Message;
                        if (outcome.Diagnostic != null) Utilities.Log_Utils.Write(outcome.Diagnostic);
                    }
                }
                else if (!job.IsCanceled)
                {
                    _nativeQueued = false;
                    _nativeStatus = "Calculation incomplete: " + (job.Exception?.GetBaseException().Message ?? "canceled");
                    Utilities.Log_Utils.Write(_nativeStatus);
                }
            }
            // Observe failed superseded tasks too; they never update the current view.
            _ = job.Exception;
            _nativeCancel?.Dispose(); _nativeCancel = null;
        }
        if (_nativeQueued && _nativeJob == null && _clock >= _nativeDue && NativeSet != null)
        {
            _nativeQueued = false;
            var definition = NativeLightingFiles.Copy(NativeSet);
            _nativeJobSet = definition.Id; _nativeJobState = CaptureNativeState(); _nativeObserved = _nativeJobState;
            _nativeCancel = new CancellationTokenSource(); CancellationToken cancel = _nativeCancel.Token;
            _nativeTicket = _nativeGate.Advance(); _nativeStatus = "Calculating · previous values hidden";
            _nativeProgress = "Capturing physical input";
            var edits = _nativeJobState.Edits;
            // Copy the profile document before a background thread reads its mutable choice caches.
            var profiles = System.Text.Json.JsonSerializer.Deserialize<LightProfileDocument>(System.Text.Json.JsonSerializer.Serialize(_lightProfileDoc));
            SceneData scene = Scene; Vector3 origin = scene.OriginOffset;
            var previous = _nativePreviousRun;
            string[] zoneKeys = _nativePartialRoomKey == null ? null : new[] { _nativePartialRoomKey };
            _nativePartialRoomKey = null;
            // Execute even if cancellation arrives before scheduling, so it also returns a handled outcome.
            _nativeJob = Task.Run(() => NativeCalculationJob.Execute(scene,definition,edits,profiles,origin,
                stage => Volatile.Write(ref _nativeProgress,stage),cancel,previous,zoneKeys));
        }
    }

    private void PublishNativeCalculation(NativeCalculationRun run)
    {
        _nativeRun = _nativePreviousRun = run;
        _calc = run.Display; _calcName = "BimGo_native_"+run.Input.Set.Id;
        _calcMode = CalcDisplayMode.Illuminance; _calcZone = null;
        _calcBuildKey = _calcTextKey = int.MinValue;
        _nativeStatus = (run.Complete ? "BimGo calculation complete" : "Calculation incomplete · iteration cap")
            + $" · {run.Display.PointCount:N0} points · revision {run.ScenarioHash[..8]}";
        _calcNotice = "BimGo calculation · exported geometry · scalar diffuse analysis";
        _nativeInputs[NativeLightingFiles.ScenePrefix+run.Input.Set.Id+".json"] = run.SceneBytes;
        _nativeRevision++;
        try
        {
            if (IsFileMode)
            {
                _calcEmbedded ??= new(StringComparer.OrdinalIgnoreCase);
                _calcEmbedded[LightingCalcFiles.ENTRY_PREFIX+_calcName+".json"] = run.Header;
                _calcEmbedded[LightingCalcFiles.ENTRY_PREFIX+_calcName+".bin"] = run.Body;
            }
            else
            {
                Directory.CreateDirectory(_calcFolder); Directory.CreateDirectory(_nativeSceneFolder);
                WriteNativeBytes(Path.Combine(_calcFolder,_calcName+".bin"),run.Body);
                WriteNativeBytes(Path.Combine(_calcFolder,_calcName+".json"),run.Header);
                WriteNativeBytes(Path.Combine(_nativeSceneFolder,run.Input.Set.Id+".json"),run.SceneBytes);
                SaveNativeLightingSettings();
            }
            ListCalcs(); RememberCalcView(); UpdateTitle();
        }
        catch (Exception ex) { _nativeStatus += " · cache not saved: "+ex.Message; }
        Toast("BimGo calculation updated · "+run.Input.Set.Name,3f);
    }
    private static void WriteNativeBytes(string path,byte[] bytes)
    {
        string temporary = path+".tmp"; File.WriteAllBytes(temporary,bytes); File.Move(temporary,path,overwrite:true);
    }
    private void ExportNativeCalculation(int kind)
    {
        var run = _nativeRun;
        if (run == null || !_nativeShown || !CaptureNativeState().Same(_nativeJobState)) { _nativeStatus = "Run the current revision before exporting a report."; return; }
        string extension = kind==0?".html":kind==1?".csv":".json";
        _window.Input.ReleaseAll();
        string path = Platform.FileDialogs.ShowSave(_window.Handle,"Export BimGo calculation",$"Calculation (*{extension})|*{extension}",SuggestedFolder(),DisplaySafe(run.Input.Set.Name)+extension,extension);
        _window.Input.ReleaseAll(); if(path==null)return;
        try
        {
            if(kind==0)File.WriteAllText(path,run.HtmlReport(CalcScaleMax,CalcInLux),Encoding.UTF8);
            else if(kind==1)File.WriteAllText(path,run.PointCsv,Encoding.UTF8);
            else File.WriteAllBytes(path,kind==2?run.StructuredResults:run.SceneBytes);
            _nativeStatus="Exported "+Path.GetFileName(path)+" · revision "+run.ScenarioHash[..8];
        }
        catch(Exception ex){_nativeStatus="Export failed: "+ex.Message;}
    }
    private void DisposeNativeLighting()
    {
        _nativeGate.Advance();
        var cancel=_nativeCancel;_nativeCancel=null;cancel?.Cancel();
        if(_nativeJob!=null)
            _nativeJob.ContinueWith(task=>{_ = task.Exception;cancel?.Dispose();},CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
        else cancel?.Dispose();
    }
}
