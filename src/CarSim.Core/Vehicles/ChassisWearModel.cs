using CarSim.Core.Common;
using CarSim.Core.Damage;
using CarSim.Core.Engines;
using CarSim.Core.Parts;
using CarSim.Core.Parts.Specs;

namespace CarSim.Core.Vehicles;

/// <summary>Energy dissipated in one vehicle step by the parts that wear by friction (J).</summary>
public sealed class FrictionEnergy
{
    public double Clutch;
    public readonly double[] BrakeAxle = new double[2];
    public readonly double[] Tyre = new double[4];

    public void Clear()
    {
        Clutch = 0;
        BrakeAxle[0] = BrakeAxle[1] = 0;
        for (int w = 0; w < 4; w++) Tyre[w] = 0;
    }
}

/// <summary>
/// Heat and wear of the friction parts: clutch, brakes and tyres. Every joule they dissipate heats a
/// lumped thermal mass and wears the friction material; hot material fades (less friction) and wears
/// much faster, so abuse snowballs: a clutch slipping under too much torque heats, fades, slips more
/// and burns out. Wear lives on the part instances, so it persists in the garage and saves.
/// Effects: clutch capacity falls with wear (clamp load drops as the facings thin) and heat; brake
/// torque falls with heat (fade) and when the pads are gone; tyre grip falls with tread wear.
/// </summary>
public sealed class ChassisWearModel
{
    public const double AmbientK = 298.15;

    /// <summary>Capacity lost when the clutch facings are fully worn.</summary>
    public const double ClutchWearCapacityLoss = 0.5;

    /// <summary>Friction lost to heat, reached 200 K above the fade temperature.</summary>
    public const double ClutchFadeLoss = 0.4;
    public const double ClutchFadeSpanK = 200;
    public const double ClutchCoolingWPerK = 15;

    /// <summary>Wear multiplier grows as (1 + ΔT/scale)² above the fade temperature (glazing, burning).</summary>
    public const double ClutchHotWearScaleK = 25;

    public const double BrakeFadeLoss = 0.45;
    public const double BrakeFadeSpanK = 250;
    public const double BrakeHotWearScaleK = 100;

    /// <summary>Convective cooling per axle: base + per m/s of road speed (vented fronts cool better).</summary>
    public const double BrakeCoolingBaseWPerK = 10, FrontBrakeCoolingPerMs = 3.5, RearBrakeCoolingPerMs = 2.0;

    /// <summary>Radiating area × emissivity per axle (m²).</summary>
    public const double BrakeRadiatingArea = 0.15;

    /// <summary>Brake torque left with the pads worn to the backing plates.</summary>
    public const double WornPadTorqueFactor = 0.4;

    /// <summary>Grip lost as the tread wears (hardened, thin tyres); worn to the cords loses much more.</summary>
    public const double TyreWearGripLoss = 0.10, WornOutGripFactor = 0.75;

    /// <summary>Clutch capacity left once the facings are gone.</summary>
    public const double BurntClutchCapacityFactor = 0.08;

    private readonly VehicleConfiguration _c;
    private readonly PartInstance? _clutch, _brakes, _tyresFront, _tyresRear;
    private readonly List<FailureReport> _failures = new();
    private double _maxClutchC, _maxBrakeC;
    private double _clutchSlipSeconds;
    private double _peakSlipTorque;
    private double? _wearAtFirstSlip;

    public ChassisWearModel(VehicleConfiguration config)
    {
        _c = config;
        _clutch = config.Chassis.PartIn("clutch");
        _brakes = config.Chassis.PartIn("brakes");
        _tyresFront = config.Chassis.PartIn("tires_front");
        _tyresRear = config.Chassis.PartIn("tires_rear");
        ClutchTemperature = AmbientK + 20;
        BrakeTemperature[0] = BrakeTemperature[1] = AmbientK + 20;
        _maxClutchC = Units.KToC(ClutchTemperature);
        _maxBrakeC = Units.KToC(BrakeTemperature[0]);
    }

