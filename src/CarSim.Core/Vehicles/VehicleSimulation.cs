using CarSim.Core.Common;
using CarSim.Core.Simulation;

namespace CarSim.Core.Vehicles;

/// <summary>
/// Planar vehicle model coupled to the engine model. Each <see cref="Step"/> runs the engine once
/// (speed-held at the current crank speed) and then <see cref="Substeps"/> driveline/chassis substeps:
/// tyre forces from wheel slip, clutch and differential torques, brakes, wheel spin, body motion
/// (surge, sway, yaw) and roll/pitch load transfer as damped second-order responses.
/// </summary>
public sealed class VehicleSimulation
{
    public const int Substeps = 8;
    public const double AirDensity = 1.2;
    public const double LowSpeedSlipReference = 2.0;
    public const double HandbrakeTorqueNm = 1500.0;

    /// <summary>
    /// While the automated clutch re-engages after a gear change it lets through at most the engine's
    /// torque plus this much (a smooth engagement, like a good driver), so shifts do not shock the
    /// gearbox. Launches from rest are not limited: dumping a sticky clutch at high revs still can.
    /// </summary>
    public const double ShiftSyncTorqueMarginNm = 80.0;

    private bool _syncingAfterShift;

    private readonly double[] _wheelX = new double[4];
    private readonly double[] _wheelY = new double[4];
    private readonly double[] _fz = new double[4];
    private readonly double[] _fx = new double[4];
    private readonly double[] _fy = new double[4];
    private readonly double[] _slipRatio = new double[4];
    private readonly double[] _slipAngle = new double[4];
    private readonly double[] _driveTorque = new double[4];

    public VehicleSimulation(VehicleConfiguration config, EngineSimulation engine, VehicleState? state = null)
    {
        Config = config;
        Engine = engine;
        State = state ?? new VehicleState();
        var c = config;
        _wheelX[Wheel.FL] = _wheelX[Wheel.FR] = c.CgToFront;
        _wheelX[Wheel.RL] = _wheelX[Wheel.RR] = -c.CgToRear;
        _wheelY[Wheel.FL] = c.TrackFront / 2; _wheelY[Wheel.FR] = -c.TrackFront / 2;
        _wheelY[Wheel.RL] = c.TrackRear / 2; _wheelY[Wheel.RR] = -c.TrackRear / 2;
        Wear = new ChassisWearModel(config);
        Tyres = new TyreThermalModel(config);
    }

    public VehicleConfiguration Config { get; }
    public EngineSimulation Engine { get; }

    /// <summary>Heat and wear of the clutch, brakes and tyres (and their failures).</summary>
    public ChassisWearModel Wear { get; }

    /// <summary>Tyre tread temperatures and the pressures and grip that follow from them.</summary>
    public TyreThermalModel Tyres { get; }

    private readonly TyreState[] _tyreState = new TyreState[4];

    private readonly ChassisLoads _energy = new();

    /// <summary>Optional circuit: provides surface grip under each wheel (asphalt, kerb, grass).</summary>
    public TrackLayout? Track { get; set; }

    private readonly int[] _nearest = { -1, -1, -1, -1 };
    private readonly double[] _surfaceGrip = { 1, 1, 1, 1 };

    /// <summary>Surface under each wheel this step.</summary>
    public Surface[] WheelSurface { get; } = new Surface[4];
    public VehicleState State { get; }
    public VehicleTelemetry? Last { get; private set; }

    public double IdleRpm => Engine.Ecu.Tune.IdleRpm;

    private double WheelInertia(int w, int gear)
    {
        double i = Config.TireOf(w).InertiaKgM2;
        if (Config.IsDriven(w) && gear != 0)
        {
            double g = Config.OverallRatio(gear);
            i += Config.Gearbox.InputInertiaKgM2 * g * g / 2.0;
        }
        return i;
    }

