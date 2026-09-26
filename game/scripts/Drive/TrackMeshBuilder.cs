using System;
using CarSim.Core.Vehicles;
using Godot;

namespace CarTuningSim.Drive;

/// <summary>
/// Builds the visible circuit from the same <see cref="TrackLayout"/> the physics uses, so what the
/// player sees (asphalt, kerbs, grass) is exactly where the grip changes.
/// Mapping: simulation (x, y) on the ground plane → Godot (x, height, −y); heading → rotation about Y.
/// </summary>
public static class TrackMeshBuilder
{
    public static readonly Color Asphalt = new(0.17f, 0.17f, 0.18f);
    public static readonly Color KerbRed = new(0.78f, 0.12f, 0.10f);
    public static readonly Color KerbWhite = new(0.92f, 0.92f, 0.90f);
    public static readonly Color Line = new(0.95f, 0.95f, 0.95f);
    public static readonly Color Grass = new(0.17f, 0.30f, 0.12f);

    public static Vector3 ToGodot(double x, double y, double height = 0) => new((float)x, (float)height, (float)-y);

    public static Node3D Build(TrackLayout track)
    {
        var root = new Node3D { Name = "Track" };
        root.AddChild(Ground(track));
        root.AddChild(Strip(track, "Asphalt", -track.Width / 2, track.Width / 2, 0.02, _ => Asphalt));
        double edge = track.Width / 2;
        root.AddChild(Strip(track, "LineLeft", edge - 0.35, edge - 0.2, 0.03, _ => Line));
        root.AddChild(Strip(track, "LineRight", -edge + 0.2, -edge + 0.35, 0.03, _ => Line));
        // Kerbs run along both edges (the physics treats the whole band as kerb): 2 m red/white blocks.
        root.AddChild(Strip(track, "KerbLeft", edge, edge + TrackLayout.KerbWidth, 0.035, i => i % 2 == 0 ? KerbRed : KerbWhite));
        root.AddChild(Strip(track, "KerbRight", -edge - TrackLayout.KerbWidth, -edge, 0.035, i => i % 2 == 0 ? KerbRed : KerbWhite));
        root.AddChild(StartLine(track));
        AddBrakingBoards(track, root);
        return root;
    }

    private static StandardMaterial3D VertexColorMaterial(float roughness = 0.9f) => new()
    {
        VertexColorUseAsAlbedo = true,
        Roughness = roughness,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    /// <summary>A band between two lateral offsets (positive = left of the centreline), one quad per sample.</summary>
    private static MeshInstance3D Strip(TrackLayout track, string name, double from, double to, double height, Func<int, Color> color)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        for (int i = 0; i < track.Count; i++)
        {
            var (a0, a1) = Edge(track, i, from, to, height);
            var (b0, b1) = Edge(track, i + 1, from, to, height);
            var c = color(i);
            st.SetNormal(Vector3.Up);
            foreach (var v in new[] { a0, a1, b1, a0, b1, b0 })
            {
                st.SetColor(c);
                st.AddVertex(v);
            }
        }
        return new MeshInstance3D { Name = name, Mesh = st.Commit(), MaterialOverride = VertexColorMaterial() };
    }

    private static (Vector3, Vector3) Edge(TrackLayout track, int i, double from, double to, double height)
    {
        var (x, y) = track.Point(i);
        double h = track.HeadingAt(i);
        double nx = -Math.Sin(h), ny = Math.Cos(h);
        return (ToGodot(x + nx * from, y + ny * from, height), ToGodot(x + nx * to, y + ny * to, height));
    }

