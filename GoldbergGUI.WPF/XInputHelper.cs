using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace GoldbergGUI.WPF
{
    /// <summary>
    /// Polls XInput controllers and maps physical inputs to gbe_fork binding strings.
    /// Requires xinput1_4.dll (present on Windows 8.1+).
    /// </summary>
    internal static class XInputHelper
    {
        private const short Deadzone          = 10_000;  // ~30 % of 32 767
        private const byte  TriggerThreshold  = 100;     // ~40 % of 255
        private const int   PollMs            = 50;

        // ── XInput P/Invoke ──────────────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_GAMEPAD
        {
            public ushort wButtons;
            public byte   bLeftTrigger;
            public byte   bRightTrigger;
            public short  sThumbLX;
            public short  sThumbLY;
            public short  sThumbRX;
            public short  sThumbRY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_STATE
        {
            public uint           dwPacketNumber;
            public XINPUT_GAMEPAD Gamepad;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState",
                   CallingConvention = CallingConvention.Winapi)]
        private static extern uint XInputGetState(uint index, out XINPUT_STATE state);

        // ── Button bitmask table ─────────────────────────────────────────────

        private static readonly (ushort Mask, string Binding)[] ButtonMap =
        {
            (0x0001, "DUP"),     (0x0002, "DDOWN"),   (0x0004, "DLEFT"),   (0x0008, "DRIGHT"),
            (0x0010, "START"),   (0x0020, "BACK"),
            (0x0040, "LSTICK"),  (0x0080, "RSTICK"),
            (0x0100, "LBUMPER"), (0x0200, "RBUMPER"),
            (0x1000, "A"),       (0x2000, "B"),        (0x4000, "X"),       (0x8000, "Y"),
        };

        // ── Public API ───────────────────────────────────────────────────────

        /// <summary>
        /// Asynchronously polls all connected XInput controllers until a new input is
        /// detected or <paramref name="ct"/> is cancelled.
        /// </summary>
        /// <returns>
        /// A gbe_fork binding string such as "RBUMPER" or "DRJOYDOWN",
        /// or <c>null</c> if the token was cancelled before any input was detected.
        /// </returns>
        /// <exception cref="System.DllNotFoundException">
        /// Thrown when xinput1_4.dll is not available on this system.
        /// </exception>
        public static async Task<string> DetectInputAsync(CancellationToken ct)
        {
            // Snapshot the current state so only *newly* activated inputs are reported.
            var baseline = new XINPUT_STATE[4];
            for (uint i = 0; i < 4; i++)
                XInputGetState(i, out baseline[i]);   // error return is fine; zero struct = neutral baseline

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(PollMs, ct).ConfigureAwait(false);

                for (uint i = 0; i < 4; i++)
                {
                    if (XInputGetState(i, out var cur) != 0) continue;  // not connected

                    ref readonly var gp = ref cur.Gamepad;
                    ref readonly var bp = ref baseline[i].Gamepad;

                    // ── Buttons ──────────────────────────────────────────────
                    var newBtns = (ushort)(gp.wButtons & ~bp.wButtons);
                    foreach (var (mask, name) in ButtonMap)
                        if ((newBtns & mask) != 0) return name;

                    // ── Triggers ─────────────────────────────────────────────
                    if (gp.bLeftTrigger  > TriggerThreshold && bp.bLeftTrigger  <= TriggerThreshold)
                        return "LTRIGGER=trigger";
                    if (gp.bRightTrigger > TriggerThreshold && bp.bRightTrigger <= TriggerThreshold)
                        return "RTRIGGER=trigger";

                    // ── Left stick ───────────────────────────────────────────
                    if (gp.sThumbLX >  Deadzone && bp.sThumbLX <=  Deadzone) return "DLJOYRIGHT";
                    if (gp.sThumbLX < -Deadzone && bp.sThumbLX >= -Deadzone) return "DLJOYLEFT";
                    if (gp.sThumbLY >  Deadzone && bp.sThumbLY <=  Deadzone) return "DLJOYUP";
                    if (gp.sThumbLY < -Deadzone && bp.sThumbLY >= -Deadzone) return "DLJOYDOWN";

                    // ── Right stick ──────────────────────────────────────────
                    if (gp.sThumbRX >  Deadzone && bp.sThumbRX <=  Deadzone) return "DRJOYRIGHT";
                    if (gp.sThumbRX < -Deadzone && bp.sThumbRX >= -Deadzone) return "DRJOYLEFT";
                    if (gp.sThumbRY >  Deadzone && bp.sThumbRY <=  Deadzone) return "DRJOYUP";
                    if (gp.sThumbRY < -Deadzone && bp.sThumbRY >= -Deadzone) return "DRJOYDOWN";
                }
            }

            return null;
        }
    }
}
