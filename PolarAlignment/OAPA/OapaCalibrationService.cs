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

        /// <summary>
        /// Marks the start of a calibration pass so the solver can freeze its topocentric
        /// reference epoch. A tracked field keeps its RA/Dec while its Alt/Az drifts with
        /// sidereal time, so samples transformed each at their own wall-clock time alias
        /// sky rotation into platform motion — the displacement comparisons only mean
        /// "axis motion" when every sample of a pass is expressed at one common epoch.
        /// Default is a no-op for solvers that are time-invariant already (test fakes).
        /// </summary>
        void BeginCalibration() { }
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
        public float BacklashEnteringPositiveArcmin { get; init; }
        public float BacklashEnteringNegativeArcmin { get; init; }
        public bool DirectionalBacklash { get; init; }
        /// <summary>True when the backlash pair was not measurable and both directions were zeroed.</summary>
        public bool BacklashSuspect { get; init; }
        /// <summary>True when the forward and reverse responses disagree by more than a factor of two.</summary>
        public bool ResponseSuspect { get; init; }
        /// <summary>True when the Reverse flag had to be flipped (and the flip verified) to obtain a consistent result.</summary>
        public bool Flipped { get; init; }
        /// <summary>True only when the closing moves verifiably returned the axis to its baseline (residual within tolerance).</summary>
        public bool RestoredToBaseline { get; init; }
        /// <summary>Residual against the baseline after the closing moves, in axis arcminutes; NaN when it could not be measured.</summary>
        public float ClosingResidualArcmin { get; init; }
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
    ///  S5 opposite transition -> second backlash measure, directionality verdict;
    ///  S6 iterative close of the loop against the baseline solve.
    /// On failure or cancellation the restore is measured against the baseline too,
    /// falling back to the commanded sum only when solving is unavailable.
    /// </summary>
    public sealed class OapaCalibrationService {
        private readonly IOapaCalibrationMotion motion;
        private readonly IOapaCalibrationSolver solver;
        private readonly float calibrationStepArcmin;
        private readonly TimeSpan settleTime;
        private readonly Func<TimeSpan, CancellationToken, Task> delay;

        public OapaCalibrationService(
            IOapaCalibrationMotion motion,
            IOapaCalibrationSolver solver,
            float calibrationStepArcmin = 45.0f,
            TimeSpan settleTime = default,
            Func<TimeSpan, CancellationToken, Task> delay = null) {

            this.motion = motion;
            this.solver = solver;
            this.calibrationStepArcmin = calibrationStepArcmin;
            this.settleTime = settleTime;
            this.delay = delay ?? ((t, ct) => Task.Delay(t, ct));
        }

        /// <summary>
        /// Every move goes through here so none can be added later without its settle.
        ///
        /// A high-friction axis keeps relaxing after the controller reports idle - one
        /// tester watched the position creep for about a second after every stop. Capturing
        /// into that relaxation measures a moving target: the response reads short and the
        /// two backlash transitions disagree for a reason that has nothing to do with the
        /// mechanism. The correction loop has waited between move and solve since it
        /// existed; the calibration never did.
        /// </summary>
        private async Task MoveAndSettle(Axis axis, float arcmin, CancellationToken token) {
            await motion.MoveRelative(axis, arcmin, token).ConfigureAwait(false);
            if (settleTime > TimeSpan.Zero) {
                await delay(settleTime, token).ConfigureAwait(false);
            }
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
            BacklashEnteringPositiveArcmin = r.BacklashEnteringPositiveArcmin,
            BacklashEnteringNegativeArcmin = r.BacklashEnteringNegativeArcmin,
            DirectionalBacklash = r.DirectionalBacklash,
            BacklashSuspect = r.BacklashSuspect,
            ResponseSuspect = r.ResponseSuspect,
            Flipped = flipped,
            RestoredToBaseline = r.RestoredToBaseline,
            ClosingResidualArcmin = r.ClosingResidualArcmin
        };

        private async Task<AxisCalibrationResult> CalibrateAxis(
            Axis axis, float currentRatio, bool reversed,
            string axisLabel, Action<string> reportStatus, CancellationToken token) {

            bool isAzimuth = axis == Axis.XAxis;
            float dirSign = reversed ? -1f : 1f;
            int solveCount = 0;
            float movedArcmin = 0f;
            // Whether the axis has physically left its baseline. Deliberately NOT the
            // commanded sum: with backlash the sum returns to zero while the mechanism is
            // still displaced (the reversal legs lose motion the forward legs delivered),
            // so a failure at that exact moment used to skip the restore and leave the
            // platform off its starting position. Once any move has been commanded, only a
            // verified restore may clear the need for one.
            var needsRestore = false;
            CalibrationSolveSample last = default;
            CalibrationSolveSample baseline = default;

            // Freeze the solver's topocentric epoch for this pass: displacements between
            // samples must measure axis motion only, not the sidereal drift of a tracked
            // field's Alt/Az between solve times. Field signature of the aliasing: closing
            // residuals against a minutes-old baseline that no iteration could remove
            // (0.6' on one rig, 5' on a slower one).
            solver.BeginCalibration();

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
                needsRestore = true; // set before the move: a failure mid-motion leaves the position unknown
                await MoveAndSettle(axis, wire, token).ConfigureAwait(false);
                movedArcmin += wire;
                var now = await NextSolve().ConfigureAwait(false);
                var d = OapaCalibrationGeometry.SignedAxisDisplacementArcmin(isAzimuth, last, now);
                last = now;

                // Travel budget, measured on the sky rather than on the commanded sum: a
                // wrong factor makes commanded arcminutes meaningless as a bound, but the
                // solve always knows how far from the start the axis really is. This is
                // the backstop for the pathological cases the self-scaling legs cannot
                // absorb (stick-slip that fools the probe, a runaway response) before the
                // platform is driven toward its mechanical travel limit.
                var excursion = OapaCalibrationGeometry.SignedAxisDisplacementArcmin(isAzimuth, baseline, now);
                var budget = MaxExcursionSteps * calibrationStepArcmin;
                if (Math.Abs(excursion) > budget) {
                    throw new InvalidOperationException(
                        $"{axisLabel}: calibration exceeded its travel budget ({excursion:F0}' from the start, budget ±{budget:F0}'); " +
                        "aborting to protect the axis travel. Check that the axis moves freely and that the factor is plausible.");
                }
                return d;
            }

            // S0: solve noise with the axis at rest; everything below detects motion
            // against a threshold derived from it (fail fast on an unsolvable field too).
            reportStatus?.Invoke($"{axisLabel}: measuring solve noise...");
            var s0a = await NextSolve().ConfigureAwait(false);
            baseline = await NextSolve().ConfigureAwait(false);
            var noise = Math.Abs(OapaCalibrationGeometry.SignedAxisDisplacementArcmin(isAzimuth, s0a, baseline));
            var threshold = Math.Max(NoiseSigmaFactor * noise, DetectionFloorArcmin);
            last = baseline;

            if (isAzimuth && Math.Cos(baseline.AltitudeDegrees * Math.PI / 180.0) < OapaCalibrationGeometry.MinimumAzimuthCosAltitude) {
                throw new InvalidOperationException(
                    $"{axisLabel}: field altitude {baseline.AltitudeDegrees:F0}° is too close to the zenith for azimuth calibration. " +
                    "Point the scope at a lower altitude (ideally toward the celestial pole) and retry.");
            }

            // The altitude actuator tilts the rig about the horizontal east-west axis; a field at
            // azimuth A only shows cos(A) of that tilt in its altitude. Near due east/west there is
            // no signal to measure, and a factor calibrated on the residual projection is inflated
            // by 1/|cos(A)| — three field sessions on one rig produced 97.5/202.7/255.0 for a true
            // factor of 73-85 this way. Refusing here, before any motion, beats measuring a number
            // that will oscillate or stall the correction loop later.
            var baselineCosAz = Math.Cos(baseline.AzimuthDegrees * Math.PI / 180.0);
            if (!isAzimuth && Math.Abs(baselineCosAz) < OapaCalibrationGeometry.MinimumAltitudeCosAzimuth) {
                throw new InvalidOperationException(
                    $"{axisLabel}: field azimuth {baseline.AzimuthDegrees:F0}° is too close to due east/west for altitude calibration — " +
                    $"only {Math.Abs(baselineCosAz):P0} of the axis motion is visible there. " +
                    "Point the scope toward the meridian or the celestial pole and retry.");
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
                // The leg self-scales to the measured response, so a barely-responding
                // probe would ask for an unbounded logical leg (8'/0.04 = 200'). Cap every
                // single commanded leg at the same bound the closing moves already use:
                // a leg that large means the response measurement is not to be trusted,
                // not that the axis should be driven three degrees in one command.
                var legLogical = (float)Math.Min(3.0 * calibrationStepArcmin, Math.Max(1.0, TargetCleanLegPhysicalArcmin / roughResponse));
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
                //
                // The raw (unclamped) value is kept: a *negative* transition means the
                // reversal leg travelled further than the response predicts, which no amount
                // of real play can produce. Clamping it to zero turns that impossibility into
                // a plausible-looking "no play this way", and paired against a significant
                // value in the other direction that zero becomes tens of arcminutes of
                // compensation made of nothing.
                var rawForward = backlashLeg * reverseResponse - reversalTravel;
                var backlashForward = Math.Max(0, rawForward);

                // S5: opposite transition. This is a *second quantity*, not a second sample
                // of the first: the two are equal only on a mechanism whose play costs the
                // same to cross both ways, which an axis carrying its load against gravity
                // is not. Their disagreement is therefore a directionality verdict.
                reportStatus?.Invoke($"{axisLabel}: measuring the opposite transition...");
                var dBack = await MoveAndMeasure(backlashLeg, +1f).ConfigureAwait(false);
                var rawReverse = backlashLeg * forwardResponse - Math.Abs(dBack);
                var backlashReverse = Math.Max(0, rawReverse);

                var maxResponse = Math.Max(forwardResponse, reverseResponse);
                var responseAgreement = maxResponse > 0 ? Math.Min(forwardResponse, reverseResponse) / maxResponse : 0.0;
                var asymmetric = 1.0 - responseAgreement > AsymmetryFlagThreshold;
                // Beyond a factor of two the mean is not a compromise between the two
                // directions, it is wrong for both - and each backlash transition is
                // evaluated against the *other* direction's response, so the pair goes with
                // it. Field evidence: fwd=0.860 against rev=0.102 produced a factor three
                // times what the axis delivered during the corrections that followed.
                var responseSuspect = responseAgreement < ResponseAgreementFloor;

                double backlash;
                var directional = false;
                var backlashSuspect = responseSuspect;
                var significant = Math.Max(backlashForward, backlashReverse);
                if (significant < 2 * threshold) {
                    backlash = 0; // both transitions indistinguishable from noise
                } else if (backlashSuspect || Math.Min(rawForward, rawReverse) < -threshold) {
                    // An impossible transition invalidates the pair, not just itself: both
                    // are computed from the same two responses over the same escalated leg.
                    backlashSuspect = true;
                    backlash = 0;
                } else {
                    // A transition indistinguishable from zero paired against a significant
                    // one cannot establish directionality. Zero-against-large is the field
                    // signature of a slipped measurement, not of directional mechanics: the
                    // same axis measured 4.10'/4.31' and, five minutes later, 0.00'/8.69' -
                    // stable sum, flipped split - and the phantom pair threw a 23" residual
                    // to 6'32" at the finish line. (0.00'/27.21' on the same rig is the other
                    // occurrence; no genuine pair with a zero side has ever repeated.) The
                    // mean is the safe collapse: a symmetric value's magnitude cancels out of
                    // the two-leg plan, so even an imperfect mean only costs travel time.
                    var bothTransitionsMeasurable = Math.Min(backlashForward, backlashReverse) >= threshold;
                    directional = bothTransitionsMeasurable
                        && Math.Abs(backlashForward - backlashReverse) > Math.Max(DirectionalRelativeThreshold * significant, 2 * threshold);
                    backlash = (backlashForward + backlashReverse) / 2.0;
                    if (!bothTransitionsMeasurable) {
                        Logger.Info($"OAPA cal {axisLabel}: one backlash transition is indistinguishable from zero against " +
                            $"{significant:F2}' on the other - the split is not established (slip signature); using the mean {backlash:F2}' for both directions");
                    }
                }

                // backlashForward was measured entering the leg direction -dirSign (S3),
                // backlashReverse entering +dirSign (S5). The Reverse flag therefore swaps
                // which one belongs to which commanded sign; resolving it here means no
                // consumer has to know about dirSign at all.
                var enteringPositive = (float)(dirSign > 0 ? backlashReverse : backlashForward);
                var enteringNegative = (float)(dirSign > 0 ? backlashForward : backlashReverse);
                if (!directional) {
                    // A "not directional" verdict is the statement that these two figures are
                    // the same quantity measured twice. Reporting them separately anyway hands
                    // the planner a difference made of measurement noise, and a two-leg
                    // reversal travels `move - outward + back`: that gap becomes a fixed bias
                    // on every reversal, so the axis can never be corrected by less than the
                    // gap and requests below it move it the wrong way. Two field rigs stalled
                    // at exactly their own gap - 9.3' and 7.3' - with `directional=false` in
                    // the same log line. Collapsing to the mean restores the single-value
                    // behaviour wherever the difference is not established, and changes
                    // nothing where it is.
                    enteringPositive = enteringNegative = (float)backlash;
                }

                var meanResponse = (forwardResponse + reverseResponse) / 2.0;

                // A factor error is a scale: it affects both directions identically. Two
                // responses that disagree by more than the agreement floor are therefore
                // not two measurements of the scale - the weaker direction is losing
                // motion mechanically (stall, slip, insufficient torque) and blending it
                // in poisons the factor. Field case: responses 0.199/0.958 blended into a
                // factor 1.7x too large, and recalibrating on top of that compounded it to
                // 3.6x, while the strong direction alone was within a few percent of the
                // truth. When the pair is suspect, the strong direction IS the scale.
                var scaleResponse = responseSuspect ? Math.Max(forwardResponse, reverseResponse) : meanResponse;

                var result = new AxisCalibrationResult {
                    Ratio = (float)(currentRatio / scaleResponse),
                    ForwardRatio = (float)(currentRatio / forwardResponse),
                    ReverseRatio = (float)(currentRatio / reverseResponse),
                    BacklashArcmin = (float)backlash,
                    NoiseSigmaArcmin = (float)noise,
                    Consistent = directionConsistent,
                    Asymmetric = asymmetric,
                    BacklashEnteringPositiveArcmin = enteringPositive,
                    BacklashEnteringNegativeArcmin = enteringNegative,
                    DirectionalBacklash = directional,
                    BacklashSuspect = backlashSuspect,
                    ResponseSuspect = responseSuspect
                };

                // Both the raw pair and the pair that will actually be applied: they differ
                // whenever the directionality verdict collapses them, and a log that only
                // showed one of the two is what made this take three field sessions to find.
                // The pointing and its projection make a future log self-diagnosing: a factor
                // measured through a foreshortened projection is visible right where it was born.
                Logger.Info($"OAPA cal {axisLabel}: noise={noise:F2}', responses fwd={forwardResponse:F3}/rev={reverseResponse:F3} '/unit, " +
                    $"backlash={backlashForward:F2}'/{backlashReverse:F2}' -> applied +{enteringPositive:F2}'/-{enteringNegative:F2}', ratio={result.Ratio:F2}, " +
                    $"consistent={result.Consistent}, asymmetric={result.Asymmetric}, directional={result.DirectionalBacklash}, " +
                    $"backlashSuspect={backlashSuspect}, responseSuspect={responseSuspect}, solves={solveCount}, " +
                    $"field alt={baseline.AltitudeDegrees:F1}/az={baseline.AzimuthDegrees:F1}, proj={(isAzimuth ? 1.0 : baselineCosAz):F3}");

                // S6: physically return to the baseline. The response just measured makes
                // the closing moves exact; iterating covers the backlash a closing reversal
                // eats on its first move. The outcome is carried on the result: a
                // calibration that measured fine but did not verifiably return home must
                // say so, not report full success.
                var responsePerWire = f1 / (dirSign * legLogical);
                var (restored, closingResidual) = await CloseLoopAgainstBaseline(axis, isAzimuth, baseline, responsePerWire, axisLabel, reportStatus, NextSolve, token).ConfigureAwait(false);
                result.RestoredToBaseline = restored;
                result.ClosingResidualArcmin = closingResidual;

                return result;
            } catch (Exception) when (needsRestore) {
                await BestEffortRestore(axis, isAzimuth, baseline, movedArcmin, axisLabel).ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>
        /// Drives the measured residual against the baseline back to zero, up to
        /// <see cref="MaxClosingIterations"/> moves: the first closing reversal loses the
        /// backlash, the following iteration completes the travel. A failed closing move
        /// never discards the calibration result, but it must not be silent either: the
        /// returned pair says whether the axis is verifiably back home (residual measured
        /// and within tolerance) and what that residual was (NaN when unmeasurable).
        /// Cancellation is not handled here — it propagates so the caller's best-effort
        /// restore runs and the cancellation reaches the user instead of being converted
        /// into an apparent success.
        /// </summary>
        private async Task<(bool restored, float residualArcmin)> CloseLoopAgainstBaseline(
            Axis axis, bool isAzimuth, CalibrationSolveSample baseline, double responsePerWire,
            string axisLabel, Action<string> reportStatus, Func<Task<CalibrationSolveSample>> nextSolve, CancellationToken token) {

            if (Math.Abs(responsePerWire) < 1e-3) {
                Logger.Warning($"OAPA cal {axisLabel}: response too small to close the loop; the axis was not returned to its baseline");
                return (false, float.NaN);
            }
            try {
                var current = await nextSolve().ConfigureAwait(false);
                for (var i = 0; i < MaxClosingIterations; i++) {
                    var residual = OapaCalibrationGeometry.SignedAxisDisplacementArcmin(isAzimuth, baseline, current);
                    if (Math.Abs(residual) < RestoreToleranceArcmin) {
                        Logger.Info($"OAPA cal {axisLabel}: closed loop against baseline; residual {residual:F2}'");
                        return (true, (float)residual);
                    }
                    var closing = (float)Math.Clamp(-residual / responsePerWire, -3.0 * calibrationStepArcmin, 3.0 * calibrationStepArcmin);
                    reportStatus?.Invoke($"{axisLabel}: returning to start ({residual:+0.0;-0.0}' off)...");
                    await MoveAndSettle(axis, closing, token).ConfigureAwait(false);
                    current = await nextSolve().ConfigureAwait(false);
                }
                var final = OapaCalibrationGeometry.SignedAxisDisplacementArcmin(isAzimuth, baseline, current);
                var withinTolerance = Math.Abs(final) < RestoreToleranceArcmin;
                Logger.Info($"OAPA cal {axisLabel}: closing iterations exhausted; residual {final:F2}'" +
                    (withinTolerance ? string.Empty : " (out of tolerance - the axis is not back at its baseline)"));
                return (withinTolerance, (float)final);
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                // The calibration result is already measured; a failed closing move must not discard it.
                Logger.Warning($"OAPA cal {axisLabel}: failed to close the loop against the baseline ({ex.Message})");
                return (false, float.NaN);
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
                    await MoveAndSettle(axis, restore, CancellationToken.None).ConfigureAwait(false);
                }
            } catch (Exception measureEx) {
                Logger.Warning($"OAPA cal {axisLabel}: measured restore unavailable ({measureEx.Message}); driving back the commanded sum");
                try {
                    await MoveAndSettle(axis, -movedArcmin, CancellationToken.None).ConfigureAwait(false);
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
        /// <summary>Below this min/max response ratio (a factor of two) the pass measured nothing usable.</summary>
        private const double ResponseAgreementFloor = 0.5;
        /// <summary>Backlash-transition disagreement share above which the play is declared direction-dependent.</summary>
        private const double DirectionalRelativeThreshold = 0.20;
        /// <summary>Hard solve budget per axis pass; exceeded means something is off and the sequence aborts honestly.</summary>
        private const int MaxSolvesPerAxis = 20;
        /// <summary>Physical cap for a single escalated leg, in axis arcminutes (sky excursion guard).</summary>
        private const double MaxLegPhysicalArcmin = 90.0;
        /// <summary>Closing/restore iterations: one reversal may eat its backlash, the next completes.</summary>
        private const int MaxClosingIterations = 3;

        /// <summary>Residuals below this are indistinguishable from solve noise and left alone.</summary>
        private const float RestoreToleranceArcmin = 0.5f;

        /// <summary>
        /// Travel budget as a multiple of the calibration step: the measured excursion
        /// from the baseline may never exceed this (4 x 45' = 3° by default). Escalated
        /// backlash legs on high-play rigs legitimately reach ~2° of round trip; anything
        /// beyond this is a malfunction, not a measurement.
        /// </summary>
        private const float MaxExcursionSteps = 4f;
    }
}
