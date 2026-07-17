using FluentAssertions;
using NINA.Plugins.PolarAlignment;
using NUnit.Framework;

namespace NINA.Plugins.PolarAlignment.Test {

    [TestFixture]
    public class MoveCloserAdjustmentTest {

        [Test]
        public void AxisAdjustment_FarFromPole_UsesFarGain() {
            // 5' error, positive sign -> 5 * 0.9 = 4.5
            var adjustment = TPAPAVM.AxisAdjustment(5.0, 1f);
            adjustment.Should().BeApproximately(4.5f, 0.001f);
        }

        [Test]
        public void AxisAdjustment_NearPole_UsesNearGain() {
            // 1' error, positive sign -> 1 * 0.6 = 0.6
            var adjustment = TPAPAVM.AxisAdjustment(1.0, 1f);
            adjustment.Should().BeApproximately(0.6f, 0.001f);
        }

        [Test]
        public void AxisAdjustment_ExactlyAtThreshold_UsesNearGain() {
            var adjustment = TPAPAVM.AxisAdjustment(TPAPAVM.FarThresholdArcmin, 1f);
            adjustment.Should().BeApproximately((float)(TPAPAVM.FarThresholdArcmin * TPAPAVM.NearGain), 0.001f);
        }

        [Test]
        public void AxisAdjustment_NegativeError_PreservesDirection() {
            var adjustment = TPAPAVM.AxisAdjustment(-5.0, 1f);
            adjustment.Should().BeApproximately(-4.5f, 0.001f);
        }

        [Test]
        public void AxisAdjustment_ReversedSign_FlipsDirection() {
            var adjustment = TPAPAVM.AxisAdjustment(5.0, -1f);
            adjustment.Should().BeApproximately(-4.5f, 0.001f);
        }

        [Test]
        public void AxisAdjustment_NegativeErrorNearPole_UsesNearGain() {
            var adjustment = TPAPAVM.AxisAdjustment(-1.5, 1f);
            adjustment.Should().BeApproximately(-0.9f, 0.001f);
        }

        [Test]
        public void DeadBand_IsSmallerThanNearThreshold() {
            // Sanity: the dead-band must be well below the far/near gain threshold,
            // otherwise the correction loop could stall before entering the fine regime.
            TPAPAVM.DeadBandArcmin.Should().BeLessThan(TPAPAVM.FarThresholdArcmin);
            TPAPAVM.DeadBandArcmin.Should().BeGreaterThan(0);
        }
    }
}
