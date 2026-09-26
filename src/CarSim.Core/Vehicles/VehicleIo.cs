using CarSim.Core.Simulation;

namespace CarSim.Core.Vehicles;

public struct VehicleInputs
{
    public VehicleInputs() { }
    public double Throttle = 0.0;
    public double Brake = 0.0;

    /// <summary>−1 (full right) … +1 (full left).</summary>
    public double Steer = 0.0;

    public double Handbrake = 0.0;

    /// <summary>Request an upshift this step (edge-triggered by the caller).</summary>
    public bool ShiftUp = false;

    public bool ShiftDown = false;
    public bool Ignition = true;
    public bool Starter = false;
}

/// <summary>Wheel order in all per-wheel arrays: front-left, front-right, rear-left, rear-right.</summary>
public static class Wheel
{
    public const int FL = 0, FR = 1, RL = 2, RR = 3;
    public static readonly string[] Names = { "FL", "FR", "RL", "RR" };
}

public sealed class VehicleState
{
    public double Time;
    public double X, Y, Heading;
    public double U, V, YawRate;
    public readonly double[] WheelOmega = new double[4];
    public double EngineOmega;
    public int Gear;
    public int PendingGear;
    public double ShiftTimer;
    public double Clutch = 1.0;
    public double SteerAngle;
    public double LongTransfer, LongTransferRate;
    public double LatTransfer, LatTransferRate;
    public double Ax, Ay;
    public double Distance;
}

/// <summary>Per-step vehicle outputs (SI). Per-wheel arrays follow <see cref="Wheel"/> order.</summary>
public sealed class VehicleTelemetry
{
    public double Time;
    public double Speed;
    public double SpeedKmh => Speed * 3.6;
    public int Gear;
    public double EngineRpm;
    public double Throttle, Brake, Steer;
    public double Clutch;
    public double ClutchSlipRpm;
    public double LongitudinalG, LateralG;
    public double YawRate;
    public double BodySlipAngle;
    public double X, Y, Heading;
    public double Distance;
    public double[] WheelLoad = new double[4];
    public double[] SlipRatio = new double[4];
    public double[] SlipAngle = new double[4];
    public double[] WheelSpeed = new double[4];
    public double[] TyreUsage = new double[4];
    public double[] TyreTemperatureC = new double[4];
    public double[] TyrePressureKpa = new double[4];
    public double ClutchTemperatureC;
    public double ClutchCapacityNm;
    public double BrakeTemperatureFrontC, BrakeTemperatureRearC;
    public EngineTelemetry Engine = null!;

    public string GearLabel => Gear switch { 0 => "N", < 0 => "R", _ => Gear.ToString() };
}
