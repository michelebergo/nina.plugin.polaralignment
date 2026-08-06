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
    ///
    /// The play is configured per direction, because entering one direction and entering
    /// the other are two different physical quantities: an axis carrying its load against
    /// gravity crosses its own play unaided one way and has to be driven across it the
    /// other, so the two can differ several-fold on the same axis. Every leg is therefore
    /// compensated with the value of the direction *that leg* travels, which is what makes
    /// a two-leg plan land exactly: the overshoot leg pays one transition and the return
    /// leg pays the other, and they cancel only when each is given its own number. Passing
    /// one value for both directions reproduces the symmetric behaviour exactly.
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
        /// Plans the command sequence for a requested relative move on an axis whose play
        /// costs the same in both directions.
        /// </summary>
        public static float[] PlanMoves(OapaBacklashMode mode, float move, float backlashArcmin, LastDirection lastDirection)
            => PlanMoves(mode, move, backlashArcmin, backlashArcmin, lastDirection);

        /// <summary>
        /// Plans the command sequence for a requested relative move. Non-reversing moves
        /// (and reversals with no configured backlash, or mode Off) are passed through.
        /// </summary>
        /// <param name="backlashEnteringPositive">Motion lost when a move in the positive direction reverses into it.</param>
        /// <param name="backlashEnteringNegative">Motion lost when a move in the negative direction reverses into it.</param>
        public static float[] PlanMoves(OapaBacklashMode mode, float move,
            float backlashEnteringPositive, float backlashEnteringNegative, LastDirection lastDirection) {

            var sign = Math.Sign(move);
            var lastSign = lastDirection == LastDirection.Positive ? 1 : -1;

            // The move itself reverses into `sign`, so it pays that direction's play; a
            // return leg travels the other way and pays the other direction's.
            var outward = sign > 0 ? backlashEnteringPositive : backlashEnteringNegative;
            var back = sign > 0 ? backlashEnteringNegative : backlashEnteringPositive;

            if (sign == 0 || sign == lastSign || outward <= 0f || mode == OapaBacklashMode.Off) {
                return new[] { move };
            }

            switch (mode) {
                case OapaBacklashMode.Soft:
                    return new[] { move + sign * SoftFraction * outward };
                case OapaBacklashMode.Full:
                    return new[] { move + sign * outward };
                case OapaBacklashMode.Unidirectional:
                    // The overshoot cancels between the legs whatever its size, so it is
                    // sized off the larger transition purely as margin against an
                    // underestimate; each leg still carries its own direction's play.
                    var overshoot = OvershootFractionOfBacklash * Math.Max(outward, back) + OvershootFloorArcmin;
                    return new[] {
                        move + sign * (outward + overshoot),
                        -sign * (back + overshoot)
                    };
                default:
                    return new[] { move };
            }
        }

        /// <summary>
        /// Recommends a mode from the calibration measurements: not measurable -> Off;
        /// small -> Full; large -> Unidirectional. Soft stays a manual, conservative choice.
        /// The threshold is about the *size* of the play, not its symmetry: a directional
        /// play is compensated exactly by the per-direction plan, so it does not change
        /// which mode is appropriate.
        /// </summary>
        public static OapaBacklashMode Recommend(float backlashArcmin, float noiseSigmaArcmin) {
            var measurable = Math.Max(MeasurableSigmaFactor * noiseSigmaArcmin, MeasurableFloorArcmin);
            if (backlashArcmin < measurable) {
                return OapaBacklashMode.Off;
            }
            return backlashArcmin <= LargeBacklashArcmin ? OapaBacklashMode.Full : OapaBacklashMode.Unidirectional;
        }

        /// <summary>
        /// Recommends from a per-direction measurement. The worse direction decides: a mode
        /// safe for the cheaper transition is not necessarily safe for the expensive one.
        /// </summary>
        public static OapaBacklashMode Recommend(float backlashEnteringPositive, float backlashEnteringNegative, float noiseSigmaArcmin)
            => Recommend(Math.Max(backlashEnteringPositive, backlashEnteringNegative), noiseSigmaArcmin);
    }
}
