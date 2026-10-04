using System;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace CadHarness.Cli
{
    public sealed class CylinderMeasurement
    {
        public int OwnerFeatureId { get; set; }
        public double RadiusMm { get; set; }
        public double[] AxisPointMm { get; set; }
        public double[] AxisDirection { get; set; }
        public double[] ZRangeMm { get; set; }
        public int FullCircleBoundaryEdges { get; set; }
    }

    public sealed class EditConfirmationResult
    {
        public Dictionary<string, double> DimensionsMm { get; set; }
        public Dictionary<string, int> FeatureIds { get; set; }
        public Dictionary<string, int> FeatureErrors { get; set; }
        public Dictionary<string, bool> FeatureWarnings { get; set; }
        public int NeedsRebuild { get; set; }
        public int SolidBodyCount { get; set; }
        public int AllBodyCount { get; set; }
        public double[] ExactBoxMm { get; set; }
        public int PatternCount { get; set; }
        public double PatternSpacingMm { get; set; }
        public bool PatternReverseDirection { get; set; }
        public bool PatternGeometryPattern { get; set; }
        public int SeedEndCondition { get; set; }
        public double FilletDefinitionRadiusMm { get; set; }
        public List<CylinderMeasurement> Holes { get; set; }
        public List<CylinderMeasurement> Fillets { get; set; }
        public double? MeasuredHoleSpacingMm { get; set; }
    }

    // Fixed canonical edit confirmation only. Measurements never replace persistent references.
    internal static class EditConfirmation
    {
        internal static EditConfirmationResult Read(EditContext context)
        {
            EditConfirmationResult result = new EditConfirmationResult {
                DimensionsMm = context.ReadAll(), FeatureIds = new Dictionary<string, int>(),
                FeatureErrors = new Dictionary<string, int>(), FeatureWarnings = new Dictionary<string, bool>(),
                Holes = new List<CylinderMeasurement>(), Fillets = new List<CylinderMeasurement>(),
                NeedsRebuild = context.Model.Extension.NeedsRebuild2
            };
            foreach (SemanticFeature entry in context.State.Features.All()) ObserveFeature(context, result, entry.SemanticId);
            ObserveFeature(context, result, "base_plate.profile_sketch");
            ObserveFeature(context, result, "mounting_hole_seed.profile_sketch");
            ILinearPatternFeatureData pattern = (ILinearPatternFeatureData)context.Feature("mounting_holes").GetDefinition();
            result.PatternCount = pattern.D1TotalInstances;
            result.PatternSpacingMm = pattern.D1Spacing * 1000;
            result.PatternReverseDirection = pattern.D1ReverseDirection;
            result.PatternGeometryPattern = pattern.GeometryPattern;
            result.SeedEndCondition = ((IExtrudeFeatureData2)context.Feature("mounting_hole_seed").GetDefinition()).GetEndCondition(true);
            result.FilletDefinitionRadiusMm = ((ISimpleFilletFeatureData2)context.Feature("outer_edge_fillet").GetDefinition()).DefaultRadius * 1000;
            Array solids = ((IPartDoc)context.Model).GetBodies2((int)swBodyType_e.swSolidBody, false) as Array;
            Array all = ((IPartDoc)context.Model).GetBodies2((int)swBodyType_e.swAllBodies, false) as Array;
            result.SolidBodyCount = solids == null ? 0 : solids.Length;
            result.AllBodyCount = all == null ? 0 : all.Length;
            if (result.SolidBodyCount != 1) return result;
            IBody2 body = (IBody2)solids.GetValue(0);
            result.ExactBoxMm = new double[6];
            for (int axis = 0; axis < 3; axis++)
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    double x, y, z;
                    if (!body.GetExtremePoint(axis == 0 ? sign : 0, axis == 1 ? sign : 0, axis == 2 ? sign : 0, out x, out y, out z))
                        throw new Program.PhaseFailure("EDIT_MEASUREMENT_FAILED", "GetExtremePoint failed.");
                    result.ExactBoxMm[axis + (sign > 0 ? 3 : 0)] = (axis == 0 ? x : (axis == 1 ? y : z)) * 1000;
                }
            foreach (IFace2 face in (object[])body.GetFaces())
            {
                ISurface surface = (ISurface)face.GetSurface();
                if (!surface.IsCylinder()) continue;
                IFeature owner = (IFeature)face.GetFeature();
                int id = owner == null ? -1 : owner.GetID();
                double[] cylinder = (double[])surface.CylinderParams, box = (double[])face.GetBox();
                CylinderMeasurement measurement = new CylinderMeasurement { OwnerFeatureId = id,
                    RadiusMm = cylinder[6] * 1000, AxisPointMm = new double[] { cylinder[0]*1000, cylinder[1]*1000, cylinder[2]*1000 },
                    AxisDirection = new double[] { cylinder[3], cylinder[4], cylinder[5] }, ZRangeMm = new double[] { box[2]*1000, box[5]*1000 } };
                foreach (IEdge edge in (object[])face.GetEdges())
                {
                    ICurve curve = (ICurve)edge.GetCurve();
                    double start, end; bool closed, periodic;
                    if (curve.IsCircle() && curve.GetEndParams(out start, out end, out closed, out periodic) && closed)
                        measurement.FullCircleBoundaryEdges++;
                }
                if (id == result.FeatureIds["mounting_hole_seed"] || id == result.FeatureIds["mounting_holes"]) result.Holes.Add(measurement);
                else if (id == result.FeatureIds["outer_edge_fillet"]) result.Fillets.Add(measurement);
            }
            result.Holes.Sort(delegate(CylinderMeasurement a, CylinderMeasurement b) { return a.AxisPointMm[0].CompareTo(b.AxisPointMm[0]); });
            if (result.Holes.Count == 2) result.MeasuredHoleSpacingMm = result.Holes[1].AxisPointMm[0] - result.Holes[0].AxisPointMm[0];
            return result;
        }

        internal static void Require(EditConfirmationResult result, Dictionary<string, double> expected, CadState state)
        {
            foreach (KeyValuePair<string, double> item in expected) Check(Near(result.DimensionsMm[item.Key], item.Value), "Native dimension differs: " + item.Key);
            foreach (SemanticFeature feature in state.Features.All()) Check(result.FeatureIds[feature.SemanticId] == feature.SwFeatureIdAtCapture, "Feature identity changed.");
            foreach (int error in result.FeatureErrors.Values) Check(error == 0, "Native feature rebuild error.");
            Check(result.NeedsRebuild == 0 && result.AllBodyCount == 1 && result.SolidBodyCount == 1, "Rebuild/body count is incorrect.");
            double w = expected["base_plate.width"], h = expected["base_plate.height"], t = expected["base_plate.thickness"];
            double spacing = expected["mounting_holes.spacing"], diameter = expected["mounting_hole_seed.diameter"], radius = expected["outer_edge_fillet.radius"];
            double[] box = { -w/2, -h/2, 0, w/2, h/2, t };
            for (int i = 0; i < 6; i++) Check(Near(result.ExactBoxMm[i], box[i]), "Exact bounding box differs.");
            Check(result.PatternCount == 2 && !result.PatternReverseDirection && Near(result.PatternSpacingMm, spacing), "Native pattern definition differs.");
            Check(result.SeedEndCondition == (int)swEndConditions_e.swEndCondThroughAll, "Seed is not Through All.");
            Check(Near(result.FilletDefinitionRadiusMm, radius), "Native fillet definition differs.");
            Check(result.Holes.Count == 2 && result.MeasuredHoleSpacingMm.HasValue && Near(result.MeasuredHoleSpacingMm.Value, spacing), "Expected two through holes at the requested spacing.");
            for (int i = 0; i < result.Holes.Count; i++)
            {
                CylinderMeasurement hole = result.Holes[i];
                Check(Near(hole.RadiusMm*2, diameter) && Near(hole.AxisPointMm[0], (i == 0 ? -1 : 1)*spacing/2) && Near(hole.AxisPointMm[1], 0) &&
                    Near(Math.Abs(hole.AxisDirection[2]), 1) && Near(hole.ZRangeMm[0], 0) && Near(hole.ZRangeMm[1], t) && hole.FullCircleBoundaryEdges == 2,
                    "Through-hole geometry differs.");
            }
            Check(result.Fillets.Count == 4, "Expected four outer vertical fillet faces.");
            HashSet<string> corners = new HashSet<string>();
            foreach (CylinderMeasurement fillet in result.Fillets)
            {
                Check(Near(fillet.RadiusMm, radius) && Near(Math.Abs(fillet.AxisPointMm[0]), w/2-radius) && Near(Math.Abs(fillet.AxisPointMm[1]), h/2-radius) &&
                    Near(Math.Abs(fillet.AxisDirection[2]), 1) && Near(fillet.ZRangeMm[0], 0) && Near(fillet.ZRangeMm[1], t), "Outer fillet geometry differs.");
                corners.Add(Math.Sign(fillet.AxisPointMm[0]) + ":" + Math.Sign(fillet.AxisPointMm[1]));
            }
            Check(corners.Count == 4, "Fillets do not occupy four distinct corners.");
        }

        private static void ObserveFeature(EditContext context, EditConfirmationResult result, string id)
        {
            IFeature feature = context.Feature(id); bool warning;
            result.FeatureIds.Add(id, feature.GetID());
            result.FeatureErrors.Add(id, feature.GetErrorCode2(out warning));
            result.FeatureWarnings.Add(id, warning);
        }
        internal static bool Near(double a, double b) { return Math.Abs(a-b) < 0.0001; }
        private static void Check(bool value, string message) { if (!value) throw new Program.PhaseFailure("EDIT_CONFIRMATION_FAILED", message); }
    }
}
