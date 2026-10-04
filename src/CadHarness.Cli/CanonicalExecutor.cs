using System;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace CadHarness.Cli
{
    internal static class CanonicalExecutor
    {
        internal static string[] Create(ISldWorks app, IModelDoc2 model, IFeature plane, CadIntent intent, ref bool mutationStarted)
        {
            // FeatureExtrusion3 computes the body used by the next direct API selectors.
            // Defer only the redundant explicit plate rebuild; retain sketch exits.
            IFeature plate = Program.CreatePlate(app, model, plane, intent.BasePlate, ref mutationStarted, true);
            plate.Name = "Extrude.Base";
            IFeature baseSketch = (IFeature)plate.GetFirstSubFeature();
            if (baseSketch != null) baseSketch.Name = "Sketch.BaseProfile";

            IFeature seed = CreateHoleSeed(app, model, intent);
            seed.Name = "Hole.MountingSeed";
            IFeature holeSketch = (IFeature)seed.GetFirstSubFeature();
            if (holeSketch != null) holeSketch.Name = "Sketch.HoleSeed";

            IFeature pattern = CreateHolePattern(model, seed, intent);
            pattern.Name = "Pattern.MountingHoles";
            IFeature fillet = CreateOuterFillet(model, intent);
            fillet.Name = "Fillet.OuterVerticalEdges";
            if (!SolidWorksBatchScope.Rebuild(model))
                throw new Program.PhaseFailure("REBUILD_FAILED", "SOLIDWORKS reported a final rebuild failure.");
            foreach (IFeature feature in new IFeature[] { plate, seed, pattern, fillet })
                RequireFeatureWithoutError(feature);
            model.ClearSelection2(true);
            return new string[] { plate.Name, seed.Name, pattern.Name, fillet.Name };
        }

        private static IFeature CreateHoleSeed(ISldWorks app, IModelDoc2 model, CadIntent intent)
        {
            ISketchManager sketches = model.SketchManager;
            bool previousPrompt = app.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate);
            bool sketchOpened = false;
            try
            {
                app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate, false);
                IFace2 face = FindTopFace(GetSingleBody(model), intent.BasePlate);
                model.ClearSelection2(true);
                if (!((IEntity)face).Select4(false, null))
                    throw new Program.PhaseFailure("HOLE_FACE_SELECTION_FAILED", "Could not select the plate top face.");
                BenchmarkTelemetry.Call("ISketchManager.InsertSketch",delegate{sketches.InsertSketch(true);});
                sketchOpened = model.GetActiveSketch2() != null;
                if (!sketchOpened)
                    throw new Program.PhaseFailure("HOLE_SKETCH_CREATION_FAILED", "Could not create the hole sketch.");
                ISketch sketch = (ISketch)model.GetActiveSketch2();
                IFeature profile = (IFeature)sketch;
                IMathUtility math = (IMathUtility)app.GetMathUtility();
                IMathPoint center = (IMathPoint)math.CreatePoint(new double[] {
                    Program.Mm(intent.HoleSeed.CenterXmm), Program.Mm(intent.HoleSeed.CenterYmm),
                    Program.Mm(intent.BasePlate.ThicknessMm) });
                center = (IMathPoint)center.MultiplyTransform(sketch.ModelToSketchTransform);
                double[] sketchCenter = (double[])center.ArrayData;
                ISketchSegment circle;
                using(new SketchBatchScope(sketches))
                    circle = BenchmarkTelemetry.Call("ISketchManager.CreateCircleByRadius",delegate{return sketches.CreateCircleByRadius(sketchCenter[0], sketchCenter[1], 0,
                        Program.Mm(intent.HoleSeed.DiameterMm / 2));});
                if (circle == null)
                    throw new Program.PhaseFailure("HOLE_CIRCLE_CREATION_FAILED", "CreateCircleByRadius returned no circle.");

                // A fixed center preserves the hard-coded seed location while its diameter remains editable.
                model.ClearSelection2(true);
                ISketchPoint centerPoint = (ISketchPoint)((ISketchArc)circle).GetCenterPoint2();
                if (!centerPoint.Select4(false, null))
                    throw new Program.PhaseFailure("HOLE_CENTER_SELECTION_FAILED", "Could not select the circle center.");
                BenchmarkTelemetry.Call("IModelDoc2.SketchAddConstraints",delegate{model.SketchAddConstraints("sgFIXED");});
                model.ClearSelection2(true);
                if (!circle.Select4(false, null))
                    throw new Program.PhaseFailure("HOLE_CIRCLE_SELECTION_FAILED", "Could not select the circle for dimensioning.");
                IDisplayDimension display = (IDisplayDimension)BenchmarkTelemetry.Call("IModelDoc2.AddDimension2",delegate{return model.AddDimension2(
                    sketchCenter[0] + Program.Mm(8), sketchCenter[1] + Program.Mm(8), 0);});
                if (display == null)
                    throw new Program.PhaseFailure("HOLE_DIAMETER_DIMENSION_FAILED", "Could not create the hole diameter dimension.");
                int status = BenchmarkTelemetry.Call("IDimension.SetSystemValue3",delegate{return display.GetDimension2(0).SetSystemValue3(Program.Mm(intent.HoleSeed.DiameterMm),
                    (int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration, null);});
                if (status != (int)swSetValueReturnStatus_e.swSetValue_Successful)
                    throw new Program.PhaseFailure("HOLE_DIAMETER_SET_FAILED", "SetSystemValue3 returned status " + status + ".");
                model.ClearSelection2(true);
                BenchmarkTelemetry.Call("ISketchManager.InsertSketch",delegate{sketches.InsertSketch(true);});
                sketchOpened = false;
                if (!profile.Select2(false, 0))
                    throw new Program.PhaseFailure("HOLE_PROFILE_SELECTION_FAILED", "Could not select the completed hole sketch.");

                IFeature cut = BenchmarkTelemetry.Call("IFeatureManager.FeatureCut4",delegate{return model.FeatureManager.FeatureCut4(
                    true, false, false,
                    (int)swEndConditions_e.swEndCondThroughAll, (int)swEndConditions_e.swEndCondBlind,
                    0, 0, false, false, false, false, 0, 0,
                    false, false, false, false,
                    false, true, true, false, false, false,
                    (int)swStartConditions_e.swStartSketchPlane, 0, false, false);});
                if (cut == null)
                    throw new Program.PhaseFailure("THROUGH_HOLE_CREATION_FAILED", "FeatureCut4 returned no feature.");
                RequireFeatureWithoutError(cut);
                return cut;
            }
            finally
            {
                try
                {
                    if (sketchOpened && model.GetActiveSketch2() != null) BenchmarkTelemetry.Call("ISketchManager.InsertSketch",delegate{sketches.InsertSketch(true);});
                    model.ClearSelection2(true);
                }
                finally
                {
                    app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate, previousPrompt);
                }
            }
        }

        private static IFeature CreateHolePattern(IModelDoc2 model, IFeature seed, CadIntent intent)
        {
            IEdge axis = FindTopHorizontalEdge(GetSingleBody(model), intent.BasePlate);
            double[] line = (double[])((ICurve)axis.GetCurve()).LineParams;
            ISelectionMgr selections = (ISelectionMgr)model.SelectionManager;
            ISelectData selection = selections.CreateSelectData();
            selection.Mark = 1; // Native linear-pattern direction selection mark.
            model.ClearSelection2(true);
            try
            {
                if (!((IEntity)axis).Select4(false, (SelectData)selection) || !seed.Select2(true, 4))
                    throw new Program.PhaseFailure("PATTERN_SELECTION_FAILED", "Could not select the X direction edge and seed feature.");
                IFeature pattern = BenchmarkTelemetry.Call("IFeatureManager.FeatureLinearPattern5",delegate{return model.FeatureManager.FeatureLinearPattern5(
                    intent.HolePattern.Count, Program.Mm(intent.HolePattern.SpacingMm), 1, 0,
                    line[3] < 0, false, "", "", false, false,
                    false, false, false, false, false, false, false, false, 0, 0, false, false);});
                if (pattern == null)
                    throw new Program.PhaseFailure("LINEAR_PATTERN_CREATION_FAILED", "FeatureLinearPattern5 returned no feature.");
                RequireFeatureWithoutError(pattern);
                return pattern;
            }
            finally { model.ClearSelection2(true); }
        }

        private static IFeature CreateOuterFillet(IModelDoc2 model, CadIntent intent)
        {
            List<IEdge> edges = FindOuterVerticalEdges(GetSingleBody(model), intent.BasePlate);
            if (edges.Count != 4)
                throw new Program.PhaseFailure("OUTER_EDGES_NOT_FOUND", "Expected exactly four outer vertical edges; found " + edges.Count + ".");
            ISelectionMgr selections = (ISelectionMgr)model.SelectionManager;
            ISelectData selection = selections.CreateSelectData();
            selection.Mark = 1;
            model.ClearSelection2(true);
            try
            {
                foreach (IEdge edge in edges)
                    if (!((IEntity)edge).Select4(true, (SelectData)selection))
                        throw new Program.PhaseFailure("FILLET_EDGE_SELECTION_FAILED", "Could not select an outer vertical edge.");
                IFeature fillet = (IFeature)BenchmarkTelemetry.Call("IFeatureManager.FeatureFillet3",delegate{return model.FeatureManager.FeatureFillet3(
                    (int)swFeatureFilletOptions_e.swFeatureFilletUniformRadius,
                    Program.Mm(intent.OuterFillet.RadiusMm), 0, 0,
                    (int)swFeatureFilletType_e.swFeatureFilletType_Simple,
                    0, 0, null, null, null, null, null, null, null);});
                if (fillet == null)
                    throw new Program.PhaseFailure("FILLET_CREATION_FAILED", "FeatureFillet3 returned no feature.");
                RequireFeatureWithoutError(fillet);
                return fillet;
            }
            finally { model.ClearSelection2(true); }
        }

        // These selectors are specific to the fixed canonical plate in model XY, extruded along +Z.
        // They do not store IDs, persistent references, semantic state or fallback rules.
        private static IFace2 FindTopFace(IBody2 body, BasePlateIntent plate)
        {
            IFace2 found = null;
            foreach (IFace2 face in (object[])body.GetFaces())
            {
                if (!((ISurface)face.GetSurface()).IsPlane()) continue;
                double[] box = (double[])face.GetBox();
                double[] normal = (double[])face.Normal;
                if (Near(box[2], Program.Mm(plate.ThicknessMm)) && Near(box[5], Program.Mm(plate.ThicknessMm)) && Near(normal[2], 1))
                {
                    if (found != null)
                        throw new Program.PhaseFailure("PLATE_TOP_FACE_AMBIGUOUS", "More than one candidate plate top face.");
                    found = face;
                }
            }
            if (found == null)
                throw new Program.PhaseFailure("PLATE_TOP_FACE_NOT_FOUND", "Canonical execution requires a standard XY profile extruded along +Z.");
            return found;
        }

        private static IEdge FindTopHorizontalEdge(IBody2 body, BasePlateIntent plate)
        {
            foreach (IEdge edge in (object[])body.GetEdges())
            {
                if (!((ICurve)edge.GetCurve()).IsLine()) continue;
                IVertex start = (IVertex)edge.GetStartVertex();
                IVertex end = (IVertex)edge.GetEndVertex();
                if (start == null || end == null) continue;
                double[] a = (double[])start.GetPoint(), b = (double[])end.GetPoint();
                if (Near(Math.Abs(a[0] - b[0]), Program.Mm(plate.WidthMm)) &&
                    Near(a[1], -Program.Mm(plate.HeightMm / 2)) && Near(b[1], a[1]) &&
                    Near(a[2], Program.Mm(plate.ThicknessMm)) && Near(b[2], a[2])) return edge;
            }
            throw new Program.PhaseFailure("PATTERN_DIRECTION_EDGE_NOT_FOUND", "Could not find the plate's horizontal top edge.");
        }

        private static List<IEdge> FindOuterVerticalEdges(IBody2 body, BasePlateIntent plate)
        {
            List<IEdge> result = new List<IEdge>();
            foreach (IEdge edge in (object[])body.GetEdges())
            {
                if (!((ICurve)edge.GetCurve()).IsLine()) continue;
                IVertex start = (IVertex)edge.GetStartVertex();
                IVertex end = (IVertex)edge.GetEndVertex();
                if (start == null || end == null) continue;
                double[] a = (double[])start.GetPoint(), b = (double[])end.GetPoint();
                if (Near(a[0], b[0]) && Near(a[1], b[1]) &&
                    Near(Math.Abs(a[0]), Program.Mm(plate.WidthMm / 2)) &&
                    Near(Math.Abs(a[1]), Program.Mm(plate.HeightMm / 2)) &&
                    Near(Math.Min(a[2], b[2]), 0) && Near(Math.Max(a[2], b[2]), Program.Mm(plate.ThicknessMm))) result.Add(edge);
            }
            return result;
        }

        private static IBody2 GetSingleBody(IModelDoc2 model)
        {
            object[] bodies = ((IPartDoc)model).GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
            if (bodies == null || bodies.Length != 1)
                throw new Program.PhaseFailure("EXPECTED_SINGLE_BODY", "Canonical execution requires one current solid body.");
            return (IBody2)bodies[0];
        }

        private static void RequireFeatureWithoutError(IFeature feature)
        {
            bool warning;
            int error = feature.GetErrorCode2(out warning);
            if (error != 0 && !warning)
                throw new Program.PhaseFailure("FEATURE_API_ERROR", feature.Name + " reported SOLIDWORKS feature error " + error + ".");
        }

        private static bool Near(double a, double b) { return Math.Abs(a - b) < 1e-7; }
    }
}
