namespace CarSim.Core.Common;

/// <summary>
/// Unit conversions. The simulation works in SI (m, kg, s, Pa, K, rad/s, N·m, W).
/// Content files use conventional automotive units; conversions happen once, at load time,
/// through these helpers.
/// </summary>
public static class Units
{
    public const double MillimetresToMetres = 1e-3;
    public const double CubicCentimetresToCubicMetres = 1e-6;
    public const double BarToPascal = 1e5;
    public const double KilopascalToPascal = 1e3;
    public const double PsiToPascal = 6894.757293168;
    public const double InchesOfWaterToPascal = 249.08891;
    public const double CfmToCubicMetresPerSecond = 4.719474432e-4;
    public const double WattsPerMechanicalHorsepower = 745.6998715822702;
    public const double WattsPerMetricHorsepower = 735.49875;
    public const double NewtonMetresToPoundFeet = 0.7375621492772654;
    public const double CelsiusOffset = 273.15;

    public static double RpmToRadPerSec(double rpm) => rpm * (2.0 * Math.PI / 60.0);
    public static double RadPerSecToRpm(double omega) => omega * (60.0 / (2.0 * Math.PI));

    public static double MmToM(double mm) => mm * MillimetresToMetres;
    public static double MToMm(double m) => m / MillimetresToMetres;
    public static double CcToM3(double cc) => cc * CubicCentimetresToCubicMetres;
    public static double M3ToCc(double m3) => m3 / CubicCentimetresToCubicMetres;
    public static double M3ToLitres(double m3) => m3 * 1000.0;

    public static double BarToPa(double bar) => bar * BarToPascal;
    public static double PaToBar(double pa) => pa / BarToPascal;
    public static double KpaToPa(double kpa) => kpa * KilopascalToPascal;
    public static double PaToKpa(double pa) => pa / KilopascalToPascal;
    public static double PaToPsi(double pa) => pa / PsiToPascal;
    public static double PsiToPa(double psi) => psi * PsiToPascal;

    public static double CToK(double celsius) => celsius + CelsiusOffset;
    public static double KToC(double kelvin) => kelvin - CelsiusOffset;

    public static double DegToRad(double deg) => deg * (Math.PI / 180.0);
    public static double RadToDeg(double rad) => rad * (180.0 / Math.PI);

    /// <summary>cc/min (injector rating) → m³/s.</summary>
    public static double CcPerMinToM3PerSec(double ccPerMin) => ccPerMin * CubicCentimetresToCubicMetres / 60.0;
    public static double M3PerSecToCcPerMin(double m3PerSec) => m3PerSec / CubicCentimetresToCubicMetres * 60.0;
    /// <summary>L/h (fuel pump rating) → m³/s.</summary>
    public static double LitresPerHourToM3PerSec(double lph) => lph * 1e-3 / 3600.0;
    public static double M3PerSecToLitresPerHour(double m3PerSec) => m3PerSec * 3600.0 * 1e3;
    public static double CfmToM3PerSec(double cfm) => cfm * CfmToCubicMetresPerSecond;

    public static double WToHp(double watts) => watts / WattsPerMechanicalHorsepower;
    public static double HpToW(double hp) => hp * WattsPerMechanicalHorsepower;
    public static double WToKw(double watts) => watts / 1000.0;
    public static double NmToLbFt(double nm) => nm * NewtonMetresToPoundFeet;

    /// <summary>Gram → kilogram.</summary>
    public static double GToKg(double grams) => grams * 1e-3;
}
