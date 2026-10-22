param(
    [Parameter(Mandatory=$true)][string]$BuildDirectory,
    [Parameter(Mandatory=$true)][string]$ModelPath,
    [Parameter(Mandatory=$true)][string]$ReportPath
)
$ErrorActionPreference='Stop'
$buildRoot=(Resolve-Path -LiteralPath $BuildDirectory).Path
foreach($assembly in @('BimGo.Core.dll','Lighting.Engine.dll','BimGo.Calculations.dll')) {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $buildRoot $assembly))
}
function Check([bool]$condition,[string]$message) { if(!$condition){throw $message} }
$archive=[IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $ModelPath).Path)
function Read-Entry([string]$name) {
    $entry=$archive.GetEntry($name)
    if(!$entry){throw "Missing $name"}
    $stream=$entry.Open();$memory=[IO.MemoryStream]::new()
    try {$stream.CopyTo($memory);return ,$memory.ToArray()} finally {$stream.Dispose();$memory.Dispose()}
}
try {
    $parseError=''
    $setup=[BimGo.Format.NativeLightingFiles]::Parse((Read-Entry 'native-lighting.json'),[ref]$parseError)
    if(!$setup){throw $parseError}
    $id=$setup.Selected
    $calculationInput=[BimGo.Calculations.CalculationInput]::Parse((Read-Entry ('lighting-scenes/'+$id+'.json')))
    $stem='lighting-calcs/BimGo_native_'+$id
    $old=[BimGo.Format.LightingCalcFiles]::Parse((Read-Entry ($stem+'.json')),(Read-Entry ($stem+'.bin')),'saved',[Numerics.Vector3]::Zero,[DateTime]::UtcNow,[ref]$parseError)
    if(!$old){throw $parseError}
} finally {$archive.Dispose()}
$run=[BimGo.Calculations.NativeCalculator]::Run($calculationInput,[Numerics.Vector3]::Zero,$null,[Threading.CancellationToken]::None)
Check ($old.ScenarioHash -eq $run.ScenarioHash) 'Reporting changes altered the saved physical revision.'
Check ($old.ScaleMax -eq $run.Display.ScaleMax) 'Default report/model scale changed.'
Check ($old.Zones.Length -eq $run.Display.Zones.Length) 'Zone count changed.'
for($i=0;$i -lt $old.Zones.Length;$i++) {
    $before=$old.Zones[$i];$after=$run.Display.Zones[$i]
    Check ($before.PointValues.Length -eq $after.PointValues.Length) 'Point count changed.'
    for($j=0;$j -lt $before.PointValues.Length;$j++) {
        Check ($before.PointValues[$j].Equals($after.PointValues[$j])) "Numerical result changed at zone $i point $j."
        Check ($before.DirectValues[$j].Equals($after.DirectValues[$j])) "Direct result changed at zone $i point $j."
    }
}
$html=$run.HtmlReport($run.Display.ScaleMax,$false)
Check ($html.Contains('class="luminaire"')) 'Missing fixture markers.'
Check ([regex]::Matches($html,'class="luminaire"').Count -eq ($run.Zones | Measure-Object -Property LuminaireCount -Sum).Sum) 'Plan fixture markers differ from within-zone fixture counts.'
Check ($html.Contains('Actual solve diagnostics')) 'Missing solve diagnostics.'
if($calculationInput.Emitters.Where({$_.Estimated}).Count -gt 0){Check ($html.Contains('ESTIMATED:')) 'Missing estimated-photometry identification.'}
[IO.File]::WriteAllText($ReportPath,$html)
Write-Output ("Native report replay passed: {0}; {1} points; revision {2}; {3} fc scale; complete={4}. Report: {5}" -f $calculationInput.Set.Name,$run.Display.PointCount,$run.ScenarioHash,$run.Display.ScaleMax,$run.Complete,$ReportPath)
