using System;
using System.Collections.Generic;
using System.Linq;

namespace NINA.Plugins.PolarAlignment {

    /// <summary>
    /// Rolling mean over the recent stable error estimates for the precision-finish mode:
    /// near completion the single-solve noise (~0.1-0.2') is what makes sub-0.5' finish
    /// decisions unreliable, and averaging four samples halves it. Active only below an
    /// activation threshold (in the coarse phase averaging would just add lag) and reset
    /// whenever the mount moves, because older samples no longer describe the state.
    /// </summary>
    public sealed class ErrorEstimateAverager {

        /// <summary>Window size: noise / 2 while staying responsive to real changes.</summary>
        private const int WindowSize = 4;
        /// <summary>Averaging engages below 2 arcminutes of total error.</summary>
        private const double ActivationThresholdDegrees = 2.0 / 60.0;

        private readonly Queue<(double az, double alt)> samples = new Queue<(double az, double alt)>();

        /// <summary>
        /// Registers a stable estimate and returns the value the finish decision should
        /// use: the raw estimate while far from completion, the rolling mean near it.
        /// </summary>
        public (double azimuthDegrees, double altitudeDegrees) Register(double azimuthDegrees, double altitudeDegrees) {
            var total = Math.Sqrt(azimuthDegrees * azimuthDegrees + altitudeDegrees * altitudeDegrees);
            if (total > ActivationThresholdDegrees) {
                samples.Clear();
                return (azimuthDegrees, altitudeDegrees);
            }

            samples.Enqueue((azimuthDegrees, altitudeDegrees));
            while (samples.Count > WindowSize) {
                samples.Dequeue();
            }
            return (samples.Average(s => s.az), samples.Average(s => s.alt));
        }

        /// <summary>The mount moved: previous samples no longer describe the current state.</summary>
        public void Reset() {
            samples.Clear();
        }
    }
}
