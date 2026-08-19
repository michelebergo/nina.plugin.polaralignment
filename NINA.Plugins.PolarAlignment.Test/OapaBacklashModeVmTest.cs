using FluentAssertions;
using NINA.Plugins.PolarAlignment.OAPA;
using System;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Equipment.Interfaces.Mediator;
using System.Linq;
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
            // Symmetric by construction: these tests are about the modes, not about
            // direction-dependent play, and the setting is process-global.
            vm.YBacklashCompensationNegative = yCompensation;
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

        /// <summary>
        /// Drives the axis negative so the one-sided clearing has something to do, then runs an
        /// absolute move and returns only what the clearing emitted.
        /// </summary>
        private static async Task<List<(Axis axis, float move)>> ClearingAfterAbsoluteMove(
            UniversalPolarAlignmentOAPAVM vm, FakeSystem system) {

            (await vm.TryNudgeY(-10f, CancellationToken.None)).Should().BeTrue();
            system.RelativeMoves.Clear();
            vm.TargetPositionY = -20f;
            await vm.MoveY(CancellationToken.None);
            return system.RelativeMoves;
        }

        [Test]
        public async Task Off_AbsoluteMove_CompensatesNothing() {
            // Field report (gilas, 17/08): with the mode on Off, every "move to" was still
            // followed by two extra moves of the full backlash - 34.82' on that rig, several
            // turns of the adjustment knob. The modes only governed the relative nudges; the
            // absolute path asked for a compensation value and got the raw setting.
            //
            // It is not only an annoyance: it makes measuring one's own backlash by hand
            // impossible, because the only tool available for the measurement perturbs the
            // thing being measured.
            var (vm, system) = Vm(OapaBacklashMode.Off);

            var clearing = await ClearingAfterAbsoluteMove(vm, system);

            clearing.Should().BeEmpty("Off has to mean off on every path that moves the axis");
        }

        [Test]
        public async Task Full_AbsoluteMove_StillCompensates() {
            // The other half of the same claim: silencing Off must not silence the modes that
            // are supposed to compensate.
            var (vm, system) = Vm(OapaBacklashMode.Full);

            var clearing = await ClearingAfterAbsoluteMove(vm, system);

            clearing.Should().NotBeEmpty("a configured mode still clears the play after an absolute move");
        }

        [Test]
        public void AReversalFinerThanTheMeasurement_IsNotCommanded() {
            // Field case (16/08): the correction loop asked the altitude axis for 0.19' with a
            // 0.68' compensation configured. The plan came out at 0.87', the error tripled,
            // and the same thing happened four times before the alignment gave up at 0.38' -
            // where a fresh measurement then found 17'34". A reversal finer than the play was
            // measured to cannot be honoured: compensating overshoots it, not compensating
            // loses it to the play.
            var plan = BacklashModePlanner.PlanMoves(OapaBacklashMode.Full, -0.19f, 2.19f, 0.68f,
                LastDirection.Positive, minimumReversalArcmin: 0.25f);

            plan.Should().BeEmpty();
        }

        [Test]
        public void TheSameRequestInTheEngagedDirection_IsCommandedInFull() {
            // Only reversals pay the play. An axis continuing the way it was already going
            // arrives exactly at any size, which is why a clean axis keeps converging as
            // finely as the solver can measure.
            var plan = BacklashModePlanner.PlanMoves(OapaBacklashMode.Full, 0.19f, 2.19f, 0.68f,
                LastDirection.Positive, minimumReversalArcmin: 0.25f);

            plan.Should().Equal(0.19f);
        }

        [Test]
        public void WithoutAMeasuredFloor_SmallReversalsAreStillPlanned() {
            // The floor is supplied by whoever knows how well the play was measured, and is
            // absent by default: the field replay suite shows that under a mechanism losing
            // exactly its configured play, small reversals land exactly and are needed to
            // converge. Their size is not what makes them unsafe.
            var plan = BacklashModePlanner.PlanMoves(OapaBacklashMode.Full, -0.19f, 2.19f, 0.68f,
                LastDirection.Positive);

            plan.Should().Equal(-0.19f - 0.68f);
        }

        [Test]
        public async Task SameDirection_AnyMode_PlainMove() {
            var (vm, system) = Vm(OapaBacklashMode.Unidirectional);
            (await vm.TryNudgeY(15f, CancellationToken.None)).Should().BeTrue();
            system.RelativeMoves.Clear();
            (await vm.TryNudgeY(5f, CancellationToken.None)).Should().BeTrue();
            system.RelativeMoves.Should().Equal((Axis.YAxis, 5f));
        }

        /// <summary>Applies a calibration result with the given Y pair, single-step.</summary>
        private static UniversalPolarAlignmentOAPAVM ApplyYPair(float positive, float negative,
            UniversalPolarAlignmentOAPAVM existing = null) {

            var vm = existing;
            if (vm == null) {
                vm = new UniversalPolarAlignmentOAPAVM(null, null, null, null, null);
                vm.upa = new FakeSystem();
                Properties.Settings.Default.OAPAXBacklashSource = "Default";
                Properties.Settings.Default.OAPAYBacklashSource = "Default";
                Properties.Settings.Default.OAPAXGearRatioSource = "Default";
                Properties.Settings.Default.OAPAYGearRatioSource = "Default";
                // The remembered split outlives the VM - it is a setting, and settings are
                // process-global in these fixtures. Without clearing it, a test asking for a
                // first-ever calibration inherits the previous test's pass and gets its split
                // confirmed instead of collapsed.
                Properties.Settings.Default.OAPAXBacklashSplitLast = 0f;
                Properties.Settings.Default.OAPAYBacklashSplitLast = 0f;
            }
            vm.DiscoveredXRatio = 100;
            vm.DiscoveredYRatio = 100;
            vm.DiscoveredXBacklash = 1f;
            vm.DiscoveredXBacklashNegative = 1f;
            vm.DiscoveredYBacklash = positive;
            vm.DiscoveredYBacklashNegative = negative;
            vm.DiscoveredXNoise = 0.05f;
            vm.DiscoveredYNoise = 0.05f;
            vm.HasCalibrationResult = true;
            vm.ApplyCalibration();
            return vm;
        }

        [Test]
        public void AFirstDirectionSplit_IsAppliedAsItsMean_UntilASecondPassAgrees() {
            // One pass cannot tell a real asymmetry from a slipped measurement, and the two
            // are not equally cheap to get wrong: the difference between the pair lands as a
            // fixed bias on every reversal, so the axis can no longer be corrected by less
            // than that difference. Two rigs stalled at exactly their configured gap.
            var vm = ApplyYPair(2.19f, 0.68f);

            vm.YBacklashCompensation.Should().BeApproximately(1.435f, 0.01f);
            vm.YBacklashCompensationNegative.Should().BeApproximately(1.435f, 0.01f);
        }

        [Test]
        public void WhatApplyReports_IsWhatTheAxisWillUse_NotWhatWasMeasured() {
            // Field log (Valo, 18/08) read, in the same second:
            //   "applying the mean 2.99' to both directions"
            //   "OAPA calibration applied: ... backlash X=+1.66'/-4.32' ..."
            // Whoever reads the second line believes the split was applied. The panel's status
            // line carried the same contradiction, and it is the only place a user can look.
            var vm = ApplyYPair(2.19f, 0.68f);

            vm.CalibrationStatus.Should().Contain("1.44", "the mean is what the axis will use");
            vm.CalibrationStatus.Should().NotContain("2.19");
            vm.CalibrationStatus.Should().NotContain("0.68");
        }

        [Test]
        public void ASplitThatFlipsBetweenCalibrations_IsCollapsed_NotApplied() {
            // Field evidence, one rig, two consecutive nights, same axis: 1.45'/1.96' and then
            // 2.19'/0.68'. The sum barely moved, the larger side changed places. A stable sum
            // with a flipped split is slippage, not mechanics.
            var vm = ApplyYPair(1.45f, 1.96f);
            ApplyYPair(1.50f, 2.10f, vm);          // agrees: the negative side is heavier both times
            vm.YBacklashCompensation.Should().BeApproximately(1.50f, 0.01f, "two passes agreed on which side is heavier");
            vm.YBacklashCompensationNegative.Should().BeApproximately(2.10f, 0.01f);

            ApplyYPair(2.19f, 0.68f, vm);          // flips

            vm.YBacklashCompensation.Should().BeApproximately(1.435f, 0.01f);
            vm.YBacklashCompensationNegative.Should().BeApproximately(1.435f, 0.01f);
        }

        [Test]
        public void ApplyCalibration_SetsTheRecommendedModePerAxis_AndSaysSo() {
            var vm = new UniversalPolarAlignmentOAPAVM(null, null, null, null, null);
            vm.upa = new FakeSystem();
            vm.XBacklashMode = OapaBacklashMode.Full;
            vm.YBacklashMode = OapaBacklashMode.Full;
            // Arranged values are test fixtures, not manual user edits: keep Apply single-step.
            Properties.Settings.Default.OAPAXGearRatioSource = "Default";
            Properties.Settings.Default.OAPAYGearRatioSource = "Default";
            Properties.Settings.Default.OAPAXBacklashSource = "Default";
            Properties.Settings.Default.OAPAYBacklashSource = "Default";
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
        // ===== Play hysteresis in the fine phase =====
        //
        // Near the tolerance the polar-error estimate wanders more than the error itself
        // (0.083' between consecutive readings against 0.2' of residual on the 18/08 rig),
        // so the *direction* of each requested correction is largely noise. Compensating the
        // play means executing every one of those jitter-driven reversals faithfully - which
        // is how a 1' probe move destroyed an alignment that had reached 22 arcseconds.
        // Leaving the play uncompensated below a band turns the mechanism's own play into a
        // hysteresis element that swallows them.

        /// <summary>
        /// Presents the VM with a live total error, the way the correction loop does every
        /// cycle. A run is reported from where it started, because the rule stands down when
        /// the play is comparable to the whole misalignment - so a test that reports only the
        /// residual is testing a run that never had a coarse phase.
        /// </summary>
        private static void Observing(UniversalPolarAlignmentOAPAVM vm, double totalErrorArcmin,
                                      double startedAtArcmin = 240) {
            vm.GetMaximumCorrectionMagnitude(startedAtArcmin);
            vm.GetMaximumCorrectionMagnitude(totalErrorArcmin);
        }

        private static (UniversalPolarAlignmentOAPAVM vm, FakeSystem system) HysteresisVm(
                float play, double multiple = 3.0, bool enabled = true) {
            var (vm, system) = Vm(OapaBacklashMode.Full, play);
            vm.AdaptiveSpeedUp = false;
            vm.MaxCorrectionMagnitude = 30;   // process-global setting; another test moves it
            vm.PlayHysteresis = enabled;
            vm.PlayHysteresisMultiple = multiple;
            return (vm, system);
        }

        [Test]
        public async Task AboveTheBand_TheReversalIsStillCompensated() {
            var (vm, system) = HysteresisVm(play: 8f);   // band 24', ceiling 30'
            Observing(vm, 40.0);
            await Reversal(vm, system);
            system.RelativeMoves.Should().Equal(new[] { (Axis.YAxis, -18f) }, "40' of error is outside the 24' band");
        }

        [Test]
        public async Task InsideTheBand_TheReversalIsCommandedExactlyAsAsked() {
            var (vm, system) = HysteresisVm(play: 8f);   // band 24'
            Observing(vm, 5.0);
            await Reversal(vm, system);
            system.RelativeMoves.Should().Equal(new[] { (Axis.YAxis, -10f) },
                "inside the band the play is left to absorb jitter-driven reversals");
        }

        [Test]
        public async Task WhenThePlayExceedsTheCeiling_TheBandFollowsThePlay_NotTheCeiling() {
            // A 56' play axis cannot be compensated safely at any error the ceiling allows:
            // the largest move it can be asked for is 30', and adding 56' to it makes the
            // commanded travel a bet whose stake is bigger than the move itself. Clamping the
            // band to the ceiling would compensate exactly in that range; the 08/08 rig ends
            // 3.55' out that way instead of converging at 0.37'.
            var (vm, system) = HysteresisVm(play: 56f);
            Observing(vm, 40.0);
            await Reversal(vm, system);
            system.RelativeMoves.Should().Equal(new[] { (Axis.YAxis, -10f) },
                "40' is inside the 56' band, which the 30' ceiling must not cut");
        }

        [Test]
        public async Task TheBandNeverCoversTheCoarsePhase() {
            // Without this bound a large multiple disables compensation for the whole run,
            // which is what left a 56' play rig 266' out in the replay suite. The correction
            // ceiling bounds the band, so an absurd multiple is harmless rather than fatal.
            var (vm, system) = HysteresisVm(play: 8f, multiple: 1000.0);
            Observing(vm, 40.0);
            await Reversal(vm, system);
            system.RelativeMoves.Should().Equal(new[] { (Axis.YAxis, -18f) }, "the band is capped at the 30' ceiling");
        }

        [Test]
        public async Task WithTheRuleOff_TheFinePhaseBehavesExactlyAsBefore() {
            var (vm, system) = HysteresisVm(play: 8f, enabled: false);
            Observing(vm, 0.2);
            await Reversal(vm, system);
            system.RelativeMoves.Should().Equal((Axis.YAxis, -18f));
        }

        [Test]
        public async Task WithNoErrorObservedYet_TheRuleStaysOut() {
            // Manual nudging from the panel happens with no alignment running and no error
            // published. Suspending compensation there would silently change what the buttons
            // do, so the rule only engages once the loop has reported where it is.
            var (vm, system) = HysteresisVm(play: 8f);
            await Reversal(vm, system);
            system.RelativeMoves.Should().Equal((Axis.YAxis, -18f));
        }

        [Test]
        public void TheBandIsReportedPerAxis_SoThePanelCanShowIt() {
            var (vm, _) = HysteresisVm(play: 8f);
            vm.XBacklashCompensation = 2f;
            vm.XBacklashCompensationNegative = 2f;
            Observing(vm, 1.0);   // a run that started far enough out for the rule to engage
            vm.PlayHysteresisBandArcmin(Axis.XAxis).Should().BeApproximately(6.0, 1e-6);
            vm.PlayHysteresisBandArcmin(Axis.YAxis).Should().BeApproximately(24.0, 1e-6);
        }
        [Test]
        public async Task AnObservationThatHasGoneStale_NoLongerSuspendsCompensation() {
            // The correction loop is the only thing that reports where the alignment stands,
            // so the last value it reported sits in the VM for as long as the panel is open.
            // Nudging by hand the next evening must not be governed by last night's error.
            var (vm, system) = HysteresisVm(play: 8f);
            var now = new DateTime(2026, 8, 19, 22, 0, 0, DateTimeKind.Utc);
            vm.Clock = () => now;
            Observing(vm, 0.2);

            now = now.AddMinutes(5);
            await Reversal(vm, system);

            system.RelativeMoves.Should().Equal(new[] { (Axis.YAxis, -18f) },
                "an observation this old says nothing about where the platform is now");
        }

        [Test]
        public async Task AnObservationFromTheCurrentCycle_StillCounts() {
            var (vm, system) = HysteresisVm(play: 8f);
            var now = new DateTime(2026, 8, 19, 22, 0, 0, DateTimeKind.Utc);
            vm.Clock = () => now;
            Observing(vm, 0.2);

            now = now.AddSeconds(12);   // one correction cycle
            await Reversal(vm, system);

            system.RelativeMoves.Should().Equal(new[] { (Axis.YAxis, -10f) });
        }

        [Test]
        public void TheBandIsOnePureFunction_SharedWithTheReplayHarness() {
            // The replay suite convinced us this rule works. It only remains evidence if the
            // suite and production compute the same band, so there is exactly one definition.
            const double seen = 400;   // a run that started far out: the third bound is slack
            UniversalPolarAlignmentOAPAVM.PlayHysteresisBand(8, 3, 30, seen)
                .Should().BeApproximately(24, 1e-9);
            UniversalPolarAlignmentOAPAVM.PlayHysteresisBand(56, 3, 30, seen)
                .Should().BeApproximately(56, 1e-9, "the ceiling may not cut the band below one play");
            UniversalPolarAlignmentOAPAVM.PlayHysteresisBand(8, 1000, 30, seen)
                .Should().BeApproximately(30, 1e-9, "the band may not cover the coarse phase");
            UniversalPolarAlignmentOAPAVM.PlayHysteresisBand(0, 3, 30, seen)
                .Should().Be(0, "no measurable play, nothing to leave uncompensated");
        }

        [Test]
        public void WhenThePlayIsComparableToTheWholeMisalignment_TheRuleStandsDown() {
            // Found by a randomised sweep of 800 synthetic rigs, not by the archive: with 56'
            // of play and a run that starts 60' out, the band covers the entire run, every
            // move is smaller than the play, and nothing is ever delivered. Rigs that
            // converged at 0.5' without the rule ended 82' out with it.
            UniversalPolarAlignmentOAPAVM.PlayHysteresisBand(56, 3, 30, runStartedAtArcmin: 60)
                .Should().Be(0, "compensating is the lesser evil once the play is half the error");
            UniversalPolarAlignmentOAPAVM.PlayHysteresisBand(56, 3, 30, runStartedAtArcmin: 256)
                .Should().BeApproximately(56, 1e-9, "the 08/08 rig started far enough out for the rule to help");
        }

        [Test]
        public void TheBandNeverCoversTheRunItIsIn() {
            // Even where the rule does engage, the band is kept below half of what the run
            // started with, so a coarse phase always exists in which the play is compensated.
            UniversalPolarAlignmentOAPAVM.PlayHysteresisBand(8, 3, 30, runStartedAtArcmin: 30)
                .Should().BeApproximately(15, 1e-9, "24' would leave only the last 6' compensated");
        }

        [Test]
        public void BeforeAnyRun_ThePanelStillReportsTheBand() {
            // Found on the bench: with 4' of play measured on both axes and no alignment yet
            // run, the panel said "no measurable play on either axis". The live band folds in
            // the error the current run started with, which is zero outside a run - so the
            // display has to use the configured band, not the live one.
            var (vm, _) = HysteresisVm(play: 4f);
            vm.XBacklashCompensation = 4f;
            vm.XBacklashCompensationNegative = 4f;

            vm.PlayHysteresisStatus.Should().NotContain("no measurable play");
            vm.PlayHysteresisStatus.Should().Contain("12.00'");
            vm.ConfiguredPlayHysteresisBandArcmin(Axis.YAxis).Should().BeApproximately(12, 1e-9);
            vm.PlayHysteresisBandArcmin(Axis.YAxis).Should().Be(0, "no run has said where the platform is");
        }

        [Test]
        public void WithNoPlayAtAll_ThePanelSaysThatInstead() {
            var (vm, _) = HysteresisVm(play: 0f);
            vm.XBacklashCompensation = 0f;
            vm.XBacklashCompensationNegative = 0f;

            vm.PlayHysteresisStatus.Should().Contain("no measurable play");
        }

        [Test]
        public void TheRuleAnnouncesItselfOncePerRun() {
            // Not a display concern: a tester's log has to say whether the play was being
            // compensated, and the band cannot be reconstructed from the settings alone
            // because it depends on the error the run started with.
            var (vm, _) = HysteresisVm(play: 4f);
            var now = new DateTime(2026, 8, 19, 22, 0, 0, DateTimeKind.Utc);
            vm.Clock = () => now;

            vm.GetMaximumCorrectionMagnitude(240);      // run starts
            vm.GetMaximumCorrectionMagnitude(60);       // same run
            vm.GetMaximumCorrectionMagnitude(2);        // same run
            now = now.AddMinutes(10);
            vm.GetMaximumCorrectionMagnitude(180);      // a different run

            // The observable consequence: the second run re-reads its own starting error, so
            // the band follows it rather than the previous run's.
            vm.PlayHysteresisBandArcmin(Axis.YAxis).Should().BeApproximately(12, 1e-9);
        }

        [Test]
        public void AZeroBandWidth_IsNotReportedAsAPlayProblem() {
            // Field log 19/08: a tester set the band width to zero and the log said "standing
            // down (3.23' of play is too much for this run)" on a rig whose run started at
            // five degrees. The play was fine; the rule was simply switched off by the number.
            var (vm, _) = HysteresisVm(play: 4f, multiple: 0);
            Observing(vm, 1.0);

            vm.PlayHysteresisStatus.Should().NotContain("too much");
            vm.PlayHysteresisBandArcmin(Axis.YAxis).Should().Be(0);
        }

        // ===== Why the Calibrate button is disabled =====
        //
        // Field report (18/08): an alignment halted, the halt message told the user to
        // "re-run the OAPA Self-Calibration", and the button was greyed out with no
        // explanation. A halt pauses the alignment rather than ending it, and a paused
        // alignment still owns the camera - so the one remedy we had just recommended was
        // silently unavailable. The user rebooted the machine. The button state was right;
        // the silence was the defect.

        [Test]
        public void WhenNothingIsInTheWay_ThereIsNoReason() {
            UniversalPolarAlignmentOAPAVM.CalibrationBlockedBy(
                connected: true, moving: false, calibrating: false, cameraBusy: false).Should().BeEmpty();
        }

        [Test]
        public void NotConnected_IsSaidFirst() {
            // Reported ahead of everything else even when other conditions also fail: it is
            // the only one the user can act on without knowing anything about the rest.
            var reason = UniversalPolarAlignmentOAPAVM.CalibrationBlockedBy(
                connected: false, moving: true, calibrating: false, cameraBusy: true);

            reason.Should().ContainEquivalentOf("connect");
        }

        [Test]
        public void AMovingAxis_IsSaidSo() {
            UniversalPolarAlignmentOAPAVM.CalibrationBlockedBy(
                connected: true, moving: true, calibrating: false, cameraBusy: false)
                .Should().Contain("moving");
        }

        [Test]
        public void ACalibrationAlreadyRunning_IsSaidSo() {
            UniversalPolarAlignmentOAPAVM.CalibrationBlockedBy(
                connected: true, moving: false, calibrating: true, cameraBusy: false)
                .Should().Contain("already");
        }

        [Test]
        public void ACameraHeldByAnAlignment_CarriesTheRemedy_NotJustTheDiagnosis() {
            // The 18/08 case. Saying "the camera is busy" would have left the user exactly
            // where they were: what unblocks them is knowing that the halted alignment is
            // still running and has to be stopped.
            var reason = UniversalPolarAlignmentOAPAVM.CalibrationBlockedBy(
                connected: true, moving: false, calibrating: false, cameraBusy: true);

            reason.Should().Contain("camera").And.Contain("alignment").And.Contain("Stop");
        }

        [Test]
        public void TheReasonIsEmptyExactlyWhenTheButtonIsEnabled() {
            // The invariant that keeps the two from drifting apart: a disabled button always
            // has something to say, an enabled one never does.
            foreach (var connected in new[] { false, true }) {
                foreach (var moving in new[] { false, true }) {
                    var (vm, _) = Vm(OapaBacklashMode.Off);
                    vm.Connected = connected;
                    vm.IsNotMoving = !moving;

                    vm.CalibrateUnavailableReason.Any().Should().Be(!vm.CanCalibrate(),
                        $"connected={connected}, moving={moving}");
                }
            }
        }
    }
}
