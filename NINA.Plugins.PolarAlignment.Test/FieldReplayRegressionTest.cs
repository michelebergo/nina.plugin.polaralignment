using FluentAssertions;
using NINA.Plugins.PolarAlignment.OAPA;
using System;
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
    ///
    /// The physics model covers: per-direction response (wrong factors, stalling
    /// motors), engagement-based dead travel with per-reversal stick-slip variation,
    /// reversed wiring, cos(field azimuth) projection of the altitude actuator,
    /// sidereal drift during calibration (frozen vs legacy per-solve epochs), solve
    /// noise, estimate drift in the correction loop, and serial dropouts mid-loop.
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
            /// <summary>Stick-slip: each reversal re-draws the effective deadband as
            /// base ± variation (Valo's field pair: 4.10/4.31 one night, 0.00/8.69 five
            /// minutes later - stable sum, flipped split).</summary>
            public double DeadbandVariationArcmin;
            /// <summary>-1 models a rig wired so the sky moves opposite the commanded sign.</summary>
            public int PhysicalSign = 1;

            private double engagement;
            private double currentDeadband;
            private int lastGearedSign;
            private uint rng = 24681357;
            public double Physical { get; private set; }

            public void InitEngagement() {
                currentDeadband = DeadbandArcmin;
                engagement = currentDeadband; // rests engaged positive
                lastGearedSign = 1;
            }

            private double Roll() {
                rng = rng * 1664525u + 1013904223u;
                return ((rng >> 8) / (double)(1 << 24) - 0.5) * 2;
            }

            private double pendingOppositeCost = -1;

            /// <summary>Executes a commanded logical move; returns the physical displacement.</summary>
            public double Move(double commandedLogical) {
                var geared = commandedLogical * (commandedLogical >= 0 ? ResponseFwd : ResponseRev);
                var sign = Math.Sign(geared);

                if (DeadbandVariationArcmin > 0) {
                    // Valo's slip signature: the ROUND-TRIP play is conserved while the
                    // split between the two crossing directions flips freely (4.10/4.31
                    // one pass, 0.00/8.69 five minutes later). Model: the first crossing
                    // of a pair draws its cost, the opposite crossing pays the remainder.
                    double physicalSlip = 0;
                    if (sign != 0 && sign != lastGearedSign) {
                        double cost;
                        if (pendingOppositeCost >= 0) {
                            cost = pendingOppositeCost;
                            pendingOppositeCost = -1;
                        } else {
                            var roundTrip = 2 * DeadbandArcmin;
                            cost = Math.Clamp(DeadbandArcmin + Roll() * DeadbandVariationArcmin, 0, roundTrip);
                            pendingOppositeCost = roundTrip - cost;
                        }
                        var eaten = Math.Min(Math.Abs(geared), cost);
                        physicalSlip = sign * (Math.Abs(geared) - eaten) - geared; // shortfall, signed
                    }
                    if (sign != 0) { lastGearedSign = sign; }
                    var delta = PhysicalSign * (geared + physicalSlip);
                    Physical += delta;
                    return delta;
                }

                if (sign != 0) { lastGearedSign = sign; }

                double physicalDelta = 0;
                if (geared > 0) {
                    var eaten = Math.Min(currentDeadband - engagement, geared);
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

        // ----- A whole rig: two axes, an alignment error, field geometry, solve noise -----

        private sealed class FieldRig {
            public readonly RigAxis X = new();
            public readonly RigAxis Y = new();
            public double AzErrArcmin;
            public double AltErrArcmin;
            public double NoiseAmplitudeArcmin;
            /// <summary>Where the camera points. The altitude actuator's physical tilt shows
            /// in the field's altitude only as cos(azimuth) of the tilt (rc16 geometry).</summary>
            public double FieldAzimuthDegrees;
            private uint rng = 987654321;

            public double AltProjection => Math.Cos(FieldAzimuthDegrees * Math.PI / 180.0);
            public double TrueTotalArcmin => Math.Sqrt(AzErrArcmin * AzErrArcmin + AltErrArcmin * AltErrArcmin);

            public double Noise() {
                if (NoiseAmplitudeArcmin <= 0) { return 0; }
                rng = rng * 1664525u + 1013904223u;
                return ((rng >> 8) / (double)(1 << 24) - 0.5) * 2 * NoiseAmplitudeArcmin;
            }

            public void MoveX(double logical) { AzErrArcmin -= X.Move(logical); }
            public void MoveY(double logical) { AltErrArcmin -= Y.Move(logical) * AltProjection; }
        }

        /// <summary>
        /// Presents one rig axis to the calibration service through its production seams,
        /// with a solve clock: each solve advances session time, and a tracked field's
        /// sidereal rotation leaks into the measurement unless the epoch is frozen
        /// (the rc17.1 sampler behavior; legacy per-solve epochs model the pre-fix bug).
        /// </summary>
        private sealed class CalibrationAdapter : IOapaCalibrationMotion, IOapaCalibrationSolver {
            private readonly RigAxis axis;
            private readonly FieldRig rig;
            private readonly bool projectAltitude;
            private readonly double sessionOrigin;
            private readonly double driftArcminPerSolve;
            private readonly bool epochFrozen;
            private int solves;

            public CalibrationAdapter(FieldRig rig, RigAxis axis, bool projectAltitude,
                                      double driftArcminPerSolve = 0, bool epochFrozen = true) {
                this.rig = rig;
                this.axis = axis;
                this.projectAltitude = projectAltitude;
                this.driftArcminPerSolve = driftArcminPerSolve;
                this.epochFrozen = epochFrozen;
                sessionOrigin = axis.Physical;
            }

            public Task MoveRelative(Axis a, float arcmin, CancellationToken token) {
                axis.Move(arcmin);
                return Task.CompletedTask;
            }

            public Task<CalibrationSolveSample> CaptureAndSolve(CancellationToken token) {
                solves++;
                var projection = projectAltitude ? rig.AltProjection : 1.0;
                var leakedDrift = epochFrozen ? 0.0 : driftArcminPerSolve * solves;
                var observed = (axis.Physical - sessionOrigin) * projection + leakedDrift + rig.Noise();
                return Task.FromResult(new CalibrationSolveSample(
                    10.0, observed / 60.0, 30.0 + observed / 60.0, projectAltitude ? rig.FieldAzimuthDegrees : 0.0));
            }
        }

        // ----- Calibrate + Apply, the way the panel does it -----

        private sealed class AppliedAxis {
            public AxisCalibrationOutcome Outcome;
            public OapaBacklashMode Mode;
            public float BacklashPos;
            public float BacklashNeg;
        }

        private static async Task<AppliedAxis> CalibrateAndApply(FieldRig rig, RigAxis axis, float currentRatio = 100f,
                                                                 double driftArcminPerSolve = 0, bool epochFrozen = true) {
            var adapter = new CalibrationAdapter(rig, axis, projectAltitude: ReferenceEquals(axis, rig.Y),
                                                 driftArcminPerSolve, epochFrozen);
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
            public bool VerificationRan;
            public string Reason = "";
        }

        private sealed class LoopConditions {
            /// <summary>Continuous-estimator drift: arcmin added to the MEASURED error per
            /// cycle, while the true error stays put (the rc8/Kevin class of failure).</summary>
            public double EstimateDriftAzPerCycle;
            public double EstimateDriftAltPerCycle;
            /// <summary>Serial dropout window: commanded moves fail in these cycles.</summary>
            public int MovesFailFromCycle = int.MaxValue;
            public int MovesFailForCycles;
        }

        private static ReplayResult RunAlignment(FieldRig rig, AppliedAxis x, AppliedAxis y,
                                                 LoopConditions conditions = null, double toleranceArcmin = 0.5) {
            conditions ??= new LoopConditions();
            var controller = new AutomatedAdjustmentController { AggressiveCorrections = true };
            var monitor = new ConvergenceMonitor(toleranceArcmin);
            var lastX = LastDirection.Positive;
            var lastY = LastDirection.Positive;
            double lastCmdMag = 0;
            double driftAz = 0, driftAlt = 0;
            var moved = false;
            var first = true;
            var verificationRan = false;

            for (var cycle = 1; cycle <= 120; cycle++) {
                driftAz += conditions.EstimateDriftAzPerCycle;
                driftAlt += conditions.EstimateDriftAltPerCycle;
                var azM = rig.AzErrArcmin + driftAz + rig.Noise();
                var altM = rig.AltErrArcmin + driftAlt + rig.Noise();
                var total = Math.Sqrt(azM * azM + altM * altM);

                var decision = monitor.Observe(total, lastCmdMag, moved, first);
                first = false;
                moved = false;

                if (decision.Action == ConvergenceAction.Finish || decision.Action == ConvergenceAction.FinishBestEffort) {
                    // The production auto verification run: a FRESH three-point measurement
                    // carries no accumulated estimator drift. If it disagrees, the loop
                    // re-runs the correction phase once (Kevin's field night, 2026-07-27).
                    var freshTotal = rig.TrueTotalArcmin + Math.Abs(rig.Noise());
                    var honestBar = 2 * toleranceArcmin + 3 * rig.NoiseAmplitudeArcmin;
                    if (freshTotal <= honestBar) {
                        return new ReplayResult {
                            Outcome = ReplayOutcome.Converged, Cycles = cycle,
                            TrueFinalErrorArcmin = rig.TrueTotalArcmin, VerificationRan = verificationRan, Reason = decision.Reason
                        };
                    }
                    if (!verificationRan) {
                        verificationRan = true;
                        driftAz = 0; driftAlt = 0; // fresh determination, fresh estimator
                        monitor = new ConvergenceMonitor(toleranceArcmin);
                        first = true;
                        lastCmdMag = 0;
                        continue;
                    }
                    return new ReplayResult {
                        Outcome = ReplayOutcome.FalseSuccess, Cycles = cycle,
                        TrueFinalErrorArcmin = rig.TrueTotalArcmin, VerificationRan = true, Reason = decision.Reason
                    };
                }
                if (decision.Action == ConvergenceAction.HaltCalibrationSuspect || decision.Action == ConvergenceAction.HaltEstimateDrift) {
                    return new ReplayResult {
                        Outcome = ReplayOutcome.HonestHalt, Cycles = cycle,
                        TrueFinalErrorArcmin = rig.TrueTotalArcmin, VerificationRan = verificationRan, Reason = decision.Reason
                    };
                }
                if (decision.Action == ConvergenceAction.AwaitConfirmation) {
                    lastCmdMag = 0;
                    continue;
                }

                controller.MaximumMoveMagnitude = Math.Min(Math.Max(AutomatedAdjustmentController.DefaultMaximumMoveMagnitude, total * 0.8), 30.0);
                controller.UpdateObservation(azM / 60.0, altM / 60.0);
                var plan = controller.CreatePlan();
                if (controller.RunawayDetected) {
                    return new ReplayResult {
                        Outcome = ReplayOutcome.HonestHalt, Cycles = cycle,
                        TrueFinalErrorArcmin = rig.TrueTotalArcmin, VerificationRan = verificationRan, Reason = plan.Reason
                    };
                }
                if (!plan.HasMovement) {
                    lastCmdMag = 0;
                    continue;
                }

                // Production sets these before executing; a failed move still counts as
                // a commanded cycle for the monitor (TPAPAVM.MoveCloser order).
                lastCmdMag = Math.Max(Math.Abs(plan.XMagnitude), Math.Abs(plan.YMagnitude));
                moved = true;

                var linkDead = cycle >= conditions.MovesFailFromCycle
                            && cycle < conditions.MovesFailFromCycle + conditions.MovesFailForCycles;
                if (linkDead) {
                    // Mirror of MoveCloser's failure wiring and the rc17.4 pause.
                    controller.NoteFailedExecution();
                    if (controller.ExecutionUnresponsive) {
                        controller.ResetExecutionFailureStreak();
                        return new ReplayResult {
                            Outcome = ReplayOutcome.HonestHalt, Cycles = cycle,
                            TrueFinalErrorArcmin = rig.TrueTotalArcmin, VerificationRan = verificationRan,
                            Reason = "automated adjustments paused: hardware not responding (3 consecutive failed moves)"
                        };
                    }
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
            }

            return new ReplayResult { Outcome = ReplayOutcome.TimedOut, Cycles = 120, TrueFinalErrorArcmin = rig.TrueTotalArcmin, VerificationRan = verificationRan, Reason = "no decision in 120 cycles" };
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
            // commanded amount uphill but fine downhill (2:1 reduction, back-drivable
            // screw), under ~50' of play. No software can align that axis - the required
            // outcome is an HONEST one.
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
            // The rc14 class of rig: large, direction-dependent play. The calibration
            // measures it, the planner compensates it, the loop converges.
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

        // ----- Sidereal drift during calibration (rc17.1) -----

        [Test]
        public async Task SiderealDrift_LegacyPerSolveEpochs_CreateThePhantomResidual_TheFrozenEpochDoesNot() {
            // The rc17.1 finding, replayed: a tracked field near the pole drifts in
            // Alt/Az while the platform stands still. Per-solve epoch transforms hand
            // that rotation to the displacement math (gilas saw ~5' of phantom closing
            // residual on a slow calibration; Valo 0.57' on a fast one). The frozen
            // epoch keeps it out of the measurement entirely.
            var legacyRig = new FieldRig { NoiseAmplitudeArcmin = 0.05 };
            legacyRig.Y.DeadbandArcmin = 5; legacyRig.Y.InitEngagement();
            var legacy = await CalibrateAndApply(legacyRig, legacyRig.Y, driftArcminPerSolve: 0.35, epochFrozen: false);

            var frozenRig = new FieldRig { NoiseAmplitudeArcmin = 0.05 };
            frozenRig.Y.DeadbandArcmin = 5; frozenRig.Y.InitEngagement();
            var frozen = await CalibrateAndApply(frozenRig, frozenRig.Y, driftArcminPerSolve: 0.35, epochFrozen: true);

            // The legacy deception is physical, not visible in the report: the closing
            // loop chases a DRIFTING baseline, so it drives the axis away from its true
            // origin while the measured residual looks small and "restored" reads true.
            Math.Abs(legacyRig.Y.Physical).Should().BeGreaterThan(1.5,
                "chasing per-solve epochs physically displaces the axis by the leaked drift while claiming restoration");
            Math.Abs(frozenRig.Y.Physical).Should().BeLessThan(0.5,
                "with the epoch frozen the axis really returns to its origin");
            Math.Abs(frozen.Outcome.ClosingResidualArcmin).Should().BeLessThan(0.5f);
        }

        // ----- cos(field azimuth) projection (rc16) -----

        [Test]
        public async Task FieldFarFromMeridian_ProjectionIsUndone_AndTheLoopConverges() {
            // The rc16 geometry: with the camera 55 degrees from the meridian, the
            // altitude actuator's tilt shows in the field only as cos(55) = 0.57 of
            // itself. The calibration must undo the projection (not bake 1/cos into the
            // factor) and the loop - whose altitude moves are projected the same way -
            // must still converge.
            var rig = new FieldRig { AzErrArcmin = 40, AltErrArcmin = -35, NoiseAmplitudeArcmin = 0.08, FieldAzimuthDegrees = 55 };
            rig.X.DeadbandArcmin = 5; rig.Y.DeadbandArcmin = 8;
            rig.X.InitEngagement(); rig.Y.InitEngagement();

            var x = await CalibrateAndApply(rig, rig.X);
            var y = await CalibrateAndApply(rig, rig.Y);

            rig.Y.ResponseFwd.Should().BeApproximately(1.0, 0.15,
                "the factor must describe the AXIS, not the projected view of it - a 1/cos-inflated factor was the rc16 field bug");
            var result = RunAlignment(rig, x, y);
            result.Outcome.Should().Be(ReplayOutcome.Converged, result.Reason);
        }

        // ----- Variable stick-slip (rc16.1) -----

        [Test]
        public async Task ValoStickSlip_ModerateSplitVariation_ConvergesOnTheMean() {
            // Valo's slip signature: the round-trip play is conserved while the split
            // between crossings varies (4.10/4.31 one pass, 0.00/8.69 another). At the
            // moderate per-crossing variation his fine phase actually showed, the
            // mean-based compensation stays unbiased over pairs and the loop converges -
            // his rc16.1 field nights ended at 16-17 arcseconds.
            var rig = new FieldRig { AzErrArcmin = -80, AltErrArcmin = 25, NoiseAmplitudeArcmin = 0.08 };
            rig.X.DeadbandArcmin = 4.2; rig.X.DeadbandVariationArcmin = 1.5;
            rig.Y.DeadbandArcmin = 4.2; rig.Y.DeadbandVariationArcmin = 1.5;
            rig.X.InitEngagement(); rig.Y.InitEngagement();

            var x = await CalibrateAndApply(rig, rig.X);
            var y = await CalibrateAndApply(rig, rig.Y);
            var result = RunAlignment(rig, x, y);

            result.Outcome.Should().Be(ReplayOutcome.Converged,
                $"{result.Reason} (true error {result.TrueFinalErrorArcmin:F2}')");
        }

        [Test]
        public async Task ExtremeStickSlip_FullSplitFlipEveryCrossing_EndsHonestly() {
            // TRUTH SURFACED BY THIS SIMULATOR (2026-08-13): when EVERY crossing can
            // cost anywhere from zero to the full round-trip play, mean compensation
            // injects up to ±play of error at every reversal - fine convergence is
            // physically impossible, and the monitor's halt ("backlash compensation is
            // likely wrong") is the CORRECT verdict, because it literally is, on every
            // single crossing. Converging on a lucky run is acceptable; lying is not.
            var rig = new FieldRig { AzErrArcmin = -80, AltErrArcmin = 25, NoiseAmplitudeArcmin = 0.08 };
            rig.X.DeadbandArcmin = 4.2; rig.X.DeadbandVariationArcmin = 4.2;
            rig.Y.DeadbandArcmin = 4.2; rig.Y.DeadbandVariationArcmin = 4.2;
            rig.X.InitEngagement(); rig.Y.InitEngagement();

            var x = await CalibrateAndApply(rig, rig.X);
            var y = await CalibrateAndApply(rig, rig.Y);
            var result = RunAlignment(rig, x, y);

            (result.Outcome == ReplayOutcome.Converged || result.Outcome == ReplayOutcome.HonestHalt).Should().BeTrue(
                $"a mechanically unrepeatable rig must converge or halt honestly, never lie or grind forever (got {result.Outcome}: {result.Reason})");
        }

        // ----- Estimate drift in the loop (rc8 / Kevin) -----

        [Test]
        public async Task EstimateDrift_FakingConvergence_IsCaughtByTheVerificationRun_OrHalted() {
            // Kevin's field night: the continuous estimator drifted, the display showed
            // 7 arcseconds while the true error was over 2 degrees. Here the measured
            // error drifts toward zero while the truth barely moves. Acceptable honest
            // endings: the drift detector halts, or the verification run's fresh
            // measurement exposes the lie and the loop re-corrects to TRUE convergence.
            // Forbidden: a final success with the true error still large.
            var rig = new FieldRig { AzErrArcmin = 25, AltErrArcmin = 35, NoiseAmplitudeArcmin = 0.08 };
            rig.X.DeadbandArcmin = 4; rig.Y.DeadbandArcmin = 6;
            rig.X.InitEngagement(); rig.Y.InitEngagement();

            var x = await CalibrateAndApply(rig, rig.X);
            var y = await CalibrateAndApply(rig, rig.Y);
            var result = RunAlignment(rig, x, y, new LoopConditions {
                EstimateDriftAzPerCycle = -0.35,   // pushes the measured error toward zero
                EstimateDriftAltPerCycle = -0.45
            });

            result.Outcome.Should().NotBe(ReplayOutcome.FalseSuccess,
                $"estimate drift must never survive to a reported success (true error {result.TrueFinalErrorArcmin:F1}', verification ran: {result.VerificationRan}, {result.Reason})");
            result.Outcome.Should().NotBe(ReplayOutcome.TimedOut, result.Reason);
        }

        // ----- Serial dropouts inside the loop (rc17.4) -----

        [Test]
        public async Task TransientSerialDropout_MidLoop_IsAbsorbed_AndTheLoopConverges() {
            // Two cycles of failed moves (the link recovery is still reopening the
            // port): within the three-strike budget, so the loop must absorb it and
            // finish the job.
            var rig = new FieldRig { AzErrArcmin = -50, AltErrArcmin = 30, NoiseAmplitudeArcmin = 0.08 };
            rig.X.DeadbandArcmin = 5; rig.Y.DeadbandArcmin = 7;
            rig.X.InitEngagement(); rig.Y.InitEngagement();

            var x = await CalibrateAndApply(rig, rig.X);
            var y = await CalibrateAndApply(rig, rig.Y);
            var result = RunAlignment(rig, x, y, new LoopConditions { MovesFailFromCycle = 8, MovesFailForCycles = 2 });

            result.Outcome.Should().Be(ReplayOutcome.Converged, result.Reason);
        }

        [Test]
        public async Task ControllerDiesMidLoop_ThreeStrikes_PauseHonestly() {
            // gilas 22:33: the USB adapter died for good mid-loop. The rc17.4 rule:
            // after three consecutive failed moves the corrections pause with a clear
            // reason - never an endless grind, never a claimed success.
            var rig = new FieldRig { AzErrArcmin = -50, AltErrArcmin = 30, NoiseAmplitudeArcmin = 0.08 };
            rig.X.DeadbandArcmin = 5; rig.Y.DeadbandArcmin = 7;
            rig.X.InitEngagement(); rig.Y.InitEngagement();

            var x = await CalibrateAndApply(rig, rig.X);
            var y = await CalibrateAndApply(rig, rig.Y);
            var result = RunAlignment(rig, x, y, new LoopConditions { MovesFailFromCycle = 8, MovesFailForCycles = 1000 });

            result.Outcome.Should().Be(ReplayOutcome.HonestHalt, result.Reason);
            result.Reason.Should().Contain("not responding");
        }
    }
}
