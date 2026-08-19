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

            /// <summary>
            /// Spread of the *polar error estimate* between consecutive solves. This is not the
            /// plate-solve noise the calibration measures: near the pole a small position error
            /// maps to a larger polar-alignment error, so the two differ by a lot. The 18/08 rig
            /// measured 0.01' of solve noise and wandered 0.083' in azimuth between consecutive
            /// readings with nothing but the altitude axis moving. Conflating the two is why that
            /// rig first looked healthy in this harness while the field log shows it collapsing.
            /// Defaults to the solve noise, so every scenario written before the distinction
            /// existed keeps its exact behaviour.
            /// </summary>
            public double EstimateJitterArcmin;

            /// <summary>What a single plate solve measures.</summary>
            public double Noise() => Sample(NoiseAmplitudeArcmin);

            /// <summary>What the correction loop reads, as opposed to what a solve measures.</summary>
            public double Jitter() => Sample(EstimateJitterArcmin > 0 ? EstimateJitterArcmin : NoiseAmplitudeArcmin);

            private double Sample(double amplitude) {
                if (amplitude <= 0) { return 0; }
                rng = rng * 1664525u + 1013904223u;
                return ((rng >> 8) / (double)(1 << 24) - 0.5) * 2 * amplitude;
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

        // ===== The rule against rigs the archive does not contain =====
        //
        // The recorded scenarios prove the play hysteresis on the twenty rigs that happened
        // to be logged. They cannot say whether it has a bad regime somewhere else, and it
        // did: with a play comparable to the whole misalignment the band covered the entire
        // run, every move was smaller than the play, and rigs converging at 0.5' ended 80'
        // out. That found the third bound (the rule stands down when the play is more than
        // half the error the run started with). This test keeps the search running.

        [Test]
        public void AcrossEightHundredSyntheticRigs_TheRuleIsNetPositive_AndNeverCatastrophic() {
            var rng = new Random(20260819);
            var plays = new[] { 0.5, 1.0, 2.0, 4.0, 8.0, 16.0, 32.0, 56.0 };
            var jitters = new[] { 0.01, 0.05, 0.1, 0.25, 0.5 };
            var initials = new[] { 5.0, 20.0, 60.0, 200.0, 400.0 };
            var modes = new[] { OapaBacklashMode.Full, OapaBacklashMode.Unidirectional };
            int convergedWithout = 0, convergedWith = 0, worse = 0, better = 0;
            double worstWithout = 0, worstWith = 0;

            foreach (var play in plays)
            foreach (var jitter in jitters)
            foreach (var initial in initials)
            foreach (var mode in modes)
            foreach (var trial in new[] { 0, 1 }) {
                var respFwd = 0.85 + rng.NextDouble() * 0.30;
                var respRev = 0.85 + rng.NextDouble() * 0.30;
                var noise = 0.005 + rng.NextDouble() * 0.10;
                var fieldAz = rng.NextDouble() * 55.0;
                var split = rng.NextDouble();
                var err = new double[2];
                for (var k = 0; k < 2; k++) {
                    var rig = new FieldRig {
                        AzErrArcmin = -initial * 0.9, AltErrArcmin = initial * 0.3,
                        NoiseAmplitudeArcmin = noise, EstimateJitterArcmin = jitter,
                        FieldAzimuthDegrees = fieldAz
                    };
                    rig.X.ResponseFwd = respFwd; rig.X.ResponseRev = respRev; rig.X.DeadbandArcmin = play;
                    rig.Y.ResponseFwd = respRev; rig.Y.ResponseRev = respFwd; rig.Y.DeadbandArcmin = play * 0.7;
                    rig.X.InitEngagement(); rig.Y.InitEngagement();
                    var x = new AppliedAxis { Mode = mode, BacklashPos = (float)(play * split * 2), BacklashNeg = (float)(play * (1 - split) * 2), PlayHysteresisMultiple = k == 0 ? 0 : 3 };
                    var y = new AppliedAxis { Mode = mode, BacklashPos = (float)(play * 0.7), BacklashNeg = (float)(play * 0.7), PlayHysteresisMultiple = k == 0 ? 0 : 3 };
                    var r = RunAlignment(rig, x, y, toleranceArcmin: 0.5);
                    err[k] = r.TrueFinalErrorArcmin;
                    if (r.Outcome == ReplayOutcome.Converged) { if (k == 0) { convergedWithout++; } else { convergedWith++; } }
                }
                worstWithout = Math.Max(worstWithout, err[0]);
                worstWith = Math.Max(worstWith, err[1]);
                if (err[1] > err[0] + 0.5 && err[1] > err[0] * 1.2) { worse++; }
                if (err[0] > err[1] + 0.5 && err[0] > err[1] * 1.2) { better++; }
            }

            TestContext.WriteLine($"converge {convergedWithout} -> {convergedWith}, peggiora {worse}, migliora {better}, " +
                $"peggior caso {worstWithout:F1}' -> {worstWith:F1}'");

            convergedWith.Should().BeGreaterThan(convergedWithout, "the rule has to earn its place off the archive too");
            better.Should().BeGreaterThan(2 * worse, "improvements must outweigh regressions by a wide margin");
            worstWith.Should().BeLessThan(worstWithout * 1.05, "no rig may be made catastrophically worse");
        }

        // ----- Calibrate + Apply, the way the panel does it -----

        private sealed class AppliedAxis {
            public AxisCalibrationOutcome Outcome;
            public OapaBacklashMode Mode;
            public float BacklashPos;
            public float BacklashNeg;
            /// <summary>
            /// What the VM supplies in production: the calibration's own detection threshold,
            /// below which a reversal cannot be honoured. Zero unless a scenario sets it, so
            /// the scenarios written before it existed keep exercising what they were written for.
            /// </summary>
            public float MinimumReversalArcmin;

            /// <summary>
            /// Play hysteresis, mirroring UniversalPolarAlignmentOAPAVM: below the band the
            /// axis stops compensating so its own play absorbs reversals whose direction comes
            /// from estimate jitter. Defaults to the shipped 3x so the suite exercises what
            /// ships; a scenario that documents a defect in the shared controller turns it off
            /// explicitly, and says why.
            /// </summary>
            public double PlayHysteresisMultiple = 3.0;

            /// <summary>
            /// The production definition, called rather than copied: this suite is the whole
            /// evidence that the rule works, and it stops being evidence the moment the two
            /// can drift apart.
            /// </summary>
            public double BandArcmin(double correctionCeilingArcmin, double runStartedAtArcmin) =>
                UniversalPolarAlignmentOAPAVM.PlayHysteresisBand(
                    Math.Max(BacklashPos, BacklashNeg), PlayHysteresisMultiple,
                    correctionCeilingArcmin, runStartedAtArcmin);

            /// <summary>What the planner is handed once the band is taken into account.</summary>
            public (OapaBacklashMode mode, float pos, float neg, float floor) AsPlanned(double measuredTotalError, double ceiling, double largestSeen)
                => measuredTotalError < BandArcmin(ceiling, largestSeen)
                    ? (OapaBacklashMode.Off, 0f, 0f, 0f)
                    : (Mode, BacklashPos, BacklashNeg, MinimumReversalArcmin);
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
            double highestSeen = 0;

            for (var cycle = 1; cycle <= 120; cycle++) {
                driftAz += conditions.EstimateDriftAzPerCycle;
                driftAlt += conditions.EstimateDriftAltPerCycle;
                var azM = rig.AzErrArcmin + driftAz + rig.Jitter();
                var altM = rig.AltErrArcmin + driftAlt + rig.Jitter();
                var total = Math.Sqrt(azM * azM + altM * altM);
                if (highestSeen <= 0) { highestSeen = total; }   // the error this run started with

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
                if (decision.Action == ConvergenceAction.HaltEstimateDrift && !verificationRan) {
                    // Production does not stop here. A drift halt means the estimate is no longer
                    // trustworthy, which is exactly what a fresh three-point measurement fixes, so
                    // with AutoVerificationRun on - the beta default, and what every field session
                    // in this suite ran with - the instruction hands over to the verification run
                    // instead of pausing (Instructions/PolarAlignment.cs:685). That run re-activates
                    // the first step, which resets the controller (TPAPAVM.cs:112): a fresh
                    // determination is a fresh identification problem, learned response included.
                    // Valo's 18/08 session took this path twice, finishing at 0.34' and 0.16'.
                    verificationRan = true;
                    driftAz = 0; driftAlt = 0;
                    monitor = new ConvergenceMonitor(toleranceArcmin);
                    first = true;
                    lastCmdMag = 0;
                    controller.Reset();
                    continue;
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
                    var px = x.AsPlanned(total, 30.0, highestSeen);
                    foreach (var leg in BacklashModePlanner.PlanMoves(px.mode, (float)plan.XMagnitude, px.pos, px.neg, lastX, px.floor)) {
                        rig.MoveX(leg);
                        if (Math.Abs(leg) > 0) { lastX = leg >= 0 ? LastDirection.Positive : LastDirection.Negative; }
                    }
                }
                if (Math.Abs(plan.YMagnitude) > 0) {
                    var py = y.AsPlanned(total, 30.0, highestSeen);
                    foreach (var leg in BacklashModePlanner.PlanMoves(py.mode, (float)plan.YMagnitude, py.pos, py.neg, lastY, py.floor)) {
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

        // ----- Scenarios grounded in the raw log archive (Desktop/oapa_logs) -----

        [Test]
        public async Task GilasReal_20260804_FactorTenTimesOff_RecoversInOnePass_AndConverges() {
            // gilas_20260804-212914: X measured responses fwd=9.907/rev=9.668 - the
            // stored factor was TEN times off - with ~4.2' of play, noise 0.01'. The
            // real night recovered ratio 10.22 in a single pass and the alignment
            // finished at 0.39' (21 arcseconds). The replay must do the same.
            var rig = new FieldRig { AzErrArcmin = 15.5, AltErrArcmin = -34.6, NoiseAmplitudeArcmin = 0.05 };
            rig.X.ResponseFwd = 9.907; rig.X.ResponseRev = 9.668; rig.X.DeadbandArcmin = 4.2;
            rig.Y.ResponseFwd = 1.53; rig.Y.ResponseRev = 1.85; rig.Y.DeadbandArcmin = 39;
            rig.X.InitEngagement(); rig.Y.InitEngagement();

            var x = await CalibrateAndApply(rig, rig.X);
            rig.X.ResponseFwd.Should().BeApproximately(1.0, 0.1, "the 10x factor error must be recovered in one pass, as the real night did");

            var y = await CalibrateAndApply(rig, rig.Y);
            var result = RunAlignment(rig, x, y);
            result.Outcome.Should().NotBe(ReplayOutcome.FalseSuccess, result.Reason);
            result.Outcome.Should().NotBe(ReplayOutcome.TimedOut, $"true error stuck at {result.TrueFinalErrorArcmin:F2}'");
        }

        [Test]
        public async Task ValoReal_20260806_SixteenDegreesOff_Converges() {
            // valo_20260806-185035: initial error 16 degrees 33 arcmin (az -16.5 deg),
            // healthy responses, moderate play. The real night finished at 0.15'. The
            // largest initial error ever recorded in the archive - the loop must walk
            // all of it down within the per-cycle correction cap.
            var rig = new FieldRig { AzErrArcmin = -990, AltErrArcmin = 76.7, NoiseAmplitudeArcmin = 0.05 };
            rig.X.ResponseFwd = 1.02; rig.X.ResponseRev = 0.97; rig.X.DeadbandArcmin = 3.3;
            rig.Y.ResponseFwd = 1.10; rig.Y.ResponseRev = 1.05; rig.Y.DeadbandArcmin = 7.0;
            rig.X.InitEngagement(); rig.Y.InitEngagement();

            var x = await CalibrateAndApply(rig, rig.X);
            var y = await CalibrateAndApply(rig, rig.Y);
            var result = RunAlignment(rig, x, y);

            result.Outcome.Should().Be(ReplayOutcome.Converged,
                $"{result.Reason} (true error {result.TrueFinalErrorArcmin:F2}' after {result.Cycles} cycles)");
        }

        [Test]
        public async Task StrGazerReal_20260816_WithoutEstimatorDrift_TheFinePhaseSettles() {
            // strgazer_20260816-222942: EQM-35 + OnStep, 2 deg 55' off, altitude carrying its
            // load against gravity with the pair the calibration measured that night. With a
            // stable estimate the loop walks it down and stops - which is the control case for
            // the scenario below, and says the mechanism itself was never the obstacle.
            var rig = StrGazer20260816();
            var x = await CalibrateAndApply(rig, rig.X);
            var y = await CalibrateAndApply(rig, rig.Y);
            ApplyStrGazerAltitudePair(y, minimumReversalArcmin: 0f);

            var result = RunAlignment(rig, x, y);

            result.Outcome.Should().Be(ReplayOutcome.Converged, result.Reason);
            result.TrueFinalErrorArcmin.Should().BeLessThan(0.6, $"true error after {result.Cycles} cycles");
        }

        [TestCase(0f, TestName = "EstimatorDrift_FalseSuccess_WithoutTheReversalFloor")]
        [TestCase(0.35f, TestName = "EstimatorDrift_FalseSuccess_WithTheReversalFloorToo")]
        public async Task StrGazerReal_20260816_EstimatorDrift_EndsInAFalseSuccess(float minimumReversalArcmin) {
            // The same rig with the drift measured off that night's log: between two solves
            // with nothing moving, the reading worsened by ~0.14' four times in a row - about
            // 1.2' a minute at a 10s cycle, almost all of it in azimuth.
            //
            // Both parameter cases run identically on purpose. The reversal floor added in
            // rc17.7 stops an axis being pushed past its target by its own compensation, and
            // it does not rescue this: the drift is in the core's error model, on the other
            // side of the boundary, and no axis-side policy can see it. What the simulator
            // quantifies is the cost - the loop announces 0.11' and leaves the platform 2.4'
            // out - which is the evidence an issue against that estimator needs.
            var rig = StrGazer20260816();
            var x = await CalibrateAndApply(rig, rig.X);
            var y = await CalibrateAndApply(rig, rig.Y);
            ApplyStrGazerAltitudePair(y, minimumReversalArcmin);
            // Play hysteresis off: this scenario documents what the estimator does on its own.
            // The companion test below shows what the hysteresis changes about the outcome.
            x.PlayHysteresisMultiple = 0; y.PlayHysteresisMultiple = 0;

            var result = RunAlignment(rig, x, y, new LoopConditions { EstimateDriftAzPerCycle = 0.2 });

            result.Outcome.Should().Be(ReplayOutcome.FalseSuccess,
                $"{result.Reason} (true error {result.TrueFinalErrorArcmin:F2}' after {result.Cycles} cycles)");
            result.TrueFinalErrorArcmin.Should().BeGreaterThan(2.0,
                "the announced figure and the truth part company by several times the tolerance");
        }

        [Test]
        public async Task StrGazerReal_20260816_EstimatorDrift_WithThePlayHysteresis_StopsBeingALie() {
            // Same drift, same rig, shipped defaults. The hysteresis cannot see the drift and
            // does not correct it - the platform still ends several arcminutes out. What it
            // removes is the *claim*: without the jitter-driven reversals the loop no longer
            // walks itself into a state where its own figure looks converged, so the run ends
            // on an honest halt the user can act on instead of a success that is not one.
            var rig = StrGazer20260816();
            var x = await CalibrateAndApply(rig, rig.X);
            var y = await CalibrateAndApply(rig, rig.Y);
            ApplyStrGazerAltitudePair(y, 0.35f);

            var result = RunAlignment(rig, x, y, new LoopConditions { EstimateDriftAzPerCycle = 0.2 });

            result.Outcome.Should().NotBe(ReplayOutcome.FalseSuccess, result.Reason);
            result.Outcome.Should().Be(ReplayOutcome.HonestHalt,
                $"true error {result.TrueFinalErrorArcmin:F2}' after {result.Cycles} cycles");
        }

        [Test]
        public async Task ValoReal_20260818_TheOneArcminuteProbe_ThrowsAwayAConvergedAlignment() {
            // valo_20260818-201256, run 4 (and run 3 before it, identically): the cleanest rig in
            // the archive - calibration repeatable to 0.15%, 0.01' of solve noise - walked 6 deg 50'
            // of error down to 22 arcseconds and then threw it away in three moves.
            //
            // The chain, verified in the controller: a 22"->26" reading is a 18% worsening, which
            // is above ModelResetWorseningFactor, so the learned response is discarded; without it
            // the controller falls back to a probe; DefaultProbeMagnitude scales with the error only
            // upwards, so below 6.7' of error the probe is always 1'; and 1' against 0.2' of
            // remaining error overshoots by five times. The companion test below shows what turns
            // that overshoot into a collapse.
            var rig = Valo20260818();
            var (x, y) = await CalibrateAndApplyValo(rig, compensating: true);
            // Play hysteresis off: this is the reproduction of the controller's probe floor,
            // and it has to keep failing for as long as that floor is there. The mitigation
            // is the test below.
            x.PlayHysteresisMultiple = 0; y.PlayHysteresisMultiple = 0;

            var result = RunAlignment(rig, x, y, toleranceArcmin: 0.2);

            result.Outcome.Should().Be(ReplayOutcome.HonestHalt,
                $"the field session halted the same way ({result.Reason})");
            result.TrueFinalErrorArcmin.Should().BeGreaterThan(1.0,
                $"an alignment that had reached 22 arcseconds ends far outside tolerance ({result.Cycles} cycles)");
        }

        [Test]
        public async Task ValoReal_20260818_ThePlayHysteresis_MakesTheProbeHarmless() {
            // Shipped defaults on the same rig. The 1' probe still arrives, and the axis still
            // carries 2.27' of play - but inside the band the play is no longer cancelled, so
            // the probe is absorbed by the mechanism instead of being delivered faithfully to
            // an alignment that had reached 22 arcseconds. This is a filter, not a fix: the
            // probe floor is still wrong, and the issue against it stands.
            var rig = Valo20260818();
            var (x, y) = await CalibrateAndApplyValo(rig, compensating: true);

            var result = RunAlignment(rig, x, y, toleranceArcmin: 0.2);

            result.Outcome.Should().Be(ReplayOutcome.Converged, result.Reason);
            result.TrueFinalErrorArcmin.Should().BeLessThan(0.4, $"after {result.Cycles} cycles");
        }

        [Test]
        public async Task ValoReal_20260818_WithoutOurCompensation_TheSameProbeIsSurvivable() {
            // Same rig, same jitter, same 1' probe from the same controller - only our backlash
            // compensation removed. It converges. So the probe floor alone is survivable, and what
            // turns it into a collapse is OAPA's own compensation multiplying a 1' probe into 3.27'
            // of commanded motion.
            //
            // That is the whole argument for the issue, and it cuts both ways: half the fault is in
            // the core's probe floor, half is ours. A probe is an identification move; compensating
            // it corrupts the very sample it exists to produce - but IsProbe lives in the
            // controller's plan and never reaches the axis layer.
            var rig = Valo20260818();
            var (x, y) = await CalibrateAndApplyValo(rig, compensating: false);

            var result = RunAlignment(rig, x, y, toleranceArcmin: 0.2);

            result.Outcome.Should().Be(ReplayOutcome.Converged, result.Reason);
            result.TrueFinalErrorArcmin.Should().BeLessThan(0.4, $"after {result.Cycles} cycles");
        }

        /// <summary>
        /// The 18/08 rig: Az -6deg00'34", Alt +3deg16'13", responses within 1.5% of unity, and an
        /// estimate that wanders 0.083' between consecutive readings while its plate solves are
        /// clean to 0.01' - the distinction that decides whether this scenario reproduces at all.
        /// </summary>
        private static FieldRig Valo20260818() {
            var rig = new FieldRig {
                AzErrArcmin = -360.6,
                AltErrArcmin = 196.2,
                NoiseAmplitudeArcmin = 0.01,
                EstimateJitterArcmin = 0.083,
                FieldAzimuthDegrees = 14.3
            };
            rig.X.ResponseFwd = 0.987; rig.X.ResponseRev = 1.012; rig.X.DeadbandArcmin = 2.6; rig.X.DeadbandVariationArcmin = 0.6;
            rig.Y.ResponseFwd = 0.998; rig.Y.ResponseRev = 0.999; rig.Y.DeadbandArcmin = 2.27;
            rig.X.InitEngagement(); rig.Y.InitEngagement();
            return rig;
        }

        /// <summary>Applies that night's second calibration, with or without its compensation.</summary>
        private static async Task<(AppliedAxis x, AppliedAxis y)> CalibrateAndApplyValo(FieldRig rig, bool compensating) {
            var x = await CalibrateAndApply(rig, rig.X);
            var y = await CalibrateAndApply(rig, rig.Y);
            x.Mode = compensating ? OapaBacklashMode.Unidirectional : OapaBacklashMode.Off;
            y.Mode = compensating ? OapaBacklashMode.Full : OapaBacklashMode.Off;
            x.BacklashPos = compensating ? 2.05f : 0f;
            x.BacklashNeg = compensating ? 3.23f : 0f;
            y.BacklashPos = y.BacklashNeg = compensating ? 2.27f : 0f;
            // What the VM supplies in production: max(5 sigma, 0.25') on a rig whose solve noise is
            // 0.01'. It does not fire here - the harmful command is 1', well above it.
            x.MinimumReversalArcmin = y.MinimumReversalArcmin = 0.25f;
            return (x, y);
        }

        private static FieldRig StrGazer20260816() {
            var rig = new FieldRig { AzErrArcmin = 158.5, AltErrArcmin = 74, NoiseAmplitudeArcmin = 0.07 };
            rig.X.ResponseFwd = 1.05; rig.X.ResponseRev = 0.99; rig.X.DeadbandArcmin = 0.6;
            rig.Y.ResponseFwd = 0.99; rig.Y.ResponseRev = 1.06; rig.Y.DeadbandArcmin = 0.9;
            rig.X.InitEngagement(); rig.Y.InitEngagement();
            return rig;
        }

        /// <summary>The directional pair that night's calibration reported for the altitude axis.</summary>
        private static void ApplyStrGazerAltitudePair(AppliedAxis y, float minimumReversalArcmin) {
            y.Mode = OapaBacklashMode.Full;
            y.BacklashPos = 2.19f;
            y.BacklashNeg = 0.68f;
            y.MinimumReversalArcmin = minimumReversalArcmin;
        }

        [Test]
        public async Task AyhanReal_20260808_ResponseDoublePlusFiftyArcminPlay_EndsHonestly() {
            // aylan_20260808: X responses ~1.9 with 50-63' of measured play - the real
            // rig behind the synthetic 'grossly wrong factor' scenario. His night was
            // messy but honest: a first inconsistent pass, retries, four justified
            // halts, 4 degrees walked down to 13'. The replay accepts any honest
            // ending - recovery-and-convergence or a protective abort - never a lie.
            var rig = new FieldRig { AzErrArcmin = -256, AltErrArcmin = 43.5, NoiseAmplitudeArcmin = 0.08 };
            rig.X.ResponseFwd = 1.958; rig.X.ResponseRev = 1.783; rig.X.DeadbandArcmin = 56;
            rig.Y.ResponseFwd = 0.86; rig.Y.ResponseRev = 0.80; rig.Y.DeadbandArcmin = 5;
            rig.X.InitEngagement(); rig.Y.InitEngagement();

            AppliedAxis x;
            try {
                x = await CalibrateAndApply(rig, rig.X);
            } catch (InvalidOperationException ex) {
                ex.Message.Should().Contain("travel budget");
                return; // honest protective abort
            }
            var y = await CalibrateAndApply(rig, rig.Y);
            var result = RunAlignment(rig, x, y);

            result.Outcome.Should().NotBe(ReplayOutcome.FalseSuccess,
                $"(outcome {result.Outcome}, true error {result.TrueFinalErrorArcmin:F1}', {result.Reason})");
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
