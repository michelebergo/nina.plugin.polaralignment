using NINA.Core.Utility;
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
            ICameraMediator cameraMediator) : base(profileService) {
            calibrationSolver = new OapaPlateSolveSampler(profileService, imagingMediator, telescopeMediator, plateSolverFactory);
            this.cameraMediator = cameraMediator;

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
        }

        protected override string SystemName => "OAPA System";

        protected override IPolarAlignmentSystem CreateSystem() => new UniversalPolarAlignmentOAPA();

        public override bool DoAutomatedAdjustments {
            get => Properties.Settings.Default.DoAutomatedAdjustments;
            set {
                Properties.Settings.Default.DoAutomatedAdjustments = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public override double AutomatedAdjustmentSettleTime {
            get => Properties.Settings.Default.AutomatedAdjustmentSettleTime;
            set {
                Properties.Settings.Default.AutomatedAdjustmentSettleTime = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public override float XGearRatio {
            get => Properties.Settings.Default.OAPAXGearRatio;
            set => SetXGearRatio(value, MarkEdit(value, XGearRatio, XGearRatioSource));
        }

        private void SetXGearRatio(float value, OapaParameterSource source) {
            value = System.Math.Clamp(value, MinimumFactor, MaximumFactor);
            Properties.Settings.Default.OAPAXGearRatio = value;
            Properties.Settings.Default.OAPAXGearRatioSource = source.ToString();
            if (upa != null) { upa.XGearRatio = value; }
            CoreUtil.SaveSettings(Properties.Settings.Default);
            RaisePropertyChanged(nameof(XGearRatio));
            RaisePropertyChanged(nameof(XGearRatioSource));
            RaisePropertyChanged(nameof(XGearRatioSourceLabel));
            RaisePropertyChanged(nameof(PositionX));
            RefreshHomeDisplay();
        }

        public override int XSpeed {
            get => Properties.Settings.Default.OAPAXSpeed;
            set {
                Properties.Settings.Default.OAPAXSpeed = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public override float YGearRatio {
            get => Properties.Settings.Default.OAPAYGearRatio;
            set => SetYGearRatio(value, MarkEdit(value, YGearRatio, YGearRatioSource));
        }

        private void SetYGearRatio(float value, OapaParameterSource source) {
            value = System.Math.Clamp(value, MinimumFactor, MaximumFactor);
            Properties.Settings.Default.OAPAYGearRatio = value;
            Properties.Settings.Default.OAPAYGearRatioSource = source.ToString();
            if (upa != null) { upa.YGearRatio = value; }
            CoreUtil.SaveSettings(Properties.Settings.Default);
            RaisePropertyChanged(nameof(YGearRatio));
            RaisePropertyChanged(nameof(YGearRatioSource));
            RaisePropertyChanged(nameof(YGearRatioSourceLabel));
            RaisePropertyChanged(nameof(PositionY));
            RefreshHomeDisplay();
        }

        public override int YSpeed {
            get => Properties.Settings.Default.OAPAYSpeed;
            set {
                Properties.Settings.Default.OAPAYSpeed = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
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
            value = System.Math.Clamp(value, 0f, MaximumBacklashArcmin);
            Properties.Settings.Default.OAPAXBacklashCompensation = value;
            Properties.Settings.Default.OAPAXBacklashSource = source.ToString();
            CoreUtil.SaveSettings(Properties.Settings.Default);
            RaisePropertyChanged(nameof(XBacklashCompensation));
            RaisePropertyChanged(nameof(XBacklashSource));
        }

        // OAPA-specific: the altitude axis of an OAPA platform also has measurable backlash.
        // Deliberately not part of the shared VM contract - other systems do not model it.
        public float YBacklashCompensation {
            get => Properties.Settings.Default.OAPAYBacklashCompensation;
            set => SetYBacklash(value, MarkEdit(value, YBacklashCompensation, YBacklashSource));
        }

        private void SetYBacklash(float value, OapaParameterSource source) {
            value = System.Math.Clamp(value, 0f, MaximumBacklashArcmin);
            Properties.Settings.Default.OAPAYBacklashCompensation = value;
            Properties.Settings.Default.OAPAYBacklashSource = source.ToString();
            CoreUtil.SaveSettings(Properties.Settings.Default);
            RaisePropertyChanged(nameof(YBacklashCompensation));
            RaisePropertyChanged(nameof(YBacklashSource));
            RaisePropertyChanged(nameof(YBacklashSourceLabel));
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

        // Route the shared clearing logic to the OAPA-specific altitude compensation.
        protected override float GetBacklashCompensation(Axis axis) {
            return axis == Axis.YAxis ? YBacklashCompensation : base.GetBacklashCompensation(axis);
        }

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
            var plan = BacklashModePlanner.PlanMoves(mode, position, GetBacklashCompensation(axis), LastDirectionOf(axis));
            if (plan.Length > 1 || System.Math.Abs(plan[0] - position) > float.Epsilon) {
                Logger.Info($"OAPA backlash mode {mode} on {axis}: move {position:F2}' planned as [{string.Join(", ", plan)}]");
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
            return System.Math.Min(autoScaled, MaxCorrectionMagnitude);
        }

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

        [ObservableProperty]
        private float discoveredXBacklash;

        [ObservableProperty]
        private float discoveredYBacklash;

        // Solve noise measured by the calibration (S0), kept per axis so Apply can derive
        // the recommended backlash mode from backlash-vs-noise.
        [ObservableProperty]
        private float discoveredXNoise;

        [ObservableProperty]
        private float discoveredYNoise;

        [ObservableProperty]
        private string calibrationConsistencyMessage = string.Empty;

        // Slipping mechanics have no valid constant compensation: the measured values stay
        // visible for diagnosis, but applying them is blocked.
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ApplyCalibrationCommand))]
        private bool calibrationSlippageDetected;

        // Armed by the first Apply when manual values would be replaced; the second Apply
        // confirms. Disarmed by Discard and by starting a new calibration.
        [ObservableProperty]
        private bool applyConfirmationPending;

        public bool CanApplyCalibration() => HasCalibrationResult && !CalibrationSlippageDetected;

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
                        CalibrationSlippageDetected = false;
                        ApplyConfirmationPending = false;
                        CalibrationStatus = "Starting calibration...";
                        CalibrationConsistencyMessage = string.Empty;
                    });

                    Logger.Info("OAPA self-calibration started");
                    var service = new OapaCalibrationService(new SpeedAwareMotion(this), calibrationSolver);
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
                    var slippage = x.SlippageDetected || y.SlippageDetected;
                    if (slippage) {
                        var axes = x.SlippageDetected && y.SlippageDetected ? "X and Y" : (x.SlippageDetected ? "X" : "Y");
                        consistencyMsg += $" \u26a0 Slippage detected on {axes}: the backlash is not repeatable, so no constant compensation is valid. Apply is disabled - check grub screws, belt tension and friction, then re-run the calibration.";
                    }

                    await RunOnUi(() => {
                        DiscoveredXRatio = x.Ratio;
                        DiscoveredYRatio = y.Ratio;
                        DiscoveredXBacklash = x.BacklashArcmin;
                        DiscoveredYBacklash = y.BacklashArcmin;
                        DiscoveredXNoise = x.NoiseSigmaArcmin;
                        DiscoveredYNoise = y.NoiseSigmaArcmin;
                        CalibrationSlippageDetected = slippage;
                        CalibrationConsistencyMessage = consistencyMsg;
                        CalibrationStatus = $"Done. X={x.Ratio:F2}, Y={y.Ratio:F2}, backlash X={x.BacklashArcmin:F2}', Y={y.BacklashArcmin:F2}'";
                        HasCalibrationResult = true;
                    });

                    Logger.Info($"OAPA calibration result: X={x.Ratio:F2}, Y={y.Ratio:F2}, backlash X={x.BacklashArcmin:F2}', Y={y.BacklashArcmin:F2}', consistency: X={x.Consistent}, Y={y.Consistent}");
                    Notification.ShowInformation(
                        $"Calibration done. X factor: {x.Ratio:F2}, Y factor: {y.Ratio:F2}, backlash X: {x.BacklashArcmin:F2}', Y: {y.BacklashArcmin:F2}'",
                        TimeSpan.FromSeconds(30));
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

        [RelayCommand(CanExecute = nameof(CanApplyCalibration))]
        public void ApplyCalibration() {
            try {
                // Manual values are deliberate user decisions: name them and require a
                // second Apply instead of overwriting silently.
                var manual = new List<string>();
                if (XGearRatioSource == OapaParameterSource.Manual) { manual.Add($"X factor {XGearRatio:F1} -> {DiscoveredXRatio:F1}"); }
                if (YGearRatioSource == OapaParameterSource.Manual) { manual.Add($"Y factor {YGearRatio:F1} -> {DiscoveredYRatio:F1}"); }
                if (XBacklashSource == OapaParameterSource.Manual) { manual.Add($"X backlash {XBacklashCompensation:F1}' -> {DiscoveredXBacklash:F1}'"); }
                if (YBacklashSource == OapaParameterSource.Manual) { manual.Add($"Y backlash {YBacklashCompensation:F1}' -> {DiscoveredYBacklash:F1}'"); }
                if (manual.Count > 0 && !ApplyConfirmationPending) {
                    ApplyConfirmationPending = true;
                    CalibrationStatus = $"These values were set manually and would be replaced: {string.Join("; ", manual)}. Press Apply again to confirm.";
                    Logger.Info($"OAPA calibration apply awaiting confirmation over manual values: {string.Join("; ", manual)}");
                    return;
                }
                ApplyConfirmationPending = false;

                SetXGearRatio(DiscoveredXRatio, OapaParameterSource.Calibrated);
                SetYGearRatio(DiscoveredYRatio, OapaParameterSource.Calibrated);
                SetXBacklash(DiscoveredXBacklash, OapaParameterSource.Calibrated);
                SetYBacklash(DiscoveredYBacklash, OapaParameterSource.Calibrated);
                // Applying the calibration includes picking the backlash strategy the
                // measurements call for; the change is stated explicitly, never silent.
                XBacklashMode = BacklashModePlanner.Recommend(DiscoveredXBacklash, DiscoveredXNoise);
                YBacklashMode = BacklashModePlanner.Recommend(DiscoveredYBacklash, DiscoveredYNoise);
                HasCalibrationResult = false;
                CalibrationStatus = $"Applied. Backlash mode set to X: {XBacklashMode}, Y: {YBacklashMode} (from the measured {DiscoveredXBacklash:F1}'/{DiscoveredYBacklash:F1}')";
                Logger.Info($"OAPA calibration applied: X={DiscoveredXRatio:F2}, Y={DiscoveredYRatio:F2}, backlash X={DiscoveredXBacklash:F2}', Y={DiscoveredYBacklash:F2}', modes X={XBacklashMode}, Y={YBacklashMode}");
                Notification.ShowInformation($"Calibration applied. Backlash mode X: {XBacklashMode}, Y: {YBacklashMode}", TimeSpan.FromSeconds(30));
            } catch (Exception ex) {
                Logger.Error(ex);
                Notification.ShowError($"Failed to apply calibration: {ex.Message}");
            }
        }

        [RelayCommand(CanExecute = nameof(HasCalibrationResult))]
        public void DiscardCalibration() {
            DiscoveredXRatio = 0;
            DiscoveredYRatio = 0;
            DiscoveredXBacklash = 0;
            DiscoveredYBacklash = 0;
            HasCalibrationResult = false;
            ApplyConfirmationPending = false;
            CalibrationConsistencyMessage = string.Empty;
            CalibrationStatus = "Discarded";
        }
    }
}