    /// <summary>Wear and heat have no effect (for isolating other behaviour in tests).</summary>
    public bool Enabled { get; set; } = true;

    public double ClutchTemperature { get; private set; }

    /// <summary>Front and rear disc temperatures (K).</summary>
    public double[] BrakeTemperature { get; } = new double[2];

    public double ClutchTemperatureC => Units.KToC(ClutchTemperature);

    public IReadOnlyList<FailureReport> Failures => _failures;
    public IReadOnlyList<EngineWarning> Warnings { get; private set; } = Array.Empty<EngineWarning>();

    private static double Wear(PartInstance? p) => p?.Wear ?? 0.0;

    /// <summary>Torque the clutch can hold fully clamped right now.</summary>
    public double ClutchCapacityNm
    {
        get
        {
            double rated = _c.Clutch.MaxTorqueNm;
            if (!Enabled) return rated;
            if (_clutch?.IsFailed == true) return rated * BurntClutchCapacityFactor;
            return rated * (1.0 - ClutchWearCapacityLoss * Wear(_clutch)) * ClutchFadeFactor;
        }
    }

    public double ClutchFadeFactor => 1.0 - ClutchFadeLoss * MathUtil.SmoothStep(0, 1, (ClutchTemperatureC - _c.Clutch.FadeStartC) / ClutchFadeSpanK);

    /// <summary>Multiplier on brake torque for axle 0 (front) or 1 (rear).</summary>
    public double BrakeFactor(int axle)
    {
        if (!Enabled) return 1.0;
        double fade = 1.0 - BrakeFadeLoss * MathUtil.SmoothStep(0, 1, (Units.KToC(BrakeTemperature[axle]) - _c.Brakes.FadeStartC) / BrakeFadeSpanK);
        return fade * (_brakes?.IsFailed == true ? WornPadTorqueFactor : 1.0);
    }

    /// <summary>Multiplier on tyre grip for a wheel.</summary>
    public double GripFactor(int wheel)
    {
        if (!Enabled) return 1.0;
        var tyres = wheel < 2 ? _tyresFront : _tyresRear;
        if (tyres?.IsFailed == true) return WornOutGripFactor;
        return 1.0 - TyreWearGripLoss * Wear(tyres);
    }

