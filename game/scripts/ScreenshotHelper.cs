using Godot;

namespace CarTuningSim;

/// <summary>
/// Development aid: <c>godot --path game -- --screenshot=out.png [--frames=N]</c> renders N frames,
/// saves the viewport to a PNG and quits. Used for automated visual checks of the UI.
/// </summary>
public partial class ScreenshotHelper : Node
{
    private string? _path;
    private int _frames = 30;

    public override void _Ready()
    {
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--screenshot=")) _path = arg["--screenshot=".Length..];
            else if (arg.StartsWith("--frames=") && int.TryParse(arg["--frames=".Length..], out var f)) _frames = f;
        }
        if (_path == null) SetProcess(false);
    }

    public override void _Process(double delta)
    {
        if (--_frames > 0) return;
        var image = GetViewport().GetTexture().GetImage();
        var error = image.SavePng(_path!);
        GD.Print(error == Error.Ok ? $"Screenshot saved to {_path}" : $"Screenshot failed: {error}");
        GetTree().Quit();
    }
}
