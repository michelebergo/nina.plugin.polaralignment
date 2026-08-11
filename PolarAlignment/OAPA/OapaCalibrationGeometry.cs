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
        /// True only when the closing moves verifiably returned the axis to its baseline:
        /// the measured residual came in under the restore tolerance. A calibration whose
        /// measurement succeeded but whose closing failed or left an out-of-tolerance
        /// residual keeps its measured values and reports false here — "measured" and
        /// "physically back at the start" are different claims, and conflating them let a
        /// failed restore be published as full success. Settable because the closing phase
        /// runs after the measured result is assembled.
        /// </summary>
        public bool RestoredToBaseline { get; set; }

        /// <summary>Residual against the baseline after the closing moves, in axis arcminutes; NaN when it could not be measured.</summary>
        public float ClosingResidualArcmin { get; set; } = float.NaN;

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
        ///
        /// This and <see cref="BacklashEnteringNegativeArcmin"/> are reported as two separate
        /// figures only when <see cref="DirectionalBacklash"/> says the difference between
        /// them is established; otherwise both carry the mean. The distinction matters
        /// because the two-leg reversal plan travels <c>move - outward + back</c>, so any gap
        /// between the pair is a fixed bias on every reversal - real play cancels it, pure
        /// measurement noise does not.
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
        /// When this is false the two per-direction figures are deliberately reported
        /// <em>equal</em> (both the mean): see <see cref="BacklashEnteringPositiveArcmin"/>.
        /// </summary>
        public bool DirectionalBacklash { get; init; }

        /// <summary>
        /// True when the backlash pair could not be measured meaningfully and both
        /// directions were reported as zero rather than as a plausible-looking number.
        ///
        /// Two ways to get here. A transition that comes out <em>negative</em> before the
        /// clamp means the reversal leg travelled further than the response predicts, which
        /// no amount of real play can produce; and a forward/reverse response disagreement
        /// beyond <see cref="ResponseSuspect"/> makes both transitions garbage, because each
        /// is evaluated against the other direction's response.
        ///
        /// Reporting zero is the conservative failure: an uncompensated reversal under-shoots
        /// and the correction loop recovers on the next cycle, whereas an over-compensated
        /// one injects error the loop then has to fight.
        /// </summary>
        public bool BacklashSuspect { get; init; }

        /// <summary>
        /// True when the clean forward and reverse responses disagree by more than a factor
        /// of two, which makes their mean - the applied <see cref="Ratio"/> - wrong for both
        /// directions rather than a compromise between them. Field evidence: an altitude axis
        /// reporting 0.860 forward against 0.102 reverse produced a factor three times the
        /// one the axis actually delivered during corrections.
        /// </summary>
        public bool ResponseSuspect { get; init; }
    }

    /// <summary>
    /// Pure geometry for the OAPA self-calibration. No hardware, imaging, or UI dependencies,
    /// so the displacement and direction rules are unit-testable in isolation.
    /// </summary>
    public static class OapaCalibrationGeometry {

        /// <summary>
        /// Azimuth calibration degenerates as cos(field altitude) approaches zero — not because the
        /// axis moves the field any less (a base rotation shifts every field's azimuth <em>coordinate</em>
        /// 1:1 at any altitude), but because the solver's on-sky noise inflates by 1/cos(alt) when
        /// expressed in azimuth coordinates. Below this floor the measurement is noise-dominated.
        /// </summary>
        public const double MinimumAzimuthCosAltitude = 0.25;

        /// <summary>
        /// Altitude calibration degenerates as the field approaches due east/west: the altitude
        /// actuator tilts the whole rig about the horizontal east-west axis, so a field at
        /// azimuth A only shows cos(A) of the tilt in its altitude. Near |cos(A)| = 0 the
        /// projection carries no signal, and the noise on what remains inflates by 1/|cos(A)|.
        /// 0.35 admits any field within ~69° of the meridian (or of north) at ~3x noise worst case.
        /// Field evidence for the failure mode this guards: one rig calibrated at A=108-137° across
        /// three sessions and every altitude factor came out inflated by exactly 1/|cos(A)| —
        /// 97.5, 202.7 and 255.0 for a mechanism whose true factor was 73-85 throughout.
        /// </summary>
        public const double MinimumAltitudeCosAzimuth = 0.35;

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
        /// Signed axis displacement between two samples, in arcminutes of <em>axis motion</em> —
        /// which is also arcminutes of polar-error change, since both TPPA error components are
        /// coordinate differences of the mount axis and the adjusters move that axis 1:1 in
        /// coordinates. That equivalence is what makes a calibrated factor mean "steps per
        /// arcminute of error" regardless of where the calibration happened to point.
        ///
        /// Azimuth: a base rotation about the vertical shifts every field's azimuth coordinate by
        /// exactly the rotation, at any altitude, so the wrapped coordinate delta *is* the axis
        /// motion. (The former cos(altitude) division converted to an on-sky angle instead, which
        /// silently scaled the factor by cos(field alt) — a 0.53 gain on a rig calibrating at
        /// alt 58°.)
        ///
        /// Altitude: the actuator tilts the rig about the horizontal east-west axis, and a field at
        /// azimuth A shows only cos(A) of that tilt in its altitude — full toward north, full and
        /// sign-reversed toward south, nothing due east/west. Dividing by the <em>signed</em>
        /// cosine recovers the tilt and makes the measured direction independent of which side of
        /// the sky the calibration pointed at, so the Reverse flag stays a property of the wiring
        /// rather than of the pointing. The projection is floored in magnitude at
        /// <see cref="MinimumAltitudeCosAzimuth"/> so restore paths never divide toward zero;
        /// the calibration refuses such pointings up front.
        /// </summary>
        public static double SignedAxisDisplacementArcmin(bool isAzimuthAxis, CalibrationSolveSample from, CalibrationSolveSample to) {
            if (!isAzimuthAxis) {
                var meanAz = from.AzimuthDegrees + WrapDegrees(to.AzimuthDegrees - from.AzimuthDegrees) / 2.0;
                var projection = Math.Cos(meanAz * Math.PI / 180.0);
                var floored = projection >= 0
                    ? Math.Max(projection, MinimumAltitudeCosAzimuth)
                    : Math.Min(projection, -MinimumAltitudeCosAzimuth);
                return (to.AltitudeDegrees - from.AltitudeDegrees) * 60.0 / floored;
            }
            return WrapDegrees(to.AzimuthDegrees - from.AzimuthDegrees) * 60.0;
        }

        /// <summary>Maps an angle difference into (−180°, 180°] so azimuth deltas across north keep their sign.</summary>
        private static double WrapDegrees(double degrees) {
            var wrapped = degrees - 360.0 * Math.Round(degrees / 360.0);
            return wrapped == -180.0 ? 180.0 : wrapped;
        }

    }
}
