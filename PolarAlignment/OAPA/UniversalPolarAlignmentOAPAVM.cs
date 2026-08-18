using NINA.Core.Utility;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Plugins.PolarAlignment.OAPA;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Interfaces.Mediator;
using NINA.PlateSolving.Interfaces;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace NINA.Plugins.PolarAlignment.OAPA {
    public partial class UniversalPolarAlignmentOAPAVM : UniversalPolarAlignmentBaseVM {
        // Internal so tests can substitute the solver boundary; production assigns it once in the ctor.
        internal IOapaCalibrationSolver calibrationSolver;
        private readonly ICameraMediator cameraMediator;
        private readonly CameraBlockToken cameraBlockToken = new();

        public UniversalPolarAlignmentOAPAVM(
            IProfileService profileService,
            IImagingMediator imagingMediator,
            ITelescopeMediator telescopeMediator,
            IPlateSolverFactory plateSolverFactory,
            ICameraMediator cameraMediator,
            IMessageBroker messageBroker = null) : base(profileService) {
            // Before any bound property reads a persisted parameter: the panel is often
            // opened long before the controller is connected.
            OapaSettingsMigration.EnsureCurrent();
            calibrationSolver = new OapaPlateSolveSampler(profileService, imagingMediator, telescopeMediator, plateSolverFactory);
            this.cameraMediator = cameraMediator;

            // The broker is optional so the ten existing test fixtures keep their
            // five-argument construction; a null broker simply means no subscription.
            ErrorMonitor = new AlignmentErrorMonitor(messageBroker);
            ErrorMonitor.Changed += OnAlignmentErrorChanged;

            // Connected and IsNotMoving live on the base VM. Their generated
            // [NotifyCanExecuteChangedFor] attributes can't reference commands declared on
            // this derived class, so re-evaluate the derived commands manually when either
            // property changes. Connected is flipped from a background Task in the base VM,
            // so marshal NotifyCanExecuteChanged onto the UI thread. The stored home is only
            // meaningful for the current controller session (the position counter restarts at
            // 0 on power-up), so it is invalidated on every connection change.
            PropertyChanged += (_, e) => {
                if (e.PropertyName == nameof(Connected) || e.PropertyName == nameof(IsNotMoving)) {
                    if (e.PropertyName == nameof(Connected)) {
                        HasHome = false;
                    }
                    var dispatcher = Application.Current?.Dispatcher;
                    if (dispatcher == null || dispatcher.CheckAccess()) {
                        NotifyDerivedCommands();
                    } else {
                        dispatcher.BeginInvoke(new Action(NotifyDerivedCommands));
                    }
                }
            };
        }

        private void NotifyDerivedCommands() {
            CalibrateGearRatiosCommand.NotifyCanExecuteChanged();
            SetHomeCommand.NotifyCanExecuteChanged();
            GoHomeCommand.NotifyCanExecuteChanged();
            StopMotionCommand.NotifyCanExecuteChanged();
        }

        /// <summary>
        /// Live alignment error for the readout above the manual controls. Internal so tests
        /// can feed it directly without a broker.
        /// </summary>
        /// <remarks>
        /// Never disposed in production: this VM is constructed once and held for the rest of
        /// the process by PolarAlignmentPlugin.UniversalPolarAlignmentOAPAVM (a static
        /// property, set in the plugin constructor and never cleared), so the monitor's
        /// heartbeat timer is intentionally scoped to the process's lifetime, same as the VM
        /// itself. The Dispose path exists for tests, which construct and discard many VMs per
        /// run and would otherwise leak a live timer per instance.
        /// </remarks>
        internal AlignmentErrorMonitor ErrorMonitor { get; }

        private void OnAlignmentErrorChanged() {
            RaisePropertyChanged(nameof(AzimuthErrorDisplay));
            RaisePropertyChanged(nameof(AltitudeErrorDisplay));
            RaisePropertyChanged(nameof(TotalErrorDisplay));
        }

        /// <summary>Placeholder shown before the first measurement and after expiry.</summary>
        private const string NoValue = "—";

        // The arcminute tick is appended rather than embedded in the format string: in a
        // .NET custom numeric format an apostrophe quotes a literal section, so "0.00'"
        // does not mean what it looks like.
        private static string Signed(double? arcmin) =>
            arcmin.HasValue ? arcmin.Value.ToString("+0.00;-0.00", CultureInfo.InvariantCulture) + "'" : NoValue;

        private static string Magnitude(double? arcmin) =>
            arcmin.HasValue ? arcmin.Value.ToString("0.00", CultureInfo.InvariantCulture) + "'" : NoValue;

        // Azimuth and altitude are shown signed as the alignment measures them, so the
        // number itself is comparable across successive nudges. The sign's meaning is
        // hemisphere- and Reverse-flag-dependent (see TPAPAVM's direction strings and the
        // correction controller's empirically learned mapping), so this readout does not
        // claim to tell you which way to press a button. Total is a magnitude.
        public string AzimuthErrorDisplay => Signed(ErrorMonitor.AzimuthErrorArcmin);
        public string AltitudeErrorDisplay => Signed(ErrorMonitor.AltitudeErrorArcmin);
        public string TotalErrorDisplay => Magnitude(ErrorMonitor.TotalErrorArcmin);

        // Safety control: available whenever connected — including while a move or the
        // calibration is driving the motors, which is precisely when it is needed.
        public bool CanStopMotion() => Connected;

        [RelayCommand(CanExecute = nameof(CanStopMotion))]
        private void StopMotion() {
            if (upa is UniversalPolarAlignmentOAPA oapa) {
                oapa.RequestStop();
            }
        }

        protected override string SystemName => "OAPA System";

        protected override IPolarAlignmentSystem CreateSystem() => new UniversalPolarAlignmentOAPA();

        public override bool DoAutomatedAdjustments {
            get => Properties.Settings.Default.DoAutomatedAdjustments;
            set {
                // The instruction header records this once at start and never revisits it,
                // while the correction loop reads it live every cycle. A tester enabling
                // adjustments mid-run produced a log that appeared to contradict itself.
                if (Properties.Settings.Default.DoAutomatedAdjustments != value) {
                    Logger.Info($"OAPA automated adjustments {(value ? "enabled" : "disabled")}");
                }
                Properties.Settings.Default.DoAutomatedAdjustments = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// What the correction loop waits after each move. This is the value the shared loop
        /// reads through the selected system, so it is the effective one - the number the user
        /// typed lives in <see cref="SettleTimeSetting"/> and is what the panel edits, which
        /// is why the two are separate properties: a box that displayed the effective value
        /// would make the user's own choice unreachable the moment the unlock took effect.
        /// </summary>
        public override double AutomatedAdjustmentSettleTime {
            get => EffectiveSettleTime;
            set => SettleTimeSetting = value;
        }

        /// <summary>The settle the user chose: used as-is unless a verified calibration unlocks the faster one.</summary>
        public double SettleTimeSetting {
            get => Properties.Settings.Default.AutomatedAdjustmentSettleTime;
            set {
                Properties.Settings.Default.AutomatedAdjustmentSettleTime = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(AutomatedAdjustmentSettleTime));
                RaisePropertyChanged(nameof(EffectiveSettleTime));
            }
        }

        /// <summary>
        /// Whether a verified calibration is allowed to speed up the corrections. Off by
        /// default: an existing installation must keep behaving exactly as it did, and the
        /// values this unlocks trade a larger single excursion for fewer cycles.
        /// </summary>
        public bool AdaptiveSpeedUp {
            get => Properties.Settings.Default.OAPAAdaptiveSpeedUp;
            set {
                Properties.Settings.Default.OAPAAdaptiveSpeedUp = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
                RaiseSpeedUpChanged();
            }
        }

        /// <summary>
        /// Whether the last applied calibration came back verified on both axes. Persisted
        /// because the corrections it authorises may happen in a later session, and
        /// invalidated whenever a factor or backlash value is edited by hand: a number the
        /// user typed has not been verified by anything, so it cannot carry the verdict of a
        /// measurement it replaced. (Same rule as the home position, which is invalidated by
        /// the same edits for the same reason.)
        /// </summary>
        public bool CalibrationTrusted => Properties.Settings.Default.OAPACalibrationTrusted;

        /// <summary>Why the last calibration is not trusted; empty when it is.</summary>
        public string CalibrationTrustNote => Properties.Settings.Default.OAPACalibrationTrustNote;

        private void SetCalibrationTrust(bool trusted, string note) {
            if (Properties.Settings.Default.OAPACalibrationTrusted == trusted
                && Properties.Settings.Default.OAPACalibrationTrustNote == note) {
                return;
            }
            Properties.Settings.Default.OAPACalibrationTrusted = trusted;
            Properties.Settings.Default.OAPACalibrationTrustNote = note;
            CoreUtil.SaveSettings(Properties.Settings.Default);
            Logger.Info($"OAPA calibration trust: {(trusted ? "verified" : $"not verified ({note})")}");
            RaiseSpeedUpChanged();
        }

        private void RaiseSpeedUpChanged() {
            RaisePropertyChanged(nameof(CalibrationTrusted));
            RaisePropertyChanged(nameof(CalibrationTrustNote));
            RaisePropertyChanged(nameof(SpeedUpUnlocked));
            RaisePropertyChanged(nameof(SpeedUpStatus));
            RaisePropertyChanged(nameof(EffectiveMaxCorrectionMagnitude));
            RaisePropertyChanged(nameof(EffectiveSettleTime));
            RaisePropertyChanged(nameof(AutomatedAdjustmentSettleTime));
        }

        public override float XGearRatio {
            get => Properties.Settings.Default.OAPAXGearRatio;
            set => SetXGearRatio(value, MarkEdit(value, XGearRatio, XGearRatioSource));
        }

        private void SetXGearRatio(float value, OapaParameterSource source) {
            // A value typed by hand carries no verdict: the measurement that earned the
            // trust has just been replaced, so whatever it authorised is withdrawn until the
            // next calibration earns it again. Same rule the home position follows.
            if (source == OapaParameterSource.Manual) {
                SetCalibrationTrust(false, "a factor or backlash value was edited by hand after the last calibration");
            }
            value = System.Math.Clamp(value, MinimumFactor, MaximumFactor);
            Properties.Settings.Default.OAPAXGearRatio = value;
            Properties.Settings.Default.OAPAXGearRatioSource = source.ToString();
            if (upa != null) { upa.XGearRatio = value; }
            CoreUtil.SaveSettings(Properties.Settings.Default);
            RaisePropertyChanged(nameof(XGearRatio));
            RaisePropertyChanged(nameof(XGearRatioSource));
            RaisePropertyChanged(nameof(XGearRatioSourceLabel));
            RaisePropertyChanged(nameof(PositionX));
            RaisePropertyChanged(nameof(XSpeedPhysical));
            RefreshHomeDisplay();
        }

        public override int XSpeed {
            get => Properties.Settings.Default.OAPAXSpeed;
            set {
                Properties.Settings.Default.OAPAXSpeed = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(XSpeedPhysical));
            }
        }

        // The speed sent to the controller is a step rate, so the same number is a
        // different sky speed on each axis. Once a calibration factor exists the rate can
        // be stated in the unit the user actually thinks in.
        public string XSpeedPhysical => PhysicalSpeed(XSpeed, XGearRatio);

        public string YSpeedPhysical => PhysicalSpeed(YSpeed, YGearRatio);

        private static string PhysicalSpeed(int stepsPerSecond, float stepsPerArcmin) {
            // A factor of 1 is the factory default: the platform has never been calibrated,
            // and inventing a reading from it would be worse than showing none.
            if (stepsPerArcmin <= 1f) { return string.Empty; }
            var arcminPerSecond = stepsPerSecond / stepsPerArcmin;
            return $"~ {arcminPerSecond.ToString("F1", CultureInfo.InvariantCulture)} '/s";
        }

        public override float YGearRatio {
            get => Properties.Settings.Default.OAPAYGearRatio;
            set => SetYGearRatio(value, MarkEdit(value, YGearRatio, YGearRatioSource));
        }

        private void SetYGearRatio(float value, OapaParameterSource source) {
            // A value typed by hand carries no verdict: the measurement that earned the
            // trust has just been replaced, so whatever it authorised is withdrawn until the
            // next calibration earns it again. Same rule the home position follows.
            if (source == OapaParameterSource.Manual) {
                SetCalibrationTrust(false, "a factor or backlash value was edited by hand after the last calibration");
            }
            value = System.Math.Clamp(value, MinimumFactor, MaximumFactor);
            Properties.Settings.Default.OAPAYGearRatio = value;
            Properties.Settings.Default.OAPAYGearRatioSource = source.ToString();
            if (upa != null) { upa.YGearRatio = value; }
            CoreUtil.SaveSettings(Properties.Settings.Default);
            RaisePropertyChanged(nameof(YGearRatio));
            RaisePropertyChanged(nameof(YGearRatioSource));
            RaisePropertyChanged(nameof(YGearRatioSourceLabel));
            RaisePropertyChanged(nameof(PositionY));
            RaisePropertyChanged(nameof(YSpeedPhysical));
            RefreshHomeDisplay();
        }

        public override int YSpeed {
            get => Properties.Settings.Default.OAPAYSpeed;
            set {
                Properties.Settings.Default.OAPAYSpeed = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(YSpeedPhysical));
            }
        }

        public override bool ReverseAzimuth {
            get => Properties.Settings.Default.OAPAReverseAzimuth;
            set {
                Properties.Settings.Default.OAPAReverseAzimuth = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public override bool ReverseAltitude {
            get => Properties.Settings.Default.OAPAReverseAltitude;
            set {
                Properties.Settings.Default.OAPAReverseAltitude = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public override float XBacklashCompensation {
            get => Properties.Settings.Default.OAPAXBacklashCompensation;
            set => SetXBacklash(value, MarkEdit(value, XBacklashCompensation, XBacklashSource));
        }

        private void SetXBacklash(float value, OapaParameterSource source) {
            // A value typed by hand carries no verdict: the measurement that earned the
            // trust has just been replaced, so whatever it authorised is withdrawn until the
            // next calibration earns it again. Same rule the home position follows.
            if (source == OapaParameterSource.Manual) {
                SetCalibrationTrust(false, "a factor or backlash value was edited by hand after the last calibration");
            }
            value = System.Math.Clamp(value, 0f, MaximumBacklashArcmin);
            Properties.Settings.Default.OAPAXBacklashCompensation = value;
            Properties.Settings.Default.OAPAXBacklashSource = source.ToString();
            CoreUtil.SaveSettings(Properties.Settings.Default);
            RaisePropertyChanged(nameof(XBacklashCompensation));
            RaisePropertyChanged(nameof(XBacklashSource));
            RaisePropertyChanged(nameof(XBacklashSourceLabel));
        }

        // OAPA-specific: the altitude axis of an OAPA platform also has measurable backlash.
        // Deliberately not part of the shared VM contract - other systems do not model it.
        public float YBacklashCompensation {
            get => Properties.Settings.Default.OAPAYBacklashCompensation;
            set => SetYBacklash(value, MarkEdit(value, YBacklashCompensation, YBacklashSource));
        }

        private void SetYBacklash(float value, OapaParameterSource source) {
            // A value typed by hand carries no verdict: the measurement that earned the
            // trust has just been replaced, so whatever it authorised is withdrawn until the
            // next calibration earns it again. Same rule the home position follows.
            if (source == OapaParameterSource.Manual) {
                SetCalibrationTrust(false, "a factor or backlash value was edited by hand after the last calibration");
            }
            value = System.Math.Clamp(value, 0f, MaximumBacklashArcmin);
            Properties.Settings.Default.OAPAYBacklashCompensation = value;
            Properties.Settings.Default.OAPAYBacklashSource = source.ToString();
            CoreUtil.SaveSettings(Properties.Settings.Default);
            RaisePropertyChanged(nameof(YBacklashCompensation));
            RaisePropertyChanged(nameof(YBacklashSource));
            RaisePropertyChanged(nameof(YBacklashSourceLabel));
        }

        // ----- Per-direction backlash -----
        // Entering one direction and entering the other are two different physical
        // quantities on an axis loaded by gravity. The pair above holds the positive
        // direction; these hold the negative one, and a stored value below zero means
        // "never set" so an axis configured before this existed stays symmetric instead of
        // silently acquiring a zero compensation one way.

        public float XBacklashCompensationNegative {
            get {
                var stored = Properties.Settings.Default.OAPAXBacklashCompensationNegative;
                return stored < 0f ? XBacklashCompensation : stored;
            }
            set => SetXBacklashNegative(value, MarkEdit(value, XBacklashCompensationNegative, XBacklashSource));
        }

        private void SetXBacklashNegative(float value, OapaParameterSource source) {
            // A value typed by hand carries no verdict: the measurement that earned the
            // trust has just been replaced, so whatever it authorised is withdrawn until the
            // next calibration earns it again. Same rule the home position follows.
            if (source == OapaParameterSource.Manual) {
                SetCalibrationTrust(false, "a factor or backlash value was edited by hand after the last calibration");
            }
            value = System.Math.Clamp(value, 0f, MaximumBacklashArcmin);
            Properties.Settings.Default.OAPAXBacklashCompensationNegative = value;
            Properties.Settings.Default.OAPAXBacklashSource = source.ToString();
            CoreUtil.SaveSettings(Properties.Settings.Default);
            RaisePropertyChanged(nameof(XBacklashCompensationNegative));
            RaisePropertyChanged(nameof(XBacklashSourceLabel));
        }

        public float YBacklashCompensationNegative {
            get {
                var stored = Properties.Settings.Default.OAPAYBacklashCompensationNegative;
                return stored < 0f ? YBacklashCompensation : stored;
            }
            set => SetYBacklashNegative(value, MarkEdit(value, YBacklashCompensationNegative, YBacklashSource));
        }

        private void SetYBacklashNegative(float value, OapaParameterSource source) {
            // A value typed by hand carries no verdict: the measurement that earned the
            // trust has just been replaced, so whatever it authorised is withdrawn until the
            // next calibration earns it again. Same rule the home position follows.
            if (source == OapaParameterSource.Manual) {
                SetCalibrationTrust(false, "a factor or backlash value was edited by hand after the last calibration");
            }
            value = System.Math.Clamp(value, 0f, MaximumBacklashArcmin);
            Properties.Settings.Default.OAPAYBacklashCompensationNegative = value;
            Properties.Settings.Default.OAPAYBacklashSource = source.ToString();
            CoreUtil.SaveSettings(Properties.Settings.Default);
            RaisePropertyChanged(nameof(YBacklashCompensationNegative));
            RaisePropertyChanged(nameof(YBacklashSourceLabel));
        }

        // ----- Microstepping -----
        // Trades resolution for speed and torque, and polar alignment has resolution to
        // spare: a platform at 1000 steps per arcminute is two orders of magnitude past
        // what the plate solve can resolve, and pays for it at 3 arcmin/s.
        //
        // Steps per arcminute scale exactly with the microstep setting, so changing it
        // invalidates the calibration factor by a known factor. Rescaling it here is not a
        // convenience: leaving a stale factor behind would make every commanded move wrong
        // by that same ratio, and on a short-travel platform the first move would drive an
        // axis into its end stop. The backlash is in physical arcminutes and does not scale.

        /// <summary>Instance property on purpose: a XAML {Binding} cannot resolve a static one.</summary>
        public int[] MicrostepOptions => SupportedMicrosteps;

        private static readonly int[] SupportedMicrosteps = { 1, 2, 4, 8, 16, 32, 64, 128, 256 };

        public int XMicrosteps {
            get => Properties.Settings.Default.OAPAXMicrosteps;
            set => SetMicrosteps(Axis.XAxis, value);
        }

        public int YMicrosteps {
            get => Properties.Settings.Default.OAPAYMicrosteps;
            set => SetMicrosteps(Axis.YAxis, value);
        }

        private void SetMicrosteps(Axis axis, int value) {
            if (Array.IndexOf(SupportedMicrosteps, value) < 0) { return; }
            var isX = axis == Axis.XAxis;
            var previous = isX ? XMicrosteps : YMicrosteps;
            if (previous == value) { return; }

            if (isX) { Properties.Settings.Default.OAPAXMicrosteps = value; } else { Properties.Settings.Default.OAPAYMicrosteps = value; }

            var scale = (float)value / previous;
            var oldRatio = isX ? XGearRatio : YGearRatio;
            var newRatio = System.Math.Clamp(oldRatio * scale, MinimumFactor, MaximumFactor);
            if (isX) { SetXGearRatio(newRatio, XGearRatioSource); } else { SetYGearRatio(newRatio, YGearRatioSource); }

            CoreUtil.SaveSettings(Properties.Settings.Default);
            Logger.Info($"OAPA microsteps {axis}: {previous} -> {value}; calibration factor rescaled {oldRatio:F2} -> {newRatio:F2} steps/arcmin");
            if (upa?.Connected == true && upa is UniversalPolarAlignmentOAPA oapa) {
                oapa.SetMicrosteps(axis, value);
            }

            RaisePropertyChanged(isX ? nameof(XMicrosteps) : nameof(YMicrosteps));
            RaisePropertyChanged(isX ? nameof(XSpeedPhysical) : nameof(YSpeedPhysical));
        }

        // ----- Parameter provenance -----
        // A hand-entered value is a deliberate user decision: it is tracked as Manual and
        // Apply will not replace it without an explicit confirmation. Values written by
        // ApplyCalibration are tracked as Calibrated.

        /// <summary>A public write is a manual edit only when it actually changes the value.</summary>
        private static OapaParameterSource MarkEdit(float newValue, float currentValue, OapaParameterSource currentSource) {
            return System.Math.Abs(newValue - currentValue) > 1e-6f ? OapaParameterSource.Manual : currentSource;
        }

        private static OapaParameterSource ParseSource(string stored) =>
            Enum.TryParse<OapaParameterSource>(stored, out var source) ? source : OapaParameterSource.Default;

        public OapaParameterSource XGearRatioSource => ParseSource(Properties.Settings.Default.OAPAXGearRatioSource);
        public OapaParameterSource YGearRatioSource => ParseSource(Properties.Settings.Default.OAPAYGearRatioSource);
        public OapaParameterSource XBacklashSource => ParseSource(Properties.Settings.Default.OAPAXBacklashSource);
        public OapaParameterSource YBacklashSource => ParseSource(Properties.Settings.Default.OAPAYBacklashSource);

        // Small provenance hints next to the fields; empty for factory defaults.
        public string XGearRatioSourceLabel => SourceLabel(XGearRatioSource);
        public string YGearRatioSourceLabel => SourceLabel(YGearRatioSource);
        public string XBacklashSourceLabel => SourceLabel(XBacklashSource);
        public string YBacklashSourceLabel => SourceLabel(YBacklashSource);
        private static string SourceLabel(OapaParameterSource source) =>
            source == OapaParameterSource.Default ? string.Empty : source.ToString().ToLowerInvariant();

        /// <summary>Factor bounds: a value below 1 is meaningless, above this it is a typo.</summary>
        private const float MinimumFactor = 1f;
        private const float MaximumFactor = 100000f;
        /// <summary>Backlash beyond 1.5 degrees is physically absurd and would command huge compensation moves.</summary>
        private const float MaximumBacklashArcmin = 90f;

        /// <summary>
        /// Safety ceiling for the per-cycle correction. Sole owner of the persisted
        /// setting: the clamp to the controller's configurable bounds lives here, so the
        /// 1-60 invariant holds no matter which public surface wrote the value (the
        /// plugin options property is a pure XAML adapter delegating to this one).
        /// </summary>
        public double MaxCorrectionMagnitude {
            get => Properties.Settings.Default.OAPAMaxCorrectionMagnitude;
            set {
                var clamped = System.Math.Max(AutomatedAdjustmentController.MinimumConfigurableMoveMagnitude,
                                              System.Math.Min(AutomatedAdjustmentController.MaximumConfigurableMoveMagnitude, value));
                Properties.Settings.Default.OAPAMaxCorrectionMagnitude = clamped;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// Route the shared clearing logic to the OAPA-specific per-axis compensation, and
        /// honour the mode while doing it. The backlash modes are the OAPA compensation policy,
        /// and Off has to mean off on every path that moves the axis.
        ///
        /// The relative nudges go through <see cref="BacklashModePlanner"/>, which handles Off.
        /// The absolute moves go through the shared clearing, which only asks for a value - so
        /// an axis set to Off still had its play compensated there: two extra moves of the full
        /// backlash after every "move to". A tester measuring his own backlash by hand watched
        /// the platform move three times per command and could conclude nothing, because the
        /// only tool he had to measure with was perturbing what he was measuring.
        /// </summary>
        protected override float GetBacklashCompensation(Axis axis) {
            if (BacklashModeOf(axis) == OapaBacklashMode.Off) { return 0f; }
            return axis == Axis.YAxis ? YBacklashCompensation : base.GetBacklashCompensation(axis);
        }

        private float GetBacklashCompensationNegative(Axis axis) {
            if (BacklashModeOf(axis) == OapaBacklashMode.Off) { return 0f; }
            return axis == Axis.YAxis ? YBacklashCompensationNegative : XBacklashCompensationNegative;
        }

        private OapaBacklashMode BacklashModeOf(Axis axis) => axis == Axis.XAxis ? XBacklashMode : YBacklashMode;

        // OAPA hardware tolerates the faster profile: error-scaled probes and the 75%
        // correction candidate. UPAS and other systems keep the legacy behavior.
        public override bool AggressiveCorrectionProfile => true;

        /// <summary>
        /// Per-axis backlash handling mode; sole owner of the persisted setting. An
        /// unrecognized stored value falls back to Full (the single-move compensation).
        /// </summary>
        public OapaBacklashMode XBacklashMode {
            get => ParseMode(Properties.Settings.Default.OAPAXBacklashMode);
            set {
                Properties.Settings.Default.OAPAXBacklashMode = value.ToString();
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(XBacklashModeName));
            }
        }

        public OapaBacklashMode YBacklashMode {
            get => ParseMode(Properties.Settings.Default.OAPAYBacklashMode);
            set {
                Properties.Settings.Default.OAPAYBacklashMode = value.ToString();
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(YBacklashModeName));
            }
        }

        private static OapaBacklashMode ParseMode(string stored) =>
            Enum.TryParse<OapaBacklashMode>(stored, out var mode) ? mode : OapaBacklashMode.Full;

        // String adapters for the XAML ComboBoxes.
        public string[] BacklashModeNames => Enum.GetNames(typeof(OapaBacklashMode));
        public string XBacklashModeName {
            get => XBacklashMode.ToString();
            set { if (Enum.TryParse<OapaBacklashMode>(value, out var mode)) { XBacklashMode = mode; } }
        }
        public string YBacklashModeName {
            get => YBacklashMode.ToString();
            set { if (Enum.TryParse<OapaBacklashMode>(value, out var mode)) { YBacklashMode = mode; } }
        }

        /// <summary>
        /// OAPA relative moves replace the legacy clear-after-move excursion with the
        /// per-axis backlash-mode plan: the compensation is folded into the move itself
        /// (Full/Soft) or the target is approached from the engaged direction only
        /// (Unidirectional). Serves both the manual and the automated fine-approach path.
        /// </summary>
        protected override async Task ExecuteRelativeMove(Axis axis, int speed, float position, CancellationToken token) {
            var mode = axis == Axis.XAxis ? XBacklashMode : YBacklashMode;
            var plan = BacklashModePlanner.PlanMoves(mode, position,
                GetBacklashCompensation(axis), GetBacklashCompensationNegative(axis), LastDirectionOf(axis),
                MinimumHonourableReversal(axis));
            if (plan.Length == 0) {
                // The request is finer than this axis can be positioned: its own backlash
                // compensation would inject a larger error than the move is trying to remove.
                // Reported rather than silently skipped - the correction loop will read the
                // same error again, and the log has to explain why nothing moved.
                Logger.Info($"OAPA backlash mode {mode} on {axis}: {position:F2}' not commanded - a reversal below " +
                    $"{MinimumHonourableReversal(axis):F2}' is finer than this axis's compensation was measured to; " +
                    "moving would add more error than it removes");
                return;
            }
            if (plan.Length > 1 || System.Math.Abs(plan[0] - position) > float.Epsilon) {
                // The net is what the axis travels if it loses no play at all, so it is the
                // floor of what a two-leg plan can achieve. When it drifts away from the
                // requested move - or flips sign - the configured pair is asking the
                // mechanism for play it does not have, which is invisible from the legs alone.
                var net = 0f;
                foreach (var m in plan) { net += m; }
                Logger.Info($"OAPA backlash mode {mode} on {axis}: move {position:F2}' planned as [{string.Join(", ", plan)}] (net {net:F2}')");
            }
            foreach (var move in plan) {
                await upa.MoveRelative(axis, speed, move, token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// OAPA correction-limit policy: scale with the measured error (80% of the current
        /// total error) so multi-degree initial errors converge in a handful of cycles,
        /// with the controller default as the floor for a gentle final approach and the
        /// user setting as a pure safety ceiling.
        /// </summary>
        public override double GetMaximumCorrectionMagnitude(double currentTotalErrorArcmin) {
            var autoScaled = System.Math.Max(AutomatedAdjustmentController.DefaultMaximumMoveMagnitude, currentTotalErrorArcmin * 0.8);
            return System.Math.Min(autoScaled, EffectiveMaxCorrectionMagnitude);
        }

        /// <summary>
        /// Correction ceiling actually in force. The user's value is the conservative case -
        /// the one used when the mechanism has proven nothing - and a calibration that came
        /// back verified on both axes unlocks the controller's configurable maximum. The
        /// unlock is opt-in: with <see cref="AdaptiveSpeedUp"/> off this is the user's value
        /// and the behaviour is what it has always been.
        ///
        /// The saving is in solves, not in motor time: the same angle gets travelled either
        /// way, but a correction cycle costs about 8 seconds of capture, download and solve
        /// around half a second of movement. From 5°51' the coarse phase takes a dozen cycles
        /// at 30' and six at 60'.
        /// </summary>
        public double EffectiveMaxCorrectionMagnitude =>
            SpeedUpUnlocked ? AutomatedAdjustmentController.MaximumConfigurableMoveMagnitude : MaxCorrectionMagnitude;

        /// <summary>Settle actually waited after each automated correction, in seconds.</summary>
        public double EffectiveSettleTime =>
            SpeedUpUnlocked ? System.Math.Min(SettleTimeSetting, TrustedSettleSeconds) : SettleTimeSetting;

        /// <summary>
        /// Whether the last applied calibration earned the faster values and the user has
        /// allowed them to be used. Both halves are required: a verdict alone must not
        /// override a limit somebody chose deliberately.
        /// </summary>
        public bool SpeedUpUnlocked => AdaptiveSpeedUp && CalibrationTrusted;

        /// <summary>
        /// Why the faster values are or are not in use. Without this the panel would show two
        /// numbers that quietly disagree with the two fields above them, and a user with a rig
        /// that never gets the unlock would have no way to find out what to fix.
        /// </summary>
        public string SpeedUpStatus =>
            !AdaptiveSpeedUp ? "using your values: the faster ones are switched off"
            : CalibrationTrusted ? "using the faster values: the last calibration was verified on both axes"
            : $"using your values: {CalibrationTrustNote}";

        /// <summary>Settle a verified mechanism is allowed to drop to, in seconds.</summary>
        private const double TrustedSettleSeconds = 0.5;

        public int XRunCurrent {
            get => Properties.Settings.Default.OAPAXRunCurrent;
            set {
                Properties.Settings.Default.OAPAXRunCurrent = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
                if (upa?.Connected == true && upa is UniversalPolarAlignmentOAPA oapa) {
                    oapa.SetXRunCurrent(value);
                }
            }
        }

        public int YRunCurrent {
            get => Properties.Settings.Default.OAPAYRunCurrent;
            set {
                Properties.Settings.Default.OAPAYRunCurrent = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
                if (upa?.Connected == true && upa is UniversalPolarAlignmentOAPA oapa) {
                    oapa.SetYRunCurrent(value);
                }
            }
        }

        public int XHoldPercent {
            get => Properties.Settings.Default.OAPAXHoldPercent;
            set {
                Properties.Settings.Default.OAPAXHoldPercent = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
                if (upa?.Connected == true && upa is UniversalPolarAlignmentOAPA oapa) {
                    oapa.SetXHoldPercent(value);
                }
            }
        }

        public int YHoldPercent {
            get => Properties.Settings.Default.OAPAYHoldPercent;
            set {
                Properties.Settings.Default.OAPAYHoldPercent = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
                if (upa?.Connected == true && upa is UniversalPolarAlignmentOAPA oapa) {
                    oapa.SetYHoldPercent(value);
                }
            }
        }

        // ----- Home position (session-scoped) -----
        // The controller's position counter restarts at 0 on power-up, so absolute home
        // coordinates from a previous session are meaningless and potentially harmful.
        // Home therefore lives in VM state only and is invalidated on every connection change.
        //
        // Home marks a physical controller position, so it is stored in controller-native
        // units: the displayed logical position is the controller position divided by the
        // gear ratio, and MoveAbsolute multiplies by it again, so a logical value saved
        // under one ratio drives somewhere else once the ratio changes (manual edit or
        // ApplyCalibration). HomeX/HomeY are display-only projections under the current
        // ratio, refreshed by the ratio setters.

        private float homeXController;
        private float homeYController;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(GoHomeCommand))]
        private bool hasHome;

        [ObservableProperty]
        private float homeX;

        [ObservableProperty]
        private float homeY;

        public bool CanSetHome() => Connected && IsNotMoving;
        public bool CanGoHome() => Connected && IsNotMoving && HasHome;

        private void RefreshHomeDisplay() {
            if (!HasHome) { return; }
            HomeX = homeXController / XGearRatio;
            HomeY = homeYController / YGearRatio;
        }

        [RelayCommand(CanExecute = nameof(CanSetHome))]
        public void SetHome() {
            homeXController = PositionX * XGearRatio;
            homeYController = PositionY * YGearRatio;
            HomeX = PositionX;
            HomeY = PositionY;
            HasHome = true;
            Logger.Info($"OAPA home position set to X={HomeX:F2}, Y={HomeY:F2} (controller {homeXController:F0}/{homeYController:F0}, valid for this connection session)");
            Notification.ShowInformation($"Home position saved for this session (X={HomeX:F2}, Y={HomeY:F2})");
        }

        [RelayCommand(CanExecute = nameof(CanGoHome))]
        public async Task GoHome(CancellationToken token) {
            try {
                await RunOnUi(() => IsNotMoving = false);
                var targetX = homeXController / XGearRatio;
                var targetY = homeYController / YGearRatio;
                Logger.Info($"OAPA moving to home position X={targetX:F2}, Y={targetY:F2} (controller {homeXController:F0}/{homeYController:F0})");
                await upa.MoveAbsolute(Axis.XAxis, XSpeed, targetX, token).ConfigureAwait(false);
                await upa.MoveAbsolute(Axis.YAxis, YSpeed, targetY, token).ConfigureAwait(false);
            } catch (OperationCanceledException) {
            } catch (Exception ex) {
                Logger.Error(ex);
                Notification.ShowError($"Failed to move to home position: {ex.Message}");
            } finally {
                await RunOnUi(() => IsNotMoving = true);
            }
        }

        // ----- Self-Calibration -----
        // Orchestration and geometry live in OapaCalibrationService/OapaCalibrationGeometry;
        // this VM only exposes commands and observable state.

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(CalibrateGearRatiosCommand))]
        private bool calibrationRunning;

        [ObservableProperty]
        private string calibrationStatus = string.Empty;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ApplyCalibrationCommand))]
        [NotifyCanExecuteChangedFor(nameof(DiscardCalibrationCommand))]
        private bool hasCalibrationResult;

        [ObservableProperty]
        private float discoveredXRatio;

        [ObservableProperty]
        private float discoveredYRatio;

        // The discovered backlash is per direction: these hold the positive one, the pair
        // below the negative one. They are equal on a symmetric axis.
        [ObservableProperty]
        private float discoveredXBacklash;

        [ObservableProperty]
        private float discoveredYBacklash;

        [ObservableProperty]
        private float discoveredXBacklashNegative;

        [ObservableProperty]
        private float discoveredYBacklashNegative;

        // Solve noise measured by the calibration (S0), kept per axis so Apply can derive
        // the recommended backlash mode from backlash-vs-noise.
        [ObservableProperty]
        private float discoveredXNoise;

        [ObservableProperty]
        private float discoveredYNoise;

        // Why this pass would not earn the faster correction values, empty when it would.
        // Held from measurement until Apply, because trust travels with the numbers: a result
        // that is measured and discarded must not leave a verdict behind.
        [ObservableProperty]
        private string discoveredTrustNote = string.Empty;

        [ObservableProperty]
        private string calibrationConsistencyMessage = string.Empty;

        // Backlash that costs a different amount in each direction. Reported so the extra
        // convergence cycles are expected rather than mysterious; it does not gate Apply,
        // because the mean compensation is imperfect, not invalid - and the calibration
        // factor, which is the more valuable half of the result, is unaffected by it.
        [ObservableProperty]
        private bool calibrationDirectionalBacklash;

        // Armed by the first Apply when manual values would be replaced; the second Apply
        // confirms. Disarmed by Discard and by starting a new calibration.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ApplyButtonText))]
        private bool applyConfirmationPending;

        /// <summary>
        /// The button states the request itself while a confirmation is armed: leaving it
        /// in the status line alone reads as "nothing happened" and costs a calibration.
        /// </summary>
        public string ApplyButtonText => ApplyConfirmationPending ? "Apply again to confirm" : "Apply";

        public bool CanApplyCalibration() => HasCalibrationResult;

        public bool CanCalibrate() => Connected && IsNotMoving && !CalibrationRunning && CameraIsFree();

        // The capture block owner is identified by reference; a dedicated token keeps the
        // camera-consumer plumbing off the public VM surface. A null mediator (tests,
        // headless hosts) means "always free" with no-op acquisition.
        private bool CameraIsFree() => cameraMediator == null || cameraMediator.IsFreeToCapture(cameraBlockToken);

        private sealed class CameraBlockToken : ICameraConsumer {
            public void UpdateDeviceInfo(CameraInfo deviceInfo) { }
            public void Dispose() { }
        }

        private sealed class SpeedAwareMotion : IOapaCalibrationMotion {
            private readonly UniversalPolarAlignmentOAPAVM vm;
            public SpeedAwareMotion(UniversalPolarAlignmentOAPAVM vm) { this.vm = vm; }
            public Task MoveRelative(Axis axis, float arcmin, CancellationToken token) {
                var speed = axis == Axis.XAxis ? vm.XSpeed : vm.YSpeed;
                return vm.upa.MoveRelative(axis, speed, arcmin, token);
            }
        }

        [RelayCommand(CanExecute = nameof(CanCalibrate))]
        public Task CalibrateGearRatios(CancellationToken token) {
            return Task.Run(async () => {
                // CanExecute is evaluated at UI time; re-check here so a sequence or another
                // TPPA run that acquired the camera in the meantime cannot be interrupted.
                if (!CameraIsFree()) {
                    Logger.Warning("OAPA self-calibration refused: another consumer owns the camera");
                    Notification.ShowWarning("Cannot calibrate: the camera is in use by a sequence or another imaging process.");
                    await RunOnUi(() => CalibrationStatus = "Camera busy - calibration not started");
                    return;
                }
                cameraMediator?.RegisterCaptureBlock(cameraBlockToken);
                try {
                    await RunOnUi(() => {
                        IsNotMoving = false;
                        CalibrationRunning = true;
                        HasCalibrationResult = false;
                        CalibrationDirectionalBacklash = false;
                        ApplyConfirmationPending = false;
                        CalibrationStatus = "Starting calibration...";
                        CalibrationConsistencyMessage = string.Empty;
                    });

                    Logger.Info($"OAPA self-calibration started (settle {AutomatedAdjustmentSettleTime}s between move and solve)");
                    // Same settle the correction loop uses: a high-friction axis is still
                    // relaxing when the controller reports idle, and solving into that
                    // relaxation is what makes the two backlash transitions disagree.
                    var service = new OapaCalibrationService(new SpeedAwareMotion(this), calibrationSolver,
                        settleTime: TimeSpan.FromSeconds(AutomatedAdjustmentSettleTime));
                    Action<string> reportStatus = s => _ = RunOnUi(() => CalibrationStatus = s);

                    var x = await service.CalibrateAxisWithAutoReverse(
                        Axis.XAxis, XGearRatio, ReverseAzimuth, "X (Azimuth)", reportStatus, token);
                    if (x.Flipped) {
                        await RunOnUi(() => ReverseAzimuth = !ReverseAzimuth);
                    }
                    var y = await service.CalibrateAxisWithAutoReverse(
                        Axis.YAxis, YGearRatio, ReverseAltitude, "Y (Altitude)", reportStatus, token);
                    if (y.Flipped) {
                        await RunOnUi(() => ReverseAltitude = !ReverseAltitude);
                    }

                    string consistencyMsg;
                    if (x.Consistent && y.Consistent) {
                        var notes = new List<string>();
                        if (x.Flipped) { notes.Add("Reverse Az auto-corrected"); }
                        if (y.Flipped) { notes.Add("Reverse Alt auto-corrected"); }
                        consistencyMsg = notes.Count == 0
                            ? "Direction consistency: OK"
                            : "Direction consistency: OK (" + string.Join(", ", notes) + ")";
                    } else {
                        consistencyMsg = $"Direction consistency: WARNING (X={(x.Consistent ? "ok" : "fail")}, Y={(y.Consistent ? "ok" : "fail")}). Auto-flip did not resolve it; check wiring.";
                    }
                    if (x.Asymmetric || y.Asymmetric) {
                        var details = new List<string>();
                        if (x.Asymmetric) { details.Add($"X forward {x.ForwardRatio:F1} / reverse {x.ReverseRatio:F1}"); }
                        if (y.Asymmetric) { details.Add($"Y forward {y.ForwardRatio:F1} / reverse {y.ReverseRatio:F1}"); }
                        consistencyMsg += $" \u26a0 The axis responds differently per direction ({string.Join("; ", details)}). The applied factor is the mean; convergence may take a few extra cycles.";
                    }
                    if (x.ResponseSuspect || y.ResponseSuspect) {
                        var details = new List<string>();
                        if (x.ResponseSuspect) { details.Add($"X forward {x.ForwardRatio:F1} / reverse {x.ReverseRatio:F1}"); }
                        if (y.ResponseSuspect) { details.Add($"Y forward {y.ForwardRatio:F1} / reverse {y.ReverseRatio:F1}"); }
                        consistencyMsg += $" ⛔ The two directions disagree by more than a factor of two ({string.Join("; ", details)}): the weaker one is losing motion (motor stall, slip, binding). The applied factor was taken from the stronger direction alone. Before re-running, check the run current and speed of this axis - a motor without torque margin loses steps against gravity.";
                    }
                    if (x.BacklashSuspect || y.BacklashSuspect) {
                        var axes = new List<string>();
                        if (x.BacklashSuspect) { axes.Add("X"); }
                        if (y.BacklashSuspect) { axes.Add("Y"); }
                        consistencyMsg += $" ⛔ The backlash on {string.Join(" and ", axes)} could not be measured (a reversal came back longer than the response allows), so it is reported as zero rather than as a number that would be applied. Re-run the calibration; if it repeats, measure the play by hand and enter it.";
                    }
                    var directional = x.DirectionalBacklash || y.DirectionalBacklash;
                    if (directional) {
                        var details = new List<string>();
                        if (x.DirectionalBacklash) { details.Add($"X {x.BacklashEnteringPositiveArcmin:F1}' vs {x.BacklashEnteringNegativeArcmin:F1}'"); }
                        if (y.DirectionalBacklash) { details.Add($"Y {y.BacklashEnteringPositiveArcmin:F1}' vs {y.BacklashEnteringNegativeArcmin:F1}'"); }
                        consistencyMsg += $" \u26a0 The backlash costs a different amount in each direction ({string.Join("; ", details)}), which is normal on an axis loaded by gravity. Each direction is compensated with its own value, so this is handled - but if the two figures also change between calibrations, the mechanics are slipping: check grub screws, belt tension and friction.";
                    }

                    await RunOnUi(() => {
                        DiscoveredXRatio = x.Ratio;
                        DiscoveredYRatio = y.Ratio;
                        DiscoveredXBacklash = x.BacklashEnteringPositiveArcmin;
                        DiscoveredYBacklash = y.BacklashEnteringPositiveArcmin;
                        DiscoveredXBacklashNegative = x.BacklashEnteringNegativeArcmin;
                        DiscoveredYBacklashNegative = y.BacklashEnteringNegativeArcmin;
                        DiscoveredXNoise = x.NoiseSigmaArcmin;
                        DiscoveredYNoise = y.NoiseSigmaArcmin;
                        CalibrationDirectionalBacklash = directional;
                        DiscoveredTrustNote = DescribeCalibrationTrust(x, y);
                        CalibrationConsistencyMessage = consistencyMsg;
                        CalibrationStatus = $"Done. X={x.Ratio:F2}, Y={y.Ratio:F2}, backlash X={Pair(x)}, Y={Pair(y)}" +
                            (x.RestoredToBaseline && y.RestoredToBaseline ? string.Empty : " ⚠ not returned to start");
                        HasCalibrationResult = true;
                    });

                    Logger.Info($"OAPA calibration result: X={x.Ratio:F2}, Y={y.Ratio:F2}, backlash X={Pair(x)}, Y={Pair(y)}, consistency: X={x.Consistent}, Y={y.Consistent}, " +
                        $"restored: X={x.RestoredToBaseline} ({x.ClosingResidualArcmin:F2}'), Y={y.RestoredToBaseline} ({y.ClosingResidualArcmin:F2}')");
                    // "Measured" and "physically back at the start" are different claims: a
                    // calibration whose closing failed must not be announced as plain success,
                    // or the platform silently keeps the calibration's last displacement.
                    if (x.RestoredToBaseline && y.RestoredToBaseline) {
                        Notification.ShowInformation(
                            $"Calibration done. X factor: {x.Ratio:F2}, Y factor: {y.Ratio:F2}, backlash X: {Pair(x)}, Y: {Pair(y)}",
                            TimeSpan.FromSeconds(30));
                    } else {
                        var offAxes = new List<string>();
                        if (!x.RestoredToBaseline) { offAxes.Add($"Azimuth ({(float.IsNaN(x.ClosingResidualArcmin) ? "residual unknown" : $"{x.ClosingResidualArcmin:F1}' off")})"); }
                        if (!y.RestoredToBaseline) { offAxes.Add($"Altitude ({(float.IsNaN(y.ClosingResidualArcmin) ? "residual unknown" : $"{y.ClosingResidualArcmin:F1}' off")})"); }
                        Notification.ShowWarning(
                            $"Calibration measured (X: {x.Ratio:F2}, Y: {y.Ratio:F2}), but the platform did not verifiably return to its starting position: {string.Join(", ", offAxes)}. " +
                            "The measured factors are valid; re-check your polar alignment before imaging.");
                    }
                } catch (OperationCanceledException) {
                    Logger.Info("OAPA self-calibration cancelled");
                    await RunOnUi(() => CalibrationStatus = "Cancelled");
                } catch (Exception ex) {
                    Logger.Error(ex);
                    Notification.ShowError($"Calibration failed: {ex.Message}");
                    await RunOnUi(() => CalibrationStatus = $"Failed: {ex.Message}");
                } finally {
                    cameraMediator?.ReleaseCaptureBlock(cameraBlockToken);
                    await RunOnUi(() => {
                        CalibrationRunning = false;
                        IsNotMoving = true;
                    });
                }
            });
        }

        /// <summary>
        /// Renders a measured backlash pair the way it will be applied. Every user-facing
        /// string goes through here: printing one of the two directions and calling it "the
        /// measured backlash" is how a rig ran a whole session with 54.34'/45.02' configured
        /// while the panel, the notification and the log all said 54.34'.
        /// </summary>
        private static string Pair(AxisCalibrationOutcome a)
            => a.BacklashEnteringPositiveArcmin == a.BacklashEnteringNegativeArcmin
                ? $"{a.BacklashEnteringPositiveArcmin:F2}'"
                : $"+{a.BacklashEnteringPositiveArcmin:F2}'/-{a.BacklashEnteringNegativeArcmin:F2}'";

        private static string Pair(float positive, float negative)
            => positive == negative ? $"{positive:F2}'" : $"+{positive:F2}'/-{negative:F2}'";

        [RelayCommand(CanExecute = nameof(CanApplyCalibration))]
        public void ApplyCalibration() {
            try {
                // Manual values are deliberate user decisions: name them and require a
                // second Apply instead of overwriting silently.
                var manual = new List<string>();
                if (XGearRatioSource == OapaParameterSource.Manual) { manual.Add($"X factor {XGearRatio:F1} -> {DiscoveredXRatio:F1}"); }
                if (YGearRatioSource == OapaParameterSource.Manual) { manual.Add($"Y factor {YGearRatio:F1} -> {DiscoveredYRatio:F1}"); }
                if (XBacklashSource == OapaParameterSource.Manual) { manual.Add($"X backlash {Pair(XBacklashCompensation, XBacklashCompensationNegative)} -> {Pair(DiscoveredXBacklash, DiscoveredXBacklashNegative)}"); }
                if (YBacklashSource == OapaParameterSource.Manual) { manual.Add($"Y backlash {Pair(YBacklashCompensation, YBacklashCompensationNegative)} -> {Pair(DiscoveredYBacklash, DiscoveredYBacklashNegative)}"); }
                if (manual.Count > 0 && !ApplyConfirmationPending) {
                    ApplyConfirmationPending = true;
                    CalibrationStatus = $"These values were set manually and would be replaced: {string.Join("; ", manual)}. Press Apply again to confirm.";
                    Logger.Info($"OAPA calibration apply awaiting confirmation over manual values: {string.Join("; ", manual)}");
                    return;
                }
                ApplyConfirmationPending = false;

                // A direction split is only applied once a second calibration agrees with the
                // first about which way costs more. One pass cannot tell a real asymmetry from
                // a slipped measurement, and the two are not equally cheap to get wrong: the
                // difference between the pair lands as a fixed bias on every reversal, so the
                // axis can no longer be corrected by less than that difference. Field evidence
                // on one rig, two consecutive nights, same axis: 1.45'/1.96' and then
                // 2.19'/0.68' - a stable sum with the larger side flipped, which is the
                // signature of slippage rather than of mechanics.
                var (xPositive, xNegative) = ConfirmedPair(Axis.XAxis, DiscoveredXBacklash, DiscoveredXBacklashNegative);
                var (yPositive, yNegative) = ConfirmedPair(Axis.YAxis, DiscoveredYBacklash, DiscoveredYBacklashNegative);

                // The solve noise the pass measured: it sets how finely this axis can be asked
                // to reverse, so it outlives the session that measured it.
                Properties.Settings.Default.OAPAXCalibrationNoise = DiscoveredXNoise;
                Properties.Settings.Default.OAPAYCalibrationNoise = DiscoveredYNoise;

                SetXGearRatio(DiscoveredXRatio, OapaParameterSource.Calibrated);
                SetYGearRatio(DiscoveredYRatio, OapaParameterSource.Calibrated);
                SetXBacklash(xPositive, OapaParameterSource.Calibrated);
                SetYBacklash(yPositive, OapaParameterSource.Calibrated);
                SetXBacklashNegative(xNegative, OapaParameterSource.Calibrated);
                SetYBacklashNegative(yNegative, OapaParameterSource.Calibrated);
                // Applying the calibration includes picking the backlash strategy the
                // measurements call for; the change is stated explicitly, never silent.
                XBacklashMode = BacklashModePlanner.Recommend(DiscoveredXBacklash, DiscoveredXBacklashNegative, DiscoveredXNoise);
                YBacklashMode = BacklashModePlanner.Recommend(DiscoveredYBacklash, DiscoveredYBacklashNegative, DiscoveredYNoise);
                // Written after the factors, and deliberately last: the setters above have just
                // withdrawn the trust as a manual-edit precaution, and this pass is the one thing
                // entitled to grant it. Applying a calibration is the only way it is ever granted.
                SetCalibrationTrust(string.IsNullOrEmpty(DiscoveredTrustNote), DiscoveredTrustNote);
                HasCalibrationResult = false;
                CalibrationStatus = $"Applied. Backlash mode set to X: {XBacklashMode}, Y: {YBacklashMode} (from the measured X {Pair(DiscoveredXBacklash, DiscoveredXBacklashNegative)}, Y {Pair(DiscoveredYBacklash, DiscoveredYBacklashNegative)})";
                Logger.Info($"OAPA calibration applied: X={DiscoveredXRatio:F2}, Y={DiscoveredYRatio:F2}, backlash X={Pair(DiscoveredXBacklash, DiscoveredXBacklashNegative)}, Y={Pair(DiscoveredYBacklash, DiscoveredYBacklashNegative)}, modes X={XBacklashMode}, Y={YBacklashMode}");
                Notification.ShowInformation($"Calibration applied. Backlash mode X: {XBacklashMode}, Y: {YBacklashMode}", TimeSpan.FromSeconds(30));
            } catch (Exception ex) {
                Logger.Error(ex);
                Notification.ShowError($"Failed to apply calibration: {ex.Message}");
            }
        }

        /// <summary>
        /// Whether this calibration earned the faster correction values, and if not, the one
        /// thing to fix. Everything the pass already reports is used, in the order that makes
        /// the message actionable: a direction that could not be established comes before a
        /// suspect response, which comes before a factor measured on too little signal.
        ///
        /// Held per calibration rather than per axis: both axes are corrected by the same loop,
        /// so a doubt about either one is a doubt about the run.
        /// </summary>
        /// <summary>
        /// Smallest reversal this axis is asked to make. A compensated reversal lands where it
        /// was sent only in so far as the configured play matches the real play, and that is
        /// only known to the precision the calibration measured it with: the same detection
        /// threshold the sequence used to decide what counted as motion at all. Asking for
        /// less means the compensation's own error exceeds the correction being attempted.
        ///
        /// Zero until a calibration has run, and zero for an axis with no compensation - those
        /// pay no play, so they stay as fine as the solver allows. This is deliberately not a
        /// constant in the planner: the field replay suite shows that under a mechanism losing
        /// exactly its configured play, small reversals land exactly and are needed to
        /// converge. What makes them unsafe is not their size but how well the play was
        /// measured, and only this side knows that.
        /// </summary>
        private float MinimumHonourableReversal(Axis axis) {
            if (BacklashModeOf(axis) == OapaBacklashMode.Off) { return 0f; }
            var noise = axis == Axis.XAxis
                ? Properties.Settings.Default.OAPAXCalibrationNoise
                : Properties.Settings.Default.OAPAYCalibrationNoise;
            return noise > 0f
                ? (float)System.Math.Max(OapaCalibrationService.NoiseSigmaFactor * noise, OapaCalibrationService.DetectionFloorArcmin)
                : 0f;
        }

        /// <summary>
        /// Returns the backlash pair to apply: the measured split when a previous calibrated
        /// pair agrees about which direction costs more, the mean of the two otherwise.
        ///
        /// Collapsing is the cheap mistake. A symmetric value's magnitude cancels out of a
        /// two-leg plan, so an imperfect mean costs travel time and nothing else; an
        /// unestablished split costs the axis its ability to be corrected finely, permanently,
        /// until someone recalibrates. So the split has to be earned twice.
        /// </summary>
        private (float positive, float negative) ConfirmedPair(Axis axis, float measuredPositive, float measuredNegative) {
            var mean = (measuredPositive + measuredNegative) / 2f;
            var split = measuredPositive - measuredNegative;

            // The comparison is against what the *previous pass measured*, not against what it
            // applied: an unconfirmed split is applied as its mean, so comparing applied values
            // would find a symmetric pair every time and no split could ever be confirmed.
            var previousSplit = axis == Axis.XAxis
                ? Properties.Settings.Default.OAPAXBacklashSplitLast
                : Properties.Settings.Default.OAPAYBacklashSplitLast;
            if (axis == Axis.XAxis) {
                Properties.Settings.Default.OAPAXBacklashSplitLast = split;
            } else {
                Properties.Settings.Default.OAPAYBacklashSplitLast = split;
            }

            if (System.Math.Abs(split) <= float.Epsilon) { return (measuredPositive, measuredNegative); }

            if (System.Math.Abs(previousSplit) <= float.Epsilon) {
                Logger.Info($"OAPA {axis}: measured a direction split ({measuredPositive:F2}'/{measuredNegative:F2}') with nothing to confirm it against; " +
                    $"applying the mean {mean:F2}' to both directions until a second calibration agrees");
                return (mean, mean);
            }

            if (System.Math.Sign(previousSplit) != System.Math.Sign(split)) {
                Logger.Warning($"OAPA {axis}: the direction split flipped between calibrations " +
                    $"(previous difference {previousSplit:+0.00;-0.00}', now {split:+0.00;-0.00}') - " +
                    $"a flipped split is slippage, not mechanics; applying the mean {mean:F2}' to both directions");
                return (mean, mean);
            }

            Logger.Info($"OAPA {axis}: direction split confirmed by two calibrations " +
                $"(difference {previousSplit:+0.00;-0.00}' then {split:+0.00;-0.00}'); applying {measuredPositive:F2}'/{measuredNegative:F2}' per direction");
            return (measuredPositive, measuredNegative);
        }

        private static string DescribeCalibrationTrust(AxisCalibrationOutcome x, AxisCalibrationOutcome y) {
            foreach (var (axis, o) in new[] { ("azimuth", x), ("altitude", y) }) {
                if (!o.Consistent) { return $"the {axis} direction could not be established"; }
                if (o.ResponseSuspect) { return $"the two {axis} directions disagreed by more than a factor of two"; }
                if (o.BacklashSuspect) { return $"the {axis} backlash pair could not be trusted"; }
                if (o.FactorProvisional) { return $"the {axis} factor was measured on a fraction of the intended signal - calibrate again now that it is applied"; }
                if (!o.RestoredToBaseline) { return $"the {axis} axis did not verifiably return to its starting position"; }
            }
            return string.Empty;
        }

        [RelayCommand(CanExecute = nameof(HasCalibrationResult))]
        public void DiscardCalibration() {
            DiscoveredXRatio = 0;
            DiscoveredYRatio = 0;
            DiscoveredXBacklash = 0;
            DiscoveredYBacklash = 0;
            DiscoveredXBacklashNegative = 0;
            DiscoveredYBacklashNegative = 0;
            HasCalibrationResult = false;
            ApplyConfirmationPending = false;
            CalibrationConsistencyMessage = string.Empty;
            CalibrationStatus = "Discarded";
        }
    }
}
