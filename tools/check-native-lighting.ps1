param(
    [Parameter(Mandatory=$true)][string]$BuildDirectory,
    [string]$ModelPath = '',
    [string]$RoomNumber = '101',
    [switch]$AllowEstimates,
    [switch]$Replay,
    [string]$Output = ''
)
$ErrorActionPreference='Stop'
$buildRoot=(Resolve-Path -LiteralPath $BuildDirectory).Path
foreach($assembly in @('BimGo.Core.dll','Lighting.Engine.dll','BimGo.Calculations.dll','BimGo.dll')) {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $buildRoot $assembly))
}
$sessionType=[Reflection.Assembly]::LoadFrom((Join-Path $buildRoot 'BimGo.dll')).GetType('BimGo.Game.GameSession',$true)
$flags=[Reflection.BindingFlags]'Instance,NonPublic,Public'
$session=[Runtime.CompilerServices.RuntimeHelpers]::GetUninitializedObject($sessionType)
function Set-Field([string]$name,$value) { $sessionType.GetField($name,$flags).SetValue($session,$value) }
function Get-Field([string]$name) { $sessionType.GetField($name,$flags).GetValue($session) }
function Invoke-Session([string]$name) { $sessionType.GetMethod($name,$flags).Invoke($session,$null) }
function Check([bool]$condition,[string]$message) { if(!$condition){throw $message} }

$hidden=[bool[]]::new(2)
Set-Field '_hidden' $hidden
$set=[BimGo.Format.NativeLightingSet]::new();$set.Rooms=@(0)
$setup=[BimGo.Format.NativeLightingDocument]::new();$setup.Sets.Add($set);$setup.Selected=$set.Id
Set-Field '_nativeLighting' $setup
$gate=[BimGo.Calculations.CalculationRevisionGate]::new();Set-Field '_nativeGate' $gate
$first=Invoke-Session 'CaptureNativeState'
$cut=[BimGo.Scene.SectionCut]::new();$cut.BoxOn=$true;$cut.MaxZ=1
Set-Field '_section' $cut;Set-Field '_sceneRevision' ([long]99)
Set-Field '_userHidden' ([bool[]]@( $true,$true ));Set-Field '_groupVisible' ([bool[]]@( $false,$false ))
$second=Invoke-Session 'CaptureNativeState'
$same=$first.GetType().GetMethod('Same',$flags)
Check ([bool]$same.Invoke($first,@($second))) 'ISO cut/visibility changed the physical snapshot.'
$hidden[0]=$true;$changed=Invoke-Session 'CaptureNativeState'
Check (![bool]$same.Invoke($first,@($changed))) 'Physical removal did not invalidate the snapshot.'
$hidden[0]=$false

$sentinel=[BimGo.Scene.LightingCalcResult]::new();$sentinel.Tool='BimGo calculation test';$sentinel.ScenarioHash='prior-revision'
Set-Field '_calc' $sentinel
Set-Field '_nativeLightingReady' $true;Set-Field '_nativeShown' $true
Set-Field '_nativeNextCheck' ([float]::MaxValue)
$ticket=$gate.Advance();Set-Field '_nativeTicket' $ticket;Set-Field '_nativeJobSet' $set.Id
Set-Field '_nativeJobState' (Invoke-Session 'CaptureNativeState')
$ignored=[BimGo.Calculations.NativeCalculationOutcome]::new()
Set-Field '_nativeJob' ([Threading.Tasks.Task]::FromResult[BimGo.Calculations.NativeCalculationOutcome]($ignored))
[void]$gate.Advance()
[void](Invoke-Session 'TickNativeLighting')
Check ([object]::ReferenceEquals($sentinel,(Get-Field '_calc'))) 'A late superseded job replaced the shown result.'
Check ($null -eq (Get-Field '_nativeJob')) 'The completed stale task was not drained.'
$cancel=[Threading.CancellationTokenSource]::new();Set-Field '_nativeCancel' $cancel;Set-Field '_nativeArmed' $true
[void](Invoke-Session 'InvalidateNativeCalculation')
Check ($null -eq (Get-Field '_calc')) 'An outdated native numerical overlay remained visible.'
Check $cancel.IsCancellationRequested 'Superseded work was not canceled.'
Check ([bool](Get-Field '_nativeQueued')) 'Automatic recalculation was not queued.'
$cancel.Dispose()
Set-Field '_nativeCancel' $null
$ticket=$gate.Advance();Set-Field '_nativeTicket' $ticket
Set-Field '_nativeJobState' (Invoke-Session 'CaptureNativeState')
$invalid=[BimGo.Calculations.NativeCalculationOutcome]::new()
$invalid.NeedsInput=$true;$invalid.Message="1 source(s) are missing full IES data. Import IES in LIGHTS or enable 'Allow estimated photometry'."
Set-Field '_nativeJob' ([Threading.Tasks.Task]::FromResult[BimGo.Calculations.NativeCalculationOutcome]($invalid))
[void](Invoke-Session 'TickNativeLighting')
Check ($null -eq (Get-Field '_calc')) 'Invalid input published a numerical result.'
Check (!(Get-Field '_nativeQueued')) 'Invalid input kept retrying without an input change.'
Check ((Get-Field '_nativeStatus').StartsWith('Inputs need attention: ')) 'Invalid input did not reach the calculator panel.'
Check ((Get-Field '_nativeStatus').Contains('Allow estimated photometry')) 'The calculator panel lost the corrective action.'
Set-Field '_paused' $true;Set-Field '_lightingOpen' $true;Set-Field '_nativeQueued' $true
Check ([bool](Invoke-Session 'PanelBack')) 'Escape did not handle the lighting workspace.'
Check (!(Get-Field '_lightingOpen')) 'Escape did not close the lighting workspace.'
Check ([bool](Get-Field '_paused')) 'Escape captured the cursor instead of returning to the pause menu.'
Check ([bool](Get-Field '_nativeQueued')) 'Closing the workspace canceled calculation work.'
Write-Output 'Compiled App checks passed: presentation invariance, physical invalidation, cancellation, automatic queue, late-result rejection, and actionable input failure without results/retries.'

