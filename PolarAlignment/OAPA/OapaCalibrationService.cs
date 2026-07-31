using NINA.Core.Utility;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.OAPA {

    /// <summary>Motion boundary for the calibration: relative axis moves in axis arcminutes.</summary>
    public interface IOapaCalibrationMotion {
        Task MoveRelative(Axis axis, float arcmin, CancellationToken token);
    }

    /// <summary>Capture-and-solve boundary for the calibration.</summary>
    public interface IOapaCalibrationSolver {
        /// <summary>Captures an image and plate-solves it, retrying internally as configured.</summary>
        Task<CalibrationSolveSample> CaptureAndSolve(CancellationToken token);
    }

    /// <summary>Outcome of calibrating one axis, including the auto-reverse retry.</summary>
    public sealed class AxisCalibrationOutcome {
        public float Ratio { get; init; }
        public float BacklashArcmin { get; init; }
        public bool Consistent { get; init; }
        public bool Asymmetric { get; init; }
        public float NoiseSigmaArcmin { get; init; }
        public float ForwardRatio { get; init; }
        public float ReverseRatio { get; init; }
        public bool SlippageDetected { get; init; }
        /// <summary>True when the Reverse flag had to be flipped (and the flip verified) to obtain a consistent result.</summary>
        public bool Flipped { get; init; }
    }

    /// <summary>
    /// Orchestrates the OAPA self-calibration against injected motion and capture/solve
    /// boundaries. Owns no UI state: progress is reported through a callback and results
    /// are returned as typed values.
    ///
    /// The sequence is staged and self-scaling, so it survives grossly wrong initial
    /// ratios and backlash larger than its own measuring legs:
    ///  S0 solve noise (no motion) -> detection threshold;
    ///  S1 engagement probe, escalating until the motion is measurable;
    ///  S2 clean forward legs (post-engagement, backlash-free) -> forward response;
    ///  S3 backlash leg, escalated until the shortfall is a minority of the leg;
    ///  S4 clean reverse legs -> reverse response, asymmetry flag;
    ///  S5 opposite transition -> second backlash measure, slippage verdict;
    ///  S6 iterative close of the loop against the baseline solve.
    /// On failure or cancellation the restore is measured against the baseline too,
    /// falling back to the commanded sum only when solving is unavailable.
    /// </summary>
    public sealed class OapaCalibrationService {
        private readonly IOapaCalibrationMotion motion;
        private readonly IOapaCalibrationSolver solver;
        private readonly float calibrationStepArcmin;

        public OapaCalibrationService(IOapaCalibrationMotion motion, IOapaCalibrationSolver solver, float calibrationStepArcmin = 45.0f) {
            this.motion = motion;
            this.solver = solver;
            this.calibrationStepArcmin = calibrationStepArcmin;
        }

        /// <summary>
        /// Calibrates an axis. If the first pass shows direction inconsistency, retries once
        /// with the direction flipped; a passing retry reports <see cref="AxisCalibrationOutcome.Flipped"/>
        /// so the caller can persist the corrected Reverse flag.
        /// </summary>
        public async Task<AxisCalibrationOutcome> CalibrateAxisWithAutoReverse(
            Axis axis, float currentRatio, bool reversed,
            string axisLabel, Action<string> reportStatus, CancellationToken token) {

            var first = await CalibrateAxis(axis, currentRatio, reversed, axisLabel, reportStatus, token).ConfigureAwait(false);
            if (first.Consistent) {
                return ToOutcome(first, flipped: false);
            }

            Logger.Info($"OAPA cal {axisLabel}: direction inconsistent, retrying with Reverse flipped ({reversed} -> {!reversed})");
            reportStatus?.Invoke($"{axisLabel}: auto-flipping Reverse and retrying...");

            var second = await CalibrateAxis(axis, currentRatio, !reversed, axisLabel, reportStatus, token).ConfigureAwait(false);
            if (second.Consistent) {
                Logger.Info($"OAPA cal {axisLabel}: auto-flip succeeded, ratio={second.Ratio:F2}");
                return ToOutcome(second, flipped: true);
            }

            Logger.Warning($"OAPA cal {axisLabel}: auto-flip did not resolve inconsistency; keeping original Reverse={reversed}");
            return ToOutcome(first, flipped: false);
        }

        private static AxisCalibrationOutcome ToOutcome(AxisCalibrationResult r, bool flipped) => new() {
            Ratio = r.Ratio,
            BacklashArcmin = r.BacklashArcmin,
            Consistent = r.Consistent,
            Asymmetric = r.Asymmetric,
            NoiseSigmaArcmin = r.NoiseSigmaArcmin,
            ForwardRatio = r.ForwardRatio,
            ReverseRatio = r.ReverseRatio,
            SlippageDetected = r.SlippageDetected,
            Flipped = flipped
        };

        private async Task<AxisCalibrationResult> CalibrateAxis(
            Axis axis, float currentRatio, bool reversed,
            string axisLabel, Action<string> reportStatus, CancellationToken token) {

            bool isAzimuth = axis == Axis.XAxis;
            float dirSign = reversed ? -1f : 1f;
            int solveCount = 0;
            float movedArcmin = 0f;
            CalibrationSolveSample last = default;

            async Task<CalibrationSolveSample> NextSolve() {
                if (++solveCount > MaxSolvesPerAxis) {
                    throw new InvalidOperationException(
                        $"{axisLabel}: calibration exceeded its solve budget ({MaxSolvesPerAxis} solves); aborting to limit the sky excursion");
                }
                var s = await solver.CaptureAndSolve(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return s;
            }

            // Moves by a positive logical amount in the given logical direction and returns
            // the signed physical displacement measured by the next solve.
            async Task<double> MoveAndMeasure(float logicalArcmin, float logicalDirection) {
                var wire = dirSign * logicalDirection * logicalArcmin;
                await motion.MoveRelative(axis, wire, token).ConfigureAwait(false);
                movedArcmin += wire;
                var now = await NextSolve().ConfigureAwait(false);
                var d = OapaCalibrationGeometry.SignedAxisDisplacementArcmin(isAzimuth, last, now);
                last = now;
                return d;
            }

            // S0: solve noise with the axis at rest; everything below detects motion
            // against a threshold derived from it (fail fast on an unsolvable field too).
            reportStatus?.Invoke($"{axisLabel}: measuring solve noise...");
            var s0a = await NextSolve().ConfigureAwait(false);
            var baseline = await NextSolve().ConfigureAwait(false);
            var noise = Math.Abs(OapaCalibrationGeometry.SignedAxisDisplacementArcmin(isAzimuth, s0a, baseline));
            var threshold = Math.Max(NoiseSigmaFactor * noise, DetectionFloorArcmin);
            last = baseline;

            if (isAzimuth && Math.Cos(baseline.AltitudeDegrees * Math.PI / 180.0) < OapaCalibrationGeometry.MinimumAzimuthCosAltitude) {
                throw new InvalidOperationException(
                    $"{axisLabel}: field altitude {baseline.AltitudeDegrees:F0}° is too close to the zenith for azimuth calibration. " +
                    "Point the scope at a lower altitude (ideally toward the celestial pole) and retry.");
            }

            try {
                // S1: engage the drive train and find the rough scale. The probe escalates
                // until the measured motion clears the noise threshold.
                float probe = InitialProbeArcmin;
                double roughResponse = 0;
                var engaged = false;
                for (var attempt = 0; attempt < MaxEngageAttempts; attempt++) {
                    reportStatus?.Invoke($"{axisLabel}: probing +{probe:F0}'...");
                    var d = await MoveAndMeasure(probe, +1f).ConfigureAwait(false);
                    if (Math.Abs(d) >= threshold) {
                        roughResponse = Math.Abs(d) / probe;
                        engaged = true;
                        break;
                    }
                    Logger.Info($"OAPA cal {axisLabel}: probe {probe:F0}' moved only {d:F2}' (threshold {threshold:F2}'), escalating");
                    probe *= EngageEscalationFactor;
                }
                if (!engaged) {
                    throw new InvalidOperationException($"{axisLabel}: axis did not move measurably; check clutch and motor current");
                }

                // S2: clean forward legs. Post-engagement, same direction: backlash-free by
                // construction. The leg size targets a fixed physical displacement so the
                // measurement scales itself to whatever the current ratio error is.
                var legLogical = (float)Math.Max(1.0, TargetCleanLegPhysicalArcmin / roughResponse);
                reportStatus?.Invoke($"{axisLabel}: forward legs ({legLogical:F0}')...");
                var beforeLeg = last;
                var f1 = await MoveAndMeasure(legLogical, +1f).ConfigureAwait(false);
                var directionConsistent = OapaCalibrationGeometry.SignedDisplacementMatchesCommand(isAzimuth, beforeLeg, last, legLogical);
                var f2 = await MoveAndMeasure(legLogical, +1f).ConfigureAwait(false);
                double forwardResponse;
                var spread = Math.Abs(Math.Abs(f1) - Math.Abs(f2)) / Math.Max(Math.Abs(f1), Math.Abs(f2));
                if (spread > CleanLegSpreadThreshold) {
                    Logger.Info($"OAPA cal {axisLabel}: forward legs spread {spread:P0}, adding a third leg");
                    var f3 = await MoveAndMeasure(legLogical, +1f).ConfigureAwait(false);
                    forwardResponse = Median(Math.Abs(f1), Math.Abs(f2), Math.Abs(f3)) / legLogical;
                } else {
                    forwardResponse = (Math.Abs(f1) + Math.Abs(f2)) / 2.0 / legLogical;
                }

                // S3: backlash leg with escalation. A reversal that comes up short by more
                // than half its own expected travel was mostly eaten by the backlash: the
                // measure is contaminated, so the leg grows and the transition is repeated
                // until the shortfall is a minority share (this is what survives backlash
                // larger than the initial leg).
                var backlashLeg = legLogical;
                double reversalTravel = 0;
                for (var i = 0; ; i++) {
                    reportStatus?.Invoke($"{axisLabel}: reversal leg -{backlashLeg:F0}'...");
                    var d = await MoveAndMeasure(backlashLeg, -1f).ConfigureAwait(false);
                    reversalTravel = Math.Abs(d);
                    var expected = backlashLeg * forwardResponse;
                    var shortfall = Math.Max(0, expected - reversalTravel);
                    if (shortfall <= BacklashLegFraction * expected) {
                        break;
                    }
                    if (i >= MaxBacklashEscalations - 1) {
                        Logger.Warning($"OAPA cal {axisLabel}: backlash still dominates the {backlashLeg:F0}' leg after escalation; the value may be underestimated");
                        break;
                    }
                    var nextLeg = (float)(backlashLeg + 2.0 * shortfall / forwardResponse);
                    nextLeg = (float)Math.Min(nextLeg, MaxLegPhysicalArcmin / forwardResponse);
                    Logger.Info($"OAPA cal {axisLabel}: reversal lost {shortfall:F1}' of {expected:F1}'; escalating the backlash leg to {nextLeg:F0}'");
                    reportStatus?.Invoke($"{axisLabel}: backlash exceeds the leg, re-measuring at {nextLeg:F0}'...");
                    await MoveAndMeasure(nextLeg, +1f).ConfigureAwait(false); // re-engage forward; not a clean sample
                    backlashLeg = nextLeg;
                }

                // S4: clean reverse legs (engaged reverse after S3).
                reportStatus?.Invoke($"{axisLabel}: reverse legs ({legLogical:F0}')...");
                var r1 = await MoveAndMeasure(legLogical, -1f).ConfigureAwait(false);
                var r2 = await MoveAndMeasure(legLogical, -1f).ConfigureAwait(false);
                var reverseResponse = (Math.Abs(r1) + Math.Abs(r2)) / 2.0 / legLogical;

                // The backlash transitions are evaluated against the response of the
                // direction the axis was travelling toward, so a direction asymmetry does
                // not masquerade as backlash (or as slippage).
                var backlashForward = Math.Max(0, backlashLeg * reverseResponse - reversalTravel);

                // S5: opposite transition for the repeatability verdict.
                reportStatus?.Invoke($"{axisLabel}: verifying backlash repeatability...");
                var dBack = await MoveAndMeasure(backlashLeg, +1f).ConfigureAwait(false);
                var backlashReverse = Math.Max(0, backlashLeg * forwardResponse - Math.Abs(dBack));

                double backlash;
                var slippage = false;
                var significant = Math.Max(backlashForward, backlashReverse);
                if (significant < 2 * threshold) {
                    backlash = 0; // both transitions indistinguishable from noise
                } else {
                    slippage = Math.Abs(backlashForward - backlashReverse) > Math.Max(SlippageRelativeThreshold * significant, 2 * threshold);
                    backlash = (backlashForward + backlashReverse) / 2.0;
                }

                var meanResponse = (forwardResponse + reverseResponse) / 2.0;
                var asymmetric = Math.Abs(forwardResponse - reverseResponse) / Math.Max(forwardResponse, reverseResponse) > AsymmetryFlagThreshold;

                var result = new AxisCalibrationResult {
                    Ratio = (float)(currentRatio / meanResponse),
                    ForwardRatio = (float)(currentRatio / forwardResponse),
                    ReverseRatio = (float)(currentRatio / reverseResponse),
                    BacklashArcmin = (float)backlash,
                    NoiseSigmaArcmin = (float)noise,
                    Consistent = directionConsistent,
                    Asymmetric = asymmetric,
                    SlippageDetected = slippage
                };

                Logger.Info($"OAPA cal {axisLabel}: noise={noise:F2}', responses fwd={forwardResponse:F3}/rev={reverseResponse:F3} '/unit, " +
                    $"backlash={backlashForward:F2}'/{backlashReverse:F2}' -> {backlash:F2}', ratio={result.Ratio:F2}, " +
                    $"consistent={result.Consistent}, asymmetric={result.Asymmetric}, slippage={result.SlippageDetected}, solves={solveCount}");

                // S6: physically return to the baseline. The response just measured makes
                // the closing moves exact; iterating covers the backlash a closing reversal
                // eats on its first move.
                var responsePerWire = f1 / (dirSign * legLogical);
                await CloseLoopAgainstBaseline(axis, isAzimuth, baseline, responsePerWire, axisLabel, reportStatus, NextSolve, token).ConfigureAwait(false);

                return result;
            } catch (Exception) when (movedArcmin != 0f) {
                await BestEffortRestore(axis, isAzimuth, baseline, movedArcmin, axisLabel).ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>
        /// Drives the measured residual against the baseline back to zero, up to
        /// <see cref="MaxClosingIterations"/> moves: the first closing reversal loses the
        /// backlash, the following iteration completes the travel. A failed closing move
        /// never discards the calibration result.
        /// </summary>
        private async Task CloseLoopAgainstBaseline(
            Axis axis, bool isAzimuth, CalibrationSolveSample baseline, double responsePerWire,
            string axisLabel, Action<string> reportStatus, Func<Task<CalibrationSolveSample>> nextSolve, CancellationToken token) {

            if (Math.Abs(responsePerWire) < 1e-3) { return; }
            try {
                var current = await nextSolve().ConfigureAwait(false);
                for (var i = 0; i < MaxClosingIterations; i++) {
                    var residual = OapaCalibrationGeometry.SignedAxisDisplacementArcmin(isAzimuth, baseline, current);
                    if (Math.Abs(residual) < RestoreToleranceArcmin) {
                        Logger.Info($"OAPA cal {axisLabel}: closed loop against baseline; residual {residual:F2}'");
                        return;
                    }
                    var closing = (float)Math.Clamp(-residual / responsePerWire, -3.0 * calibrationStepArcmin, 3.0 * calibrationStepArcmin);
                    reportStatus?.Invoke($"{axisLabel}: returning to start ({residual:+0.0;-0.0}' off)...");
                    await motion.MoveRelative(axis, closing, token).ConfigureAwait(false);
                    current = await nextSolve().ConfigureAwait(false);
                }
                var final = OapaCalibrationGeometry.SignedAxisDisplacementArcmin(isAzimuth, baseline, current);
                Logger.Info($"OAPA cal {axisLabel}: closing iterations exhausted; residual {final:F2}'");
            } catch (Exception ex) {
                // The calibration result is already measured; a failed closing move must not discard it.
                Logger.Warning($"OAPA cal {axisLabel}: failed to close the loop against the baseline ({ex.Message})");
            }
        }

        /// <summary>
        /// Restore after a mid-sequence failure or cancellation. Driving back the commanded
        /// sum is blind to backlash, so this measures the residual against the baseline and
        /// drives it back, iterating so a restore reversal that loses its backlash still
        /// completes. Only when the solve itself is unavailable - often the reason the
        /// sequence failed - does it fall back to the commanded-sum restore.
        /// </summary>
        private async Task BestEffortRestore(Axis axis, bool isAzimuth, CalibrationSolveSample baseline, float movedArcmin, string axisLabel) {
            try {
                var cap = 3f * calibrationStepArcmin;
                for (var i = 0; i < MaxClosingIterations; i++) {
                    var current = await solver.CaptureAndSolve(CancellationToken.None).ConfigureAwait(false);
                    var residual = OapaCalibrationGeometry.SignedAxisDisplacementArcmin(isAzimuth, baseline, current);
                    if (Math.Abs(residual) < RestoreToleranceArcmin) { return; }
                    var restore = (float)Math.Clamp(-residual, -cap, cap);
                    Logger.Info($"OAPA cal {axisLabel}: failure with {movedArcmin:F1}' commanded outstanding; measured {residual:F1}' from baseline, driving back");
                    await motion.MoveRelative(axis, restore, CancellationToken.None).ConfigureAwait(false);
                }
            } catch (Exception measureEx) {
                Logger.Warning($"OAPA cal {axisLabel}: measured restore unavailable ({measureEx.Message}); driving back the commanded sum");
                try {
                    await motion.MoveRelative(axis, -movedArcmin, CancellationToken.None).ConfigureAwait(false);
                } catch (Exception restoreEx) {
                    Logger.Error($"OAPA cal {axisLabel}: failed to restore start position", restoreEx);
                }
            }
        }

        private static double Median(double a, double b, double c) {
            return Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
        }

        /// <summary>First engagement probe, in logical arcminutes.</summary>
        private const float InitialProbeArcmin = 5.0f;
        /// <summary>Probe growth factor while the motion stays below the detection threshold.</summary>
        private const float EngageEscalationFactor = 3.0f;
        /// <summary>Probe attempts before declaring the axis immobile (5', 15', 45', 135').</summary>
        private const int MaxEngageAttempts = 4;
        /// <summary>Motion detection threshold is this many times the measured solve noise.</summary>
        private const double NoiseSigmaFactor = 5.0;
        /// <summary>Detection floor when the two noise solves happen to agree, in arcminutes.</summary>
        private const double DetectionFloorArcmin = 0.25;
        /// <summary>Physical size the clean measuring legs aim for, in axis arcminutes.</summary>
        private const double TargetCleanLegPhysicalArcmin = 8.0;
        /// <summary>Clean-leg disagreement above which a third leg and the median are used.</summary>
        private const double CleanLegSpreadThreshold = 0.10;
        /// <summary>A reversal shortfall above this share of the expected travel means the leg was mostly backlash: escalate.</summary>
        private const double BacklashLegFraction = 0.5;
        /// <summary>Maximum backlash-leg escalations before accepting the measure with a warning.</summary>
        private const int MaxBacklashEscalations = 3;
        /// <summary>Forward/reverse response disagreement above which the axis is flagged asymmetric.</summary>
        private const double AsymmetryFlagThreshold = 0.10;
        /// <summary>Backlash-transition disagreement share above which the mechanics are declared non-repeatable.</summary>
        private const double SlippageRelativeThreshold = 0.20;
        /// <summary>Hard solve budget per axis pass; exceeded means something is off and the sequence aborts honestly.</summary>
        private const int MaxSolvesPerAxis = 20;
        /// <summary>Physical cap for a single escalated leg, in axis arcminutes (sky excursion guard).</summary>
        private const double MaxLegPhysicalArcmin = 90.0;
        /// <summary>Closing/restore iterations: one reversal may eat its backlash, the next completes.</summary>
        private const int MaxClosingIterations = 3;

        /// <summary>Residuals below this are indistinguishable from solve noise and left alone.</summary>
        private const float RestoreToleranceArcmin = 0.5f;
    }
}
