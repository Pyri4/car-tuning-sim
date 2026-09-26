using System;
using CarSim.Core.Common;
using CarSim.Core.Vehicles;
using Godot;

namespace CarTuningSim.Drive;

/// <summary>
/// A simple visual stand-in for the car, driven entirely by the simulation state: position and heading,
/// body roll and pitch from the suspension's load-transfer states, wheel spin and steering, brake lights.
/// Local axes: +X forward, +Y up, +Z to the right (simulation +y, the car's left, is −Z).
/// </summary>
public partial class CarVisual : Node3D
{
    /// <summary>Visual body roll/pitch per g of steady-state load transfer (degrees).</summary>
    public const float RollDegPerG = 3.0f, PitchDegPerG = 2.0f;

    private readonly Node3D _body = new() { Name = "Body" };
    private readonly Node3D[] _hubs = new Node3D[4];
    private readonly Node3D[] _spin = new Node3D[4];
    private readonly double[] _spinAngle = new double[4];
    private StandardMaterial3D _brakeLight = null!;
    private VehicleConfiguration _car = null!;

    public static CarVisual Create(VehicleConfiguration car)
    {
        var v = new CarVisual { Name = "Car" };
        v._car = car;
        v.Build();
        return v;
    }

    private void Build()
    {
        var c = _car;
        float rF = (float)c.TiresFront.Radius, rR = (float)c.TiresRear.Radius;
        float front = (float)c.CgToFront, rear = (float)c.CgToRear;
        float length = front + rear + 1.6f;
        // Slightly narrower than the tyres' outer edges so the wheels (steer, spin) stay visible.
        float width = (float)Math.Max(c.TrackFront, c.TrackRear) + 0.08f;
        float centreX = (front - rear) / 2f + 0.05f;
        AddChild(_body);

        var paint = new StandardMaterial3D { AlbedoColor = new Color(0.80f, 0.16f, 0.12f), Metallic = 0.4f, Roughness = 0.35f };
        var glass = new StandardMaterial3D { AlbedoColor = new Color(0.08f, 0.10f, 0.13f), Metallic = 0.2f, Roughness = 0.1f };
        var black = new StandardMaterial3D { AlbedoColor = new Color(0.05f, 0.05f, 0.05f), Roughness = 0.8f };
        _brakeLight = new StandardMaterial3D { AlbedoColor = new Color(0.35f, 0.02f, 0.02f), EmissionEnabled = true, Emission = new Color(1, 0.05f, 0.02f), EmissionEnergyMultiplier = 0f };

        _body.AddChild(Box(new Vector3(length, 0.46f, width), new Vector3(centreX, 0.43f, 0), paint));
        _body.AddChild(Box(new Vector3(length * 0.46f, 0.40f, width * 0.84f), new Vector3(centreX - 0.25f, 0.86f, 0), glass));
        _body.AddChild(Box(new Vector3(length * 0.30f, 0.05f, width * 0.84f), new Vector3(centreX - 0.30f, 1.08f, 0), paint));
        _body.AddChild(Box(new Vector3(0.12f, 0.16f, width * 0.96f), new Vector3(centreX - length / 2 + 0.02f, 0.30f, 0), black));
        _body.AddChild(Box(new Vector3(0.12f, 0.16f, width * 0.96f), new Vector3(centreX + length / 2 - 0.02f, 0.30f, 0), black));
        foreach (float side in new[] { -1f, 1f })
        {
            _body.AddChild(Box(new Vector3(0.04f, 0.10f, 0.30f), new Vector3(centreX - length / 2 - 0.01f, 0.55f, side * (width / 2 - 0.25f)), _brakeLight));
            var headlight = new StandardMaterial3D { AlbedoColor = new Color(0.9f, 0.9f, 0.8f), EmissionEnabled = true, Emission = new Color(1, 1, 0.9f), EmissionEnergyMultiplier = 0.3f };
            _body.AddChild(Box(new Vector3(0.04f, 0.10f, 0.32f), new Vector3(centreX + length / 2 + 0.01f, 0.52f, side * (width / 2 - 0.28f)), headlight));
        }

        var tyre = new StandardMaterial3D { AlbedoColor = new Color(0.07f, 0.07f, 0.07f), Roughness = 0.9f };
        var rim = new StandardMaterial3D { AlbedoColor = new Color(0.70f, 0.72f, 0.75f), Metallic = 0.8f, Roughness = 0.3f };
        for (int w = 0; w < 4; w++)
        {
            bool isFront = w < 2;
            bool left = w == Wheel.FL || w == Wheel.RL;
            float r = isFront ? rF : rR;
            float trackHalf = (float)(isFront ? c.TrackFront : c.TrackRear) / 2f;
            float tyreWidth = (float)(isFront ? c.TiresFront.WidthMm : c.TiresRear.WidthMm) / 1000f;
            var hub = new Node3D { Position = new Vector3(isFront ? front : -rear, r, left ? -trackHalf : trackHalf) };
            var spin = new Node3D();
            hub.AddChild(spin);
            spin.AddChild(new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = r, BottomRadius = r, Height = tyreWidth, RadialSegments = 24 },
                MaterialOverride = tyre,
                RotationDegrees = new Vector3(90, 0, 0),
            });
            float outward = (left ? -1 : 1) * (tyreWidth / 2 + 0.005f);
            spin.AddChild(new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = r * 0.62f, BottomRadius = r * 0.62f, Height = 0.02f, RadialSegments = 20 },
                MaterialOverride = rim,
                RotationDegrees = new Vector3(90, 0, 0),
                Position = new Vector3(0, 0, outward),
            });
            // Spokes so wheel rotation is visible.
            foreach (float angle in new[] { 0f, 60f, 120f })
                spin.AddChild(new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = new Vector3(r * 1.2f, 0.07f, 0.03f) },
                    MaterialOverride = black,
                    RotationDegrees = new Vector3(0, 0, angle),
                    Position = new Vector3(0, 0, outward * 1.1f),
                });
            AddChild(hub);
            _hubs[w] = hub;
            _spin[w] = spin;
        }
    }

    private static MeshInstance3D Box(Vector3 size, Vector3 position, Material material) =>
        new() { Mesh = new BoxMesh { Size = size }, Position = position, MaterialOverride = material };

    /// <summary>Places the car from the simulation state (call once per rendered frame).</summary>
    public void UpdateFrom(VehicleSimulation sim, double delta, double brake)
    {
        var s = sim.State;
        Position = TrackMeshBuilder.ToGodot(s.X, s.Y);
        Rotation = new Vector3(0, (float)s.Heading, 0);

        // Effective g from the suspension's load-transfer states (includes their lag and overshoot).
        double weightTimesHeight = _car.Mass * PhysicalConstants.Gravity * _car.CgHeight;
        double latG = s.LatTransfer / weightTimesHeight;
        double longG = -s.LongTransfer * _car.Wheelbase / weightTimesHeight;
        _body.RotationDegrees = new Vector3((float)(RollDegPerG * latG), 0, (float)(PitchDegPerG * longG));

        for (int w = 0; w < 4; w++)
        {
            _spinAngle[w] = (_spinAngle[w] - s.WheelOmega[w] * delta) % (2 * Math.PI);
            _spin[w].Rotation = new Vector3(0, 0, (float)_spinAngle[w]);
            if (w < 2) _hubs[w].Rotation = new Vector3(0, (float)s.SteerAngle, 0);
        }
        _brakeLight.EmissionEnergyMultiplier = brake > 0.05 ? 3.0f : 0.0f;
    }
}