    /// <summary>
    /// Integrates heat and wear for one step. <paramref name="engineTorque"/> and
    /// <paramref name="clutchSlipRpm"/> explain a clutch failure; <paramref name="clutchCommanded"/> is
    /// whether the clutch was meant to be fully engaged (slip then means it cannot hold the torque).
    /// </summary>
    public void Update(double dt, double time, FrictionEnergy e, double speed, double engineTorque, double clutchSlipRpm, bool clutchCommanded)
    {
        if (!Enabled) { Warnings = Array.Empty<EngineWarning>(); return; }
        var warnings = new List<EngineWarning>();

        // Clutch.
        var cs = _c.Clutch;
        ClutchTemperature += (e.Clutch - ClutchCoolingWPerK * (ClutchTemperature - AmbientK) * dt) / cs.HeatCapacityJPerK;
        _maxClutchC = Math.Max(_maxClutchC, ClutchTemperatureC);
        bool slipping = clutchCommanded && Math.Abs(clutchSlipRpm) > 100;
        _clutchSlipSeconds = slipping ? _clutchSlipSeconds + dt : Math.Max(0, _clutchSlipSeconds - dt);
        if (slipping)
        {
            _peakSlipTorque = Math.Max(_peakSlipTorque, engineTorque);
            _wearAtFirstSlip ??= Wear(_clutch);
        }
        if (_clutch != null && !_clutch.IsFailed)
        {
            double hot = Math.Max(0.0, ClutchTemperatureC - cs.FadeStartC) / ClutchHotWearScaleK;
            _clutch.Wear += e.Clutch / (cs.LifeMj * 1e6) * (1 + hot) * (1 + hot);
            if (_clutch.Wear >= 1.0) FailClutch(time, clutchSlipRpm);
        }
        if (_clutchSlipSeconds > 0.3)
            warnings.Add(new EngineWarning("clutch_slip", WarningLevel.Danger,
                $"Clutch slipping: {engineTorque:F0} N·m from the engine, the clutch holds {ClutchCapacityNm:F0} N·m now."));
        if (ClutchTemperatureC > cs.FadeStartC)
            warnings.Add(new EngineWarning("clutch_hot", WarningLevel.Danger, $"Clutch overheating ({ClutchTemperatureC:F0} °C): the facings are fading and burning."));

        // Brakes: heat per axle; convective cooling grows with road speed, radiation matters when hot.
        var bs = _c.Brakes;
        for (int axle = 0; axle < 2; axle++)
        {
            double t = BrakeTemperature[axle];
            double h = BrakeCoolingBaseWPerK + (axle == 0 ? FrontBrakeCoolingPerMs : RearBrakeCoolingPerMs) * speed;
            double radiation = BrakeRadiatingArea * PhysicalConstants.StefanBoltzmann * (Math.Pow(t, 4) - Math.Pow(AmbientK, 4));
            double capacity = axle == 0 ? bs.FrontHeatCapacityJPerK : bs.RearHeatCapacityJPerK;
            BrakeTemperature[axle] = t + (e.BrakeAxle[axle] - (h * (t - AmbientK) + radiation) * dt) / capacity;
            _maxBrakeC = Math.Max(_maxBrakeC, Units.KToC(BrakeTemperature[axle]));
        }
        if (_brakes != null && !_brakes.IsFailed)
        {
            double energy = 0;
            for (int axle = 0; axle < 2; axle++)
            {
                double hot = Math.Max(0.0, Units.KToC(BrakeTemperature[axle]) - bs.FadeStartC) / BrakeHotWearScaleK;
                energy += e.BrakeAxle[axle] * (1 + hot) * (1 + hot);
            }
            // One pad set covers both axles; the front does most of the work.
            _brakes.Wear += energy / (bs.PadLifeMj * 1e6 * 2);
            if (_brakes.Wear >= 1.0) FailBrakes(time);
        }
        double hottest = Units.KToC(Math.Max(BrakeTemperature[0], BrakeTemperature[1]));
        if (hottest > bs.FadeStartC)
            warnings.Add(new EngineWarning("brake_fade", WarningLevel.Caution, $"Brakes fading ({hottest:F0} °C): stopping power down {100 * (1 - Math.Min(BrakeFactor(0), BrakeFactor(1))):F0} %."));

        // Tyres: sliding energy wears the tread.
        WearTyres(_tyresFront, _c.TiresFront, e.Tyre[Wheel.FL] + e.Tyre[Wheel.FR], "tires_front", time, warnings);
        WearTyres(_tyresRear, _c.TiresRear, e.Tyre[Wheel.RL] + e.Tyre[Wheel.RR], "tires_rear", time, warnings);

        Warnings = warnings;
    }

    private void WearTyres(PartInstance? tyres, TireSpec spec, double energy, string slot, double time, List<EngineWarning> warnings)
    {
        if (tyres == null || tyres.IsFailed) return;
        tyres.Wear += energy / (spec.TreadLifeMj * 1e6);
        if (tyres.Wear >= 1.0) FailTyres(tyres, slot, time);
        else if (tyres.Wear > 0.85)
            warnings.Add(new EngineWarning("tyres_worn", WarningLevel.Caution, $"{Label(slot)} nearly worn out ({tyres.Wear * 100:F0} %)."));
    }

    private string Label(string slot) => _c.Definition.FindSlot(slot)?.Label ?? slot;

    private void Report(PartInstance part, string slot, FailureMode mode, double time, List<ReportLine> measured, List<string> factors, List<string> recommendations)
    {
        var info = FailureModeInfo.Of(mode);
        part.Damage.Fail(mode, time);
        _failures.Add(new FailureReport(time, mode, info.Severity, info.Title, info.Cause, Label(slot), part.Definition.Name,
            measured, factors, recommendations, Array.Empty<string>()));
    }