    public VehicleTelemetry Step(double dt, VehicleInputs input)
    {
        var c = Config;
        var s = State;
        UpdateShifting(dt, input);

        double throttle = s.ShiftTimer > 0 ? 0.0 : Math.Clamp(input.Throttle, 0, 1);
        double speed = Math.Sqrt(s.U * s.U + s.V * s.V);
        var engineInput = new EngineInputs
        {
            Throttle = throttle,
            SpeedMode = SpeedMode.Held,
            HeldRpm = Units.RadPerSecToRpm(s.EngineOmega),
            Ignition = input.Ignition,
            Starter = input.Starter,
            CoolingAirSpeed = speed + 2.0,
            SumpAccelerationG = Math.Sqrt(s.Ax * s.Ax + s.Ay * s.Ay) / PhysicalConstants.Gravity,
        };
        var et = Engine.Step(dt, engineInput);
        double engineTorque = et.Torque;
        bool seized = et.Seized;
        double starter = input.Starter && !seized ? EngineSimulation.StarterTorqueNm * Math.Max(0.0, 1.0 - Units.RadPerSecToRpm(s.EngineOmega) / EngineSimulation.StarterFreeRpm) : 0.0;

        UpdateClutch(dt, input, throttle);

        UpdateSurfaces();
        double h = dt / Substeps;
        double steerTarget = Math.Clamp(input.Steer, -1, 1) * c.MaxSteer;
        s.SteerAngle += (steerTarget - s.SteerAngle) * MathUtil.LagFactor(dt, 0.05);
        double ieng = Engine.Config.RotatingInertia;
        double ratio = Wear.DriveBroken ? 0.0 : c.OverallRatio(s.Gear);
        double eff = c.Gearbox.Efficiency;
        double clutchCapacity = Wear.ClutchCapacityNm;
        double brakeFront = c.Brakes.FrontMaxTorqueNm * Wear.BrakeFactor(0);
        double brakeRear = c.Brakes.RearMaxTorqueNm * c.Brakes.RearPressureFactor * Wear.BrakeFactor(1);
        double rollStiffness = c.RollStiffnessFront + c.RollStiffnessRear;
        for (int w = 0; w < 4; w++) _tyreState[w] = Tyres.StateOf(w);
        _energy.Clear();
        double lastGearboxOmega = 0;

        for (int k = 0; k < Substeps; k++)
        {
            ComputeLoads();
            // Body roll (positive when cornering left: the body leans right) changes each wheel's camber.
            double rollDeg = s.LatTransfer / rollStiffness * 180.0 / Math.PI;
            double fxBody = 0, fyBody = 0, mz = 0;
            for (int w = 0; w < 4; w++)
            {
                double delta = w < 2 ? s.SteerAngle : 0.0;
                double uw = s.U - s.YawRate * _wheelY[w];
                double vw = s.V + s.YawRate * _wheelX[w];
                double cos = Math.Cos(delta), sin = Math.Sin(delta);
                double ul = uw * cos + vw * sin;
                double vl = -uw * sin + vw * cos;
                var tire = c.TireOf(w);
                double denom = Math.Max(Math.Abs(ul), LowSpeedSlipReference);
                _slipRatio[w] = (s.WheelOmega[w] * tire.Radius - ul) / denom;
                _slipAngle[w] = Math.Atan2(vl, denom);
                var (fx, fy) = TireModel.Forces(tire, _fz[w], _slipRatio[w], _slipAngle[w], WheelLeanLeftDeg(w, rollDeg), _tyreState[w]);
                double grip = _surfaceGrip[w] * Wear.GripFactor(w);
                fx *= grip;
                fy *= grip;
                _energy.Tyre[w] += (Math.Abs(fx * (s.WheelOmega[w] * tire.Radius - ul)) + Math.Abs(fy * vl)) * h;
                _fx[w] = fx;
                _fy[w] = fy;
                double bx = fx * cos - fy * sin;
                double by = fx * sin + fy * cos;
                fxBody += bx;
                fyBody += by;
                mz += _wheelX[w] * by - _wheelY[w] * bx;
            }
            double drag = 0.5 * AirDensity * c.DragArea * s.U * Math.Abs(s.U);
            fxBody -= drag;

            // Driveline: clutch between engine and gearbox input, differential to the driven wheels.
            int d0 = c.RearWheelDrive ? Wheel.RL : Wheel.FL, d1 = d0 + 1;
            double carrierOmega = 0.5 * (s.WheelOmega[d0] + s.WheelOmega[d1]);
            double gearboxOmega = carrierOmega * ratio;
            double clutchTorque = 0.0;
            if (ratio != 0.0 && s.Clutch > 0.0)
            {
                double iDrive = c.Gearbox.InputInertiaKgM2 + (c.TireOf(d0).InertiaKgM2 * 2) / (ratio * ratio);
                double reduced = seized ? iDrive : ieng * iDrive / (ieng + iDrive);
                double kc = 0.8 * reduced / h;
                double capacity = s.Clutch * clutchCapacity;
                if (_syncingAfterShift) capacity = Math.Min(capacity, Math.Abs(engineTorque) + ShiftSyncTorqueMarginNm);
                clutchTorque = Math.Clamp(kc * (s.EngineOmega - gearboxOmega), -capacity, capacity);
                _energy.Clutch += Math.Abs(clutchTorque * (s.EngineOmega - gearboxOmega)) * h;
            }
            lastGearboxOmega = gearboxOmega;
            double carrierTorque = clutchTorque * ratio * (clutchTorque * ratio >= 0 ? eff : 1.0 / eff);
            _energy.GearboxTorque = Math.Max(_energy.GearboxTorque, Math.Abs(clutchTorque));
            _energy.DifferentialTorque = Math.Max(_energy.DifferentialTorque, Math.Abs(carrierTorque) / c.Differential.FinalDriveRatio);
            double bias = DifferentialBias(carrierTorque, s.WheelOmega[d0], s.WheelOmega[d1], h);
            for (int w = 0; w < 4; w++) _driveTorque[w] = 0.0;
            _driveTorque[d0] = 0.5 * carrierTorque + 0.5 * bias;
            _driveTorque[d1] = 0.5 * carrierTorque - 0.5 * bias;

            // Wheels.
            for (int w = 0; w < 4; w++)
            {
                var tire = c.TireOf(w);
                double inertia = WheelInertia(w, s.Gear);
                double footBrake = input.Brake * (w < 2 ? brakeFront : brakeRear);
                double brakeTorque = footBrake
                                     + (w >= 2 ? input.Handbrake * HandbrakeTorqueNm : 0.0)
                                     + _fz[w] * TireModel.RollingResistance(tire, _tyreState[w].PressureKpa) * tire.Radius;
                double net = _driveTorque[w] - _fx[w] * tire.Radius;
                double omega = s.WheelOmega[w];
                double predicted = omega + net / inertia * h;
                double brakeStep = brakeTorque / inertia * h;
                if (Math.Abs(predicted) <= brakeStep) s.WheelOmega[w] = 0.0;
                else s.WheelOmega[w] = predicted - Math.Sign(predicted) * brakeStep;
                if (brakeTorque > 0) _energy.BrakeAxle[w < 2 ? 0 : 1] += footBrake * Math.Abs(s.WheelOmega[w]) * h;
            }

            // Engine side of the clutch.
            if (seized) s.EngineOmega = 0.0;
            else s.EngineOmega = Math.Max(0.0, s.EngineOmega + (engineTorque + starter - clutchTorque) / ieng * h);

            // Body.
            s.Ax = fxBody / c.Mass;
            s.Ay = fyBody / c.Mass;
            double du = s.Ax + s.YawRate * s.V;
            double dv = s.Ay - s.YawRate * s.U;
            s.U += du * h;
            s.V += dv * h;
            s.YawRate += mz / c.YawInertia * h;
            // Parked: kill numerical creep when nothing is driving the car.
            if (Math.Abs(s.U) < 0.05 && Math.Abs(s.V) < 0.05 && throttle < 0.01 && clutchTorque == 0.0 && input.Brake > 0.05)
            {
                s.U = s.V = s.YawRate = 0.0;
            }
            double cosH = Math.Cos(s.Heading), sinH = Math.Sin(s.Heading);
            s.X += (s.U * cosH - s.V * sinH) * h;
            s.Y += (s.U * sinH + s.V * cosH) * h;
            s.Heading += s.YawRate * h;
            s.Distance += Math.Sqrt(s.U * s.U + s.V * s.V) * h;

            // Load transfer targets and second-order suspension response.
            double longTarget = -c.Mass * s.Ax * c.CgHeight / c.Wheelbase;
            double latTarget = c.Mass * s.Ay * c.CgHeight;
            Filter(ref s.LongTransfer, ref s.LongTransferRate, longTarget, c.PitchFrequency, c.PitchDampingRatio, h);
            Filter(ref s.LatTransfer, ref s.LatTransferRate, latTarget, c.RollFrequency, c.RollDampingRatio, h);

            if (k == Substeps - 1)
            {
                Last = BuildTelemetry(et, throttle, input, gearboxOmega);
            }
        }
        s.Time += dt;
        for (int w = 0; w < 4; w++)
            _energy.TyreRolling[w] = _fz[w] * TireModel.RollingResistance(c.TireOf(w), _tyreState[w].PressureKpa) * Math.Abs(s.WheelOmega[w] * c.TireOf(w).Radius) * dt;
        Tyres.Update(dt, _energy.Tyre, _energy.TyreRolling, speed);
        if (_syncingAfterShift && (ratio == 0.0 || (s.Clutch > 0.99 && Math.Abs(s.EngineOmega - lastGearboxOmega) < Units.RpmToRadPerSec(50))))
            _syncingAfterShift = false;
        bool clutchCommanded = s.Clutch >= 0.999 && s.ShiftTimer <= 0 && s.Gear != 0;
        Wear.Update(dt, s.Time, _energy, speed, et.Torque, Units.RadPerSecToRpm(s.EngineOmega - lastGearboxOmega), clutchCommanded, s.Gear);
        return Last!;
    }

