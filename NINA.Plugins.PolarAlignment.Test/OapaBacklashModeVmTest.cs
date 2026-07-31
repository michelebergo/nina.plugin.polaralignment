using FluentAssertions;
using NINA.Plugins.PolarAlignment.OAPA;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// The four OAPA backlash modes through the production nudge paths, and the
    /// mode recommendation applied together with the calibration result.
    /// </summary>
    public class OapaBacklashModeVmTest {

        private sealed class FakeSystem : IPolarAlignmentSystem {
            public readonly List<(Axis axis, float move)> RelativeMoves = new();

            public bool Connected => true;
            public string Status => "Idle";
            public float XPosition1 => 0;
            public float YPosition1 => 0;
            public float ZPosition1 => 0;
            public float XGearRatio { get; set; } = 1;
            public float YGearRatio { get; set; } = 1;
            public float ZGearRatio { get; set; } = 1;
            public LastDirection XLastDirection { get; private set; } = LastDirection.Positive;
            public LastDirection YLastDirection { get; private set; } = LastDirection.Positive;
            public LastDirection ZLastDirection { get; private set; } = LastDirection.Positive;

            public Task MoveRelative(Axis axis, int speed, float position, CancellationToken token) {
                RelativeMoves.Add((axis, position));
                var direction = position >= 0 ? LastDirection.Positive : LastDirection.Negative;
                switch (axis) {
                    case Axis.XAxis: XLastDirection = direction; break;
                    case Axis.YAxis: YLastDirection = direction; break;
                    case Axis.ZAxis: ZLastDirection = direction; break;
                }
                return Task.CompletedTask;
            }

            public Task MoveAbsolute(Axis axis, int speed, float position, CancellationToken token) => Task.CompletedTask;
            public Task RefreshStatus(CancellationToken token) => Task.CompletedTask;
            public void Dispose() { }
        }

        private static (UniversalPolarAlignmentOAPAVM vm, FakeSystem system) Vm(OapaBacklashMode yMode, float yCompensation = 8f) {
            var vm = new UniversalPolarAlignmentOAPAVM(null, null, null, null, null);
            var system = new FakeSystem();
            vm.upa = system;
            vm.ReverseAltitude = false;
            vm.YBacklashCompensation = yCompensation;
            vm.YBacklashMode = yMode;
            return (vm, system);
        }

        private static async Task<FakeSystem> Reversal(UniversalPolarAlignmentOAPAVM vm, FakeSystem system) {
            (await vm.TryNudgeY(15f, CancellationToken.None)).Should().BeTrue();
            system.RelativeMoves.Clear();
            (await vm.TryNudgeY(-10f, CancellationToken.None)).Should().BeTrue();
            return system;
        }

        [Test]
        public async Task Off_Reversal_PlainMove() {
            var (vm, system) = Vm(OapaBacklashMode.Off);
            await Reversal(vm, system);
            system.RelativeMoves.Should().Equal((Axis.YAxis, -10f));
        }

        [Test]
        public async Task Soft_Reversal_ThreeQuartersFoldedIn() {
            var (vm, system) = Vm(OapaBacklashMode.Soft);
            await Reversal(vm, system);
            system.RelativeMoves.Should().Equal((Axis.YAxis, -16f));
        }

        [Test]
        public async Task Full_Reversal_WholeBacklashFoldedIn() {
            var (vm, system) = Vm(OapaBacklashMode.Full);
            await Reversal(vm, system);
            system.RelativeMoves.Should().Equal((Axis.YAxis, -18f));
        }

        [Test]
        public async Task Unidirectional_Reversal_OvershootsAndReturns() {
            var (vm, system) = Vm(OapaBacklashMode.Unidirectional);
            await Reversal(vm, system);
            // Overshoot = B + (0.25*B + 0.5') = 10.5: out to -(10+10.5), back +10.5.
            system.RelativeMoves.Should().Equal((Axis.YAxis, -20.5f), (Axis.YAxis, 10.5f));
        }

        [Test]
        public async Task SameDirection_AnyMode_PlainMove() {
            var (vm, system) = Vm(OapaBacklashMode.Unidirectional);
            (await vm.TryNudgeY(15f, CancellationToken.None)).Should().BeTrue();
            system.RelativeMoves.Clear();
            (await vm.TryNudgeY(5f, CancellationToken.None)).Should().BeTrue();
            system.RelativeMoves.Should().Equal((Axis.YAxis, 5f));
        }

        [Test]
        public void ApplyCalibration_SetsTheRecommendedModePerAxis_AndSaysSo() {
            var vm = new UniversalPolarAlignmentOAPAVM(null, null, null, null, null);
            vm.upa = new FakeSystem();
            vm.XBacklashMode = OapaBacklashMode.Full;
            vm.YBacklashMode = OapaBacklashMode.Full;
            vm.DiscoveredXRatio = 100;
            vm.DiscoveredYRatio = 100;
            vm.DiscoveredXBacklash = 0.2f;   // below measurability -> Off
            vm.DiscoveredYBacklash = 20f;    // large -> Unidirectional
            vm.DiscoveredXNoise = 0.05f;
            vm.DiscoveredYNoise = 0.05f;
            vm.HasCalibrationResult = true;

            vm.ApplyCalibration();

            vm.XBacklashMode.Should().Be(OapaBacklashMode.Off);
            vm.YBacklashMode.Should().Be(OapaBacklashMode.Unidirectional);
            vm.CalibrationStatus.Should().Contain("Backlash mode");
            vm.CalibrationStatus.Should().Contain("Unidirectional");
        }

        [Test]
        public void BacklashMode_PersistsAndParsesItsSetting() {
            var vm = new UniversalPolarAlignmentOAPAVM(null, null, null, null, null);

            vm.XBacklashMode = OapaBacklashMode.Unidirectional;
            Properties.Settings.Default.OAPAXBacklashMode.Should().Be("Unidirectional");
            vm.XBacklashMode.Should().Be(OapaBacklashMode.Unidirectional);

            // A corrupted stored value falls back to the default rather than crashing.
            Properties.Settings.Default.OAPAXBacklashMode = "garbage";
            vm.XBacklashMode.Should().Be(OapaBacklashMode.Full);
        }
    }
}
