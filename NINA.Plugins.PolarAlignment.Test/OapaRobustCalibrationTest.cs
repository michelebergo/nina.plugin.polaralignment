using FluentAssertions;
using NINA.Plugins.PolarAlignment.OAPA;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// Exercises the staged self-scaling calibration (noise floor, probe escalation, clean
    /// forward/reverse legs, backlash-leg escalation, slippage detection) against a richer
    /// axis simulator: direction-dependent response, per-reversal backlash variation and
    /// deterministic seeded solve noise.
    /// </summary>
    public class OapaRobustCalibrationTest {

        /// <summary>
        /// Physical axis simulator. Response scale may differ per direction (mechanical
        /// asymmetry); backlash values are consumed per direction reversal from a cycling
        /// sequence (a varying sequence models slippage, a single value repeatable
        /// mechanics); solve noise is deterministic from the seed.
        /// </summary>
        private sealed class RobustFakeAxis : IOapaCalibrationMotion, IOapaCalibrationSolver {
            private readonly double forwardScale;
            private readonly double reverseScale;
            private readonly double[] backlashSequence;
            private readonly double noiseAmplitudeArcmin;
            /// <summary>-1 models a rig wired so the sky moves opposite the commanded sign: the case the Reverse flag exists for.</summary>
            private readonly int physicalSign;
            private uint rng;
            private int reversals;
            private double physicalPositionArcmin;
            private int lastSign;

            public int SolveCount { get; private set; }
            public readonly List<float> CommandedMoves = new();

            public RobustFakeAxis(double forwardScale,
                                  double? reverseScale = null,
                                  double[] backlashSequence = null,
                                  double noiseAmplitudeArcmin = 0,
                                  int seed = 12345,
                                  int physicalSign = 1) {
                this.forwardScale = forwardScale;
                this.reverseScale = reverseScale ?? forwardScale;
                this.backlashSequence = backlashSequence ?? new[] { 0.0 };
                this.noiseAmplitudeArcmin = noiseAmplitudeArcmin;
                this.physicalSign = physicalSign;
                rng = (uint)seed;
            }

            public Task MoveRelative(Axis axis, float arcmin, CancellationToken token) {
                CommandedMoves.Add(arcmin);
                var sign = Math.Sign(arcmin);
                var scale = sign >= 0 ? forwardScale : reverseScale;
                double effective = Math.Abs(arcmin) * scale;
                if (sign != 0 && lastSign != 0 && sign != lastSign) {
                    var backlash = backlashSequence[reversals % backlashSequence.Length];
                    reversals++;
                    effective = Math.Max(0, effective - backlash);
                }
                if (sign != 0) { lastSign = sign; }
                physicalPositionArcmin += physicalSign * sign * effective;
                return Task.CompletedTask;
            }

            private double NextNoise() {
                if (noiseAmplitudeArcmin <= 0) { return 0; }
                rng = rng * 1664525u + 1013904223u;
                return ((rng >> 8) / (double)(1 << 24) - 0.5) * 2 * noiseAmplitudeArcmin;
            }

            public Task<CalibrationSolveSample> CaptureAndSolve(CancellationToken token) {
                SolveCount++;
                var observed = physicalPositionArcmin + NextNoise();
                return Task.FromResult(new CalibrationSolveSample(
                    10.0, observed / 60.0, 30.0 + observed / 60.0, 100.0));
            }

            public double PhysicalPositionArcmin => physicalPositionArcmin;
        }

        private static Task<AxisCalibrationOutcome> Calibrate(RobustFakeAxis axis, float currentRatio = 100f, bool reversed = false) {
            var service = new OapaCalibrationService(axis, axis);
            return service.CalibrateAxisWithAutoReverse(Axis.YAxis, currentRatio, reversed, "Y", null, CancellationToken.None);
        }

        [Test]
        public async Task GilasCase_BacklashLargerThanInitialLeg_RecoversRatioAndPhysicalBacklash() {
            // The field failure that motivated the staged sequence: the mechanics lose 20'
            // per reversal while the initial legs are ~8' physical. Fixed-leg measurement
            // is fully contaminated; the escalating backlash leg must grow until the
            // shortfall is a minority share, recovering both values.
            var axis = new RobustFakeAxis(forwardScale: 0.25, backlashSequence: new[] { 20.0 });

            var outcome = await Calibrate(axis);

            outcome.Ratio.Should().BeApproximately(400f, 20f, "a quarter of the motion means four times the factor");
            outcome.BacklashArcmin.Should().BeApproximately(20f, 2f, "backlash is physical and must not be contaminated by short legs");
            outcome.DirectionalBacklash.Should().BeFalse("a play that costs the same both ways is not directional");
        }

        [Test]
        public async Task GilasCase_ReturnsAxisNearItsStart() {
            var axis = new RobustFakeAxis(forwardScale: 0.25, backlashSequence: new[] { 20.0 });

            await Calibrate(axis);

            axis.PhysicalPositionArcmin.Should().BeApproximately(0.0, 1.0);
        }

        [Test]
        public async Task NearlyDeafAxis_EscalatesTheProbeUntilMotionIsVisible() {
            // 2% response: the initial 5' probe moves 0.1', below the detection floor.
            // The probe must escalate until the motion is measurable, then calibrate.
            var axis = new RobustFakeAxis(forwardScale: 0.02);

            var outcome = await Calibrate(axis);

            outcome.Ratio.Should().BeApproximately(5000f, 250f);
        }

        [Test]
        public async Task DeadAxis_FailsHonestlyAfterEscalation() {
            var axis = new RobustFakeAxis(forwardScale: 0.0001);

            var act = () => Calibrate(axis);

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*did not move measurably*");
        }

        [Test]
        public async Task NoisySolves_StillRecoverRatioAndBacklash() {
            var axis = new RobustFakeAxis(forwardScale: 0.5, backlashSequence: new[] { 5.0 }, noiseAmplitudeArcmin: 0.05);

            var outcome = await Calibrate(axis);

            outcome.Ratio.Should().BeApproximately(200f, 10f);
            outcome.BacklashArcmin.Should().BeApproximately(5f, 1f);
            outcome.NoiseSigmaArcmin.Should().BeGreaterThan(0f);
        }

        [Test]
        public async Task AsymmetricResponse_FlagsAndReportsBothRatios_WithoutFakeBacklash() {
            // 15% direction asymmetry, zero backlash: the flag must fire with both ratios
            // reported, the single model ratio is the mean, and the asymmetry must not
            // leak into the backlash or trip the directionality verdict.
            var axis = new RobustFakeAxis(forwardScale: 1.0, reverseScale: 0.85);

            var outcome = await Calibrate(axis);

            outcome.Asymmetric.Should().BeTrue();
            outcome.ForwardRatio.Should().BeApproximately(100f, 5f);
            outcome.ReverseRatio.Should().BeApproximately(117.6f, 6f);
            outcome.Ratio.Should().BeApproximately(108.1f, 5f);
            outcome.BacklashArcmin.Should().BeLessThan(1f);
            outcome.DirectionalBacklash.Should().BeFalse("a response asymmetry is not a backlash asymmetry");
        }

        [Test]
        public async Task SymmetricResponse_DoesNotFlagAsymmetry() {
            var axis = new RobustFakeAxis(forwardScale: 0.5, backlashSequence: new[] { 5.0 });

            var outcome = await Calibrate(axis);

            outcome.Asymmetric.Should().BeFalse();
        }

        [Test]
        public async Task TransitionsThatCostDifferently_AreFlaggedAsDirectional_AndBothAreReported() {
            // An axis loaded by gravity crosses its own play unaided one way and has to be
            // driven across it the other, so the two transitions legitimately differ. The
            // mean is inexact for both directions, so the caller needs the two figures, not
            // only the flag.
            var axis = new RobustFakeAxis(forwardScale: 1.0, backlashSequence: new[] { 20.0, 8.0 });

            var outcome = await Calibrate(axis);

            outcome.DirectionalBacklash.Should().BeTrue();
            outcome.BacklashEnteringNegativeArcmin.Should().BeApproximately(20f, 2f, "the first reversal the sequence meets goes negative");
            outcome.BacklashEnteringPositiveArcmin.Should().BeApproximately(8f, 2f);
            outcome.BacklashArcmin.Should().BeApproximately(14f, 2f, "the applied value is still their mean");
        }

        [Test]
        public async Task PerDirectionBacklash_IsReportedInCommandedSign_NotInTheCalibrationsOwnLegs() {
            // The calibration's "forward" leg is the commanded positive direction only when
            // Reverse is off; with Reverse on it is the commanded negative one. The same
            // mechanism must therefore report its two figures the other way round, because
            // the planner works in commanded sign and knows nothing about Reverse.
            //
            // Same 20'/8' mechanism, wired so the sky moves against the commanded sign, and
            // calibrated with Reverse already on so no auto-flip intervenes.
            var straight = new RobustFakeAxis(forwardScale: 1.0, backlashSequence: new[] { 20.0, 8.0 });
            var mirrored = new RobustFakeAxis(forwardScale: 1.0, backlashSequence: new[] { 20.0, 8.0 }, physicalSign: -1);

            var normal = await Calibrate(straight);
            var reversed = await Calibrate(mirrored, reversed: true);

            normal.Consistent.Should().BeTrue();
            reversed.Consistent.Should().BeTrue();
            reversed.Flipped.Should().BeFalse("Reverse was already correct, so nothing had to be flipped");

            normal.BacklashEnteringNegativeArcmin.Should().BeApproximately(20f, 2f);
            normal.BacklashEnteringPositiveArcmin.Should().BeApproximately(8f, 2f);

            reversed.BacklashEnteringPositiveArcmin.Should().BeApproximately(20f, 2f,
                "with Reverse on, the calibration's first reversal travels the commanded positive direction");
            reversed.BacklashEnteringNegativeArcmin.Should().BeApproximately(8f, 2f);
        }

        [Test]
        public async Task TransitionsThatCostTheSame_AreNotDirectional() {
            var axis = new RobustFakeAxis(forwardScale: 1.0, backlashSequence: new[] { 12.0 });

            var outcome = await Calibrate(axis);

            outcome.DirectionalBacklash.Should().BeFalse();
            outcome.BacklashArcmin.Should().BeApproximately(12f, 1f);
            outcome.BacklashEnteringNegativeArcmin.Should().BeApproximately(
                outcome.BacklashEnteringPositiveArcmin, 2f, "a symmetric mechanism reports the same both ways");
        }

        [Test]
        public async Task SolveBudget_IsRespected_NominalAndGilas() {
            var nominal = new RobustFakeAxis(forwardScale: 0.5, backlashSequence: new[] { 5.0 });
            await Calibrate(nominal);
            nominal.SolveCount.Should().BeLessThanOrEqualTo(15);

            var gilas = new RobustFakeAxis(forwardScale: 0.25, backlashSequence: new[] { 20.0 });
            await Calibrate(gilas);
            gilas.SolveCount.Should().BeLessThanOrEqualTo(20);
        }

        [Test]
        public async Task BacklashStaysPhysical_AcrossRatioErrorsAndSizes() {
            // Invariant ported from the fixed-leg suite: whatever the ratio error, the
            // recovered backlash is the physical loss per reversal.
            foreach (var (scale, backlash) in new[] { (1.0, 0.0), (1.0, 2.5), (0.5, 10.0), (2.0, 2.5) }) {
                var axis = new RobustFakeAxis(forwardScale: scale, backlashSequence: new[] { backlash });

                var outcome = await Calibrate(axis);

                outcome.Ratio.Should().BeApproximately((float)(100.0 / scale), (float)(5.0 / scale),
                    $"scale={scale}, backlash={backlash}");
                outcome.BacklashArcmin.Should().BeApproximately((float)backlash, 0.5f,
                    $"scale={scale}, backlash={backlash}");
            }
        }
    }
}
