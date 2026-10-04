using System;
using System.Collections.Generic;

namespace CadHarness.Cli
{
    public sealed class ValidationCheck
    {
        public string Name { get; set; }
        public object Expected { get; set; }
        public object Actual { get; set; }
        public string Status { get; set; }
        public string FailureClass { get; set; }
    }

    public sealed class ValidationResult
    {
        public bool Success { get; set; }
        public List<ValidationCheck> Checks { get; set; }
        public string FailureClass { get; set; }
        public string ErrorCode { get; set; }
        public double ValidationMs { get; set; }
        public EditConfirmationResult Measurements { get; set; }
        public ReferenceSnapshot References { get; set; }

        public ValidationResult() { Success = true; Checks = new List<ValidationCheck>(); }
        internal void Add(string name, object expected, object actual, bool passed, string failureClass)
        {
            Checks.Add(new ValidationCheck { Name=name, Expected=expected, Actual=actual,
                Status=passed ? "passed" : "failed", FailureClass=passed ? null : failureClass });
            if (!passed) { Success=false; if (FailureClass==null) FailureClass=failureClass; }
        }
        internal void Require()
        {
            if (!Success) throw new Program.PhaseFailure(ErrorCode ?? "VALIDATION_FAILED", FailureClass ?? "Deterministic validation failed.");
        }
    }

    internal static class CanonicalValues
    {
        internal static Dictionary<string, double> From(CadState state)
        {
            return new Dictionary<string, double> {
                {"base_plate.width",state.Features.BasePlate.Parameters.WidthMm},
                {"base_plate.height",state.Features.BasePlate.Parameters.HeightMm},
                {"base_plate.thickness",state.Features.BasePlate.Parameters.ThicknessMm},
                {"mounting_hole_seed.diameter",state.Features.MountingHoleSeed.Parameters.DiameterMm},
                {"mounting_holes.spacing",state.Features.MountingHoles.Parameters.SpacingMm},
                {"outer_edge_fillet.radius",state.Features.OuterEdgeFillet.Parameters.RadiusMm}
            };
        }
        internal static CadState Project(CadState state, Dictionary<string, double> values)
        {
            System.Web.Script.Serialization.JavaScriptSerializer json = new System.Web.Script.Serialization.JavaScriptSerializer();
            CadState copy=json.Deserialize<CadState>(json.Serialize(state));
            copy.Features.BasePlate.Parameters.WidthMm=values["base_plate.width"];
            copy.Features.BasePlate.Parameters.HeightMm=values["base_plate.height"];
            copy.Features.BasePlate.Parameters.ThicknessMm=values["base_plate.thickness"];
            copy.Features.MountingHoleSeed.Parameters.DiameterMm=values["mounting_hole_seed.diameter"];
            copy.Features.MountingHoles.Parameters.SpacingMm=values["mounting_holes.spacing"];
            copy.Features.OuterEdgeFillet.Parameters.RadiusMm=values["outer_edge_fillet.radius"];
            copy.Features.MountingHoleSeed.Parameters.CenterXmm=-values["mounting_holes.spacing"]/2;
            copy.Features.MountingHoleSeed.Parameters.CenterYmm=0;
            return copy;
        }
    }
}
