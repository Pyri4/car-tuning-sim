namespace CarSim.Core.Parts;

/// <summary>
/// Known part categories. A category determines which typed spec a part carries and therefore
/// which simulation properties it provides. New parts in an existing category are pure data;
/// a new category needs code because the simulation must know what its properties mean.
/// </summary>
public static class PartCategory
{
    public const string Block = "block";
    public const string MainBearings = "main_bearings";
    public const string Crankshaft = "crankshaft";
    public const string RodBearings = "rod_bearings";
    public const string ConnectingRods = "connecting_rods";
    public const string Pistons = "pistons";
    public const string HeadGasket = "head_gasket";
    public const string CylinderHead = "cylinder_head";
    public const string ValveSprings = "valve_springs";
    public const string Camshafts = "camshafts";
    public const string IntakeManifold = "intake_manifold";
    public const string ThrottleBody = "throttle_body";
    public const string Injectors = "injectors";
    public const string FuelPump = "fuel_pump";
    public const string ExhaustManifold = "exhaust_manifold";
    public const string Exhaust = "exhaust";
    public const string Turbocharger = "turbocharger";
    public const string Intercooler = "intercooler";
    public const string OilPump = "oil_pump";
    public const string OilPan = "oil_pan";
    public const string Radiator = "radiator";
    public const string Flywheel = "flywheel";
    public const string Ecu = "ecu";
}
