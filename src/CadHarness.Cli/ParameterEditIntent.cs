using System;
using System.Globalization;

namespace CadHarness.Cli
{
    internal enum ParameterEditKind { HoleSpacing, PlateThickness, HoleDiameter, FilletRadius }

    internal sealed class ParameterEditIntent
    {
        internal readonly ParameterEditKind Kind;
        internal readonly double ValueMm;
        internal readonly string TargetSemanticId;
        internal readonly string ParameterSemanticId;

        private ParameterEditIntent(ParameterEditKind kind, double value, string target, string parameter)
        { Kind = kind; ValueMm = value; TargetSemanticId = target; ParameterSemanticId = parameter; }

        internal static ParameterEditIntent ParseCli(string selector, string valueText)
        {
            double value;
            if (!Double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                Double.IsNaN(value) || Double.IsInfinity(value) || value <= 0)
                throw new Program.PhaseFailure("INVALID_EDIT_VALUE", "Edit value must be a finite positive length in mm.");
            switch (selector)
            {
                case "mounting_holes.spacing": return new ParameterEditIntent(ParameterEditKind.HoleSpacing, value, "mounting_holes", selector);
                case "base_plate.thickness": return new ParameterEditIntent(ParameterEditKind.PlateThickness, value, "base_plate", selector);
                case "mounting_hole_seed.diameter": return new ParameterEditIntent(ParameterEditKind.HoleDiameter, value, "mounting_hole_seed", selector);
                case "outer_edge_fillet.radius": return new ParameterEditIntent(ParameterEditKind.FilletRadius, value, "outer_edge_fillet", selector);
                default: throw new Program.PhaseFailure("UNSUPPORTED_TYPED_EDIT", "Only the four Phase 4 parameter selectors are supported.");
            }
        }
    }
}
