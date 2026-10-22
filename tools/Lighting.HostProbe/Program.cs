using Lighting.Baseline;
using WWTools.Lighting.Engine;

// A consumer probe, NOT the WWTools Revit add-in. Verifies binary loading and solve/report APIs.
var directory = AppContext.BaseDirectory;
if (Directory.GetFiles(directory, "BimGo*.dll").Length != 0 || File.Exists(Path.Combine(directory, "WWTools.dll")))
    throw new InvalidOperationException("Run the probe without either product's assemblies.");
var expected = new Dictionary<string, double>
{
    ["small-room"] = 17.4487496693682,
    ["open-office"] = 49.767459355,
    ["near-field-obstruction"] = 29.366295796,
    ["asymmetric-source"] = 9.309434558,
    ["connected-opening"] = 9.306995467
};
foreach (var scene in SceneCase.Corpus())
{
    var input = scene.Build();
    new LightingSolver(input.Sources, scene.Options).Solve(input.Zone, input.Triangles);
    if (Math.Abs(input.Zone.Statistics.Average - expected[scene.Name]) > 1e-8)
        throw new InvalidOperationException("Binary consumer numerical mismatch: " + scene.Name);
    if (LightingReport.Csv(new LightingReportData { Zones = { input.Zone } }).Length < 100)
        throw new InvalidOperationException("Independent CSV generation failed.");
    Console.WriteLine($"{scene.Name}: {input.Zone.Statistics.Average:F9} fc; offline report generated.");
}
Console.WriteLine("Binary consumer passed without BimGo.Core, BimGo.App, WWTools, Revit, or their installation paths.");
