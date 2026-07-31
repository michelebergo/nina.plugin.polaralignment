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
    }
}
