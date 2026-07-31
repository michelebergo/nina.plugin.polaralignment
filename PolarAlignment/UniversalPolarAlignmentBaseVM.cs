using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace NINA.Plugins.PolarAlignment {
    public abstract partial class UniversalPolarAlignmentBaseVM : BaseVM, IPolarAlignmentSystemVM {
        protected internal IPolarAlignmentSystem upa;

        protected abstract IPolarAlignmentSystem CreateSystem();
        protected abstract string SystemName { get; }

        protected UniversalPolarAlignmentBaseVM(IProfileService profileService) : base(profileService) {
            IsNotMoving = true;
        }

        /// <summary>
        /// Marshals UI-affecting property changes to the dispatcher when a WPF application
        /// exists; test hosts without one invoke directly.
        /// </summary>
        protected static async Task RunOnUi(Action action) {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null) {
                await dispatcher.BeginInvoke(action);
            } else {
                action();
            }
        }

        [ObservableProperty]
        private bool connected;

        [ObservableProperty]
        private float positionX;

        [ObservableProperty]
        private float positionY;

        [ObservableProperty]
        private float targetPositionX;

        [ObservableProperty]
        private float targetPositionY;

        public abstract bool DoAutomatedAdjustments { get; set; }
        public abstract double AutomatedAdjustmentSettleTime { get; set; }
        public abstract float XGearRatio { get; set; }
        public abstract int XSpeed { get; set; }
        public abstract float YGearRatio { get; set; }
        public abstract int YSpeed { get; set; }
        public abstract bool ReverseAzimuth { get; set; }
        public abstract bool ReverseAltitude { get; set; }
        public abstract float XBacklashCompensation { get; set; }

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(NudgeXCommand))]
        [NotifyCanExecuteChangedFor(nameof(NudgeYCommand))]
        [NotifyCanExecuteChangedFor(nameof(MoveXCommand))]
        [NotifyCanExecuteChangedFor(nameof(MoveYCommand))]
        private bool isNotMoving;

        private CancellationTokenSource pollCts;

        [RelayCommand]
        public Task Connect() {
            if (upa?.Connected == true) { return Task.CompletedTask; }
            return Task.Run(async () => {
                try {
                    await RunOnUi(() => IsNotMoving = true);

                    upa = CreateSystem();
                    _ = StartPoll();
                    Connected = true;
                    Notification.ShowInformation($"Successfully connected to {SystemName}");
                } catch (Exception ex) {
                    Logger.Error(ex);
                    Notification.ShowError($"Unable to connect to {SystemName}");
                }
            });
        }

        [RelayCommand]
        public void Disconnect() {
            if (upa?.Connected != true) { return; }
            Connected = false;
            try {
                pollCts?.Cancel();
                upa.Dispose();
            } catch (Exception ex) {
                Logger.Error(ex);
            }
            Notification.ShowInformation($"Disconnected from {SystemName}");
        }

        [RelayCommand(CanExecute = (nameof(IsNotMoving)))]
        public async Task NudgeX(float position, CancellationToken token) {
            await TryNudgeX(position, token);
        }

        // Manual nudges keep the legacy contract: always clear backlash on reversal.
        public Task<bool> TryNudgeX(float position, CancellationToken token) =>
            TryNudgeXCore(position, skipClearingBelowCompensation: false, token);

        protected async Task<bool> TryNudgeXCore(float position, bool skipClearingBelowCompensation, CancellationToken token) {
            try {
                if (ReverseAzimuth) { position = position * -1; }
                await RunOnUi(() => IsNotMoving = false);

                Logger.Info($"Nudging {SystemName} along X axis by {position}");
                var lastDirection = upa.XLastDirection;
                await upa.MoveRelative(Axis.XAxis, XSpeed, position, token).ConfigureAwait(false);
                var currentDirection = upa.XLastDirection;
                await ClearBacklash(Axis.XAxis, XSpeed, skipClearingBelowCompensation ? position : float.MaxValue, lastDirection, currentDirection, token);
                return true;
            } catch (Exception ex) {
                Logger.Error(ex);
                if (ex is TimeoutException) {
                    Notification.ShowError($"Movement timeout: {ex.Message}");
                }
                return false;
            } finally {
                await RunOnUi(() => IsNotMoving = true);
            }
        }

        [RelayCommand(CanExecute = (nameof(IsNotMoving)))]
        public async Task NudgeY(float position, CancellationToken token) {
            await TryNudgeY(position, token);
        }

        // Manual nudges keep the legacy contract: always clear backlash on reversal.
        public Task<bool> TryNudgeY(float position, CancellationToken token) =>
            TryNudgeYCore(position, skipClearingBelowCompensation: false, token);

        protected async Task<bool> TryNudgeYCore(float position, bool skipClearingBelowCompensation, CancellationToken token) {
            try {
                if (ReverseAltitude) { position = position * -1; }
                await RunOnUi(() => IsNotMoving = false);

                Logger.Info($"Nudging {SystemName} along Y axis by {position}");
                var lastDirection = upa.YLastDirection;
                await upa.MoveRelative(Axis.YAxis, YSpeed, position, token).ConfigureAwait(false);
                var currentDirection = upa.YLastDirection;
                await ClearBacklash(Axis.YAxis, YSpeed, skipClearingBelowCompensation ? position : float.MaxValue, lastDirection, currentDirection, token);
                return true;
            } catch (Exception ex) {
                Logger.Error(ex);
                if (ex is TimeoutException) {
                    Notification.ShowError($"Movement timeout: {ex.Message}");
                }
                return false;
            } finally {
                await RunOnUi(() => IsNotMoving = true);
            }
        }

        /// <summary>
        /// Automated fine-approach nudges default to the exact manual behavior. Systems
        /// with a skip-clearing policy (OAPA) override these.
        /// </summary>
        public virtual Task<bool> TryFineNudgeX(float position, CancellationToken token) => TryNudgeX(position, token);

        public virtual Task<bool> TryFineNudgeY(float position, CancellationToken token) => TryNudgeY(position, token);

        public new void RaiseAllPropertiesChanged() {
            base.RaiseAllPropertiesChanged();
        }

        [RelayCommand(CanExecute = (nameof(IsNotMoving)))]
        public async Task MoveX(CancellationToken token) {
            try {
                await RunOnUi(() => IsNotMoving = false);

                var target = TargetPositionX;
                if (ReverseAzimuth) { target = target * -1; }

                Logger.Info($"Moving {SystemName} along X axis to {target}");
                var lastDirection = upa.XLastDirection;

                await upa.MoveAbsolute(Axis.XAxis, XSpeed, target, token).ConfigureAwait(false);
                var currentDirection = upa.XLastDirection;
                await ClearBacklash(Axis.XAxis, XSpeed, float.MaxValue, lastDirection, currentDirection, token);
            } catch (Exception ex) {
                Logger.Error(ex);
                if (ex is TimeoutException) {
                    Notification.ShowError($"Movement timeout: {ex.Message}");
                }
            } finally {
                await RunOnUi(() => IsNotMoving = true);
            }
        }

        /// <summary>
        /// Backlash compensation for the given axis. The base policy compensates azimuth
        /// only: the Avalon UPAS altitude axis does not use backlash compensation. Systems
        /// with a measured altitude backlash (OAPA) override this to supply it.
        /// </summary>
        protected virtual float GetBacklashCompensation(Axis axis) {
            return axis == Axis.XAxis ? XBacklashCompensation : 0f;
        }

        private async Task ClearBacklash(Axis axis, int speed, float movedMagnitude, LastDirection lastDirection, LastDirection currentDirection, CancellationToken token) {
            var compensation = GetBacklashCompensation(axis);
            if (BacklashCompensationPlanner.ShouldClear(movedMagnitude, compensation, lastDirection, currentDirection)) {
                Logger.Info($"Direction changed on {axis}. Clearing backlash");
                var sequence = BacklashCompensationPlanner.CreateSequence(compensation, currentDirection);
                await upa.MoveRelative(axis, speed, sequence.FirstMove, token).ConfigureAwait(false);
                await upa.MoveRelative(axis, speed, sequence.SecondMove, token).ConfigureAwait(false);
            } else if (lastDirection != currentDirection && Math.Abs(compensation) > 0) {
                Logger.Info($"Direction changed on {axis} but commanded move ({movedMagnitude:F2}) is smaller than the backlash compensation ({compensation:F2}); skipping backlash clearing");
            }
        }

        /// <summary>
        /// The base policy keeps the legacy conservative controller profile; systems whose
        /// hardware tolerates faster convergence (OAPA) opt into the aggressive one.
        /// </summary>
        public virtual bool AggressiveCorrectionProfile => false;

        /// <summary>
        /// Default correction-limit capability: the controller's stock per-cycle bound.
        /// Systems with a specific policy (e.g. OAPA error-proportional scaling) override this.
        /// </summary>
        public virtual double GetMaximumCorrectionMagnitude(double currentTotalErrorArcmin) {
            return AutomatedAdjustmentController.DefaultMaximumMoveMagnitude;
        }

        [RelayCommand(CanExecute = (nameof(IsNotMoving)))]
        public async Task MoveY(CancellationToken token) {
            try {
                await RunOnUi(() => IsNotMoving = false);

                var target = TargetPositionY;
                if (ReverseAltitude) { target = target * -1; }

                Logger.Info($"Moving {SystemName} along Y axis to {target}");
                var lastDirection = upa.YLastDirection;
                await upa.MoveAbsolute(Axis.YAxis, YSpeed, target, token).ConfigureAwait(false);
                var currentDirection = upa.YLastDirection;
                await ClearBacklash(Axis.YAxis, YSpeed, float.MaxValue, lastDirection, currentDirection, token);
            } catch (Exception ex) {
                Logger.Error(ex);
                if (ex is TimeoutException) {
                    Notification.ShowError($"Movement timeout: {ex.Message}");
                }
            } finally {
                await RunOnUi(() => IsNotMoving = true);
            }
        }

        private async Task StartPoll() {
            pollCts = new CancellationTokenSource();
            var token = pollCts.Token;
            var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(300));
            try {
                while (await timer.WaitForNextTickAsync(token) && !token.IsCancellationRequested) {
                    if (IsNotMoving) {
                        await upa.RefreshStatus(token);
                    }
                    await Application.Current.Dispatcher.BeginInvoke(UpdatePositions);
                }
            } catch (OperationCanceledException) {
            } catch (Exception ex) {
                Logger.Error(ex);
            }
        }

        private void UpdatePositions() {
            if (upa == null) { return; }
            PositionX = upa.XPosition1;
            PositionY = upa.YPosition1;
        }
    }
}
