using FluentAssertions;
using NINA.Plugins.PolarAlignment;
using NINA.Plugins.PolarAlignment.OAPA;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// Which side of its play each axis is engaged on belongs to the mechanism, not to the
    /// object driving it. The alignment instruction builds a new system object on every
    /// Execute - one field session shows four "Found OAPA System on COM11" lines - while the
    /// hardware holds still, so state carried on the object made every run begin by asserting
    /// both axes were engaged positive. On an axis with tens of arcminutes of play that turns
    /// the first correction of the run into either a whole injected backlash or a whole lost
    /// one, which is the largest single error such a run makes.
    /// </summary>
    public class AxisEngagementStateTest {

        [SetUp]
        public void Reset() => AxisEngagementState.Reset();

        [Test]
        public void DefaultsToPositive_ForAnAxisNothingHasDrivenYet() {
            AxisEngagementState.Get("Any", Axis.XAxis).Should().Be(LastDirection.Positive);
        }

        [Test]
        public void RemembersPerAxis_AndPerSystem() {
            AxisEngagementState.Set("OAPA", Axis.XAxis, LastDirection.Negative);

            AxisEngagementState.Get("OAPA", Axis.XAxis).Should().Be(LastDirection.Negative);
            AxisEngagementState.Get("OAPA", Axis.YAxis).Should().Be(LastDirection.Positive, "axes are independent");
            AxisEngagementState.Get("Avalon", Axis.XAxis).Should().Be(LastDirection.Positive,
                "two controllers in one session must not inherit each other's engagement");
        }

        // ----- Through the real production path -----

        private sealed class FakeLink : ISerialLink {
            public readonly List<string> Writes = new();
            private readonly Queue<string> pending = new();
            public float X, Y;

            public bool IsOpen => true;

            public void WriteLine(string text) {
                Writes.Add(text);
                if (text == "?") {
                    pending.Enqueue($"<Idle|MPos:{X.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)},{Y.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)},0.00|V:1.2.2|>");
                    pending.Enqueue("ok");
                } else {
                    // Execute jogs instantly so the wait loop completes.
                    if (text.StartsWith("$J=G91G21X")) { X += Steps(text, "X"); }
                    if (text.StartsWith("$J=G91G21Y")) { Y += Steps(text, "Y"); }
                    pending.Enqueue("ok");
                }
            }

            private static float Steps(string cmd, string axis) {
                var body = cmd.Substring(cmd.IndexOf(axis, StringComparison.Ordinal) + 1);
                return float.Parse(body.Substring(0, body.IndexOf('F')), System.Globalization.CultureInfo.InvariantCulture);
            }

            public string ReadLine() => pending.Dequeue();
            public void Dispose() { }
        }

        private sealed class TestableOapa : UniversalPolarAlignmentOAPA {
            public TestableOapa(ISerialLink link) : base(link) { }
        }

        private static TestableOapa Build(FakeLink link) {
            Properties.Settings.Default.OAPAXGearRatio = 10f;
            Properties.Settings.Default.OAPAYGearRatio = 10f;
            var oapa = new TestableOapa(link);
            oapa.XGearRatio = 10f;
            oapa.YGearRatio = 10f;
            return oapa;
        }

        [Test]
        public async Task ANewSystemObject_InheritsTheEngagementTheMechanismIsActuallyIn() {
            // The manual nudge that preceded the field failure: the panel drives the axis
            // negative, then the instruction starts and constructs its own system object.
            var link = new FakeLink();
            var panel = Build(link);
            await panel.MoveRelative(Axis.YAxis, 1000, -10f, CancellationToken.None);
            panel.YLastDirection.Should().Be(LastDirection.Negative);

            var freshRun = Build(new FakeLink());

            freshRun.YLastDirection.Should().Be(LastDirection.Negative,
                "the axis is still resting against the same side of its play");
        }

        [Test]
        public async Task TheFirstCorrectionOfANewRun_DoesNotPayAReversalItIsNotMaking() {
            // The consequence, priced. A fresh object that assumes Positive plans a
            // reversal for a move that continues in the direction the axis is already
            // engaged in, and on this rig's configured play that injects about 50'.
            // With the persisted (Negative) engagement the outward leg carries no
            // reversal compensation - only the pinned approach-from-below excursion,
            // whose two margins cancel - so the physical arrival is exact.
            var link = new FakeLink();
            var panel = Build(link);
            await panel.MoveRelative(Axis.YAxis, 1000, -10f, CancellationToken.None);

            var freshRun = Build(new FakeLink());

            var plan = BacklashModePlanner.PlanMoves(OapaBacklashMode.Unidirectional, -2.47f,
                backlashEnteringPositive: 57.16f, backlashEnteringNegative: 49.78f, freshRun.YLastDirection);

            // Overshoot margin: 0.25 * max(pair) + 0.5' = 14.79'.
            plan.Should().HaveCount(2, "the pinned regime always arrives from below");
            plan[0].Should().BeApproximately(-2.47f - 14.79f, 0.01f,
                "the outward leg pays no entering-negative play: the axis is already engaged that way");

            // Physical arrival through a mechanism with the configured play, engaged negative:
            // outward leg loses nothing, return leg loses the entering-positive play.
            var physical = plan[0] + (plan[1] - 57.16f);
            physical.Should().BeApproximately(-2.47f, 0.01f, "the arrival must be exact - no injected backlash");

            // A fresh object that had wrongly assumed Positive would have added the
            // 49.78' entering-negative compensation to the outward leg: ~50' injected.
            var wrong = BacklashModePlanner.PlanMoves(OapaBacklashMode.Unidirectional, -2.47f,
                backlashEnteringPositive: 57.16f, backlashEnteringNegative: 49.78f, LastDirection.Positive);
            (wrong[0] - plan[0]).Should().BeApproximately(-49.78f, 0.01f);
        }
    }
}