    /// <summary>
    /// Camber of wheel <paramref name="w"/> relative to the road, as the lean of its top towards the car's
    /// left (degrees): static camber plus body roll, less what the suspension geometry recovers.
    /// </summary>
    public double WheelLeanLeftDeg(int w, double rollDeg)
    {
        var susp = Config.Suspension;
        bool front = w < 2;
        double side = w == Wheel.FL || w == Wheel.RL ? 1.0 : -1.0;
        double camber = (front ? susp.FrontCamberDeg : susp.RearCamberDeg)
                        - side * rollDeg * (1.0 - (front ? susp.FrontCamberGain : susp.RearCamberGain));
        return side * camber;
    }

    private void UpdateSurfaces()
    {
        if (Track == null) return;
        var s = State;
        double cos = Math.Cos(s.Heading), sin = Math.Sin(s.Heading);
        for (int w = 0; w < 4; w++)
        {
            double wx = s.X + _wheelX[w] * cos - _wheelY[w] * sin;
            double wy = s.Y + _wheelX[w] * sin + _wheelY[w] * cos;
            _nearest[w] = Track.Nearest(wx, wy, _nearest[w]);
            WheelSurface[w] = Track.SurfaceAt(wx, wy, _nearest[w]);
            _surfaceGrip[w] = TrackLayout.Grip(WheelSurface[w]);
        }
    }

