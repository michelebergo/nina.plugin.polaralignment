using FluentAssertions;
using NINA.Plugins.PolarAlignment.OAPA;
using NUnit.Framework;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// Reconstructing that a tester was running 62 arcmin of altitude backlash meant deriving
    /// it from the arithmetic of the $J= commands, because the effective per-axis parameters
    /// were never logged. One line per axis at connect makes a log self-sufficient.
    /// </summary>
    public class OapaParameterSummaryTest {

        [Test]
        public void ForAxis_ReportsEveryParameterNeededToReadAMoveCommand() {
            var line = OapaParameterSummary.ForAxis("Y (Altitude)", 600.0, 62.0, "Full", 1000.0);

            line.Should().Contain("Y (Altitude)");
            line.Should().Contain("600");      // steps per arcmin - turns $J= counts into arcmin
            line.Should().Contain("62");       // the value that made the corrections explode
            line.Should().Contain("Full");     // whether a reversal pays that backlash
            line.Should().Contain("1000");     // why a compensated move took 50 seconds
        }

        [Test]
        public void ForAxis_StatesTheSkyRateImpliedByTheStepRate() {
            // The same step rate is a very different sky speed per axis; without this the
            // 50-second moves in the field log look like a hang rather than arithmetic.
            var line = OapaParameterSummary.ForAxis("Y (Altitude)", 600.0, 62.0, "Full", 1000.0);

            line.Should().Contain("1.67");     // 1000 steps/s / 600 steps per arcmin
        }

        [Test]
        public void ForAxis_OmitsTheSkyRateWhenTheGearRatioIsNotUsable() {
            // Inventing a rate from a zero or negative ratio would be worse than showing none.
            var line = OapaParameterSummary.ForAxis("X (Azimuth)", 0.0, 0.0, "Off", 1000.0);

            line.Should().Contain("X (Azimuth)");
            line.Should().NotContain("'/s");
        }
    }
}
