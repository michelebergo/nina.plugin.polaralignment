using System;

namespace NINA.Plugins.PolarAlignment {

    internal enum ConvergenceAction {
        Continue,
        AwaitConfirmation,
        Finish,
        FinishBestEffort,
        HaltCalibrationSuspect,
        HaltEstimateDrift
    }

    internal sealed class ConvergenceDecision {
        public ConvergenceDecision(ConvergenceAction action, string reason) {
            Action = action;
            Reason = reason;
        }

        public ConvergenceAction Action { get; }
        public string Reason { get; }
    }

    /// <summary>
    /// Pure decision state machine for the fine phase of automated polar alignment.
    ///
    /// Field-driven design (rc7 log, 2026-07-26): the continuous error estimate can drift
    /// over time — provably, when it changes while nothing moved. This monitor therefore
    /// (a) tolerates confirmation readings within an absolute noise margin, (b) detects
    /// stationary drift and stops trusting the estimate, (c) classifies runaway streaks
    /// by the size of the moves that "caused" them before blaming the calibration, and
    /// (d) proposes a best-effort finish when the loop only oscillates around an already
    /// achieved sub-tolerance minimum.
    /// </summary>
    internal sealed class ConvergenceMonitor {
        internal const double ConfirmationMarginArcmin = 0.1;
        internal const int RequiredConsecutiveBelowTolerance = 2;
        internal const int MaxConsecutiveWorsenings = 3;
        internal const double WorseningNoiseArcmin = 0.05;
        internal const double StationaryDriftArcmin = 0.25;
        internal const double CalibrationSuspectMoveArcmin = 1.0;
        internal const int BestEffortOscillations = 4;

        private readonly double toleranceArcmin;
        private double? previousErrorArcmin;
        private int consecutiveBelowTolerance;
        private int consecutiveWorsenings;
        private int oscillationsSinceMinimum;
        private double recentLargestMoveArcmin;

        public ConvergenceMonitor(double toleranceArcmin) {
            this.toleranceArcmin = toleranceArcmin;
        }

        public bool EstimateDegraded { get; private set; }
        public double? MinimumAchievedArcmin { get; private set; }

        public ConvergenceDecision Observe(double totalErrorArcmin,
                                           double lastCommandedMagnitudeArcmin,
                                           bool movedSinceLastObservation,
                                           bool isFirstObservation = false) {
            if (isFirstObservation) {
                previousErrorArcmin = totalErrorArcmin;

                if (totalErrorArcmin <= toleranceArcmin) {
                    // Already below tolerance on the very first reading: count it as a
                    // confirmation instead of commanding a move, but skip the drift/move
                    // bookkeeping below since there is no prior observation to compare against.
                    consecutiveBelowTolerance++;
                    if (!MinimumAchievedArcmin.HasValue || totalErrorArcmin < MinimumAchievedArcmin.Value) {
                        MinimumAchievedArcmin = totalErrorArcmin;
                    }

                    return new ConvergenceDecision(ConvergenceAction.AwaitConfirmation,
                        $"Below tolerance ({totalErrorArcmin:0.00}') on the first observation, awaiting confirmation solve ({consecutiveBelowTolerance}/{RequiredConsecutiveBelowTolerance}).");
                }

                return new ConvergenceDecision(ConvergenceAction.Continue, "First observation.");
            }

            // (a) Stationarity drift detector: a change larger than the noise floor while
            // nothing moved cannot be a real polar-error change.
            if (previousErrorArcmin.HasValue
                && !movedSinceLastObservation
                && Math.Abs(totalErrorArcmin - previousErrorArcmin.Value) > StationaryDriftArcmin) {
                EstimateDegraded = true;
            }

            // Track the largest recent commanded move for runaway classification.
            if (movedSinceLastObservation) {
                recentLargestMoveArcmin = Math.Max(recentLargestMoveArcmin * 0.5, Math.Abs(lastCommandedMagnitudeArcmin));
            }

            var decision = Classify(totalErrorArcmin);
            previousErrorArcmin = totalErrorArcmin;
            return decision;
        }