    private void FailClutch(double time, double slipRpm)
    {
        var cs = _c.Clutch;
        double rated = cs.MaxTorqueNm;
        double startWear = _wearAtFirstSlip ?? 1.0;
        double heldCold = rated * (1.0 - ClutchWearCapacityLoss * startWear);
        var measured = new List<ReportLine>
        {
            new("Clutch temperature", $"{ClutchTemperatureC:F0} °C (fade starts at {cs.FadeStartC:F0} °C; hottest {_maxClutchC:F0} °C)"),
            new("Peak engine torque while slipping", _peakSlipTorque > 0 ? $"{_peakSlipTorque:F0} N·m" : "—"),
            new("Clutch capacity", $"rated {rated:F0} N·m new; {heldCold:F0} N·m when it first slipped ({startWear * 100:F0} % worn)"),
            new("Slip at failure", $"{Math.Abs(slipRpm):F0} rpm"),
        };
        var factors = new List<string>();
        if (_peakSlipTorque > 0 && _peakSlipTorque > heldCold * 0.98)
            factors.Add(startWear > 0.1
                ? $"The engine made {_peakSlipTorque:F0} N·m; the clutch was {startWear * 100:F0} % worn and held only about {heldCold:F0} N·m, so it slipped under full load."
                : $"The engine made {_peakSlipTorque:F0} N·m, more than the clutch's {rated:F0} N·m rating, so it slipped under full load.");
        if (_maxClutchC > cs.FadeStartC)
            factors.Add($"Slipping heated the facings to {_maxClutchC:F0} °C, past their {cs.FadeStartC:F0} °C limit: friction faded, it slipped more, and the facings burnt away.");
        if (factors.Count == 0) factors.Add("The facings simply reached the end of their life.");
        var recommendations = new List<string>
        {
            _peakSlipTorque > 0
                ? $"Fit a clutch rated well above the engine's {_peakSlipTorque:F0} N·m, with margin for wear (sport or twin-plate)."
                : "Replace the clutch.",
            "A clutch that slips in high gears is already failing: stop and replace it before it burns out.",
        };
        Report(_clutch!, "clutch", FailureMode.ClutchBurnout, time, measured, factors, recommendations);
    }

    private void FailBrakes(double time)
    {
        var bs = _c.Brakes;
        var measured = new List<ReportLine>
        {
            new("Hottest disc temperature", $"{_maxBrakeC:F0} °C (pads fade from {bs.FadeStartC:F0} °C)"),
            new("Pad life", $"{bs.PadLifeMj:F0} MJ per axle at normal temperatures"),
        };
        var factors = new List<string>();
        if (_maxBrakeC > bs.FadeStartC)
            factors.Add($"The discs ran to {_maxBrakeC:F0} °C, above the pads' {bs.FadeStartC:F0} °C rating; hot pads wear many times faster.");
        else factors.Add("The pads reached the end of their life.");
        Report(_brakes!, "brakes", FailureMode.BrakePadsWornOut, time, measured, factors, new List<string>
        {
            "Replace the pads (and check the discs for scoring).",
            "For track use, fit brakes with more thermal mass and higher-temperature pads, and give them cool-down laps.",
        });
    }

    private void FailTyres(PartInstance tyres, string slot, double time)
    {
        var spec = tyres.Definition.Spec as TireSpec;
        var measured = new List<ReportLine> { new("Tread life", $"{spec?.TreadLifeMj:F0} MJ of sliding energy ({spec?.Compound} compound)") };
        var factors = new List<string> { "Sliding (wheelspin, locked brakes, drifting, scrubbing through corners) wore the tread through." };
        Report(tyres, slot, FailureMode.TyresWornOut, time, measured, factors, new List<string>
        {
            "Replace the tyres. Softer compounds grip more but wear faster.",
            "Smoother inputs, less wheelspin and fewer lock-ups make a set last longer.",
        });
    }
}
