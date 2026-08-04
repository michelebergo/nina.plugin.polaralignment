using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using NINA.Plugin.Interfaces;
using NINA.Plugins.PolarAlignment.Instructions;
using NINA.Plugins.PolarAlignment.OAPA;
using NUnit.Framework;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>Message double: carries an anonymous payload exactly like the publisher.</summary>
    internal sealed class FakeMessage : IMessage {
        public Guid SenderId => Guid.Empty;
        public string Sender => "test";
        public DateTimeOffset SentAt => DateTimeOffset.MinValue;
        public Guid MessageId => Guid.Empty;
        public DateTimeOffset? Expiration => null;
        public Guid? CorrelationId => null;
        public int Version => 1;
        public IDictionary<string, object> CustomHeaders => new Dictionary<string, object>();
        public required string Topic { get; init; }
        public required object Content { get; init; }
    }

    /// <summary>
    /// The readout must never show a number that is no longer live: a user nudging by hand
    /// against a twenty-minute-old value is correcting toward a target that no longer exists.
    /// There is no end-of-run signal on the broker (the instruction reports its terminating
    /// status through the caller's progress object, not the wrapper that publishes), so the
    /// monitor expires by inactivity instead. These tests pin that rule with an injected
    /// clock so no test ever waits on real time.
    /// </summary>
    public class AlignmentErrorMonitorTest {

        private static FakeMessage ErrorMessage(double azDeg, double altDeg, double totalDeg) =>
            new() {
                Topic = AlignmentErrorMonitor.ErrorTopic,
                Content = new { AzimuthError = azDeg, AltitudeError = altDeg, TotalError = totalDeg }
            };

        private DateTime now;

        [SetUp]
        public void SetUp() => now = new DateTime(2026, 8, 4, 22, 0, 0, DateTimeKind.Utc);

        private AlignmentErrorMonitor Monitor() =>
            new(messageBroker: null, clock: () => now, startHeartbeat: false);

        [Test]
        public void Topic_MatchesThePublishedMessage() {
            // A drifting topic string would silently produce a readout that never updates.
            var published = new PolarAlignmentErrorMessage(Guid.NewGuid(), 0, 0, 0);
            AlignmentErrorMonitor.ErrorTopic.Should().Be(published.Topic);
        }

        [Test]
        public async Task AMessage_IsExposedInArcminutes() {
            var monitor = Monitor();

            // 0.25 deg = 15', -0.1 deg = -6', 0.269 deg = 16.14'
            await monitor.OnMessageReceived(ErrorMessage(0.25, -0.1, 0.269));

            monitor.AzimuthErrorArcmin.Should().BeApproximately(15.0, 1e-6);
            monitor.AltitudeErrorArcmin.Should().BeApproximately(-6.0, 1e-6);
            monitor.TotalErrorArcmin.Should().BeApproximately(16.14, 1e-6);
            monitor.HasLiveError.Should().BeTrue();
        }

        [Test]
        public async Task Values_ExpireAfterNinetySecondsOfSilence() {
            var monitor = Monitor();
            await monitor.OnMessageReceived(ErrorMessage(0.25, -0.1, 0.269));

            now = now.AddSeconds(91);

            monitor.AzimuthErrorArcmin.Should().BeNull();
            monitor.AltitudeErrorArcmin.Should().BeNull();
            monitor.TotalErrorArcmin.Should().BeNull();
            monitor.HasLiveError.Should().BeFalse();
        }

        [Test]
        public async Task Values_SurviveALongBacklashCompensatedMove() {
            // A single unidirectional move with backlash compensation held an axis for 50 s
            // with no solve in the 2026-08-03 field log. The readout must not blank out
            // mid-alignment just because the mount is busy moving.
            var monitor = Monitor();
            await monitor.OnMessageReceived(ErrorMessage(0.25, -0.1, 0.269));

            now = now.AddSeconds(60);

            monitor.HasLiveError.Should().BeTrue();
            monitor.TotalErrorArcmin.Should().BeApproximately(16.14, 1e-6);
        }

        [Test]
        public async Task ANewMessage_RestartsTheClock() {
            var monitor = Monitor();
            await monitor.OnMessageReceived(ErrorMessage(0.25, -0.1, 0.269));

            now = now.AddSeconds(80);
            await monitor.OnMessageReceived(ErrorMessage(0.1, 0.05, 0.112));
            now = now.AddSeconds(80);

            monitor.HasLiveError.Should().BeTrue();
            monitor.AzimuthErrorArcmin.Should().BeApproximately(6.0, 1e-6);
        }

        [Test]
        public async Task AnUnreadablePayload_LeavesThePreviousStateUntouched() {
            // The payload is an anonymous type read by reflection. A publisher change must
            // degrade to "no update" rather than throwing inside a broker callback.
            var monitor = Monitor();
            await monitor.OnMessageReceived(ErrorMessage(0.25, -0.1, 0.269));

            var act = async () => await monitor.OnMessageReceived(new FakeMessage {
                Topic = AlignmentErrorMonitor.ErrorTopic,
                Content = new { Unexpected = "shape" }
            });

            await act.Should().NotThrowAsync();
            monitor.TotalErrorArcmin.Should().BeApproximately(16.14, 1e-6);
        }

        [Test]
        public async Task AMessageOnAnotherTopic_IsIgnored() {
            var monitor = Monitor();

            await monitor.OnMessageReceived(new FakeMessage {
                Topic = "PolarAlignmentPlugin_PolarAlignment_Progress",
                Content = new { AzimuthError = 1.0, AltitudeError = 1.0, TotalError = 1.4 }
            });

            monitor.HasLiveError.Should().BeFalse();
        }

        [Test]
        public async Task Changed_FiresOnEachAcceptedMessage() {
            var monitor = Monitor();
            var fired = 0;
            monitor.Changed += () => fired++;

            await monitor.OnMessageReceived(ErrorMessage(0.25, -0.1, 0.269));

            fired.Should().Be(1);
        }

        [Test]
        public async Task Heartbeat_DoesNotRunUntilTheFirstMessageArrives() {
            // A timer started in the constructor would tick for the lifetime of every view
            // model that owns a monitor, including the many built with default arguments
            // across unrelated test files. It must not exist until there is something to
            // re-evaluate.
            using var monitor = new AlignmentErrorMonitor(messageBroker: null, clock: () => now, startHeartbeat: true);
            monitor.IsHeartbeatRunning.Should().BeFalse();

            await monitor.OnMessageReceived(ErrorMessage(0.25, -0.1, 0.269));

            monitor.IsHeartbeatRunning.Should().BeTrue();
        }

        [Test]
        public async Task Heartbeat_NeverStarts_WhenDisabled() {
            var monitor = Monitor(); // startHeartbeat: false, as every other test in this file relies on.
            await monitor.OnMessageReceived(ErrorMessage(0.25, -0.1, 0.269));

            monitor.IsHeartbeatRunning.Should().BeFalse();
        }

        [Test]
        public void Dispose_IsSafeToCallTwice() {
            var monitor = new AlignmentErrorMonitor(messageBroker: null, clock: () => now, startHeartbeat: true);

            var act = () => {
                monitor.Dispose();
                monitor.Dispose();
            };

            act.Should().NotThrow();
        }

        [Test]
        public async Task ADisposedMonitor_IgnoresALaterMessage_AndStartsNoTimer() {
            // Pins the ordering that matters for the Dispose/OnMessageReceived race: once
            // Dispose has returned, nothing arriving afterwards may resurrect the heartbeat.
            var monitor = new AlignmentErrorMonitor(messageBroker: null, clock: () => now, startHeartbeat: true);
            monitor.Dispose();

            var act = async () => await monitor.OnMessageReceived(ErrorMessage(0.25, -0.1, 0.269));

            await act.Should().NotThrowAsync();
            monitor.IsHeartbeatRunning.Should().BeFalse();
        }
    }

    /// <summary>
    /// The panel binds to pre-formatted strings so the "no live value" state is an em dash
    /// rather than an empty row, and so no value converter is needed.
    /// </summary>
    public class OapaErrorReadoutTest {

        // The VM's ErrorMonitor is built with startHeartbeat: true (the production default).
        // Any test that delivers a message starts a live 10-second timer; without disposing
        // it here, a test run would leak one per such test. See AlignmentErrorMonitorTest's
        // Heartbeat_DoesNotRunUntilTheFirstMessageArrives for why the timer starts lazily.
        private UniversalPolarAlignmentOAPAVM vm;

        [TearDown]
        public void TearDown() => vm?.ErrorMonitor.Dispose();

        [Test]
        public void WithNoMeasurement_AllThreeReadEmDash() {
            vm = new UniversalPolarAlignmentOAPAVM(null, null, null, null, null);

            vm.AzimuthErrorDisplay.Should().Be("—");
            vm.AltitudeErrorDisplay.Should().Be("—");
            vm.TotalErrorDisplay.Should().Be("—");
        }

        [Test]
        public async Task AfterAMeasurement_ValuesAreFormattedInArcminutesWithSign() {
            vm = new UniversalPolarAlignmentOAPAVM(null, null, null, null, null);

            await vm.ErrorMonitor.OnMessageReceived(new FakeMessage {
                Topic = AlignmentErrorMonitor.ErrorTopic,
                Content = new { AzimuthError = 0.25, AltitudeError = -0.1, TotalError = 0.269 }
            });

            // Sign matters: it lets the same axis's readings be compared nudge to nudge.
            vm.AzimuthErrorDisplay.Should().Be("+15.00'");
            vm.AltitudeErrorDisplay.Should().Be("-6.00'");
            vm.TotalErrorDisplay.Should().Be("16.14'");
        }

        [Test]
        public async Task AMeasurement_RaisesPropertyChangedForTheThreeDisplays() {
            vm = new UniversalPolarAlignmentOAPAVM(null, null, null, null, null);
            var changed = new List<string>();
            vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

            await vm.ErrorMonitor.OnMessageReceived(new FakeMessage {
                Topic = AlignmentErrorMonitor.ErrorTopic,
                Content = new { AzimuthError = 0.25, AltitudeError = -0.1, TotalError = 0.269 }
            });

            changed.Should().Contain(nameof(UniversalPolarAlignmentOAPAVM.AzimuthErrorDisplay));
            changed.Should().Contain(nameof(UniversalPolarAlignmentOAPAVM.AltitudeErrorDisplay));
            changed.Should().Contain(nameof(UniversalPolarAlignmentOAPAVM.TotalErrorDisplay));
        }
    }
}