        private ConvergenceDecision Classify(double totalErrorArcmin) {
            if (totalErrorArcmin <= toleranceArcmin) {
                consecutiveBelowTolerance++;
                consecutiveWorsenings = 0;
                oscillationsSinceMinimum = 0;
                if (!MinimumAchievedArcmin.HasValue || totalErrorArcmin < MinimumAchievedArcmin.Value) {
                    MinimumAchievedArcmin = totalErrorArcmin;
                }

                if (consecutiveBelowTolerance >= RequiredConsecutiveBelowTolerance) {
                    return new ConvergenceDecision(ConvergenceAction.Finish,
                        $"Total error {totalErrorArcmin:0.00}' below tolerance for {consecutiveBelowTolerance} consecutive solves.");
                }

                return new ConvergenceDecision(ConvergenceAction.AwaitConfirmation,
                    $"Below tolerance ({totalErrorArcmin:0.00}'), awaiting confirmation solve ({consecutiveBelowTolerance}/{RequiredConsecutiveBelowTolerance}).");
            }

            if (consecutiveBelowTolerance > 0 && totalErrorArcmin <= toleranceArcmin + ConfirmationMarginArcmin) {
                // Within the absolute noise margin: hold the confirmation state instead of
                // restarting corrections (rc7 field case: 0.58' against 0.5' tolerance).
                return new ConvergenceDecision(ConvergenceAction.AwaitConfirmation,
                    $"Reading {totalErrorArcmin:0.00}' is within the noise margin above tolerance; holding for another confirmation solve.");
            }

            consecutiveBelowTolerance = 0;

            // Best-effort/oscillation check runs before the worsening/halt check: once a
            // sub-tolerance minimum has been achieved, an oscillating or degraded estimate
            // must resolve to a graceful best-effort finish rather than a hard halt.
            if (MinimumAchievedArcmin.HasValue) {
                oscillationsSinceMinimum++;
                // Finish best effort if: (a) 4+ oscillations, or (b) the estimate is degraded
                // (stationary drift detected) — unconditional once a minimum has been achieved.
                if (oscillationsSinceMinimum >= BestEffortOscillations || EstimateDegraded) {
                    return new ConvergenceDecision(ConvergenceAction.FinishBestEffort,
                        $"Best-effort finish at previously achieved {MinimumAchievedArcmin.Value:0.00}': the estimate no longer improves ({(EstimateDegraded ? "stationary drift detected" : $"{oscillationsSinceMinimum} oscillations")}).");
                }
            }

            // Runaway streak tracking with noise floor. Readings within the confirmation
            // margin never count as worsenings.
            if (previousErrorArcmin.HasValue
                && totalErrorArcmin > toleranceArcmin + ConfirmationMarginArcmin
                && totalErrorArcmin > previousErrorArcmin.Value + WorseningNoiseArcmin) {
                consecutiveWorsenings++;
            } else {
                consecutiveWorsenings = 0;
            }

            if (consecutiveWorsenings >= MaxConsecutiveWorsenings) {
                if (recentLargestMoveArcmin >= CalibrationSuspectMoveArcmin && !EstimateDegraded) {
                    return new ConvergenceDecision(ConvergenceAction.HaltCalibrationSuspect,
                        $"Error increased for {consecutiveWorsenings} consecutive measurements under large corrections; calibration factors or backlash compensation are likely wrong.");
                }

                return new ConvergenceDecision(ConvergenceAction.HaltEstimateDrift,
                    $"Error increased for {consecutiveWorsenings} consecutive measurements while corrections were small; the error estimate appears to have drifted. This is not a calibration problem — re-run the alignment to re-measure.");
            }

            return new ConvergenceDecision(ConvergenceAction.Continue, "Continuing corrections.");
        }
    }
}