if($ModelPath) {
    $readError=''
    $doc=[BimGo.Format.BimGoReader]::Read((Resolve-Path -LiteralPath $ModelPath).Path,[BimGo.Scene.LaunchSettings]::new(),[ref]$readError)
    if(!$doc){throw $readError}
    $room=[Array]::FindIndex($doc.Scene.Rooms,[Predicate[BimGo.Scene.RoomInfo]]{param($r) $r.Number -eq $RoomNumber})
    if($room -lt 0){throw "Room $RoomNumber is not exported."}
    $definition=[BimGo.Format.NativeLightingSet]::new();$definition.Name='Native verification '+$RoomNumber;$definition.Rooms=@($room)
    $definition.AllowEstimatedPhotometry=$AllowEstimates.IsPresent
    $watch=[Diagnostics.Stopwatch]::StartNew()
    $profiles=$doc.LightProfiles;if(!$profiles){$profiles=[BimGo.Format.LightProfileDocument]::new()}
    $input=[BimGo.Calculations.ExportedSceneInput]::Build($doc.Scene,$definition,[BimGo.Calculations.CalculationEdits]::new(),$profiles,[Threading.CancellationToken]::None)
    $run=[BimGo.Calculations.NativeCalculator]::Run($input,$doc.Scene.OriginOffset,$null,[Threading.CancellationToken]::None)
    $elapsed=$watch.Elapsed.TotalSeconds
    Check ($run.Complete -and $run.Display.IsNative) 'Native model solve did not complete.'
    if($Replay) {
        $again=[BimGo.Calculations.NativeCalculator]::Run([BimGo.Calculations.CalculationInput]::Parse($run.SceneBytes),$doc.Scene.OriginOffset,$null,[Threading.CancellationToken]::None)
        Check ($run.ScenarioHash -eq $again.ScenarioHash) 'Saved-input replay changed its revision hash.'
        for($i=0;$i -lt $run.Zones.Count;$i++) {
            $a=$run.Zones[$i].Illuminance;$b=$again.Zones[$i].Illuminance
            Check ($a.Length -eq $b.Length) 'Replay grid size differs.'
            for($j=0;$j -lt $a.Length;$j++){Check ($a[$j].Equals($b[$j]) -or [Math]::Abs($a[$j]-$b[$j]) -le 1e-10+1e-10*[Math]::Abs($a[$j])) 'Replay numerical mismatch.'}
        }
    }
    $summary=[ordered]@{Kind='Native BimGo exported-geometry solve, not WWTools/Revit parity';Model=$doc.Scene.ModelTitle;Room=$RoomNumber;Triangles=$input.Triangles.Count;
        Emitters=$input.Emitters.Count;Points=$run.Display.PointCount;AverageFc=$run.Zones[0].Statistics.Average;Seconds=$elapsed;ScenarioHash=$run.ScenarioHash;
        Complete=$run.Complete;ReplayChecked=$Replay.IsPresent;AllowEstimates=$AllowEstimates.IsPresent;Warnings=@($run.Input.Warnings);Statistics=@($run.Statistics)}
    if($Output){$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $Output -Encoding utf8}
    Write-Output ("Native model check passed: {0} triangles, {1} sources, {2} points, {3:0.000} fc average, {4:0.00}s." -f $input.Triangles.Count,$input.Emitters.Count,$run.Display.PointCount,$run.Zones[0].Statistics.Average,$elapsed)
}
