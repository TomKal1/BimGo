using BimGo.Calculations;
using WWTools.Calcs.Engine;
using WWTools.Lighting.Engine;
using Win = System.Windows.Forms;
using SD = System.Drawing;

namespace BimGo.Game;

/// <summary>In-box Windows image viewer/exporter; the calculation renderer is identical to WWTools.</summary>
internal sealed class CalculatedImageForm : Win.Form
{
    private readonly IReadOnlyList<CalcZone> _zones;
    private readonly Win.ComboBox _room = new() { Width = 240, DropDownStyle = Win.ComboBoxStyle.DropDownList, DisplayMember = "DisplayName" };
    private readonly Win.ComboBox _view = new() { Width = 130, DropDownStyle = Win.ComboBoxStyle.DropDownList };
    private readonly Win.ComboBox _size = new() { Width = 130, DropDownStyle = Win.ComboBoxStyle.DropDownList };
    private readonly Win.NumericUpDown _ev = new() { Minimum = -4, Maximum = 4, Increment = .5m, DecimalPlaces = 1, Width = 65 };
    private readonly Win.PictureBox _picture = new() { Dock = Win.DockStyle.Fill, SizeMode = Win.PictureBoxSizeMode.Zoom };
    private readonly Win.Label _status = new() { Dock = Win.DockStyle.Bottom, Height = 32 };
    private readonly Win.Button _render = new() { Text = "Preview", AutoSize = true };
    private readonly Win.Button _export = new() { Text = "Export PNG…", AutoSize = true };
    private readonly CancellationTokenSource _cancel = new();
    private bool _closed;

    public CalculatedImageForm(IReadOnlyList<CalcZone> zones)
    {
        _zones = zones.Where(z => z.Surfaces.Count > 0).ToArray();
        Text = "Calculated lighting images — BimGo"; Width = 1150; Height = 820; MinimumSize = new SD.Size(850, 550);
        var bar = new Win.FlowLayoutPanel { Dock = Win.DockStyle.Top, AutoSize = true, Padding = new Win.Padding(8), WrapContents = true };
        void Add(string title, Win.Control value)
        {
            var stack = new Win.FlowLayoutPanel { FlowDirection = Win.FlowDirection.TopDown, AutoSize = true, Margin = new Win.Padding(6) };
            stack.Controls.Add(new Win.Label { Text = title, AutoSize = true }); stack.Controls.Add(value); bar.Controls.Add(stack);
        }
        _room.DataSource = _zones.ToList();
        if (_zones.Count == 0) { _status.Text = "No saved surface results. Recalculate before creating lighting images."; _render.Enabled = _export.Enabled = false; }
        _view.Items.AddRange(new object[] { "South-east", "South-west", "North-west", "North-east", "Interior perspective" }); _view.SelectedIndex = 0;
        _size.Items.AddRange(new object[] { "1920 × 1080", "3840 × 2160" }); _size.SelectedIndex = 0;
        Add("Room / saved result", _room); Add("Cutaway camera", _view); Add("Export size", _size); Add("Exposure (EV)", _ev);
        bar.Controls.Add(_render); bar.Controls.Add(_export);
        var basis = new Win.Label { Text = CalculatedImage.Basis, Dock = Win.DockStyle.Bottom, Height = 56, Padding = new Win.Padding(8) };
        Controls.Add(_picture); Controls.Add(basis); Controls.Add(_status); Controls.Add(bar);
        _render.Click += async (_, _) => await Render(false);
        _export.Click += async (_, _) => await Render(true);
        Shown += async (_, _) => await Render(false);
        FormClosed += (_, _) => { _closed = true; _cancel.Cancel(); if (_render.Enabled) _cancel.Dispose(); _picture.Image?.Dispose(); };
    }

    private async Task Render(bool export)
    {
        if (_room.SelectedIndex < 0 || !_render.Enabled) return;
        var zone = _zones[_room.SelectedIndex];
        string path = null;
        if (export)
        {
            using var dialog = new Win.SaveFileDialog { Filter = "PNG image|*.png", DefaultExt = ".png", FileName = "Lighting image.png" };
            if (dialog.ShowDialog(this) != Win.DialogResult.OK) return;
            path = dialog.FileName;
        }
        _render.Enabled = _export.Enabled = false; _status.Text = "Rendering saved calculation…";
        try
        {
            var options = LightingImageOptions.ForView(zone,_view.SelectedIndex,export?(_size.SelectedIndex==0?1920:3840):960,
                export?(_size.SelectedIndex==0?1080:2160):600,export?2:1,(double)_ev.Value);
            byte[] png = await Task.Run(() => CalculatedImage.RenderPng(zone,options,_cancel.Token),_cancel.Token);
            if (_closed) return;
            using var stream = new MemoryStream(png); using var decoded = SD.Image.FromStream(stream);
            var old = _picture.Image; _picture.Image = new SD.Bitmap(decoded); old?.Dispose();
            if (path != null) File.WriteAllBytes(path,png);
            _status.Text = (path == null ? "Preview" : "Saved " + path) + $" · {options.Width} × {options.Height} · EV {options.ExposureEv:0.0} · reference 100 cd/m²";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed) _status.Text = "Image could not be rendered: " + ex.Message; }
        finally { if (!_closed) _render.Enabled = _export.Enabled = true; else _cancel.Dispose(); }
    }
}
