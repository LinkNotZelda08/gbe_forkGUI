using GoldbergGUI.Core.Utils;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace GoldbergGUI.WPF.Views
{
    /// <summary>
    /// Multi-binding editor dialog.
    /// The user can build a binding that consists of one or more parts
    /// (e.g. "DUP,DLJOYUP") by:
    ///   • picking from the drop-down list, or
    ///   • pressing a physical controller input (with joystick disambiguation).
    /// The final comma-joined internal string is exposed via <see cref="ResultBinding"/>.
    /// </summary>
    public partial class ControllerDetectDialog : Window
    {
        // ── Binding data ────────────────────────────────────────────────────────

        /// <summary>Live list of binding parts shown in the chip list.</summary>
        private readonly ObservableCollection<BindingPart> _parts =
            new ObservableCollection<BindingPart>();

        /// <summary>
        /// The final comma-joined gbe_fork binding string (e.g. "DUP,DLJOYUP"),
        /// or <c>null</c> if the dialog was cancelled.
        /// </summary>
        public string ResultBinding { get; private set; }

        // ── Detection state ─────────────────────────────────────────────────────

        private CancellationTokenSource _detectCts;

        // Non-null while the disambiguation panel is shown.
        private TaskCompletionSource<string> _disambiguation;
        private string _pendingDirection;
        private string _pendingAnalog;

        // ── Constructor ─────────────────────────────────────────────────────────

        /// <param name="existingBinding">
        /// Comma-separated internal binding string that pre-populates the editor,
        /// e.g. "DUP,DLJOYUP".  Pass <c>null</c> or empty for an empty editor.
        /// </param>
        public ControllerDetectDialog(string existingBinding = null)
        {
            InitializeComponent();

            // Wire up the parts list to the ItemsControl.
            PartsList.ItemsSource = _parts;
            _parts.CollectionChanged += (_, __) => RefreshEmptyHint();

            // Pre-populate from the caller's existing binding.
            if (!string.IsNullOrWhiteSpace(existingBinding))
            {
                foreach (var token in existingBinding.Split(','))
                {
                    var trimmed = token.Trim();
                    if (!string.IsNullOrEmpty(trimmed))
                        _parts.Add(new BindingPart(trimmed));
                }
            }

            RefreshEmptyHint();
        }

        // ── Theme / chrome ───────────────────────────────────────────────────────

        private void Window_SourceInitialized(object sender, EventArgs e)
        {
            bool isDark = true;
            if (Application.Current?.Resources["BackgroundBrush"] is SolidColorBrush bg)
            {
                double lum = (0.299 * bg.Color.R + 0.587 * bg.Color.G + 0.114 * bg.Color.B) / 255.0;
                isDark = lum < 0.5;
            }
            MainWindow.SetTitleBarDarkMode(this, isDark);
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Nothing to do on load — detection is started by the user clicking Detect.
        }

        // ── UI helpers ───────────────────────────────────────────────────────────

        private void RefreshEmptyHint()
        {
            EmptyHint.Visibility = _parts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetDetectIdle()
        {
            DetectBtn.Content          = "🎮 Detect";
            DetectBtn.IsEnabled        = true;
            DetectStatusText.Text      = "Press a physical input to detect it automatically.";
            DisambigPanel.Visibility   = Visibility.Collapsed;
        }

        private void SetDetectListening()
        {
            DetectBtn.Content          = "⏹ Stop";
            DetectBtn.IsEnabled        = true;
            DetectStatusText.Text      = "Listening… press a button, move a stick, or squeeze a trigger.";
            DisambigPanel.Visibility   = Visibility.Collapsed;
        }

        // ── Event handlers ───────────────────────────────────────────────────────

        private void RemovePart_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is BindingPart part)
                _parts.Remove(part);
        }

        private void AddFromList_Click(object sender, RoutedEventArgs e)
        {
            if (PickerCombo.SelectedItem is not ControllerBindingOption opt) return;
            AddPartIfNew(opt.InternalValue);
        }

        private void Detect_Click(object sender, RoutedEventArgs e)
        {
            if (_detectCts != null && !_detectCts.IsCancellationRequested)
            {
                // Already listening — stop.
                StopDetection();
                SetDetectIdle();
            }
            else
            {
                StartDetection();
            }
        }

        private void DirectionBtn_Click(object sender, RoutedEventArgs e)
        {
            _disambiguation?.TrySetResult(_pendingDirection);
        }

        private void AnalogBtn_Click(object sender, RoutedEventArgs e)
        {
            _disambiguation?.TrySetResult(_pendingAnalog);
        }

        private void Accept_Click(object sender, RoutedEventArgs e)
        {
            StopDetection();
            ResultBinding  = _parts.Count > 0
                ? string.Join(",", _parts.Select(p => p.InternalValue))
                : string.Empty;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            StopDetection();
            if (DialogResult == null) DialogResult = false;
        }

        protected override void OnClosed(EventArgs e)
        {
            StopDetection();
            base.OnClosed(e);
        }

        // ── Detection logic ──────────────────────────────────────────────────────

        private void StartDetection()
        {
            _detectCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            SetDetectListening();
            _ = RunDetectLoopAsync(_detectCts.Token);
        }

        private void StopDetection()
        {
            _detectCts?.Cancel();
            _detectCts?.Dispose();
            _detectCts = null;
            _disambiguation?.TrySetCanceled();
            _disambiguation = null;
        }

        private async Task RunDetectLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    string raw;
                    try
                    {
                        raw = await XInputHelper.DetectInputAsync(ct);
                    }
                    catch (DllNotFoundException)
                    {
                        DetectStatusText.Text = "XInput (xinput1_4.dll) not available on this system.";
                        SetDetectIdle();
                        return;
                    }

                    if (raw == null)
                    {
                        // Timed out without input.
                        DetectStatusText.Text = "No input detected (timed out). Try again.";
                        SetDetectIdle();
                        return;
                    }

                    if (ct.IsCancellationRequested) return;

                    string chosen;
                    if (IsJoystickDirection(raw))
                    {
                        chosen = await AskDisambiguateAsync(raw, ct);
                        if (chosen == null) return;  // cancelled during disambiguation
                    }
                    else
                    {
                        chosen = raw;
                    }

                    if (ct.IsCancellationRequested) return;

                    AddPartIfNew(chosen);

                    // Reset detection state so the user can detect another input.
                    _disambiguation = null;
                    DisambigPanel.Visibility = Visibility.Collapsed;

                    // Restart a fresh CTS for the next round (reuse same loop).
                    _detectCts?.Dispose();
                    _detectCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    SetDetectListening();

                    // Tail-recurse: loop will use the new token on next await.
                    // But since ct is already cancelled (old token), we have to re-enter.
                    _ = RunDetectLoopAsync(_detectCts.Token);
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                // Detection was stopped — leave the UI as-is unless we're still "listening".
            }
            catch (Exception ex)
            {
                DetectStatusText.Text = $"Error: {ex.Message}";
                SetDetectIdle();
            }
        }

        /// <summary>
        /// Shows the disambiguation panel and awaits the user's direction vs. analog choice.
        /// Returns <c>null</c> if the user cancels or detection is stopped.
        /// </summary>
        private async Task<string> AskDisambiguateAsync(string dirBinding, CancellationToken ct)
        {
            var (dirB, analogB, friendlyDir, friendlyAnalog, stickName) = GetJoystickInfo(dirBinding);
            _pendingDirection = dirB;
            _pendingAnalog    = analogB;

            DetectStatusText.Text    = $"{stickName} stick detected — choose the binding type:";
            DirectionBtn.Content     = friendlyDir;
            AnalogBtn.Content        = friendlyAnalog;
            DisambigPanel.Visibility = Visibility.Visible;
            DetectBtn.IsEnabled      = false;   // prevent re-entrance during disambiguation

            _disambiguation = new TaskCompletionSource<string>();

            try
            {
                using (ct.Register(() => _disambiguation.TrySetCanceled()))
                    return await _disambiguation.Task;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            finally
            {
                DetectBtn.IsEnabled      = true;
                DisambigPanel.Visibility = Visibility.Collapsed;
            }
        }

        // ── Parts helpers ────────────────────────────────────────────────────────

        private void AddPartIfNew(string internalValue)
        {
            if (string.IsNullOrWhiteSpace(internalValue)) return;
            if (_parts.Any(p => string.Equals(p.InternalValue, internalValue,
                                              StringComparison.OrdinalIgnoreCase)))
                return;   // duplicate — ignore silently
            _parts.Add(new BindingPart(internalValue));
        }

        // ── Joystick helpers ─────────────────────────────────────────────────────

        private static bool IsJoystickDirection(string b) =>
            b == "DLJOYUP"   || b == "DLJOYDOWN"  || b == "DLJOYLEFT"  || b == "DLJOYRIGHT" ||
            b == "DRJOYUP"   || b == "DRJOYDOWN"  || b == "DRJOYLEFT"  || b == "DRJOYRIGHT";

        private static (string dirBinding, string analogBinding,
                        string friendlyDir, string friendlyAnalog, string stickName)
            GetJoystickInfo(string dirBinding)
        {
            bool isLeft      = dirBinding.StartsWith("DLJ");
            string analogB   = isLeft ? "LJOY=joystick_move" : "RJOY=joystick_move";
            string stickName = isLeft ? "Left" : "Right";
            string friendlyD = ControllerBindingMap.ToFriendly(dirBinding);
            string friendlyA = ControllerBindingMap.ToFriendly(analogB);
            return (dirBinding, analogB, friendlyD, friendlyA, stickName);
        }
    }

    // ── BindingPart ──────────────────────────────────────────────────────────────

    /// <summary>A single entry in the multi-binding list.</summary>
    public sealed class BindingPart
    {
        public string InternalValue { get; }
        public string FriendlyName  { get; }

        public BindingPart(string internalValue)
        {
            InternalValue = internalValue;
            FriendlyName  = ControllerBindingMap.ToFriendly(internalValue);
        }
    }
}
