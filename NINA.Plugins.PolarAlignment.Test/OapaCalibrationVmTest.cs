using FluentAssertions;
using NINA.Plugins.PolarAlignment.OAPA;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// Drives the calibration through the production VM command with a fake controller
    /// and solver: a slippage verdict must disable Apply with an explanatory message,
    /// and an asymmetry flag must report both directional ratios.
    /// </summary>
    public class OapaCalibrationVmTest {

        /// <summary>
        /// Fake OAPA hardware and solver in one: motion updates per-axis physical positions
        /// (with configurable response scale and per-reversal backlash sequence), solves
        /// report them. Altitude ~0 keeps cos(alt)=1 so measured azimuth equals posX.
        /// </summary>
        private sealed class FakeRig : IPolarAlignmentSystem, IOapaCalibrationSolver {
            private readonly double forwardScale;
            private readonly double reverseScale;
            private readonly double[] backlashSequence;
            private readonly Dictionary<Axis, (double pos, int lastSign, int reversals)> axes = new() {
                [Axis.XAxis] = (0, 0, 0),
                [Axis.YAxis] = (0, 0, 0),
            };

            public FakeRig(double forwardScale = 1.0, double? reverseScale = null, double[] backlashSequence = null) {
                this.forwardScale = forwardScale;
                this.reverseScale = reverseScale ?? forwardScale;
                this.backlashSequence = backlashSequence ?? new[] { 0.0 };
            }

            public bool Connected => true;
            public string Status => "Idle";
            public float XPosition1 => 0;
            public float YPosition1 => 0;
            public float ZPosition1 => 0;
            public float XGearRatio { get; set; } = 1;
            public float YGearRatio { get; set; } = 1;
            public float ZGearRatio { get; set; } = 1;
            public LastDirection XLastDirection => LastDirection.Positive;
            public LastDirection YLastDirection => LastDirection.Positive;
            public LastDirection ZLastDirection => LastDirection.Positive;

            public Task MoveRelative(Axis axis, int speed, float position, CancellationToken token) {
                var (pos, lastSign, reversals) = axes[axis];
                var sign = Math.Sign(position);
                var scale = sign >= 0 ? forwardScale : reverseScale;
                double effective = Math.Abs(position) * scale;
                if (sign != 0 && lastSign != 0 && sign != lastSign) {
                    effective = Math.Max(0, effective - backlashSequence[reversals % backlashSequence.Length]);
                    reversals++;
                }
                if (sign != 0) { lastSign = sign; }
                axes[axis] = (pos + sign * effective, lastSign, reversals);
                return Task.CompletedTask;
            }

            public Task MoveAbsolute(Axis axis, int speed, float position, CancellationToken token) => Task.CompletedTask;
            public Task RefreshStatus(CancellationToken token) => Task.CompletedTask;
            public void Dispose() { }

            public Task<CalibrationSolveSample> CaptureAndSolve(CancellationToken token) {
                return Task.FromResult(new CalibrationSolveSample(
                    10.0, axes[Axis.YAxis].pos / 60.0, axes[Axis.YAxis].pos / 60.0, 100.0 + axes[Axis.XAxis].pos / 60.0));
            }
        }

        private static UniversalPolarAlignmentOAPAVM Vm(FakeRig rig) {
            var vm = new UniversalPolarAlignmentOAPAVM(null, null, null, null, null);
            vm.upa = rig;
            vm.calibrationSolver = rig;
            vm.ReverseAzimuth = false;
            vm.ReverseAltitude = false;
            vm.XGearRatio = 100;
            vm.YGearRatio = 100;
            return vm;
        }

        [Test]
        public async Task SlippingMechanics_DisableApply_WithDiagnosis() {
            var vm = Vm(new FakeRig(backlashSequence: new[] { 20.0, 8.0 }));

            await vm.CalibrateGearRatios(CancellationToken.None);

            vm.HasCalibrationResult.Should().BeTrue("the measured values are still shown for diagnosis");
            vm.CalibrationSlippageDetected.Should().BeTrue();
            vm.CanApplyCalibration().Should().BeFalse("no constant compensation is valid on slipping mechanics");
            vm.CalibrationConsistencyMessage.Should().Contain("Slippage");
            vm.CalibrationConsistencyMessage.Should().Contain("Apply is disabled");
        }

        [Test]
        public async Task RepeatableMechanics_KeepApplyEnabled() {
            var vm = Vm(new FakeRig(backlashSequence: new[] { 5.0 }));

            await vm.CalibrateGearRatios(CancellationToken.None);

            vm.HasCalibrationResult.Should().BeTrue();
            vm.CalibrationSlippageDetected.Should().BeFalse();
            vm.CanApplyCalibration().Should().BeTrue();
        }

        [Test]
        public async Task AsymmetricAxis_ReportsBothDirectionalRatios() {
            var vm = Vm(new FakeRig(forwardScale: 1.0, reverseScale: 0.8));

            await vm.CalibrateGearRatios(CancellationToken.None);

            vm.CalibrationConsistencyMessage.Should().Contain("forward");
            vm.CalibrationConsistencyMessage.Should().Contain("reverse");
        }
    }
}
