using System;

namespace NINA.Plugins.PolarAlignment.OAPA {

    /// <summary>
    /// How an OAPA axis handles its mechanical backlash on direction reversals,
    /// from the most conservative to the most aggressive strategy.
    /// </summary>
    public enum OapaBacklashMode {
        /// <summary>No compensation: plain moves (negligible measured backlash).</summary>
        Off,
        /// <summary>Single move extended by 75% of the backlash: limits the injected error when the value is overestimated.</summary>
        Soft,
        /// <summary>Single move extended by the full backlash: the engagement is part of the move, no out-and-back excursion.</summary>
        Full,
        /// <summary>Overshoot past the target and return so the final approach always comes from the engaged direction.</summary>
        Unidirectional
    }

    /// <summary>
    /// Pure move planning for the OAPA backlash modes and the mode recommendation rule.
    /// The invariant of every reversal plan is the physical arrival point: under a
    /// mechanics that loses exactly the configured backlash per reversal, the axis lands
    /// on the requested target (Soft deliberately trades up to B/4 of that for safety
    /// against an overestimated value).
    /// </summary>
    public static class BacklashModePlanner {

        /// <summary>Share of the backlash applied by the Soft mode.</summary>
        private const float SoftFraction = 0.75f;
        /// <summary>Unidirectional overshoot beyond the backlash: 25% of it plus a fixed floor, absorbing estimate errors.</summary>
        private const float OvershootFractionOfBacklash = 0.25f;
        private const float OvershootFloorArcmin = 0.5f;

        /// <summary>Backlash below max(2 sigma, floor) is indistinguishable from solve noise.</summary>
        private const float MeasurableSigmaFactor = 2f;
        private const float MeasurableFloorArcmin = 0.5f;
        /// <summary>Above this, the compensation error itself is significant: approach from one side only.</summary>
        private const float LargeBacklashArcmin = 3f;

        /// <summary>
        /// Plans the command sequence for a requested relative move. Non-reversing moves
        /// (and reversals with no configured backlash, or mode Off) are passed through.
        /// </summary>
        public static float[] PlanMoves(OapaBacklashMode mode, float move, float backlashArcmin, LastDirection lastDirection) {
            var sign = Math.Sign(move);
            var lastSign = lastDirection == LastDirection.Positive ? 1 : -1;
            if (sign == 0 || sign == lastSign || backlashArcmin <= 0f || mode == OapaBacklashMode.Off) {
                return new[] { move };
            }

            switch (mode) {
                case OapaBacklashMode.Soft:
                    return new[] { move + sign * SoftFraction * backlashArcmin };
                case OapaBacklashMode.Full:
                    return new[] { move + sign * backlashArcmin };
                case OapaBacklashMode.Unidirectional:
                    var overshoot = OvershootFractionOfBacklash * backlashArcmin + OvershootFloorArcmin;
                    return new[] {
                        move + sign * (backlashArcmin + overshoot),
                        -sign * (backlashArcmin + overshoot)
                    };
                default:
                    return new[] { move };
            }
        }

        /// <summary>
        /// Recommends a mode from the calibration measurements: not measurable -> Off;
        /// small and repeatable -> Full; large -> Unidirectional. Soft stays a manual,
        /// conservative choice; slippage never reaches this (Apply is blocked).
        /// </summary>
        public static OapaBacklashMode Recommend(float backlashArcmin, float noiseSigmaArcmin) {
            var measurable = Math.Max(MeasurableSigmaFactor * noiseSigmaArcmin, MeasurableFloorArcmin);
            if (backlashArcmin < measurable) {
                return OapaBacklashMode.Off;
            }
            return backlashArcmin <= LargeBacklashArcmin ? OapaBacklashMode.Full : OapaBacklashMode.Unidirectional;
        }
    }
}
