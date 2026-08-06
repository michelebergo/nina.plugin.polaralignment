using FluentAssertions;
using NINA.Plugins.PolarAlignment.OAPA;
using System.Linq;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// The pure move-planning rules for the four OAPA backlash handling modes. All plans
    /// are expressed as command sequences; the arithmetic invariant is that the physical
    /// arrival point equals the requested move under a mechanics that loses exactly the
    /// configured backlash on each reversal.
    /// </summary>
    public class BacklashModePlannerTest {

        private const float B = 8f;      // configured backlash
        private const float O = 2.5f;    // unidirectional overshoot margin: 0.25*B + 0.5'

        private static float[] Plan(OapaBacklashMode mode, float move, LastDirection last) =>
            BacklashModePlanner.PlanMoves(mode, move, B, last).ToArray();

        /// <summary>Simulates the physical arrival of a command sequence with backlash b.</summary>
        private static float Arrive(float[] moves, float b, LastDirection last) {
            var position = 0f;
            var lastSign = last == LastDirection.Positive ? 1 : -1;
            foreach (var move in moves) {
                var sign = System.Math.Sign(move);
                var effective = System.Math.Abs(move);
                if (sign != 0 && sign != lastSign) {
                    effective = System.Math.Max(0, effective - b);
                    lastSign = sign;
                }
                position += sign * effective;
            }
            return position;
        }

        [Test]
        public void SameDirection_AllModes_PlainMove() {
            foreach (var mode in new[] { OapaBacklashMode.Off, OapaBacklashMode.Soft, OapaBacklashMode.Full, OapaBacklashMode.Unidirectional }) {
                Plan(mode, 10f, LastDirection.Positive).Should().Equal(new[] { 10f }, $"mode={mode}");
            }
        }

        [Test]
        public void Off_Reversal_PlainMove() {
            Plan(OapaBacklashMode.Off, -10f, LastDirection.Positive).Should().Equal(-10f);
        }

        [Test]
        public void Full_Reversal_SingleMoveIncludesTheBacklash() {
            // One motion of d+B: the engagement is part of the move, no out-and-back.
            var plan = Plan(OapaBacklashMode.Full, -10f, LastDirection.Positive);
            plan.Should().Equal(-(10f + B));
            Arrive(plan, B, LastDirection.Positive).Should().BeApproximately(-10f, 0.001f);
        }

        [Test]
        public void Soft_Reversal_SingleMoveIncludesThreeQuartersOfTheBacklash() {
            var plan = Plan(OapaBacklashMode.Soft, -10f, LastDirection.Positive);
            plan.Should().Equal(-(10f + 0.75f * B));
            // With a perfectly measured backlash the arrival is short by B/4 - the price
            // of the conservative mode when the value is NOT overestimated.
            Arrive(plan, B, LastDirection.Positive).Should().BeApproximately(-10f + 0.25f * B, 0.001f);
        }

        [Test]
        public void Unidirectional_Reversal_OvershootsAndReturnsFromThePreferredDirection() {
            var plan = Plan(OapaBacklashMode.Unidirectional, -10f, LastDirection.Positive);
            plan.Should().Equal(-(10f + B + O), B + O);
            Arrive(plan, B, LastDirection.Positive).Should().BeApproximately(-10f, 0.001f);
        }

        [Test]
        public void Unidirectional_FinalApproachDirection_MatchesThePreviousDirection() {
            // The whole point of the mode: the last commanded motion is always in the
            // direction the axis was already engaged in, so backlash never enters the
            // final positioning.
            var plan = Plan(OapaBacklashMode.Unidirectional, -10f, LastDirection.Positive);
            System.Math.Sign(plan[^1]).Should().Be(1);

            var mirrored = Plan(OapaBacklashMode.Unidirectional, 10f, LastDirection.Negative);
            System.Math.Sign(mirrored[^1]).Should().Be(-1);
            Arrive(mirrored, B, LastDirection.Negative).Should().BeApproximately(10f, 0.001f);
        }

        [Test]
        public void ZeroBacklash_ReversalIsAPlainMove_InEveryMode() {
            foreach (var mode in new[] { OapaBacklashMode.Soft, OapaBacklashMode.Full, OapaBacklashMode.Unidirectional }) {
                BacklashModePlanner.PlanMoves(mode, -10f, 0f, LastDirection.Positive)
                    .Should().Equal(new[] { -10f }, $"mode={mode}");
            }
        }

        [Test]
        public void Recommendation_FollowsTheMeasuredBacklashAndNoise() {
            // Below detectability: no compensation at all.
            BacklashModePlanner.Recommend(backlashArcmin: 0.3f, noiseSigmaArcmin: 0.1f).Should().Be(OapaBacklashMode.Off);
            // Clearly measurable and small: single-move full compensation.
            BacklashModePlanner.Recommend(backlashArcmin: 2f, noiseSigmaArcmin: 0.1f).Should().Be(OapaBacklashMode.Full);
            // Large: the compensation error itself becomes significant - approach from one side only.
            BacklashModePlanner.Recommend(backlashArcmin: 20f, noiseSigmaArcmin: 0.1f).Should().Be(OapaBacklashMode.Unidirectional);
            // Noisy measurement floor: 0.45' with sigma 0.3' is not measurable.
            BacklashModePlanner.Recommend(backlashArcmin: 0.45f, noiseSigmaArcmin: 0.3f).Should().Be(OapaBacklashMode.Off);
        }

        // ----- Per-direction play -----
        // The arrival invariant is what these pin: simulate a mechanism that loses a
        // different amount entering each direction, run the plan through it, and check
        // where the axis actually ends up. Asserting on the emitted numbers alone would
        // not catch the two values being swapped.

        /// <summary>
        /// Applies a plan to a mechanism with per-direction lost motion and returns the net
        /// physical displacement, starting engaged in <paramref name="lastDirection"/>.
        /// </summary>
        private static float Travel(float[] plan, float lostEnteringPositive, float lostEnteringNegative, LastDirection lastDirection) {
            var position = 0f;
            var lastSign = lastDirection == LastDirection.Positive ? 1 : -1;
            foreach (var move in plan) {
                var sign = System.Math.Sign(move);
                if (sign == 0) { continue; }
                var lost = sign != lastSign ? (sign > 0 ? lostEnteringPositive : lostEnteringNegative) : 0f;
                position += move - sign * System.Math.Min(System.Math.Abs(move), lost);
                lastSign = sign;
            }
            return position;
        }

        [Test]
        public void Unidirectional_WithDirectionalPlay_LandsExactly_WhenEachLegCarriesItsOwnValue() {
            // The field case: 55' entering one direction, 16' entering the other.
            var plan = BacklashModePlanner.PlanMoves(OapaBacklashMode.Unidirectional, -15f, 16f, 55f, LastDirection.Positive);

            Travel(plan, lostEnteringPositive: 16f, lostEnteringNegative: 55f, LastDirection.Positive)
                .Should().BeApproximately(-15f, 0.01f, "each leg pays the play of the direction it travels, so the two cancel");
        }

        [Test]
        public void Unidirectional_WithDirectionalPlay_MissesByTheDifference_IfBothLegsShareOneValue() {
            // Why the pair exists: the mean is not a conservative approximation, it is wrong
            // in both directions, and the residual it leaves is the whole spread.
            var mean = (16f + 55f) / 2f;
            var plan = BacklashModePlanner.PlanMoves(OapaBacklashMode.Unidirectional, -15f, mean, LastDirection.Positive);

            Travel(plan, lostEnteringPositive: 16f, lostEnteringNegative: 55f, LastDirection.Positive)
                .Should().BeApproximately(-15f + (55f - 16f), 0.01f);
        }

        [Test]
        public void Full_WithDirectionalPlay_UsesTheDirectionTheMoveTravels() {
            BacklashModePlanner.PlanMoves(OapaBacklashMode.Full, -15f, 16f, 55f, LastDirection.Positive)
                .Should().Equal(new[] { -70f }, "a negative move pays the negative direction's play");
            BacklashModePlanner.PlanMoves(OapaBacklashMode.Full, +15f, 16f, 55f, LastDirection.Negative)
                .Should().Equal(new[] { +31f }, "a positive move pays the positive one's");
        }

        [Test]
        public void EqualValues_ReproduceTheSymmetricPlansExactly_InEveryMode() {
            foreach (var mode in new[] { OapaBacklashMode.Off, OapaBacklashMode.Soft, OapaBacklashMode.Full, OapaBacklashMode.Unidirectional }) {
                foreach (var move in new[] { -10f, +10f }) {
                    var last = move > 0 ? LastDirection.Negative : LastDirection.Positive;
                    BacklashModePlanner.PlanMoves(mode, move, 6f, 6f, last)
                        .Should().Equal(BacklashModePlanner.PlanMoves(mode, move, 6f, last), $"mode={mode}, move={move}");
                }
            }
        }

        [Test]
        public void OneSidedPlay_NeedsNoCompensation_EnteringTheFreeDirection() {
            // A gravity-loaded axis can lose essentially nothing one way: a plain move
            // already lands exactly, and inventing an excursion would only cost time.
            BacklashModePlanner.PlanMoves(OapaBacklashMode.Unidirectional, -15f, 40f, 0f, LastDirection.Positive)
                .Should().Equal(new[] { -15f });
        }

        [Test]
        public void Recommendation_FollowsTheWorseDirection() {
            // A mode safe for the cheap transition is not necessarily safe for the expensive
            // one, so the larger figure decides.
            BacklashModePlanner.Recommend(backlashEnteringPositive: 1f, backlashEnteringNegative: 20f, noiseSigmaArcmin: 0.1f)
                .Should().Be(OapaBacklashMode.Unidirectional);
            BacklashModePlanner.Recommend(backlashEnteringPositive: 0.2f, backlashEnteringNegative: 2f, noiseSigmaArcmin: 0.1f)
                .Should().Be(OapaBacklashMode.Full);
        }
    }
}