    private static MeshInstance3D Ground(TrackLayout track)
    {
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        for (int i = 0; i < track.Count; i++)
        {
            var (x, y) = track.Point(i);
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        float size = (float)Math.Max(maxX - minX, maxY - minY) + 8000f;
        var mesh = new PlaneMesh { Size = new Vector2(size, size) };
        var material = new StandardMaterial3D { AlbedoColor = Grass, Roughness = 1.0f };
        return new MeshInstance3D
        {
            Name = "Grass",
            Mesh = mesh,
            MaterialOverride = material,
            Position = ToGodot((minX + maxX) / 2, (minY + maxY) / 2),
        };
    }

    /// <summary>Chequered start/finish line across the asphalt at sample 0, with a gantry.</summary>
    private static Node3D StartLine(TrackLayout track)
    {
        var node = new Node3D { Name = "StartFinish" };
        var (x, y) = track.Point(0);
        double h = track.HeadingAt(0);
        node.Position = ToGodot(x, y);
        node.Rotation = new Vector3(0, (float)h, 0);
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        const float cell = 0.5f;
        int across = (int)Math.Round(track.Width / cell);
        for (int row = 0; row < 2; row++)
            for (int col = 0; col < across; col++)
            {
                var c = (row + col) % 2 == 0 ? new Color(0.05f, 0.05f, 0.05f) : Line;
                float x0 = row * cell - cell, z0 = (float)(-track.Width / 2) + col * cell;
                var a = new Vector3(x0, 0.04f, z0);
                var b = new Vector3(x0 + cell, 0.04f, z0);
                var d = new Vector3(x0, 0.04f, z0 + cell);
                var e = new Vector3(x0 + cell, 0.04f, z0 + cell);
                st.SetNormal(Vector3.Up);
                foreach (var v in new[] { a, b, e, a, e, d }) { st.SetColor(c); st.AddVertex(v); }
            }
        node.AddChild(new MeshInstance3D { Mesh = st.Commit(), MaterialOverride = VertexColorMaterial() });

        var steel = new StandardMaterial3D { AlbedoColor = new Color(0.35f, 0.37f, 0.40f), Metallic = 0.6f, Roughness = 0.4f };
        float half = (float)(track.Width / 2 + TrackLayout.KerbWidth + 1.0);
        foreach (float side in new[] { -half, half })
            node.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.3f, 5.5f, 0.3f) }, MaterialOverride = steel, Position = new Vector3(0, 2.75f, side) });
        var banner = new StandardMaterial3D { AlbedoColor = new Color(0.91f, 0.64f, 0.24f) };
        node.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.4f, 0.9f, half * 2) }, MaterialOverride = banner, Position = new Vector3(0, 5.3f, 0) });
        var label = new Label3D { Text = "KESTREL TEST FACILITY", FontSize = 96, PixelSize = 0.01f, Position = new Vector3(-0.25f, 5.3f, 0), Modulate = new Color(0.1f, 0.1f, 0.1f) };
        label.Rotation = new Vector3(0, -Mathf.Pi / 2, 0);
        node.AddChild(label);
        return node;
    }

    /// <summary>100/50 m boards before every corner that follows a long straight (on the outside).</summary>
    private static void AddBrakingBoards(TrackLayout track, Node3D root)
    {
        double s = 0;
        TrackSegment? previous = null;
        foreach (var seg in track.Segments)
        {
            if (seg.IsArc && previous is { IsArc: false } straight && straight.Length >= 150)
            {
                foreach (int metres in new[] { 150, 100, 50 })
                {
                    int i = IndexAtDistance(track, s - metres);
                    var (x, y) = track.Point(i);
                    double h = track.HeadingAt(i);
                    double side = seg.Left ? -1 : 1; // outside of the coming corner
                    double off = side * (track.Width / 2 + TrackLayout.KerbWidth + 2.5);
                    var board = new Node3D { Position = ToGodot(x - Math.Sin(h) * off, y + Math.Cos(h) * off), Rotation = new Vector3(0, (float)h, 0) };
                    var white = new StandardMaterial3D { AlbedoColor = Line };
                    board.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.1f, 1.2f, 1.4f) }, MaterialOverride = white, Position = new Vector3(0, 1.6f, 0) });
                    board.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.12f, 1.0f, 0.12f) }, MaterialOverride = white, Position = new Vector3(0, 0.5f, 0) });
                    var text = new Label3D { Text = metres.ToString(), FontSize = 72, PixelSize = 0.012f, Modulate = new Color(0.05f, 0.05f, 0.05f), Position = new Vector3(-0.07f, 1.6f, 0) };
                    text.Rotation = new Vector3(0, -Mathf.Pi / 2, 0);
                    board.AddChild(text);
                    root.AddChild(board);
                }
            }
            s += seg.Length;
            previous = seg;
        }
    }

    private static int IndexAtDistance(TrackLayout track, double distance)
    {
        double d = ((distance % track.Length) + track.Length) % track.Length;
        int best = 0;
        for (int i = 0; i < track.Count; i++)
            if (Math.Abs(track.DistanceAt(i) - d) < Math.Abs(track.DistanceAt(best) - d)) best = i;
        return best;
    }
}
