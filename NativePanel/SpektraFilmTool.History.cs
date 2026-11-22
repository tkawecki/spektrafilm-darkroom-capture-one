using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

// Undo and redo of Spektrafilm looks inside the panel (2026-11-19). Capture One's Ctrl+Z does not reach looks saved
// by the panel, so the panel keeps its own per-variant history (LookHistory) and handles Ctrl+Z, Ctrl+Y and
// Ctrl+Shift+Z while it has keyboard focus. Defaults and Load preset first keep the replaced look as a version.
internal static partial class SpektraFilmTool
{
    private sealed partial class LookPanel
    {
        private const int MaxVersionSourceName = 90;
        private readonly LookHistory history = new();
        private Button? undoButton;
        private Button? redoButton;
        private bool restoringHistory;

        private FrameworkElement MakeHistoryControls()
        {
            undoButton = Button("Undo", () => { StepHistory(redo: false); return System.Threading.Tasks.Task.CompletedTask; });
            redoButton = Button("Redo", () => { StepHistory(redo: true); return System.Threading.Tasks.Task.CompletedTask; });
            undoButton.ToolTip = "Undo the last Spektrafilm change on this image (Ctrl+Z while the panel has focus). Capture One's own Undo does not cover Spektrafilm settings. The history lasts until Capture One closes.";
            redoButton.ToolTip = "Redo (Ctrl+Y or Ctrl+Shift+Z while the panel has focus).";
            undoButton.IsEnabled = redoButton.IsEnabled = false;
            var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            row.Children.Add(undoButton);
            row.Children.Add(redoButton);
            PreviewKeyDown += OnHistoryKey;
            return row;
        }

        // The panel's own text boxes are numeric slider fields, so Ctrl+Z always means the look history here.
        // Ctrl+Alt (AltGr on Polish keyboards) is left alone. The keys stay handled even when there is nothing
        // to undo, so a press inside the panel never undoes an unrelated Capture One adjustment.
        private void OnHistoryKey(object sender, KeyEventArgs args)
        {
            var modifiers = Keyboard.Modifiers;
            if (args.Key is not (Key.Z or Key.Y) || (modifiers & ModifierKeys.Control) == 0 || (modifiers & ModifierKeys.Alt) != 0)
                return;
            args.Handled = true;
            if (!args.IsRepeat)
                StepHistory(redo: args.Key == Key.Y || (modifiers & ModifierKeys.Shift) != 0);
        }

        private void StepHistory(bool redo)
        {
            var shown = displayedVariant;
            if (!SyncSelection() || displayedVariant is not { } variant || saveBlockedForInvalidLook)
                return;
            if (shown != variant)
            {
                // The panel has just switched to another image; do not undo it without the user seeing it first.
                status.Text = "The panel now shows another image. Press Undo or Redo again to change this image.";
                UpdateHistoryButtons();
                return;
            }
            SaveLook(); // Records a change still waiting in the coalescing window.
            var snapshot = history.Peek(variant, redo);
            if (snapshot is null)
            {
                status.Text = redo ? "Nothing to redo." : "Nothing to undo.";
                UpdateHistoryButtons();
                return;
            }
            // Deferred change events from the re-applied controls settle the step instead of becoming new steps.
            restoringHistory = true;
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => restoringHistory = false));
            try
            {
                Apply(snapshot);
                history.Commit(variant, redo); // Only after the look was applied, so a failure keeps the history in place.
                SaveLook();
                status.Text = redo ? "Redone." : "Undone.";
            }
            catch (Exception error)
            {
                Report(error);
                ReapplyCurrent(variant);
            }
            UpdateHistoryButtons();
        }

        private void Apply(string snapshot)
        {
            var look = JsonSerializer.Deserialize<VariantLook>(snapshot)
                ?? throw new InvalidDataException("The undo history entry is empty.");
            ApplyControls(look.Look, look.Optics, look.Enabled, look.Grain);
        }

        // Puts back the controls of the current step after a failed undo, which may have applied part of a look.
        private void ReapplyCurrent(Guid variant)
        {
            if (history.Current(variant) is not { } current)
                return;
            try { Apply(current); }
            catch (Exception error) { StartupHook.Log("Could not re-apply the current Spektrafilm look after a failed undo: " + error); }
        }

        // History must never stop a look from being shown or saved, so its failures are only logged.
        private void RecordHistory(Guid variant, VariantLook look, bool discrete)
        {
            try
            {
                var snapshot = JsonSerializer.Serialize(look);
                if (restoringHistory)
                    history.Settle(variant, snapshot);
                else
                    history.Record(variant, snapshot, MonotonicNow, discrete);
            }
            catch (Exception error)
            {
                StartupHook.Log("Spektrafilm undo history could not record a change: " + error);
            }
            UpdateHistoryButtons();
        }

        private static DateTime MonotonicNow => DateTime.MinValue + TimeSpan.FromMilliseconds(Environment.TickCount64);

        // Records the displayed controls without saving them (a newly shown image or a look written elsewhere).
        private void RecordDisplayedLook()
        {
            try
            {
                if (!saveBlockedForInvalidLook && displayedVariant is { } variant && CaptureLook() is { } look)
                {
                    RecordHistory(variant, look, discrete: true);
                    return;
                }
            }
            catch (Exception error)
            {
                StartupHook.Log("Spektrafilm undo history could not read the displayed look: " + error);
            }
            UpdateHistoryButtons();
        }

        private void UpdateHistoryButtons()
        {
            if (undoButton is null || redoButton is null)
                return;
            var usable = !disposed && StocksReady && !saveBlockedForInvalidLook && displayedVariant is not null;
            undoButton.IsEnabled = usable && history.CanUndo(displayedVariant!.Value);
            redoButton.IsEnabled = usable && history.CanRedo(displayedVariant!.Value);
        }

        /// <summary>
        /// Keeps the displayed look as a version before an action replaces all of it. Returns false, and reports
        /// why, when the version could not be written; the caller then leaves the look unchanged.
        /// </summary>
        private bool KeepVersionBefore(string name)
        {
            if (displayedVariant is not { } variant)
                return true;
            VariantLook? previous;
            try
            {
                SaveLook();
                previous = looks.Read(variant);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException)
            {
                // An unreadable saved look is archived by VariantLookStore before it is replaced.
                return true;
            }
            if (previous is null)
                return true;
            try
            {
                LookVersions.Save(looks.VersionsFolder(variant), variant, name, previous);
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or JsonException)
            {
                Report(new IOException($"The current look could not be kept as a version, so it was not replaced. {error.Message}", error));
                return false;
            }
        }

        private static string VersionSourceName(string path)
        {
            var name = Path.GetFileName(path);
            return name.Length <= MaxVersionSourceName ? name : name[..MaxVersionSourceName];
        }
    }
}
