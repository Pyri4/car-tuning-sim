namespace CarSim.Core.Common;

/// <summary>Physical constants and reference conditions used across the simulation (SI).</summary>
public static class PhysicalConstants
{
    /// <summary>Standard sea-level atmospheric pressure, Pa.</summary>
    public const double StandardPressure = 101_325.0;

    /// <summary>Reference ambient temperature for dyno correction and defaults, K (25 °C).</summary>
    public const double StandardTemperature = 298.15;

    /// <summary>Specific gas constant of dry air, J/(kg·K).</summary>
    public const double AirGasConstant = 287.05;

    /// <summary>Specific heat of air at constant pressure, J/(kg·K).</summary>
    public const double AirCp = 1005.0;

    /// <summary>Ratio of specific heats for air.</summary>
    public const double AirGamma = 1.4;

    /// <summary>Specific gas constant of exhaust gas, J/(kg·K).</summary>
    public const double ExhaustGasConstant = 288.0;

    /// <summary>Specific heat of exhaust gas at constant pressure, J/(kg·K).</summary>
    public const double ExhaustCp = 1150.0;

    /// <summary>Ratio of specific heats for hot exhaust gas.</summary>
    public const double ExhaustGamma = 1.33;

    /// <summary>Stefan–Boltzmann constant, W/(m²·K⁴).</summary>
    public const double StefanBoltzmann = 5.670374e-8;

    /// <summary>Polytropic exponent used for in-cylinder compression estimates.</summary>
    public const double CompressionPolytropicExponent = 1.3;

    /// <summary>Standard gravity, m/s².</summary>
    public const double Gravity = 9.80665;

    /// <summary>
    /// Flow-bench test depression (28 inches of water). Airflow ratings in CFM for heads, throttle
    /// bodies and manifolds are quoted at this pressure drop.
    /// </summary>
    public const double FlowBenchPressureDrop = 28.0 * Units.InchesOfWaterToPascal;

    /// <summary>Air density at flow-bench standard conditions (≈ 20 °C, 1 atm), kg/m³.</summary>
    public const double FlowBenchAirDensity = 1.204;
}
