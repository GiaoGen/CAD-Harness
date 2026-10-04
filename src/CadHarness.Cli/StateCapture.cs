using System;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace CadHarness.Cli
{
    internal static class StateCapture
    {
        // Initial adoption is deliberately limited to the verified Phase 2 feature names.
        // Reloading uses persisted IDs only; these names are never a resolver fallback.
        internal static CadState Capture(ISldWorks app, IModelDoc2 model)
        {
            if (model.GetActiveSketch2() != null || model.Extension.NeedsRebuild2 != 0)
                throw new Program.PhaseFailure("STATE_CAPTURE_MODEL_NOT_READY", "Finish sketch editing and rebuild the canonical Part before capturing state.");
            IFeature plate = FindFeature(model, "Extrude.Base"), seed = FindFeature(model, "Hole.MountingSeed");
            IFeature pattern = FindFeature(model, "Pattern.MountingHoles"), fillet = FindFeature(model, "Fillet.OuterVerticalEdges");
            IFeature plateSketch = FindProfile(plate), seedSketch = FindProfile(seed);
            if (plate.GetTypeName2() != "Extrusion" || pattern.GetTypeName2() != "LPattern" || fillet.GetTypeName2() != "Fillet" ||
                (seed.GetTypeName2() != "ICE" && seed.GetTypeName2() != "Cut"))
                throw new Program.PhaseFailure("STATE_CAPTURE_UNSUPPORTED", "The expected native canonical feature types are not present.");
            IExtrudeFeatureData2 cutData = (IExtrudeFeatureData2)seed.GetDefinition();
            ILinearPatternFeatureData patternData = (ILinearPatternFeatureData)pattern.GetDefinition();
            ISimpleFilletFeatureData2 filletData = (ISimpleFilletFeatureData2)fillet.GetDefinition();
            if (patternData.D1TotalInstances != 2 || cutData.GetEndCondition(true) != (int)swEndConditions_e.swEndCondThroughAll)
                throw new Program.PhaseFailure("STATE_CAPTURE_UNSUPPORTED", "Phase 3 adoption is restricted to the two-hole Through All canonical model.");

            CadState state = new CadState {
                SchemaVersion = StateSerializer.Version, CapturedUtc = DateTime.UtcNow.ToString("o"), Units = "mm",
                Document = new DocumentIdentity {
                    Id = DocumentIdentityStore.Read(model) ?? Guid.NewGuid().ToString("D"),
                    IdentityProperty = DocumentIdentityStore.PropertyName, Path = model.GetPathName(), TitleAtCapture = model.GetTitle(),
                    Configuration = ((IConfiguration)model.GetActiveConfiguration()).Name, SolidWorksRevision = app.RevisionNumber()
                },
                Features = new CanonicalFeatureState {
                    BasePlate = Identify(model, new BasePlateState { Parameters = new BasePlateParameters {
                        WidthMm = ReadDimension(plateSketch, "D1"), HeightMm = ReadDimension(plateSketch, "D2"),
                        ThicknessMm = ReadDimension(plate, "D1") } }, plate, "base_plate", "base_plate", new string[0]),
                    MountingHoleSeed = Identify(model, new HoleSeedState { Parameters = new HoleSeedParameters {
                        DiameterMm = ReadDimension(seedSketch, "D1"), DepthMode = "through_all", NativeEndCondition = cutData.GetEndCondition(true),
                        CenterConstraint = "fixed_sketch_point" } }, seed, "mounting_hole_seed", "through_hole", new string[] { "base_plate" }),
                    MountingHoles = Identify(model, new HolePatternState { Parameters = new HolePatternParameters {
                        Count = patternData.D1TotalInstances, SpacingMm = patternData.D1Spacing * 1000, Direction = "+X",
                        ReverseDirection = patternData.D1ReverseDirection, GeometryPattern = patternData.GeometryPattern,
                        CountDefinitionProperty = "ILinearPatternFeatureData.D1TotalInstances" } }, pattern, "mounting_holes", "linear_pattern", new string[] { "mounting_hole_seed" }),
                    OuterEdgeFillet = Identify(model, new FilletState { Parameters = new FilletParameters { RadiusMm = filletData.DefaultRadius * 1000 } },
                        fillet, "outer_edge_fillet", "fillet", new string[] { "base_plate", "mounting_holes" })
                },
                Dependencies = new List<DesignDependency> {
                    Dependency("base_plate", "mounting_hole_seed", "host"), Dependency("mounting_hole_seed", "mounting_holes", "pattern_seed"),
                    Dependency("base_plate", "outer_edge_fillet", "edge_owner"), Dependency("mounting_holes", "outer_edge_fillet", "preceding_body_feature") },
                SemanticEntities = new List<SemanticEntityReference>(), DimensionBindings = new List<DimensionBinding>()
            };

            AddEntity(model, state, "base_plate.profile_sketch", "feature", "base_plate", "Native base profile, owning width and height dimensions.", new object[] { plateSketch });
            AddEntity(model, state, "mounting_hole_seed.profile_sketch", "feature", "mounting_hole_seed", "Native seed profile, owning the diameter dimension.", new object[] { seedSketch });
            ISketch seedProfile = (ISketch)seedSketch.GetSpecificFeature2();
            ISketchPoint center = null;
            foreach (ISketchSegment segment in (object[])seedProfile.GetSketchSegments())
                if (!segment.ConstructionGeometry && segment is ISketchArc)
                {
                    if (center != null) throw new Program.PhaseFailure("STATE_CAPTURE_UNSUPPORTED", "The hole seed profile must contain one circle.");
                    center = (ISketchPoint)((ISketchArc)segment).GetCenterPoint2();
                }
            if (center == null) throw new Program.PhaseFailure("STATE_CAPTURE_UNSUPPORTED", "No circle center exists in the seed profile.");
            AddEntity(model, state, "mounting_hole_seed.center_point", "sketch_point", "mounting_hole_seed", "Fixed circle center in seed sketch coordinates; no position edit is implemented.", new object[] { center });
            AddBinding(state, "base_plate.width", "base_plate.profile_sketch", plateSketch, "D1");
            AddBinding(state, "base_plate.height", "base_plate.profile_sketch", plateSketch, "D2");
            AddBinding(state, "base_plate.thickness", "base_plate", plate, "D1");
            AddBinding(state, "mounting_hole_seed.diameter", "mounting_hole_seed.profile_sketch", seedSketch, "D1");
            AddBinding(state, "mounting_holes.spacing", "mounting_holes", pattern, "D3");
            AddBinding(state, "outer_edge_fillet.radius", "outer_edge_fillet", fillet, "D1");
            CaptureFinalEntities(model, state, seed, pattern, fillet);
            return state;
        }

        private static void CaptureFinalEntities(IModelDoc2 model, CadState state, IFeature seed, IFeature pattern, IFeature fillet)
        {
            object[] bodies = ((IPartDoc)model).GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
            if (bodies == null || bodies.Length != 1) throw new Program.PhaseFailure("STATE_CAPTURE_UNSUPPORTED", "The canonical model must contain one solid body.");
            IBody2 body = (IBody2)bodies[0];
            BasePlateParameters plate = state.Features.BasePlate.Parameters;
            double thickness = Program.Mm(plate.ThicknessMm), radius = Program.Mm(state.Features.OuterEdgeFillet.Parameters.RadiusMm);
            IFace2 top = null, bottom = null, seedWall = null, instanceWall = null;
            List<object> rounded = new List<object>();
            foreach (IFace2 face in (object[])body.GetFaces())
            {
                ISurface surface = (ISurface)face.GetSurface();
                double[] box = (double[])face.GetBox();
                if (surface.IsPlane())
                {
                    double[] normal = (double[])face.Normal;
                    if (Near(normal[2], 1) && Near(box[2], thickness) && Near(box[5], thickness)) AssignUnique(ref top, face);
                    if (Near(normal[2], -1) && Near(box[2], 0) && Near(box[5], 0)) AssignUnique(ref bottom, face);
                }
                if (!surface.IsCylinder()) continue;
                double[] cylinder = (double[])surface.CylinderParams;
                IFeature owner = (IFeature)face.GetFeature();
                if (owner != null && Near(Math.Abs(cylinder[5]), 1) && Near(box[2], 0) && Near(box[5], thickness))
                {
                    if (owner.GetID() == seed.GetID() && Near(cylinder[6], Program.Mm(state.Features.MountingHoleSeed.Parameters.DiameterMm / 2)))
                    {
                        AssignUnique(ref seedWall, face);
                        state.Features.MountingHoleSeed.Parameters.CenterXmm = cylinder[0] * 1000;
                        state.Features.MountingHoleSeed.Parameters.CenterYmm = cylinder[1] * 1000;
                    }
                    if (owner.GetID() == pattern.GetID() && Near(cylinder[6], Program.Mm(state.Features.MountingHoleSeed.Parameters.DiameterMm / 2))) AssignUnique(ref instanceWall, face);
                    if (owner.GetID() == fillet.GetID() && Near(cylinder[6], radius)) rounded.Add(face);
                }
            }
            if (top == null || bottom == null || seedWall == null || instanceWall == null || rounded.Count != 4)
                throw new Program.PhaseFailure("STATE_ENTITY_CAPTURE_FAILED", "Required canonical final faces could not be uniquely identified.");
            double[] seedAxis = (double[])((ISurface)seedWall.GetSurface()).CylinderParams;
            double[] instanceAxis = (double[])((ISurface)instanceWall.GetSurface()).CylinderParams;
            if (instanceAxis[0] <= seedAxis[0] || !Near(seedAxis[1], instanceAxis[1]))
                throw new Program.PhaseFailure("STATE_CAPTURE_UNSUPPORTED", "Canonical adoption requires a horizontal +X hole pattern.");
            state.Features.MountingHoles.Parameters.Direction = "+X";
            AddEntity(model, state, "base_plate.top_face", "face", "base_plate", "Current final planar +Z cap, after holes and fillets.", new object[] { top });
            AddEntity(model, state, "base_plate.bottom_face", "face", "base_plate", "Current final planar -Z cap.", new object[] { bottom });
            AddEntity(model, state, "mounting_hole_seed.wall_face", "face", "mounting_hole_seed", "Current seed cylindrical through-hole wall.", new object[] { seedWall });
            AddEntity(model, state, "mounting_holes.instance_2.wall_face", "face", "mounting_holes", "Current second-instance cylindrical wall.", new object[] { instanceWall });
            AddEntity(model, state, "outer_edge_fillet.rounded_faces", "face_set", "outer_edge_fillet", "Four current R3 corner cylindrical faces.", rounded.ToArray());

            IEdge direction = null;
            List<IEdge> tangentEdges = new List<IEdge>();
            double halfWidth = Program.Mm(plate.WidthMm / 2), halfHeight = Program.Mm(plate.HeightMm / 2);
            foreach (IEdge edge in (object[])body.GetEdges())
            {
                if (!((ICurve)edge.GetCurve()).IsLine()) continue;
                IVertex start = (IVertex)edge.GetStartVertex(), end = (IVertex)edge.GetEndVertex();
                if (start == null || end == null) continue;
                double[] a = (double[])start.GetPoint(), b = (double[])end.GetPoint();
                if (Near(a[1], -halfHeight) && Near(b[1], -halfHeight) && Near(a[2], thickness) && Near(b[2], thickness) &&
                    Near(Math.Min(a[0], b[0]), -halfWidth + radius) && Near(Math.Max(a[0], b[0]), halfWidth - radius))
                {
                    if (direction != null) throw new Program.PhaseFailure("STATE_ENTITY_CAPTURE_FAILED", "Ambiguous final direction edge.");
                    direction = edge;
                }
                if (Near(a[0], b[0]) && Near(a[1], b[1]) && Near(Math.Min(a[2], b[2]), 0) && Near(Math.Max(a[2], b[2]), thickness) &&
                    ((Near(Math.Abs(a[0]), halfWidth) && Near(Math.Abs(a[1]), halfHeight - radius)) ||
                     (Near(Math.Abs(a[0]), halfWidth - radius) && Near(Math.Abs(a[1]), halfHeight)))) tangentEdges.Add(edge);
            }
            if (direction == null || tangentEdges.Count != 8)
                throw new Program.PhaseFailure("STATE_ENTITY_CAPTURE_FAILED", "Expected the final direction edge and eight current outer tangent edges.");
            tangentEdges.Sort(delegate(IEdge a, IEdge b) {
                double[] pa = (double[])((IVertex)a.GetStartVertex()).GetPoint(), pb = (double[])((IVertex)b.GetStartVertex()).GetPoint();
                int x = pa[0].CompareTo(pb[0]); return x != 0 ? x : pa[1].CompareTo(pb[1]); });
            AddEntity(model, state, "mounting_holes.direction_edge", "edge", "mounting_holes", "Surviving final horizontal top edge, collinear with +X; not the consumed original pattern input edge.", new object[] { direction });
            AddEntity(model, state, "outer_edge_fillet.vertical_tangent_edges", "edge_set", "outer_edge_fillet", "Eight final vertical tangent boundaries. The four original sharp edges were consumed by the fillet and are not recorded as current edges.", tangentEdges.ToArray());
        }

        internal static double ReadDimension(IFeature owner, string name)
        {
            IDimension dimension = owner.Parameter(name) as IDimension;
            if (dimension == null) throw new Program.PhaseFailure("DIMENSION_BINDING_UNRESOLVED", "Could not query local dimension " + name + " on " + owner.Name + ".");
            double[] values = dimension.GetSystemValue3((int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration, null) as double[];
            if (values == null || values.Length != 1) throw new Program.PhaseFailure("DIMENSION_BINDING_UNRESOLVED", "Could not read the current-configuration dimension value.");
            return values[0] * 1000;
        }

        private static IFeature FindFeature(IModelDoc2 model, string name)
        {
            for (IFeature feature = (IFeature)model.FirstFeature(); feature != null; feature = (IFeature)feature.GetNextFeature())
                if (feature.Name == name) return feature;
            throw new Program.PhaseFailure("CANONICAL_FEATURE_NOT_FOUND", "Initial capture requires the verified Phase 2 feature " + name + ".");
        }
        private static IFeature FindProfile(IFeature owner)
        {
            for (IFeature child = (IFeature)owner.GetFirstSubFeature(); child != null; child = (IFeature)child.GetNextSubFeature())
                if (child.GetTypeName2() == "ProfileFeature") return child;
            throw new Program.PhaseFailure("CANONICAL_PROFILE_NOT_FOUND", "No native sketch profile for " + owner.Name + ".");
        }
        private static T Identify<T>(IModelDoc2 model, T entry, IFeature feature, string id, string semanticType, string[] dependencies) where T : SemanticFeature
        {
            entry.SemanticId = id; entry.SemanticType = semanticType; entry.FeatureType = feature.GetTypeName2();
            entry.SwFeatureNameAtCapture = feature.Name; entry.SwFeatureIdAtCapture = feature.GetID();
            entry.PersistentReference = PersistentReferences.Capture(model, feature); entry.Dependencies = dependencies;
            return entry;
        }
        private static void AddEntity(IModelDoc2 model, CadState state, string id, string kind, string owner, string description, object[] objects)
        {
            string[] references = new string[objects.Length];
            for (int i = 0; i < objects.Length; i++) references[i] = PersistentReferences.Capture(model, objects[i]);
            state.SemanticEntities.Add(new SemanticEntityReference { SemanticId = id, Kind = kind, OwnerSemanticId = owner, Description = description, PersistentReferences = references });
        }
        private static void AddBinding(CadState state, string id, string ownerId, IFeature owner, string localName)
        {
            state.DimensionBindings.Add(new DimensionBinding { ParameterSemanticId = id, OwnerObjectSemanticId = ownerId, LocalDimensionName = localName,
                ValueMmAtCapture = ReadDimension(owner, localName), AccessMethod = "feature.Parameter" });
        }
        private static DesignDependency Dependency(string parent, string child, string kind)
        { return new DesignDependency { ParentSemanticId = parent, ChildSemanticId = child, Kind = kind }; }
        private static void AssignUnique(ref IFace2 value, IFace2 face)
        {
            if (value != null) throw new Program.PhaseFailure("STATE_ENTITY_CAPTURE_FAILED", "More than one canonical face candidate.");
            value = face;
        }
        private static bool Near(double a, double b) { return Math.Abs(a - b) < 1e-7; }
    }
}
