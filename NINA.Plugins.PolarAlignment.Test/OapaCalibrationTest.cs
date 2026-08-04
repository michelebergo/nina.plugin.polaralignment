using FluentAssertions;
using NINA.Plugins.PolarAlignment.OAPA;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.Test {

    public class OapaCalibrationGeometryTest {

        private static CalibrationSolveSample Sample(double raDeg, double decDeg, double altDeg = 30, double azDeg = 100) =>
            new(raDeg, decDeg, altDeg, azDeg);

        [Test]
        public void SignedDisplacement_AltitudeAxis_TransfersOneToOne() {
            var a = Sample(10, 0, altDeg: 60);
            var b = Sample(10, 0.5, altDeg: 60.5);
            OapaCalibrationGeometry.SignedAxisDisplacementArcmin(isAzimuthAxis: false, a, b).Should().BeApproximately(30.0, 0.01);
        }

        [Test]
        public void SignedDisplacement_AzimuthAxis_CorrectsForCosAltitude() {
            // At altitude 60°, cos(alt)=0.5: a 30' azimuth sky displacement means 60' of axis motion.
            var a = Sample(10, 0, altDeg: 60, azDeg: 100.0);
            var b = Sample(10, 0.5, altDeg: 60, azDeg: 100.5);
            OapaCalibrationGeometry.SignedAxisDisplacementArcmin(isAzimuthAxis: true, a, b).Should().BeApproximately(60.0, 0.05);
        }

        [Test]
        public void SignedDisplacement_AltitudeRisesOnPositiveCommand_IsConsistent() {
            var a = Sample(10, 0, altDeg: 30);
            var b = Sample(10, 0.5, altDeg: 30.75);
            OapaCalibrationGeometry.SignedDisplacementMatchesCommand(isAzimuthAxis: false, a, b, 45f).Should().BeTrue();
            OapaCalibrationGeometry.SignedDisplacementMatchesCommand(isAzimuthAxis: false, a, b, -45f).Should().BeFalse();
        }

        [Test]
        public void SignedDisplacement_AltitudeFallsOnPositiveCommand_IsInconsistent() {
            // A physically reversed altitude axis: the field drops when commanded up.
            var a = Sample(10, 0, altDeg: 30);
            var b = Sample(10, -0.5, altDeg: 29.25);
            OapaCalibrationGeometry.SignedDisplacementMatchesCommand(isAzimuthAxis: false, a, b, 45f).Should().BeFalse();
            OapaCalibrationGeometry.SignedDisplacementMatchesCommand(isAzimuthAxis: false, a, b, -45f).Should().BeTrue();
        }

        [Test]
        public void SignedDisplacement_AzimuthUsesTopocentricAzimuth() {
            var a = Sample(10, 0, azDeg: 100.0);
            var b = Sample(10.5, 0, azDeg: 100.75);
            OapaCalibrationGeometry.SignedDisplacementMatchesCommand(isAzimuthAxis: true, a, b, 45f).Should().BeTrue();
            OapaCalibrationGeometry.SignedDisplacementMatchesCommand(isAzimuthAxis: true, a, b, -45f).Should().BeFalse();
        }

        [Test]
        public void SignedDisplacement_AzimuthAcrossNorth_KeepsItsSign() {
            // 359.9° -> 0.65° is +0.75° of azimuth, not -359.25°: the wrap must not flip the verdict.
            var a = Sample(10, 0, azDeg: 359.9);
            var b = Sample(10.5, 0, azDeg: 0.65);
            OapaCalibrationGeometry.SignedDisplacementMatchesCommand(isAzimuthAxis: true, a, b, 45f).Should().BeTrue();

            var back = OapaCalibrationGeometry.SignedDisplacementMatchesCommand(isAzimuthAxis: true, b, a, 45f);
            back.Should().BeFalse("0.65° -> 359.9° is negative azimuth motion");
        }
    }

    public class OapaCalibrationServiceTest {

        /// <summary>
        /// Simulates an axis with a physical response and backlash. The backlash lives in the
        /// mechanics, so it is expressed in physical arcminutes and subtracted after the
        /// response scaling. A direction sign of -1 models physically inverted wiring: the
        /// axis moves opposite to the commanded direction. Solves report the accumulated
        /// physical position as a Dec offset and as topocentric altitude (1:1 for the
        /// altitude axis).
        /// </summary>
        private sealed class FakeAxis : IOapaCalibrationMotion, IOapaCalibrationSolver {
            private readonly double responseScale;
            private readonly double physicalBacklashArcmin;
            private readonly int directionSign;
            private double physicalPositionArcmin;
            private int lastSign;
            public readonly List<float> CommandedMoves = new();

            public FakeAxis(double responseScale, double physicalBacklashArcmin, int directionSign = 1) {
                this.responseScale = responseScale;
                this.physicalBacklashArcmin = physicalBacklashArcmin;
                this.directionSign = directionSign;
            }

            public Task MoveRelative(Axis axis, float arcmin, CancellationToken token) {
                CommandedMoves.Add(arcmin);
                var sign = Math.Sign(arcmin);
                double effective = Math.Abs(arcmin) * responseScale;
                if (sign != 0 && lastSign != 0 && sign != lastSign) {
                    effective = Math.Max(0, effective - physicalBacklashArcmin);
                }
                if (sign != 0) { lastSign = sign; }
                physicalPositionArcmin += sign * directionSign * effective;
                return Task.CompletedTask;
            }

            public Task<CalibrationSolveSample> CaptureAndSolve(CancellationToken token) {
                return Task.FromResult(new CalibrationSolveSample(
                    10.0, physicalPositionArcmin / 60.0, 30.0 + physicalPositionArcmin / 60.0, 100.0));
            }

            /// <summary>Where the axis physically sits, in arcminutes from its start.</summary>
            public double PhysicalPositionArcmin => physicalPositionArcmin;
        }

        /// <summary>
        /// Wraps a <see cref="FakeAxis"/> and records the order of moves, settles and solves.
        /// The settle exists to separate two specific operations, so ordering is the thing
        /// worth asserting - a settle that happens at the wrong moment is no settle at all.
        /// </summary>
        private sealed class OrderRecordingAxis : IOapaCalibrationMotion, IOapaCalibrationSolver {
            private readonly FakeAxis inner;
            public readonly List<string> Events = new();
            public readonly List<TimeSpan> Settles = new();

            public OrderRecordingAxis(FakeAxis inner) {
                this.inner = inner;
            }

            public Task MoveRelative(Axis axis, float arcmin, CancellationToken token) {
                Events.Add("move");
                return inner.MoveRelative(axis, arcmin, token);
            }

            public Task<CalibrationSolveSample> CaptureAndSolve(CancellationToken token) {
                Events.Add("solve");
                return inner.CaptureAndSolve(token);
            }

            /// <summary>Stands in for Task.Delay so the tests never wait on real time.</summary>
            public Task Settle(TimeSpan requested, CancellationToken token) {
                Events.Add("settle");
                Settles.Add(requested);
                return Task.CompletedTask;
            }
        }

        [Test]
        public async Task Calibration_SettlesAfterEveryMove_BeforeMeasuring() {
            // A high-friction axis keeps relaxing for about a second after the controller
            // reports idle. Solving into that relaxation measures a moving target: the
            // response comes out short, the two backlash transitions disagree, and the
            // slippage detector then correctly refuses a calibration that was never
            // measurable. The correction loop already waits; this is the same wait.
            var recorder = new OrderRecordingAxis(new FakeAxis(responseScale: 0.5, physicalBacklashArcmin: 5.0));
            var service = new OapaCalibrationService(recorder, recorder,
                settleTime: TimeSpan.FromSeconds(2), delay: recorder.Settle);

            await service.CalibrateAxisWithAutoReverse(
                Axis.YAxis, currentRatio: 100f, reversed: false, "Y", null, CancellationToken.None);

            for (var i = 0; i < recorder.Events.Count - 1; i++) {
                if (recorder.Events[i] == "move") {
                    recorder.Events[i + 1].Should().Be("settle",
                        $"every move must be followed by a settle before anything else (event {i})");
                }
            }
            recorder.Settles.Should().NotBeEmpty("the calibration moves, so it must settle");
            recorder.Settles.Should().AllSatisfy(t => t.Should().Be(TimeSpan.FromSeconds(2)));
        }

        [Test]
        public async Task Calibration_DoesNotSettleBeforeTheNoiseMeasurement() {
            // S0 measures solve noise with the axis at rest. Nothing has moved, so waiting
            // there would only lengthen every calibration for no reason.
            var recorder = new OrderRecordingAxis(new FakeAxis(responseScale: 0.5, physicalBacklashArcmin: 5.0));
            var service = new OapaCalibrationService(recorder, recorder,
                settleTime: TimeSpan.FromSeconds(2), delay: recorder.Settle);

            await service.CalibrateAxisWithAutoReverse(
                Axis.YAxis, currentRatio: 100f, reversed: false, "Y", null, CancellationToken.None);

            recorder.Events.First().Should().Be("solve", "the noise measurement comes before any motion");
        }

        [Test]
        public async Task Calibration_WithNoSettleConfigured_DoesNotWaitAtAll() {
            // The default has to stay zero: every other calibration test constructs the
            // service without a settle and must keep running at full speed.
            var recorder = new OrderRecordingAxis(new FakeAxis(responseScale: 0.5, physicalBacklashArcmin: 5.0));
            var service = new OapaCalibrationService(recorder, recorder, delay: recorder.Settle);

            await service.CalibrateAxisWithAutoReverse(
                Axis.YAxis, currentRatio: 100f, reversed: false, "Y", null, CancellationToken.None);

            recorder.Settles.Should().BeEmpty("a zero settle must not queue a delay at all");
        }

        [Test]
        public async Task Calibration_RecoversResponseAndPhysicalBacklash() {
            // The axis physically moves half of what is commanded and its mechanics lose
            // 5 physical arcminutes on reversal. The discovered backlash must be those
            // 5 physical arcminutes — not the shortfall re-expressed in the obsolete
            // command system (which would be 10').
            var axis = new FakeAxis(responseScale: 0.5, physicalBacklashArcmin: 5.0);
            var service = new OapaCalibrationService(axis, axis);

            var outcome = await service.CalibrateAxisWithAutoReverse(
                Axis.YAxis, currentRatio: 100f, reversed: false, "Y", null, CancellationToken.None);

            outcome.Ratio.Should().BeApproximately(200f, 1f, "half the motion means double the factor");
            outcome.BacklashArcmin.Should().BeApproximately(5f, 0.5f, "backlash is physical axis arcminutes");
            outcome.Consistent.Should().BeTrue();
            outcome.Flipped.Should().BeFalse();
        }

        [Test]
        public async Task Calibration_InvertedPhysicalResponse_FlipsAndSucceeds() {
            // The axis physically moves opposite to the commanded direction. The first pass
            // must detect the direction mismatch, the auto-flip retry must succeed, and the
            // outcome must report Flipped so the caller persists the corrected Reverse flag.
            var axis = new FakeAxis(responseScale: 1.0, physicalBacklashArcmin: 0.0, directionSign: -1);
            var service = new OapaCalibrationService(axis, axis);

            var outcome = await service.CalibrateAxisWithAutoReverse(
                Axis.YAxis, currentRatio: 100f, reversed: false, "Y", null, CancellationToken.None);

            outcome.Flipped.Should().BeTrue("an inverted physical response must be resolved by flipping Reverse");
            outcome.Consistent.Should().BeTrue();
            outcome.Ratio.Should().BeApproximately(100f, 1f);
        }

        private sealed class AlwaysNegativeAxis : IOapaCalibrationMotion, IOapaCalibrationSolver {
            private double physicalPositionArcmin;
            public Task MoveRelative(Axis axis, float arcmin, CancellationToken token) {
                physicalPositionArcmin -= Math.Abs(arcmin);
                return Task.CompletedTask;
            }
            public Task<CalibrationSolveSample> CaptureAndSolve(CancellationToken token) {
                return Task.FromResult(new CalibrationSolveSample(
                    10.0, physicalPositionArcmin / 60.0, 30.0 + physicalPositionArcmin / 60.0, 100.0));
            }
        }

        [Test]
        public async Task Calibration_DirectionUnresolvableByFlip_ReportsInconsistent() {
            // An axis that always drifts the same way no matter the command: flipping Reverse
            // cannot fix it, so the outcome must surface the inconsistency without a flip.
            var axis = new AlwaysNegativeAxis();
            var service = new OapaCalibrationService(axis, axis);

            var outcome = await service.CalibrateAxisWithAutoReverse(
                Axis.YAxis, currentRatio: 100f, reversed: false, "Y", null, CancellationToken.None);

            outcome.Flipped.Should().BeFalse();
            outcome.Consistent.Should().BeFalse();
        }

        [Test]
        public async Task Calibration_ReturnsAxisToItsPhysicalStart() {
            // Supersedes the old zero-command-sum assertion: with backlash, a zero net command
            // does not mean the axis returned. The invariant is the physical position.
            var axis = new FakeAxis(responseScale: 1.0, physicalBacklashArcmin: 0.0);
            var service = new OapaCalibrationService(axis, axis);

            await service.CalibrateAxisWithAutoReverse(Axis.YAxis, 100f, false, "Y", null, CancellationToken.None);

            axis.PhysicalPositionArcmin.Should().BeApproximately(0.0, 0.6, "the axis must end where it started");
        }

        [Test]
        public async Task Calibration_WithBacklash_ClosesLoopAgainstBaseline() {
            // The four-move sequence loses the backlash on the reversal leg, so a zero command
            // sum leaves the axis ~backlash away from its start. The closing move measured
            // against the baseline solve must bring it back.
            var axis = new FakeAxis(responseScale: 0.5, physicalBacklashArcmin: 5.0);
            var service = new OapaCalibrationService(axis, axis);

            await service.CalibrateAxisWithAutoReverse(Axis.YAxis, 100f, false, "Y", null, CancellationToken.None);

            axis.PhysicalPositionArcmin.Should().BeApproximately(0.0, 0.6,
                "closing the loop against the baseline must undo the backlash offset");
        }

        private sealed class FailingSolver : IOapaCalibrationSolver {
            private readonly IOapaCalibrationSolver inner;
            private int calls;
            private readonly int failFrom;
            public FailingSolver(IOapaCalibrationSolver inner, int failFrom) { this.inner = inner; this.failFrom = failFrom; }
            public Task<CalibrationSolveSample> CaptureAndSolve(CancellationToken token) {
                calls++;
                if (calls >= failFrom) { throw new InvalidOperationException("solver failed"); }
                return inner.CaptureAndSolve(token);
            }
        }

        /// <summary>Fails exactly one solve, letting the best-effort restore solve succeed.</summary>
        private sealed class FailOnceSolver : IOapaCalibrationSolver {
            private readonly IOapaCalibrationSolver inner;
            private int calls;
            private readonly int failAtCall;
            private readonly Exception toThrow;
            public FailOnceSolver(IOapaCalibrationSolver inner, int failAtCall, Exception toThrow = null) {
                this.inner = inner;
                this.failAtCall = failAtCall;
                this.toThrow = toThrow ?? new InvalidOperationException("solver failed");
            }
            public Task<CalibrationSolveSample> CaptureAndSolve(CancellationToken token) {
                calls++;
                if (calls == failAtCall) { throw toThrow; }
                return inner.CaptureAndSolve(token);
            }
        }

        [Test]
        public async Task MidSequenceFailure_SolverUnavailable_FallsBackToCommandSumRestore() {
            var axis = new FakeAxis(responseScale: 1.0, physicalBacklashArcmin: 0.0);
            // Solve B and everything after it fail, so the measured restore is impossible
            // and the commanded-sum fallback must still bring the axis home.
            var service = new OapaCalibrationService(axis, new FailingSolver(axis, failFrom: 3));

            var act = () => service.CalibrateAxisWithAutoReverse(Axis.YAxis, 100f, false, "Y", null, CancellationToken.None);
            await act.Should().ThrowAsync<InvalidOperationException>();

            axis.PhysicalPositionArcmin.Should().BeApproximately(0.0, 0.6, "the fallback restore must return the axis to its start");
        }

        [Test]
        public async Task MidSequenceFailure_WithBacklash_RestoresAgainstBaseline() {
            // Solve C fails after the reversal leg has eaten the backlash: the commanded sum
            // says -45' outstanding, but the axis physically sits at +50'. The restore solve
            // succeeds, so the measured residual - not the command sum - must be driven back.
            var axis = new FakeAxis(responseScale: 1.0, physicalBacklashArcmin: 40.0);
            var service = new OapaCalibrationService(axis, new FailOnceSolver(axis, failAtCall: 4));

            var act = () => service.CalibrateAxisWithAutoReverse(Axis.YAxis, 100f, false, "Y", null, CancellationToken.None);
            await act.Should().ThrowAsync<InvalidOperationException>();

            axis.PhysicalPositionArcmin.Should().BeApproximately(0.0, 0.6,
                "the measured restore must undo the backlash-distorted position");
        }

        [Test]
        public async Task Cancellation_MidSequence_RestoresAgainstBaseline() {
            var axis = new FakeAxis(responseScale: 1.0, physicalBacklashArcmin: 40.0);
            var service = new OapaCalibrationService(axis,
                new FailOnceSolver(axis, failAtCall: 4, new OperationCanceledException()));

            var act = () => service.CalibrateAxisWithAutoReverse(Axis.YAxis, 100f, false, "Y", null, CancellationToken.None);
            await act.Should().ThrowAsync<OperationCanceledException>();

            axis.PhysicalPositionArcmin.Should().BeApproximately(0.0, 0.6,
                "cancellation must not strand the axis away from its start");
        }

        [Test]
        public void BaselineNearZenith_AbortsAzimuthBeforeAnyMotion() {
            var axis = new FakeAxis(responseScale: 1.0, physicalBacklashArcmin: 0.0);
            var zenithSolver = new ZenithSolver();
            var service = new OapaCalibrationService(axis, zenithSolver);

            var act = () => service.CalibrateAxisWithAutoReverse(Axis.XAxis, 100f, false, "X", null, CancellationToken.None);
            act.Should().ThrowAsync<InvalidOperationException>();
            axis.CommandedMoves.Should().BeEmpty("no motion may be commanded when the baseline field is unusable");
        }

        private sealed class ZenithSolver : IOapaCalibrationSolver {
            public Task<CalibrationSolveSample> CaptureAndSolve(CancellationToken token) =>
                Task.FromResult(new CalibrationSolveSample(10.0, 0.0, 85.0, 100.0));
        }
    }
}
