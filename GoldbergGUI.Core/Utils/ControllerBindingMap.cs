using System;
using System.Collections.Generic;
using System.Linq;

namespace GoldbergGUI.Core.Utils
{
    /// <summary>One selectable binding option shown in the controller Bindings drop-down.</summary>
    public sealed record ControllerBindingOption(string FriendlyName, string InternalValue)
    {
        // Ensures ComboBox selection boxes (and any other ToString consumers) always show
        // the friendly display name rather than the auto-generated record representation.
        public override string ToString() => FriendlyName;
    }

    /// <summary>
    /// Bidirectional map between gbe_fork internal binding strings (e.g. "RBUMPER") and
    /// human-readable display names (e.g. "Right Bumper").
    /// Supports comma-separated multi-bindings (e.g. "DUP,DLJOYUP" ↔ "D-Pad Up, Left Stick Up").
    /// </summary>
    public static class ControllerBindingMap
    {
        /// <summary>All valid bindings ordered for the drop-down list.</summary>
        public static IReadOnlyList<ControllerBindingOption> AllOptions { get; } =
            new List<ControllerBindingOption>
            {
                // ── Face buttons ─────────────────────────────────────────────────
                new("A Button",                   "A"),
                new("B Button",                   "B"),
                new("X Button",                   "X"),
                new("Y Button",                   "Y"),
                // ── Shoulder buttons ─────────────────────────────────────────────
                new("Left Bumper",                "LBUMPER"),
                new("Right Bumper",               "RBUMPER"),
                // ── Triggers — digital (on/off) ───────────────────────────────────
                new("Left Trigger (Digital)",     "DLTRIGGER"),
                new("Right Trigger (Digital)",    "DRTRIGGER"),
                // ── Triggers — analog (axis value) ───────────────────────────────
                new("Left Trigger (Analog)",      "LTRIGGER=trigger"),
                new("Right Trigger (Analog)",     "RTRIGGER=trigger"),
                // ── Menu buttons ─────────────────────────────────────────────────
                new("Start",                      "START"),
                new("Back / Select",              "BACK"),
                // ── Stick clicks ─────────────────────────────────────────────────
                new("Left Stick Click",           "LSTICK"),
                new("Right Stick Click",          "RSTICK"),
                // ── D-Pad ────────────────────────────────────────────────────────
                new("D-Pad Up",                   "DUP"),
                new("D-Pad Down",                 "DDOWN"),
                new("D-Pad Left",                 "DLEFT"),
                new("D-Pad Right",                "DRIGHT"),
                // ── Left stick — digital directions ──────────────────────────────
                new("Left Stick Up",              "DLJOYUP"),
                new("Left Stick Down",            "DLJOYDOWN"),
                new("Left Stick Left",            "DLJOYLEFT"),
                new("Left Stick Right",           "DLJOYRIGHT"),
                // ── Right stick — digital directions ─────────────────────────────
                new("Right Stick Up",             "DRJOYUP"),
                new("Right Stick Down",           "DRJOYDOWN"),
                new("Right Stick Left",           "DRJOYLEFT"),
                new("Right Stick Right",          "DRJOYRIGHT"),
                // ── Analog sticks ────────────────────────────────────────────────
                new("Left Joystick (Analog)",     "LJOY=joystick_move"),
                new("Right Joystick (Analog)",    "RJOY=joystick_move"),
            }.AsReadOnly();

        private static readonly Dictionary<string, string> _toFriendly;
        private static readonly Dictionary<string, string> _toInternal;

        static ControllerBindingMap()
        {
            _toFriendly = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _toInternal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var opt in AllOptions)
            {
                _toFriendly[opt.InternalValue] = opt.FriendlyName;
                _toInternal[opt.FriendlyName]  = opt.InternalValue;
            }
        }

        // ── Public API ───────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the friendly display name for <paramref name="internalValue"/>
        /// (e.g. "RBUMPER" → "Right Bumper").  Handles comma-separated multi-bindings:
        /// "DUP,DLJOYUP" → "D-Pad Up, Left Stick Up".
        /// Returns <paramref name="internalValue"/> unchanged for unknown tokens.
        /// </summary>
        public static string ToFriendly(string internalValue)
        {
            if (string.IsNullOrEmpty(internalValue)) return internalValue ?? string.Empty;
            if (internalValue.Contains(','))
                return string.Join(", ", internalValue.Split(',')
                    .Select(p => ToFriendlySingle(p.Trim())));
            return ToFriendlySingle(internalValue.Trim());
        }

        /// <summary>
        /// Returns the gbe_fork internal string for <paramref name="displayName"/>
        /// (e.g. "Right Bumper" → "RBUMPER").  Handles comma-separated multi-bindings:
        /// "D-Pad Up, Left Stick Up" → "DUP,DLJOYUP".
        /// Also accepts already-internal tokens (e.g. "DUP") and passes them through.
        /// </summary>
        public static string ToInternal(string displayName)
        {
            if (string.IsNullOrEmpty(displayName)) return displayName ?? string.Empty;
            if (displayName.Contains(','))
                return string.Join(",", displayName.Split(',')
                    .Select(p => ToInternalSingle(p.Trim())));
            return ToInternalSingle(displayName.Trim());
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        private static string ToFriendlySingle(string v) =>
            _toFriendly.TryGetValue(v, out var f) ? f : v;

        private static string ToInternalSingle(string v) =>
            _toInternal.TryGetValue(v, out var i) ? i : v;
    }
}
