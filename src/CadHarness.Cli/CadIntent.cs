namespace CadHarness.Cli
{
    // Phase 2 has one fixed, strongly typed intent. No text parser or planner.
    internal sealed class CadIntent
    {
        internal readonly BasePlateIntent BasePlate;
        internal readonly ThroughHoleSeedIntent HoleSeed;
        internal readonly LinearPatternIntent HolePattern;
        internal readonly OuterVerticalFilletIntent OuterFillet;

        private CadIntent()
        {
            BasePlate = new BasePlateIntent(80, 50, 10);
            HoleSeed = new ThroughHoleSeedIntent(8, -20, 0);
            HolePattern = new LinearPatternIntent(2, 40);
            OuterFillet = new OuterVerticalFilletIntent(3);
        }

        internal static CadIntent Canonical() { return new CadIntent(); }
    }

    internal sealed class BasePlateIntent
    {
        internal readonly double WidthMm, HeightMm, ThicknessMm;
        internal BasePlateIntent(double widthMm, double heightMm, double thicknessMm)
        { WidthMm = widthMm; HeightMm = heightMm; ThicknessMm = thicknessMm; }
    }

    internal sealed class ThroughHoleSeedIntent
    {
        internal readonly double DiameterMm, CenterXmm, CenterYmm;
        internal ThroughHoleSeedIntent(double diameterMm, double centerXmm, double centerYmm)
        { DiameterMm = diameterMm; CenterXmm = centerXmm; CenterYmm = centerYmm; }
    }

    internal sealed class LinearPatternIntent
    {
        internal readonly int Count;
        internal readonly double SpacingMm;
        // This canonical operation always runs along positive model X.
        internal LinearPatternIntent(int count, double spacingMm)
        { Count = count; SpacingMm = spacingMm; }
    }

    internal sealed class OuterVerticalFilletIntent
    {
        internal readonly double RadiusMm;
        internal OuterVerticalFilletIntent(double radiusMm) { RadiusMm = radiusMm; }
    }
}
