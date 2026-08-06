using System;

namespace NINA.Plugins.PolarAlignment.OAPA {

    /// <summary>
    /// A single plate-solved sample used by the OAPA self-calibration: the solved
    /// field center plus its topocentric altitude and azimuth at solve time.
    /// </summary>
    public readonly record struct CalibrationSolveSample(double RADegrees, double DecDegrees, double AltitudeDegrees, double AzimuthDegrees);

    /// <summary>
    /// Result of calibrating a single axis from the four-solve leg sequence.
    /// </summary>
    public sealed class AxisCalibrationResult {
        /// <summary>Discovered calibration factor (motor units per arcminute of axis motion).</summary>
        public float Ratio { get; init; }

        /// <summary>
        /// Mechanical backlash measured from the reversal-leg shortfall, in physical axis
        /// arcminutes (clean − reversal). Physical units make the value valid under the
        /// discovered <see cref="Ratio"/> when later converted into compensation moves.
        /// </summary>
        public float BacklashArcmin { get; init; }

        /// <summary>True when the forward leg moved in the topocentric direction the logical command asked for.</summary>
        public bool Consistent { get; init; }

        /// <summary>True when the clean forward and reverse responses disagree by more than the asymmetry threshold.</summary>
        public bool Asymmetric { get; init; }

        /// <summary>Solve noise measured before any motion (S0), in axis arcminutes.</summary>
        public float NoiseSigmaArcmin { get; init; }

        /// <summary>Calibration factor measured from the clean forward legs alone (reported when <see cref="Asymmetric"/>).</summary>
        public float ForwardRatio { get; init; }

        /// <summary>Calibration factor measured from the clean reverse legs alone (reported when <see cref="Asymmetric"/>).</summary>
        public float ReverseRatio { get; init; }

        /// <summary>
        /// Lost motion when a move in the positive commanded direction reverses into it, in
        /// axis arcminutes. Expressed in commanded (wire) sign, not in the calibration's own
        /// forward/reverse legs, because the Reverse flag inverts the relation between the
        /// two and the consumer of this value plans in wire sign.
        /// </summary>
        public float BacklashEnteringPositiveArcmin { get; init; }

        /// <summary>Lost motion when a move in the negative commanded direction reverses into it.</summary>
        public float BacklashEnteringNegativeArcmin { get; init; }

        /// <summary>
        /// True when the two backlash transitions disagree beyond noise and tolerance.
        ///
        /// They are two distinct physical quantities, not two samples of one: an axis loaded
        /// by gravity crosses its own play unaided going down and has to be driven across it
        /// going up, so the two can legitimately differ several-fold and still be perfectly
        /// repeatable (field evidence: 53.4'/16.2' and 59.9'/15.9' on two consecutive runs of
        /// the same axis). Their disagreement therefore says the backlash is *directional*,
        /// which is compensable; it says nothing about repeatability, which would need the
        /// same transition measured twice.
        ///
        /// <see cref="BacklashArcmin"/> is their mean, so it is inexact for both directions:
        /// a reversal keeps a residual of about half their difference. That costs extra
        /// convergence cycles, which is a warning, not a reason to withhold the result.
        /// </summary>
        public bool DirectionalBacklash { get; init; }
    }

    /// <summary>
    /// Pure geometry for the OAPA self-calibration. No hardware, imaging, or UI dependencies,
    /// so the displacement and direction rules are unit-testable in isolation.
    /// </summary>
    public static class OapaCalibrationGeometry {

        /// <summary>Azimuth calibration degenerates as cos(alt) approaches zero; below this the lever is too foreshortened.</summary>
        public const double MinimumAzimuthCosAltitude = 0.25;

        /// <summary>
        /// Whether the observed topocentric displacement between two samples matches the sign
        /// of the logical command that produced it. A positive logical azimuth command must
        /// increase the field's topocentric azimuth and a positive logical altitude command
        /// its topocentric altitude — the convention the correction controller and the manual
        /// nudge buttons assume. A physically reversed axis produces the opposite topocentric
        /// direction, so this check fails on the first pass and succeeds after the Reverse
        /// flag is flipped, which is what makes the auto-flip retry reachable.
        /// </summary>
        public static bool SignedDisplacementMatchesCommand(bool isAzimuthAxis, CalibrationSolveSample from, CalibrationSolveSample to, float logicalCommand) {
            return Math.Sign(SignedAxisDisplacementArcmin(isAzimuthAxis, from, to)) == Math.Sign(logicalCommand);
        }

        /// <summary>
        /// Signed axis displacement between two samples, in arcminutes: positive when the field
        /// moved in the positive topocentric direction of the axis. Azimuth deltas are wrapped
        /// across north and corrected for cos(altitude) foreshortening; the cosine is floored at
        /// <see cref="MinimumAzimuthCosAltitude"/> so restore paths never divide toward zero.
        /// </summary>
        public static double SignedAxisDisplacementArcmin(bool isAzimuthAxis, CalibrationSolveSample from, CalibrationSolveSample to) {
            if (!isAzimuthAxis) {
                return (to.AltitudeDegrees - from.AltitudeDegrees) * 60.0;
            }
            var meanAlt = (from.AltitudeDegrees + to.AltitudeDegrees) / 2.0;
            var cosAlt = Math.Max(Math.Cos(meanAlt * Math.PI / 180.0), MinimumAzimuthCosAltitude);
            return WrapDegrees(to.AzimuthDegrees - from.AzimuthDegrees) * 60.0 / cosAlt;
        }

        /// <summary>Maps an angle difference into (−180°, 180°] so azimuth deltas across north keep their sign.</summary>
        private static double WrapDegrees(double degrees) {
            var wrapped = degrees - 360.0 * Math.Round(degrees / 360.0);
            return wrapped == -180.0 ? 180.0 : wrapped;
        }

    }
}
