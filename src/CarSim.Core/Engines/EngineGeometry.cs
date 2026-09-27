using CarSim.Core.Parts;
using CarSim.Core.Parts.Specs;

namespace CarSim.Core.Engines;

/// <summary>
/// Geometry derived from the installed block, crankshaft, rods, pistons, head gasket and head.
/// All values SI (m, m², m³, kg). This is where compression ratio comes from — it is never authored.
/// <para>
/// Geometry belongs to a bank: the bottom end (block, crank, rods, pistons) is shared, but each bank has its own head
/// gasket and cylinder head, so its own chamber, gasket volume, quench and compression ratio. The per-cylinder values
/// are that bank's; <see cref="Cylinders"/> and <see cref="Displacement"/> stay the whole engine's. On a single-bank
/// engine the bank's geometry is the engine's.
/// </para>
/// </summary>
public sealed record EngineGeometry(
    int Cylinders,
    double Bore,
    double Stroke,
    double RodLength,
    double CompressionHeight,
    double DeckHeight,
    double GasketBore,
    double GasketThickness,
    double ChamberVolume,
    double PistonDishVolume,
    double PistonMass,
    double RodMass)
{
    public double PistonArea => Math.PI / 4.0 * Bore * Bore;
    public double CrankRadius => Stroke / 2.0;
    public double SweptVolumePerCylinder => PistonArea * Stroke;
    public double Displacement => SweptVolumePerCylinder * Cylinders;

    /// <summary>Piston crown below the deck at TDC (negative = crown protrudes above the deck).</summary>
    public double DeckClearance => DeckHeight - (CrankRadius + RodLength + CompressionHeight);

    /// <summary>Crown-to-head distance at TDC (deck clearance + compressed gasket).</summary>
    public double PistonToHeadClearance => DeckClearance + GasketThickness;

    public double GasketVolume => Math.PI / 4.0 * GasketBore * GasketBore * GasketThickness;

    /// <summary>Volume between the crown and the deck at TDC (negative if the crown protrudes).</summary>
    public double DeckVolume => PistonArea * DeckClearance;

    public double ClearanceVolume => ChamberVolume + GasketVolume + DeckVolume + PistonDishVolume;

    /// <summary>Static compression ratio (V_swept + V_clearance) / V_clearance.</summary>
    public double CompressionRatio => ClearanceVolume > 0
        ? (SweptVolumePerCylinder + ClearanceVolume) / ClearanceVolume
        : double.PositiveInfinity;

    public double RodRatio => RodLength / Stroke;

    /// <summary>Reciprocating mass per cylinder: piston assembly + ≈⅓ of the rod (small-end share).</summary>
    public double ReciprocatingMass => PistonMass + RodMass / 3.0;

    /// <summary>Mean piston speed at <paramref name="rpm"/>, m/s.</summary>
    public double MeanPistonSpeed(double rpm) => 2.0 * Stroke * rpm / 60.0;

    /// <summary>
    /// Peak inertial (tensile) rod load at TDC of the exhaust stroke:
    /// F = m_recip · ω² · r · (1 + r/l).
    /// </summary>
    public double RodInertiaLoad(double omega) =>
        ReciprocatingMass * omega * omega * CrankRadius * (1.0 + CrankRadius / RodLength);

    /// <summary>
    /// Creates the geometry of the first bank (the whole engine's on a single-bank engine) if every part that defines it
    /// is installed. Anything that depends on the head or gasket of a multi-bank engine must use the bank overload.
    /// </summary>
    public static EngineGeometry? TryCreate(EngineAssembly a, out IReadOnlyList<string> missingCategories) =>
        TryCreate(a, 0, out missingCategories);

    /// <summary>
    /// Creates the geometry of bank <paramref name="bank"/> — the shared bottom end with the head gasket and cylinder head
    /// that serve that bank — if every part that defines it is installed.
    /// </summary>
    public static EngineGeometry? TryCreate(EngineAssembly a, int bank, out IReadOnlyList<string> missingCategories)
    {
        var block = a.SpecOf<BlockSpec>(PartCategory.Block);
        var crank = a.SpecOf<CrankshaftSpec>(PartCategory.Crankshaft);
        var rods = a.SpecOf<ConnectingRodSpec>(PartCategory.ConnectingRods);
        var pistons = a.SpecOf<PistonSpec>(PartCategory.Pistons);
        var gasket = a.SpecFor<HeadGasketSpec>(PartCategory.HeadGasket, bank);
        var head = a.SpecFor<CylinderHeadSpec>(PartCategory.CylinderHead, bank);
        var missing = new List<string>();
        if (block == null) missing.Add(PartCategory.Block);
        if (crank == null) missing.Add(PartCategory.Crankshaft);
        if (rods == null) missing.Add(PartCategory.ConnectingRods);
        if (pistons == null) missing.Add(PartCategory.Pistons);
        if (gasket == null) missing.Add(PartCategory.HeadGasket);
        if (head == null) missing.Add(PartCategory.CylinderHead);
        missingCategories = missing;
        if (missing.Count > 0) return null;

        return new EngineGeometry(
            Cylinders: block!.Cylinders,
            // The cylinder is the block's bore; the piston's authored bore is checked for fit by the validator.
            Bore: block.Bore,
            Stroke: crank!.Stroke,
            RodLength: rods!.Length,
            CompressionHeight: pistons!.CompressionHeight,
            DeckHeight: block.DeckHeight,
            GasketBore: gasket!.Bore,
            GasketThickness: gasket.CompressedThickness,
            ChamberVolume: head!.ChamberVolume,
            PistonDishVolume: pistons.DishVolume,
            PistonMass: pistons.MassEach,
            RodMass: rods.MassEach);
    }
}
