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
        public string Topic { get; init; }
        public object Content { get; init; }
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
    }
}
