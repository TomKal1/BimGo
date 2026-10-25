param(
    [Parameter(Mandatory = $true)]
    [string]$BuildDirectory
)

# Exercise the compiled App's review/overlay policy and CPU mesh builder without opening a window or GL context.
$ErrorActionPreference = 'Stop'
$buildRoot = (Resolve-Path -LiteralPath $BuildDirectory).Path
[void][Reflection.Assembly]::LoadFrom((Join-Path $buildRoot 'BimGo.Core.dll'))
$appAssembly = [Reflection.Assembly]::LoadFrom((Join-Path $buildRoot 'BimGo.dll'))
$sessionType = $appAssembly.GetType('BimGo.Game.GameSession', $true)
$overlayType = $appAssembly.GetType('BimGo.Rendering.CalcOverlay', $true)
$members = [Reflection.BindingFlags]'Instance,NonPublic,Public'
$session = [Runtime.CompilerServices.RuntimeHelpers]::GetUninitializedObject($sessionType)

function Set-SessionField([string]$Name, $Value) {
    $sessionType.GetField($Name, $members).SetValue($session, $Value)
}
function Session-Property([string]$Name) {
    return $sessionType.GetProperty($Name, $members).GetValue($session)
}
function Check([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
}

$patch = [BimGo.Scene.CalcPatch]::new()
$patch.A = [Numerics.Vector3]::Zero
$patch.B = [Numerics.Vector3]::UnitX
$patch.C = [Numerics.Vector3]::UnitY
$patch.Normal = [Numerics.Vector3]::UnitZ
$zone = [BimGo.Scene.CalcZoneResult]::new()
$zone.Points = [Numerics.Vector3[]]@([Numerics.Vector3]::new(0.25, 0.25, 0.75))
$zone.PointValues = [single[]]@(25)
$zone.Patches = [BimGo.Scene.CalcPatch[]]@($patch)
$zone.CornerIlluminance = [single[]]@(25, 25, 25)
$zone.CornerLuminance = [single[]]@(10, 10, 10)
$zone.CeilingZ = 3
$result = [BimGo.Scene.LightingCalcResult]::new()
$result.Zones = [BimGo.Scene.CalcZoneResult[]]@($zone)
Set-SessionField '_calc' $result
$overlay = [Activator]::CreateInstance($overlayType, $true)
$build = $overlayType.GetMethod('Build', $members)
$vertices = $overlayType.GetProperty('VertexCount', $members)

$cases = 0
foreach ($review in @($false, $true)) {
    Set-SessionField '_reviewOpen' $review
    foreach ($view in 0..3) {
        Set-SessionField '_reviewView' $view
        foreach ($mode in 0..3) {
            Set-SessionField '_calcMode' ([BimGo.Scene.CalcDisplayMode]$mode)
            $effective = Session-Property 'CalcOverlayMode'
            $expected = if ($review -and $mode -eq 0) { 3 } else { $mode }
            Check ([int]$effective -eq $expected) "Incorrect overlay mode: review=$review view=$view mode=$mode"
            Check ((Session-Property 'CalcShowing') -eq ($review -or $mode -ne 0)) 'Incorrect result visibility'
            [void]$build.Invoke($overlay, @($result, $effective, $true, $true, $false, [single]0, [single]100))
            $expectedVertices = if ($expected -eq 0) { 0 } elseif ($expected -eq 3) { 36 } else { 39 }
            Check ($vertices.GetValue($overlay) -eq $expectedVertices) 'Material preview must retain the point mesh without false-colour surface triangles'
            $cases++
        }
    }
}

Set-SessionField '_calc' $null
Check (!(Session-Property 'CalcShowing')) 'Missing results must never draw an overlay'
Set-SessionField '_reviewOpen' $false
Check (!(Session-Property 'ReviewMaterialShading')) 'Walkthrough shading must retain its existing settings'
Set-SessionField '_reviewOpen' $true
Check (Session-Property 'ReviewMaterialShading') 'Review must start with neutral material shading'
Set-SessionField '_reviewSceneLighting' $true
Check (!(Session-Property 'ReviewMaterialShading')) 'Scene lighting must be an explicit preview option'

# Unit changes must update actual cached HUD/export strings and luminance ticks, without relabelling point illuminance.
Set-SessionField '_calc' $result
Set-SessionField '_calcTicks' ([string[]]::new(5))
Set-SessionField '_calcScaleTop' ([single]1)
Set-SessionField '_calcTextKey' ([int]::MinValue)
$result.SetName = 'Unit check'
$result.LuminanceReference = [single](5 * [BimGo.Scene.LightingCalcs]::CD_PER_FOOTLAMBERT)
$unitFormat = $appAssembly.GetType('BimGo.Game.UnitFormat', $true)
$unitSet = $unitFormat.GetMethod('Set', [Reflection.BindingFlags]'Public,Static')
$updateTexts = $sessionType.GetMethod('UpdateCalcTexts', $members)
$unitCases = 0
foreach ($imperial in @($false, $true, $false)) {
    $system = if ($imperial) { [BimGo.Scene.UnitSystem]::Imperial } else { [BimGo.Scene.UnitSystem]::Metric }
    [void]$unitSet.Invoke($null, @($system, $false))
    foreach ($mode in 0..3) {
        Set-SessionField '_calcMode' ([BimGo.Scene.CalcDisplayMode]$mode)
        [void]$updateTexts.Invoke($session, $null)
        $title = $sessionType.GetField('_calcLegendTitle', $members).GetValue($session)
        $pointCaption = $sessionType.GetField('_calcPointCaption', $members).GetValue($session)
        $surfaceCaption = $sessionType.GetField('_calcSurfaceCaption', $members).GetValue($session)
        $pointUnit = if ($imperial) { 'fc' } else { 'lx' }
        Check ($pointCaption.EndsWith("($pointUnit)")) 'Point caption must explicitly identify illuminance in the selected units'
        Check ($pointCaption.Contains('illuminance')) 'Point values must not be mislabelled as luminance'
        if ($mode -eq 2) {
            $surfaceUnit = if ($imperial) { 'fL' } else { 'cd/m²' }
            Check ($title.Contains("SURFACE LUMINANCE [$surfaceUnit]")) 'Luminance legend must honour the unit preference'
            Check ($surfaceCaption.EndsWith("($surfaceUnit)")) 'Dock surface units must match the legend'
            $ticks = $sessionType.GetField('_calcTicks', $members).GetValue($session)
            $top = [single]::Parse($ticks[4], [Globalization.CultureInfo]::InvariantCulture)
            $expectedTop = if ($imperial) { 10 } else { 34 }
            Check ([Math]::Abs($top - $expectedTop) -lt 0.001) 'Luminance tick numbers must be converted, not just their unit label'
        } else {
            Check ($title.Contains("ILLUMINANCE [$pointUnit]")) 'Illuminance legend must honour the unit preference'
        }
        $unitCases++
    }
}
Write-Output "PASS: $cases review/view/mode mesh cases; $unitCases live unit/mode legend cases; missing results and review lighting isolation checked."
