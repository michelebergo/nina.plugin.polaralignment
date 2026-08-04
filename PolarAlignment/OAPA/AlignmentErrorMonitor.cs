using NINA.Core.Utility;
using NINA.Plugin.Interfaces;
using System;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.OAPA {

    /// <summary>
    /// Listens for the alignment error the polar-alignment instruction publishes and exposes
    /// it to the OAPA panel, so manual nudging no longer requires switching between the
    /// options page and the alignment window.
    ///
    /// Values expire by inactivity rather than on an end-of-run signal: the instruction
    /// reports its terminating status through the caller's progress object, not through the
    /// wrapper that publishes to the broker, so no subscriber ever sees a "finished"
    /// message. Expiry also covers what such a signal would miss anyway - the alignment
    /// window closed mid-run, a crash, a disconnected cable.
    /// </summary>
    public sealed class AlignmentErrorMonitor : ISubscriber, IDisposable {

        /// <summary>
        /// Topic published by <see cref="Instructions.PolarAlignmentErrorMessage"/>. Held as a
        /// literal rather than composed from nameof() because "PolarAlignment" is both a
        /// namespace and a type here; a test pins it against the message's own Topic.
        /// </summary>
        public const string ErrorTopic = "PolarAlignmentPlugin_PolarAlignment_AlignmentError";

        /// <summary>
        /// Silence after which the readout is no longer trustworthy. Well clear of the 50 s a
        /// single backlash-compensated move occupied an axis in the 2026-08-03 field log.
        /// </summary>
        private static readonly TimeSpan Expiry = TimeSpan.FromSeconds(90);

        /// <summary>Re-evaluation cadence so an expiry becomes visible without a message.</summary>
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);

        private readonly Func<DateTime> clock;
        private readonly bool startHeartbeat;

        /// <summary>
        /// Guards <see cref="heartbeat"/> and <see cref="disposed"/>. Both are read and written
        /// from at least two threads: whatever delivers broker messages, and whichever thread
        /// disposes the owning view model, plus the ThreadPool thread the timer itself ticks
        /// on. Never invoke <see cref="Changed"/> while holding it - a subscriber running under
        /// this lock is how a deadlock gets made.
        /// </summary>
        private readonly object gate = new();

        private System.Timers.Timer heartbeat;
        private bool disposed;

        private double azimuthDegrees;
        private double altitudeDegrees;
        private double totalDegrees;
        private DateTime? receivedAt;

        public AlignmentErrorMonitor(IMessageBroker messageBroker, Func<DateTime> clock = null, bool startHeartbeat = true) {
            this.clock = clock ?? (() => DateTime.UtcNow);
            this.startHeartbeat = startHeartbeat;

            // A null broker is legitimate: the view model is constructed without one in tests
            // and in any host that does not provide the plugin message broker.
            messageBroker?.Subscribe(ErrorTopic, this);

            // No timer is created here. This type is constructed by every OAPA view model in
            // every test that touches one - a timer started here would tick, live, for the
            // duration of every such test. It is started lazily in OnMessageReceived instead,
            // once there is an actual measurement whose expiry is worth re-evaluating.
        }

        /// <summary>Raised when the exposed values may have changed. The view model forwards it.</summary>
        public event Action Changed;

        private bool IsLive => receivedAt.HasValue && clock() - receivedAt.Value <= Expiry;

        public bool HasLiveError => IsLive;
        public double? AzimuthErrorArcmin => IsLive ? azimuthDegrees * 60.0 : null;
        public double? AltitudeErrorArcmin => IsLive ? altitudeDegrees * 60.0 : null;
        public double? TotalErrorArcmin => IsLive ? totalDegrees * 60.0 : null;

        /// <summary>Test-only visibility into whether the lazily-started heartbeat is currently ticking.</summary>
        internal bool IsHeartbeatRunning {
            get {
                lock (gate) {
                    return heartbeat?.Enabled ?? false;
                }
            }
        }

        public Task OnMessageReceived(IMessage message) {
            if (message?.Topic != ErrorTopic) { return Task.CompletedTask; }

            try {
                var content = message.Content;
                var azimuth = ReadDouble(content, "AzimuthError");
                var altitude = ReadDouble(content, "AltitudeError");
                var total = ReadDouble(content, "TotalError");

                if (azimuth is null || altitude is null || total is null) {
                    Logger.Debug("Alignment error message had an unexpected payload shape; keeping the previous values.");
                    return Task.CompletedTask;
                }

                azimuthDegrees = azimuth.Value;
                altitudeDegrees = altitude.Value;
                totalDegrees = Math.Abs(total.Value);
                receivedAt = clock();
            } catch (Exception ex) {
                // A broker callback must never throw: the publisher is the alignment loop.
                Logger.Error(ex);
                return Task.CompletedTask;
            }

            StartHeartbeatIfNeeded();
            Changed?.Invoke();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Starts the heartbeat on the first accepted measurement, and restarts it if a prior
        /// run stopped itself after an expiry. A no-op once disposed - the disposed check and
        /// the read/create/start of <see cref="heartbeat"/> all happen inside <see cref="gate"/>
        /// so a message racing a concurrent <see cref="Dispose"/> either completes first (and
        /// Dispose then tears down the timer it created) or observes <see cref="disposed"/>
        /// already set (and creates nothing) - it can never resurrect a timer after Dispose has
        /// already returned.
        /// </summary>
        private void StartHeartbeatIfNeeded() {
            if (!startHeartbeat) { return; }

            lock (gate) {
                if (disposed) { return; }

                if (heartbeat == null) {
                    heartbeat = new System.Timers.Timer(HeartbeatInterval.TotalMilliseconds) { AutoReset = true };
                    heartbeat.Elapsed += OnHeartbeatElapsed;
                }

                if (!heartbeat.Enabled) {
                    heartbeat.Start();
                }
            }
        }

        /// <summary>
        /// Re-evaluates on the heartbeat cadence so an expiry becomes visible without a
        /// further message, then stops itself once expired: there is nothing left to expire,
        /// so there is nothing left to re-evaluate. <see cref="StartHeartbeatIfNeeded"/> starts
        /// it again if a new measurement arrives later.
        ///
        /// Operates on <paramref name="sender"/> rather than the <see cref="heartbeat"/> field:
        /// <see cref="System.Timers.Timer"/> dispatches Elapsed on the ThreadPool, so a tick
        /// already queued when <see cref="Dispose"/> runs can still execute after the field has
        /// been nulled out. Reading the field here would risk a NullReferenceException on a
        /// background thread; the sender is the timer instance itself and is never null.
        /// </summary>
        private void OnHeartbeatElapsed(object sender, System.Timers.ElapsedEventArgs e) {
            if (sender is System.Timers.Timer tickedTimer && !IsLive) {
                lock (gate) {
                    // Only stop it if it is still the current, live heartbeat: Dispose may have
                    // already taken ownership of this exact instance and be tearing it down.
                    if (!disposed && ReferenceEquals(heartbeat, tickedTimer)) {
                        tickedTimer.Stop();
                    }
                }
            }

            Changed?.Invoke();
        }

        /// <summary>
        /// The published payload is an anonymous type, so it is read by property name. A shape
        /// change degrades to "no update" instead of an exception.
        /// </summary>
        private static double? ReadDouble(object content, string propertyName) {
            var value = content?.GetType().GetProperty(propertyName)?.GetValue(content);
            return value is null ? null : Convert.ToDouble(value);
        }

        public void Dispose() {
            System.Timers.Timer owned;

            lock (gate) {
                if (disposed) { return; }
                disposed = true;

                owned = heartbeat;
                heartbeat = null;
            }

            if (owned != null) {
                owned.Elapsed -= OnHeartbeatElapsed;
                owned.Stop();
                owned.Dispose();
            }
        }
    }
}