    private static void Filter(ref double x, ref double rate, double target, double wn, double zeta, double h)
    {
        double acc = wn * wn * (target - x) - 2.0 * zeta * wn * rate;
        rate += acc * h;
        x += rate * h;
    }

    /// <summary>Differential locking torque passed from the faster to the slower wheel.</summary>
    private double DifferentialBias(double carrierTorque, double omegaLeft, double omegaRight, double h)
    {
        var d = Config.Differential;
        double limit = d.Type switch
        {
            "locked" => 1e5,
            "clutch_lsd" => d.PreloadNm + (carrierTorque >= 0 ? d.LockingAccel : d.LockingDecel) * Math.Abs(carrierTorque),
            _ => 0.0,
        };
        if (limit <= 0) return 0.0;
        var tire = Config.RearWheelDrive ? Config.TiresRear : Config.TiresFront;
        double kd = 0.8 * (tire.InertiaKgM2 / 2.0) / h;
        return Math.Clamp(kd * (omegaRight - omegaLeft), -limit, limit);
    }

    private void ComputeLoads()
    {
        var c = Config;
        var s = State;
        double weight = c.Mass * PhysicalConstants.Gravity;
        double front = weight * c.CgToRear / c.Wheelbase + s.LongTransfer;
        double rear = weight * c.CgToFront / c.Wheelbase - s.LongTransfer;
        double latF = s.LatTransfer * c.FrontRollShare / c.TrackFront;
        double latR = s.LatTransfer * (1 - c.FrontRollShare) / c.TrackRear;
        // Positive lateral acceleration (turning left) loads the right-hand wheels.
        _fz[Wheel.FL] = Math.Max(0, front / 2 - latF);
        _fz[Wheel.FR] = Math.Max(0, front / 2 + latF);
        _fz[Wheel.RL] = Math.Max(0, rear / 2 - latR);
        _fz[Wheel.RR] = Math.Max(0, rear / 2 + latR);
    }

    private void UpdateShifting(double dt, VehicleInputs input)
    {
        var s = State;
        int top = Config.Gearbox.Ratios.Count;
        if (s.ShiftTimer <= 0)
        {
            int target = s.Gear;
            if (input.ShiftUp && s.Gear < top) target = s.Gear + 1;
            else if (input.ShiftDown)
            {
                if (s.Gear > 1 || s.Gear == 1) target = s.Gear - 1;
                else if (s.Gear == 0 && Math.Abs(s.U) < 1.0) target = -1;
            }
            if (target != s.Gear)
            {
                s.PendingGear = target;
                s.ShiftTimer = Config.Gearbox.ShiftTimeS;
            }
        }
        else
        {
            double before = s.ShiftTimer;
            s.ShiftTimer = Math.Max(0, s.ShiftTimer - dt);
            if (before > Config.Gearbox.ShiftTimeS / 2 && s.ShiftTimer <= Config.Gearbox.ShiftTimeS / 2)
            {
                s.Gear = s.PendingGear;
                // Moving: a shift, engaged smoothly. At rest: a launch, which is the driver's business.
                _syncingAfterShift = s.Gear != 0 && Math.Abs(s.U) > 2.0;
            }
        }
    }

