using System.Numerics;
using BimGo.Format;
using BimGo.Scene;

namespace BimGo.Calculations;

/// <summary>A worker outcome; only a successful run can be published as numerical results.</summary>
public sealed class NativeCalculationOutcome
{
    public NativeCalculationRun Run { get; init; }
    public bool NeedsInput { get; init; }
    public bool Canceled { get; init; }
    public string Message { get; init; }
    public string Diagnostic { get; init; }
}

public static class NativeCalculationJob
{
    /// <summary>Handle failures inside user code, before they escape into Task infrastructure.</summary>
    public static NativeCalculationOutcome Execute(SceneData scene, NativeLightingSet definition,
        CalculationEdits edits, LightProfileDocument profiles, Vector3 origin,
        Action<string> progress = null, CancellationToken cancel = default, NativeCalculationRun previous = null, IReadOnlyCollection<string> zoneKeys = null)
    {
        try
        {
            cancel.ThrowIfCancellationRequested();
            var input = ExportedSceneInput.Build(scene, definition, edits, profiles, cancel);
            return new() { Run = NativeCalculator.Run(input, origin, progress, cancel, previous, zoneKeys) };
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return new() { Canceled = true };
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException)
        {
            return new() { NeedsInput = true, Message = ex.Message, Diagnostic = ex.ToString() };
        }
        catch (Exception ex)
        {
            // Keep the full diagnostic for the log, while keeping results absent on failure.
            return new() { Message = ex.Message, Diagnostic = ex.ToString() };
        }
    }
}
