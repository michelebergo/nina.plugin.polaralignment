using FluentAssertions;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// The precision-finish averaging rules: a rolling mean of the recent stable
    /// estimates, active only near completion, reset whenever the mount moves.
    /// </summary>
    public class ErrorEstimateAveragerTest {

        private const double Arcmin = 1.0 / 60.0;

        [Test]
        public void NearCompletion_ReturnsTheRollingMean() {
            var averager = new ErrorEstimateAverager();

            averager.Register(0.4 * Arcmin, 0.0);
            var (az, alt) = averager.Register(0.6 * Arcmin, 0.0);

            az.Should().BeApproximately(0.5 * Arcmin, 1e-9);
            alt.Should().Be(0.0);
        }

        [Test]
        public void Window_SlidesOverTheLastFourSamples() {
            var averager = new ErrorEstimateAverager();

            averager.Register(1.0 * Arcmin, 0.0);   // pushed out by the next four
            averager.Register(0.4 * Arcmin, 0.0);
            averager.Register(0.4 * Arcmin, 0.0);
            averager.Register(0.4 * Arcmin, 0.0);
            var (az, _) = averager.Register(0.4 * Arcmin, 0.0);

            az.Should().BeApproximately(0.4 * Arcmin, 1e-9);
        }

        [Test]
        public void FarFromCompletion_PassesThroughAndClears() {
            var averager = new ErrorEstimateAverager();
            averager.Register(0.4 * Arcmin, 0.0);

            // A 30' error is coarse-phase territory: averaging would only add lag.
            var (az, alt) = averager.Register(0.5, 0.0);
            az.Should().Be(0.5);

            // Coming back below the threshold starts a fresh window.
            var (back, _) = averager.Register(0.6 * Arcmin, 0.0);
            back.Should().BeApproximately(0.6 * Arcmin, 1e-9);
        }

        [Test]
        public void Reset_StartsAFreshWindow() {
            var averager = new ErrorEstimateAverager();
            averager.Register(0.4 * Arcmin, 0.0);

            averager.Reset();
            var (az, _) = averager.Register(0.8 * Arcmin, 0.0);

            az.Should().BeApproximately(0.8 * Arcmin, 1e-9, "samples measured before the move no longer describe the state");
        }
    }

    /// <summary>
    /// The TPAPAVM seam: with the mode off the estimate passes through untouched; with
    /// it on, the display/finish value is the rolling mean while a move resets the window.
    /// </summary>
    public class PrecisionFinishModeTest {

        private const double Arcmin = 1.0 / 60.0;

        private static TPAPAVM Vm(bool precision) {
            Properties.Settings.Default.PrecisionFinishMode = precision;
            return new TPAPAVM(null, null);
        }

        [Test]
        public void ModeOff_IsAnIdentityFilter() {
            var vm = Vm(false);

            vm.FilterEstimate(0.4 * Arcmin, 0.2 * Arcmin).Should().Be((0.4 * Arcmin, 0.2 * Arcmin));
            vm.FilterEstimate(0.6 * Arcmin, 0.2 * Arcmin).Should().Be((0.6 * Arcmin, 0.2 * Arcmin));
        }

        [Test]
        public void ModeOn_AveragesUntilAMoveResetsTheWindow() {
            var vm = Vm(true);

            vm.FilterEstimate(0.4 * Arcmin, 0.0);
            var (az, _) = vm.FilterEstimate(0.6 * Arcmin, 0.0);
            az.Should().BeApproximately(0.5 * Arcmin, 1e-9);

            vm.OnAutomatedMoveExecuted();
            var (fresh, _) = vm.FilterEstimate(0.8 * Arcmin, 0.0);
            fresh.Should().BeApproximately(0.8 * Arcmin, 1e-9);
        }
    }
}
