using FluentAssertions;
using NINA.Plugins.PolarAlignment.OAPA;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// Field replay: every documented tester rig becomes a physical model, and every
    /// release must prove end-to-end - real OapaCalibrationService, real
    /// AutomatedAdjustmentController, real BacklashModePlanner, real ConvergenceMonitor -
    /// that the full calibrate-apply-align cycle either converges or halts HONESTLY on
    /// that rig. The one outcome no scenario may ever produce is a false success:
    /// reporting convergence while the true (noise-free) error is still large.
    /// </summary>
    public class FieldReplayRegressionTest {

        // ----- The physical truth of one axis -----

        private sealed class RigAxis {
            /// <summary>Physical arcmin produced per commanded logical arcmin, by direction.
            /// Encodes both a wrong stored factor (same in both directions) and mechanical
            /// loss like a stalling motor (direction-dependent).</summary>
            public double ResponseFwd = 1.0;
            public double ResponseRev = 1.0;
            /// <summary>Dead travel: geared motion eaten while the drivetrain re-engages.</summary>
            public double DeadbandArcmin;
            /// <summary>-1 models a rig wired so the sky moves opposite the commanded sign.</summary>
            public int PhysicalSign = 1;

            private double engagement;
            public double Physical { get; private set; }

            public RigAxis() { engagement = 0; }

            public void InitEngagement() { engagement = DeadbandArcmin; } // rests engaged positive

            /// <summary>Executes a commanded logical move; returns the physical displacement.</summary>
            public double Move(double commandedLogical) {
                var geared = commandedLogical * (commandedLogical >= 0 ? ResponseFwd : ResponseRev);
                double physicalDelta = 0;
                if (geared > 0) {
                    var eaten = Math.Min(DeadbandArcmin - engagement, geared);
                    engagement += eaten;
                    physicalDelta = PhysicalSign * (geared - eaten);
                } else if (geared < 0) {
                    var eaten = Math.Min(engagement, -geared);
                    engagement -= eaten;
                    physicalDelta = -PhysicalSign * (-geared - eaten);
                }
                Physical += physicalDelta;
                return physicalDelta;
            }

            /// <summary>Applying a new calibration factor rescales what a logical command produces.</summary>
            public void ApplyRatio(double newRatio, double oldRatio) {
                ResponseFwd *= newRatio / oldRatio;
                ResponseRev *= newRatio / oldRatio;
            }
        }

        // ----- A whole rig: two axes, an alignment error, solve noise -----

        private sealed class FieldRig {
            public readonly RigAxis X = new();
            public readonly RigAxis Y = new();
            public double AzErrArcmin;
            public double AltErrArcmin;
            public double NoiseAmplitudeArcmin;
            private uint rng = 987654321;

            public double TrueTotalArcmin => Math.Sqrt(AzErrArcmin * AzErrArcmin + AltErrArcmin * AltErrArcmin);

            public double Noise() {
                if (NoiseAmplitudeArcmin <= 0) { return 0; }
                rng = rng * 1664525u + 1013904223u;
                return ((rng >> 8) / (double)(1 << 24) - 0.5) * 2 * NoiseAmplitudeArcmin;
            }

            public void MoveX(double logical) { AzErrArcmin -= X.Move(logical); }
            public void MoveY(double logical) { AltErrArcmin -= Y.Move(logical); }
        }

        /// <summary>Presents one rig axis to the calibration service through its production seams.</summary>
        private sealed class CalibrationAdapter : IOapaCalibrationMotion, IOapaCalibrationSolver {
            private readonly RigAxis axis;
            private readonly FieldRig rig;
            private readonly double sessionOrigin;

            public CalibrationAdapter(FieldRig rig, RigAxis axis) {
                this.rig = rig;
                this.axis = axis;
                sessionOrigin = axis.Physical;
            }

            public Task MoveRelative(Axis a, float arcmin, CancellationToken token) {
                axis.Move(arcmin);
                return Task.CompletedTask;
            }

            public Task<CalibrationSolveSample> CaptureAndSolve(CancellationToken token) {
                var observed = (axis.Physical - sessionOrigin) + rig.Noise();
                return Task.FromResult(new CalibrationSolveSample(10.0, observed / 60.0, 30.0 + observed / 60.0, 0.0));
            }
        }

        // ----- Calibrate + Apply, the way the panel does it -----

        private sealed class AppliedAxis {
            public AxisCalibrationOutcome Outcome;
            public OapaBacklashMode Mode;
            public float BacklashPos;
            public float BacklashNeg;
        }

        private static async Task<AppliedAxis> CalibrateAndApply(FieldRig rig, RigAxis axis, float currentRatio = 100f) {
            var adapter = new CalibrationAdapter(rig, axis);
            var service = new OapaCalibrationService(adapter, adapter);
            var outcome = await service.CalibrateAxisWithAutoReverse(Axis.YAxis, currentRatio, false, "sim", null, CancellationToken.None);

            axis.ApplyRatio(outcome.Ratio, currentRatio);
            var backlashPos = outcome.BacklashSuspect ? 0f : outcome.BacklashEnteringPositiveArcmin;
            var backlashNeg = outcome.BacklashSuspect ? 0f : outcome.BacklashEnteringNegativeArcmin;
            var mode = BacklashModePlanner.Recommend(Math.Max(backlashPos, backlashNeg), outcome.NoiseSigmaArcmin);
            return new AppliedAxis { Outcome = outcome, Mode = mode, BacklashPos = backlashPos, BacklashNeg = backlashNeg };
        }

        // ----- The alignment loop, mirroring the production cycle -----

        private enum ReplayOutcome { Converged, HonestHalt, TimedOut, FalseSuccess }

        private sealed class ReplayResult {
            public ReplayOutcome Outcome;
            public int Cycles;
            public double TrueFinalErrorArcmin;
            public string Reason = "";
        }

        private static ReplayResult RunAlignment(FieldRig rig, AppliedAxis x, AppliedAxis y, double toleranceArcmin = 0.5) {
            var controller = new AutomatedAdjustmentController { AggressiveCorrections = true };
            var monitor = new ConvergenceMonitor(toleranceArcmin);
            var lastX = LastDirection.Positive;
            var lastY = LastDirection.Positive;
            double lastCmdMag = 0;
            var moved = false;
            var first = true;

            for (var cycle = 1; cycle <= 80; cycle++) {
                var azM = rig.AzErrArcmin + rig.Noise();
                var altM = rig.AltErrArcmin + rig.Noise();
                var total = Math.Sqrt(azM * azM + altM * altM);

                var decision = monitor.Observe(total, lastCmdMag, moved, first);
                first = false;
                moved = false;

                if (decision.Action == ConvergenceAction.Finish || decision.Action == ConvergenceAction.FinishBestEffort) {
                    var trueErr = rig.TrueTotalArcmin;
                    return new ReplayResult {
                        Outcome = trueErr <= 2 * toleranceArcmin + 3 * rig.NoiseAmplitudeArcmin ? ReplayOutcome.Converged : ReplayOutcome.FalseSuccess,
                        Cycles = cycle,
                        TrueFinalErrorArcmin = trueErr,
                        Reason = decision.Reason
                    };
                }
                if (decision.Action == ConvergenceAction.HaltCalibrationSuspect || decision.Action == ConvergenceAction.HaltEstimateDrift) {
                    return new ReplayResult { Outcome = ReplayOutcome.HonestHalt, Cycles = cycle, TrueFinalErrorArcmin = rig.TrueTotalArcmin, Reason = decision.Reason };
                }
                if (decision.Action == ConvergenceAction.AwaitConfirmation) {
                    lastCmdMag = 0;
                    continue;
                }

                controller.MaximumMoveMagnitude = Math.Min(Math.Max(AutomatedAdjustmentController.DefaultMaximumMoveMagnitude, total * 0.8), 30.0);
                controller.UpdateObservation(azM / 60.0, altM / 60.0);
                var plan = controller.CreatePlan();
                if (controller.RunawayDetected) {
                    return new ReplayResult { Outcome = ReplayOutcome.HonestHalt, Cycles = cycle, TrueFinalErrorArcmin = rig.TrueTotalArcmin, Reason = plan.Reason };
                }
                if (!plan.HasMovement) {
                    lastCmdMag = 0;
                    continue;
                }

                if (Math.Abs(plan.XMagnitude) > 0) {
                    foreach (var leg in BacklashModePlanner.PlanMoves(x.Mode, (float)plan.XMagnitude, x.BacklashPos, x.BacklashNeg, lastX)) {
                        rig.MoveX(leg);
                        if (Math.Abs(leg) > 0) { lastX = leg >= 0 ? LastDirection.Positive : LastDirection.Negative; }
                    }
                }
                if (Math.Abs(plan.YMagnitude) > 0) {
                    foreach (var leg in BacklashModePlanner.PlanMoves(y.Mode, (float)plan.YMagnitude, y.BacklashPos, y.BacklashNeg, lastY)) {
                        rig.MoveY(leg);
                        if (Math.Abs(leg) > 0) { lastY = leg >= 0 ? LastDirection.Positive : LastDirection.Negative; }
                    }
                }
                controller.NoteSuccessfulExecution(plan);
                lastCmdMag = Math.Max(Math.Abs(plan.XMagnitude), Math.Abs(plan.YMagnitude));
                moved = true;
            }

            return new ReplayResult { Outcome = ReplayOutcome.TimedOut, Cycles = 80, TrueFinalErrorArcmin = rig.TrueTotalArcmin, Reason = "no decision in 80 cycles" };
        }

        // =====================================================================
        // Scenarios - each one is a documented tester rig and night.
        // =====================================================================

        [Test]
        public async Task HealthyRig_Valo_TwoDegreesOff_Converges() {
            // Valo's rig after the hardware fixes: symmetric ~4.2' of play, honest
            // response, low noise. The bread-and-butter case: it must converge.
            var rig = new FieldRig { AzErrArcmin = -126, AltErrArcmin = -24, NoiseAmplitudeArcmin = 0.08 };
            rig.X.DeadbandArcmin = 4.2; rig.Y.DeadbandArcmin = 4.2;
            rig.X.InitEngagement(); rig.Y.InitEngagement();

            var x = await CalibrateAndApply(rig, rig.X);
            var y = await CalibrateAndApply(rig, rig.Y);
            var result = RunAlignment(rig, x, y);

            x.Outcome.Consistent.Should().BeTrue();
            result.Outcome.Should().Be(ReplayOutcome.Converged, result.Reason);
            result.Cycles.Should().BeLessThan(45);
        }

        [Test]
        public async Task WrongFactorRig_TwiceTooLarge_CalibrationRecoversAndConverges() {
            // The moderate wrong-factor case: stored factor 2x too large plus real play.
            // Calibration must recover the factor within the travel budget and converge.
            var rig = new FieldRig { AzErrArcmin = 90, AltErrArcmin = 45, NoiseAmplitudeArcmin = 0.1 };
            rig.X.ResponseFwd = rig.X.ResponseRev = 2.0; rig.X.DeadbandArcmin = 8;
            rig.Y.ResponseFwd = rig.Y.ResponseRev = 2.0; rig.Y.DeadbandArcmin = 15;
            rig.X.InitEngagement(); rig.Y.InitEngagement();

            var x = await CalibrateAndApply(rig, rig.X);
            var y = await CalibrateAndApply(rig, rig.Y);

            rig.Y.ResponseFwd.Should().BeApproximately(1.0, 0.15, "the recovered factor must land the response near unity");
            var result = RunAlignment(rig, x, y);
            result.Outcome.Should().Be(ReplayOutcome.Converged, result.Reason);
        }

        [Test]
        public async Task AyhanRig_FactorGrosslyWrongPlusBigPlay_EndsInOneOfTheTwoHonestOutcomes() {
            // FOUND BY THIS SIMULATOR (2026-08-13): Ayhan's class of rig - stored factor
            // ~3.6x too large AND ~40' of play - swings the S3 backlash escalation near
            // the rc17.3 travel budget (the physical legs are 3.6x the intended size
            // until the factor is corrected). Depending on solve noise the calibration
            // either completes within budget or aborts protectively. BOTH are honest;
            // what is forbidden is the third path: an apply that leaves the response far
            // from unity, followed by a loop that pretends to work.
            var rig = new FieldRig { AzErrArcmin = 90, AltErrArcmin = 45, NoiseAmplitudeArcmin = 0.1 };
            rig.X.DeadbandArcmin = 8;
            rig.Y.ResponseFwd = rig.Y.ResponseRev = 3.6; rig.Y.DeadbandArcmin = 40;
            rig.X.InitEngagement(); rig.Y.InitEngagement();

            var x = await CalibrateAndApply(rig, rig.X);
            AppliedAxis y;
            try {
                y = await CalibrateAndApply(rig, rig.Y);
            } catch (InvalidOperationException ex) {
                ex.Message.Should().Contain("travel budget", "the only acceptable abort is the protective, actionable one");
                return; // honest outcome #1: the budget refused to sweep the platform blind
            }

            // Honest outcome #2: the factor was recovered - then the loop must converge.
            rig.Y.ResponseFwd.Should().BeApproximately(1.0, 0.2, "an apply that survives must leave the response near unity");
            var result = RunAlignment(rig, x, y);
            result.Outcome.Should().Be(ReplayOutcome.Converged, result.Reason);
        }

        [Test]
        public async Task GilasRig_AltitudeMotorStalling_NeverReportsFalseSuccess() {
            // gilas, night of 2026-08-12: the altitude motor at 600 mA moved ~5% of the
            // commanded amount uphill but fine downhill, under ~50' of play. No software
            // can align that axis - the required outcome is an HONEST one: either the
            // monitor halts, or the loop times out, or - if convergence is reported -
            // the true error really is small. A false success is the one forbidden result.
            var rig = new FieldRig { AzErrArcmin = 63, AltErrArcmin = -45, NoiseAmplitudeArcmin = 0.06 };
            rig.X.DeadbandArcmin = 6.8;
            rig.Y.ResponseFwd = 0.05; rig.Y.ResponseRev = 1.24; rig.Y.DeadbandArcmin = 50;
            rig.X.InitEngagement(); rig.Y.InitEngagement();

            var x = await CalibrateAndApply(rig, rig.X);
            AppliedAxis y;
            try {
                y = await CalibrateAndApply(rig, rig.Y);
            } catch (InvalidOperationException ex) {
                // Honest outcome #1 - and the best available: legs sized for the dead
                // uphill direction are monsters in the live downhill one, so the travel
                // budget refuses the sweep before a poisoned factor can even be applied.
                // (In the real 2026-08-12 night, pre-budget rc17.2 applied ratio 1041
                // instead of ~543 and physically swept ~4 degrees.)
                ex.Message.Should().Contain("travel budget");
                return;
            }

            y.Outcome.ResponseSuspect.Should().BeTrue("a 25x directional disagreement must be flagged");
            var result = RunAlignment(rig, x, y);
            result.Outcome.Should().NotBe(ReplayOutcome.FalseSuccess,
                $"a stalled axis must never be reported as aligned (outcome {result.Outcome}, true error {result.TrueFinalErrorArcmin:F1}', {result.Reason})");
        }

        [Test]
        public async Task ReversedAzimuthAxis_IsAutoDetected_AndConverges() {
            // A rig wired so the sky moves opposite the commanded direction on azimuth.
            // Calibration's auto-reverse must absorb it and the loop must converge.
            var rig = new FieldRig { AzErrArcmin = -40, AltErrArcmin = 20, NoiseAmplitudeArcmin = 0.08 };
            rig.X.PhysicalSign = -1; rig.X.DeadbandArcmin = 5;
            rig.Y.DeadbandArcmin = 8;
            rig.X.InitEngagement(); rig.Y.InitEngagement();

            var x = await CalibrateAndApply(rig, rig.X);
            var y = await CalibrateAndApply(rig, rig.Y);
            var result = RunAlignment(rig, x, y);

            result.Outcome.Should().Be(ReplayOutcome.Converged, result.Reason);
        }

        [Test]
        public async Task BigDirectionalBacklash_IsMeasuredPerDirection_AndConverges() {
            // The rc14 class of rig: large, direction-dependent play (9.3'/7.3' measured
            // in the field on top of a big deadband). The calibration measures it, the
            // planner compensates it, the loop converges.
            var rig = new FieldRig { AzErrArcmin = 70, AltErrArcmin = -30, NoiseAmplitudeArcmin = 0.1 };
            rig.X.DeadbandArcmin = 9.3;
            rig.Y.DeadbandArcmin = 25;
            rig.X.InitEngagement(); rig.Y.InitEngagement();

            var x = await CalibrateAndApply(rig, rig.X);
            var y = await CalibrateAndApply(rig, rig.Y);
            var result = RunAlignment(rig, x, y);

            y.Outcome.BacklashArcmin.Should().BeGreaterThan(15, "the large play must be measured, not lost");
            result.Outcome.Should().Be(ReplayOutcome.Converged, result.Reason);
        }

        [Test]
        public async Task NoisySeeing_StillConvergesOrWaits_NeverFalseSuccess() {
            // Poor seeing: solve noise at 0.3' against a 0.5' tolerance. The loop may
            // take longer and lean on confirmation cycles, but must not claim success
            // while the true error is large.
            var rig = new FieldRig { AzErrArcmin = 30, AltErrArcmin = 15, NoiseAmplitudeArcmin = 0.3 };
            rig.X.DeadbandArcmin = 4; rig.Y.DeadbandArcmin = 6;
            rig.X.InitEngagement(); rig.Y.InitEngagement();

            var x = await CalibrateAndApply(rig, rig.X);
            var y = await CalibrateAndApply(rig, rig.Y);
            var result = RunAlignment(rig, x, y);

            result.Outcome.Should().NotBe(ReplayOutcome.FalseSuccess, result.Reason);
            result.Outcome.Should().NotBe(ReplayOutcome.TimedOut, $"true error stuck at {result.TrueFinalErrorArcmin:F2}'");
        }
    }
}
