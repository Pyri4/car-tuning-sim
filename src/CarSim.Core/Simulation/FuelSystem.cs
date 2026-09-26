using CarSim.Core.Parts.Specs;

namespace CarSim.Core.Simulation;

public readonly record struct FuelDelivery(double FuelPerCycle, double Duty, double RailPressure, FuelLimit Limit);

/// <summary>
/// Injectors + pump + 1:1 manifold-referenced regulator. The regulator holds the rail at
/// <c>regulated</c> above manifold pressure while the pump keeps up. The pump's flow falls linearly
/// with the pressure it pushes against (free flow → zero at dead-head), so boost (which raises the
/// pump's outlet pressure) eats pump capacity. When demand exceeds supply, rail pressure sags until
/// the two balance; injector flow scales with √(ΔP).
/// </summary>
public static class FuelSystem
{
    public const double InjectorWearFlowLoss = 0.25;
    public const double PumpWearFlowLoss = 0.30;

    public static FuelDelivery Deliver(
        InjectorSpec injectors, double injectorWear,
        FuelPumpSpec pump, double pumpWear,
        double fuelDensity, double commandedPulseWidth, double cycleTime,
        double manifoldPressure, double ambientPressure)
    {
        if (commandedPulseWidth <= 0 || cycleTime <= 0)
            return new FuelDelivery(0, 0, pump.RegulatedPressure, FuelLimit.None);

        double commandedDuty = commandedPulseWidth / cycleTime;
        double duty = Math.Min(1.0, commandedDuty);
        double ratedFlow = injectors.RatedFlow * (1.0 - InjectorWearFlowLoss * Math.Clamp(injectorWear, 0, 1));
        double regulated = pump.RegulatedPressure;
        double boost = manifoldPressure - ambientPressure;
        double pumpFree = pump.FreeFlow * (1.0 - PumpWearFlowLoss * Math.Clamp(pumpWear, 0, 1));

        double Demand(double dp) => injectors.Count * duty * ratedFlow * Math.Sqrt(Math.Max(0, dp) / injectors.RatedPressure);
        double Supply(double dp) => pumpFree * Math.Max(0.0, 1.0 - (dp + boost) / pump.MaxPressure);

        double railDp = regulated;
        if (Demand(regulated) > Supply(regulated))
        {
            double lo = 0.0, hi = regulated;
            for (int i = 0; i < 40; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (Demand(mid) > Supply(mid)) hi = mid; else lo = mid;
            }
            railDp = 0.5 * (lo + hi);
        }

        double perInjectorFlow = ratedFlow * Math.Sqrt(railDp / injectors.RatedPressure);
        double fuelPerCycle = fuelDensity * perInjectorFlow * duty * cycleTime;
        FuelLimit limit = commandedDuty > 1.0 ? FuelLimit.InjectorCapacity
            : railDp < regulated - 1000.0 ? FuelLimit.PumpCapacity
            : FuelLimit.None;
        return new FuelDelivery(fuelPerCycle, commandedDuty, railDp, limit);
    }
}
