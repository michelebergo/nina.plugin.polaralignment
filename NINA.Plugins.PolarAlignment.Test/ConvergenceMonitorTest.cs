using FluentAssertions;
using NINA.Plugins.PolarAlignment;
using NUnit.Framework;

namespace NINA.Plugins.PolarAlignment.Test {

    [TestFixture]
    public class ConvergenceMonitorTest {

        private static ConvergenceMonitor NewMonitor() => new ConvergenceMonitor(0.5);

        [Test]
        public void TwoConsecutiveBelowTolerance_Finishes() {
            var m = NewMonitor();
            m.Observe(0.42, 0.3, true).Action.Should().Be(ConvergenceAction.AwaitConfirmation);
            m.Observe(0.40, 0, false).Action.Should().Be(ConvergenceAction.Finish);
        }

        [Test]
        public void ConfirmationSurvivesReadingWithinAbsoluteMargin() {
            // reading within tolerance+0.1 must not reset the confirmation counter
            var m = NewMonitor();
            m.Observe(0.42, 0.3, true).Action.Should().Be(ConvergenceAction.AwaitConfirmation);
            m.Observe(0.58, 0, false).Action.Should().Be(ConvergenceAction.AwaitConfirmation);
            m.Observe(0.45, 0, false).Action.Should().Be(ConvergenceAction.Finish);
        }

        [Test]
        public void ConfirmationResetsAboveMargin() {
            var m = NewMonitor();
            m.Observe(0.42, 0.3, true).Action.Should().Be(ConvergenceAction.AwaitConfirmation);
            m.Observe(0.64, 0, false).Action.Should().Be(ConvergenceAction.Continue);
            m.MinimumAchievedArcmin.Should().Be(0.42);
        }

        [Test]
        public void StationaryDrift_SetsDegraded() {
            var m = NewMonitor();
            m.Observe(3.0, 0, false, isFirstObservation: true);
            m.Observe(3.4, 0, false); // +0.4' with no movement > 0.25' threshold
            m.EstimateDegraded.Should().BeTrue();
        }

        [Test]
        public void MovementExplainsChange_NoDegradation() {
            var m = NewMonitor();
            m.Observe(3.0, 2.0, true, isFirstObservation: true);
            m.Observe(1.2, 2.0, true);
            m.EstimateDegraded.Should().BeFalse();
        }

        [Test]
        public void WorseningStreakWithLargeMoves_HaltsAsCalibrationSuspect() {
            var m = NewMonitor();
            m.Observe(2.0, 1.5, true, isFirstObservation: true);
            m.Observe(2.2, 1.5, true).Action.Should().Be(ConvergenceAction.Continue);
            m.Observe(2.5, 1.5, true).Action.Should().Be(ConvergenceAction.Continue);
            m.Observe(2.9, 1.5, true).Action.Should().Be(ConvergenceAction.HaltCalibrationSuspect);
        }

        [Test]
        public void WorseningStreakWithSubNoiseMoves_HaltsAsEstimateDrift() {
            // rc7 field case: guard fired after 0.23'/1' nudges — must blame the estimate, not calibration.
            var m = NewMonitor();
            m.Observe(0.62, 0.23, true, isFirstObservation: true);
            m.Observe(0.85, 0.23, true).Action.Should().Be(ConvergenceAction.Continue);
            m.Observe(1.10, 0.9, true).Action.Should().Be(ConvergenceAction.Continue);
            m.Observe(1.41, 0.9, true).Action.Should().Be(ConvergenceAction.HaltEstimateDrift);
        }

        [Test]
        public void ImprovementResetsWorseningStreak() {
            var m = NewMonitor();
            m.Observe(2.0, 1.5, true, isFirstObservation: true);
            m.Observe(2.2, 1.5, true);
            m.Observe(2.5, 1.5, true);
            m.Observe(1.8, 1.5, true).Action.Should().Be(ConvergenceAction.Continue);
            m.Observe(2.0, 1.5, true).Action.Should().Be(ConvergenceAction.Continue);
        }

        [Test]
        public void OscillationAroundAchievedMinimum_FinishesBestEffort() {
            var m = NewMonitor();
            m.Observe(0.45, 0.3, true).Action.Should().Be(ConvergenceAction.AwaitConfirmation); // min achieved 0.45
            m.Observe(0.75, 0.2, true); // resets confirmation, oscillation 1 (movement explains the change -> no drift flag)
            m.Observe(0.70, 0.2, true); // oscillation 2
            m.Observe(0.72, 0.2, true); // oscillation 3
            var last = m.Observe(0.74, 0.2, true); // oscillation 4 -> best effort
            last.Action.Should().Be(ConvergenceAction.FinishBestEffort);
            m.MinimumAchievedArcmin.Should().Be(0.45);
        }

        [Test]
        public void DegradedWithAchievedMinimum_FinishesBestEffortImmediately() {
            var m = NewMonitor();
            m.Observe(0.45, 0.3, true).Action.Should().Be(ConvergenceAction.AwaitConfirmation);
            var d = m.Observe(0.80, 0, false); // stationary +0.35 > 0.25 → degraded, min exists → best effort
            d.Action.Should().Be(ConvergenceAction.FinishBestEffort);
        }

        [Test]
        public void WithinMarginReadingsDoNotCountAsWorsenings() {
            var m = NewMonitor();
            m.Observe(0.42, 0.3, true);
            m.Observe(0.55, 0, false);
            m.Observe(0.58, 0, false);
            m.Observe(0.60, 0, false).Action.Should().NotBe(ConvergenceAction.HaltEstimateDrift);
        }

        [Test]
        public void DegradedWithMinimum_PrefersBestEffortOverHalt() {
            // Once a sub-tolerance minimum exists, degradation detected on a stationary
            // reading must resolve to a graceful best-effort finish, not fall through to
            // the worsening-streak/halt path.
            var m = NewMonitor();
            m.Observe(0.42, 0.3, true).Action.Should().Be(ConvergenceAction.AwaitConfirmation);
            var d = m.Observe(0.68, 0, false); // stationary +0.26 > 0.25 -> degraded; minimum exists -> best effort, not halt
            d.Action.Should().Be(ConvergenceAction.FinishBestEffort);
        }

        [Test]
        public void FirstObservationBelowTolerance_CountsAsConfirmation() {
            var m = NewMonitor();
            m.Observe(0.35, 0, false, isFirstObservation: true).Action.Should().Be(ConvergenceAction.AwaitConfirmation);
            m.Observe(0.40, 0, false).Action.Should().Be(ConvergenceAction.Finish);
        }

        [Test]
        public void FirstObservationAboveTolerance_Continues() {
            var m = NewMonitor();
            m.Observe(2.0, 0, false, isFirstObservation: true).Action.Should().Be(ConvergenceAction.Continue);
        }
    }
}
