using System;
using System.Collections.Generic;

namespace CadHarness.Cli
{
    // Data-only schema: never serialize COM objects or process-local handles.
    public sealed class CadState
    {
        public string SchemaVersion { get; set; }
        public string CapturedUtc { get; set; }
        public string UpdatedUtc { get; set; }
        public int EditRevision { get; set; }
        public string LastEditId { get; set; }
        public ReferenceSnapshot ReferenceHealth { get; set; }
        public string Units { get; set; }
        public DocumentIdentity Document { get; set; }
        public CanonicalFeatureState Features { get; set; }
        public List<DesignDependency> Dependencies { get; set; }
        public List<SemanticEntityReference> SemanticEntities { get; set; }
        public List<DimensionBinding> DimensionBindings { get; set; }
    }

    public sealed class DocumentIdentity
    {
        public string Id { get; set; }
        public string IdentityProperty { get; set; }
        public string Path { get; set; }
        public string TitleAtCapture { get; set; }
        public string Configuration { get; set; }
        public string SolidWorksRevision { get; set; }
    }

    public sealed class CanonicalFeatureState
    {
        public BasePlateState BasePlate { get; set; }
        public HoleSeedState MountingHoleSeed { get; set; }
        public HolePatternState MountingHoles { get; set; }
        public FilletState OuterEdgeFillet { get; set; }

        internal SemanticFeature[] All()
        { return new SemanticFeature[] { BasePlate, MountingHoleSeed, MountingHoles, OuterEdgeFillet }; }
    }

    public abstract class SemanticFeature
    {
        public string SemanticId { get; set; }
        public string SemanticType { get; set; }
        public string FeatureType { get; set; }
        public string SwFeatureNameAtCapture { get; set; }
        public int SwFeatureIdAtCapture { get; set; }
        public string PersistentReference { get; set; }
        public string[] Dependencies { get; set; }
    }

    public sealed class BasePlateState : SemanticFeature
    { public BasePlateParameters Parameters { get; set; } }
    public sealed class HoleSeedState : SemanticFeature
    { public HoleSeedParameters Parameters { get; set; } }
    public sealed class HolePatternState : SemanticFeature
    { public HolePatternParameters Parameters { get; set; } }
    public sealed class FilletState : SemanticFeature
    { public FilletParameters Parameters { get; set; } }

    public sealed class BasePlateParameters
    {
        public double WidthMm { get; set; }
        public double HeightMm { get; set; }
        public double ThicknessMm { get; set; }
    }
    public sealed class HoleSeedParameters
    {
        public double DiameterMm { get; set; }
        public double CenterXmm { get; set; }
        public double CenterYmm { get; set; }
        public string DepthMode { get; set; }
        public int NativeEndCondition { get; set; }
        public string CenterConstraint { get; set; }
    }
    public sealed class HolePatternParameters
    {
        public int Count { get; set; }
        public double SpacingMm { get; set; }
        public string Direction { get; set; }
        public bool ReverseDirection { get; set; }
        public bool GeometryPattern { get; set; }
        public string CountDefinitionProperty { get; set; }
    }
    public sealed class FilletParameters
    { public double RadiusMm { get; set; } }

    public sealed class DesignDependency
    {
        public string ParentSemanticId { get; set; }
        public string ChildSemanticId { get; set; }
        public string Kind { get; set; }
    }

    public sealed class SemanticEntityReference
    {
        public string SemanticId { get; set; }
        public string Kind { get; set; }
        public string OwnerSemanticId { get; set; }
        public string Description { get; set; }
        public string[] PersistentReferences { get; set; }
    }

    public sealed class DimensionBinding
    {
        public string ParameterSemanticId { get; set; }
        public string OwnerObjectSemanticId { get; set; }
        public string LocalDimensionName { get; set; }
        public double ValueMmAtCapture { get; set; }
        public double? CurrentValueMm { get; set; }
        public string AccessMethod { get; set; }
    }
}