    /// <summary>
    /// Automatic clutch (manual gears): open during shifts and in neutral, slips to launch from rest,
    /// opens to avoid stalling, otherwise fully engaged.
    /// </summary>
    private void UpdateClutch(double dt, VehicleInputs input, double throttle)
    {
        var s = State;
        double target;
        double rpm = Units.RadPerSecToRpm(s.EngineOmega);
        double ratio = Config.OverallRatio(s.Gear);
        int d0 = Config.RearWheelDrive ? Wheel.RL : Wheel.FL;
        double gearboxRpm = Units.RadPerSecToRpm(0.5 * (s.WheelOmega[d0] + s.WheelOmega[d0 + 1]) * ratio);
        if (s.ShiftTimer > 0 || s.Gear == 0 || !Engine.State.Running && !input.Starter) target = 0.0;
        else if (rpm < IdleRpm * 0.85 && Math.Abs(gearboxRpm) < rpm + 50) target = 0.0;
        else if (rpm - Math.Abs(gearboxRpm) > 150 && Math.Abs(gearboxRpm) < 2500)
            target = MathUtil.SmoothStep(IdleRpm + 150, IdleRpm + 1800, rpm) * (throttle > 0.02 ? 1.0 : 0.3);
        else target = 1.0;
        double rate = target > s.Clutch ? 4.0 : 25.0;
        s.Clutch += Math.Clamp(target - s.Clutch, -rate * dt, rate * dt);
    }

    private VehicleTelemetry BuildTelemetry(EngineTelemetry et, double throttle, VehicleInputs input, double gearboxOmega)
    {
        var s = State;
        var t = new VehicleTelemetry
        {
            Time = s.Time,
            Speed = Math.Sqrt(s.U * s.U + s.V * s.V),
            Gear = s.Gear,
            EngineRpm = Units.RadPerSecToRpm(s.EngineOmega),
            Throttle = throttle,
            Brake = input.Brake,
            Steer = s.SteerAngle,
            Clutch = s.Clutch,
            ClutchSlipRpm = Units.RadPerSecToRpm(s.EngineOmega - gearboxOmega),
            LongitudinalG = s.Ax / PhysicalConstants.Gravity,
            LateralG = s.Ay / PhysicalConstants.Gravity,
            YawRate = s.YawRate,
            BodySlipAngle = Math.Abs(s.U) > 1 ? Math.Atan2(s.V, Math.Abs(s.U)) : 0.0,
            X = s.X, Y = s.Y, Heading = s.Heading, Distance = s.Distance,
            Engine = et,
            ClutchTemperatureC = Wear.ClutchTemperatureC,
            ClutchCapacityNm = Wear.ClutchCapacityNm,
            BrakeTemperatureFrontC = Units.KToC(Wear.BrakeTemperature[0]),
            BrakeTemperatureRearC = Units.KToC(Wear.BrakeTemperature[1]),
        };
        for (int w = 0; w < 4; w++)
        {
            t.WheelLoad[w] = _fz[w];
            t.SlipRatio[w] = _slipRatio[w];
            t.SlipAngle[w] = _slipAngle[w];
            t.WheelSpeed[w] = s.WheelOmega[w] * Config.TireOf(w).Radius;
            double available = TireModel.Friction(Config.TireOf(w), _fz[w], _tyreState[w]) * _fz[w] * _surfaceGrip[w];
            t.TyreTemperatureC[w] = Units.KToC(Tyres.TemperatureK[w]);
            t.TyrePressureKpa[w] = _tyreState[w].PressureKpa;
            t.TyreUsage[w] = available > 0 ? Math.Sqrt(_fx[w] * _fx[w] + _fy[w] * _fy[w]) / available : 0.0;
        }
        return t;
    }

    /// <summary>Places the car at rest with the engine idling in neutral.</summary>
    public void StartIdling()
    {
        State.EngineOmega = Units.RpmToRadPerSec(IdleRpm);
        Engine.State.Running = true;
        Engine.State.Omega = State.EngineOmega;
    }
}
