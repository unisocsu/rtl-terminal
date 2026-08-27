using System.Text;

namespace RtlTerminal.Services
{
    /// <summary>
    /// Strips ANSI/VT escape sequences (CSI, OSC, etc.) from ConPTY output.
    /// This is intentionally a "level 1" terminal: it does not interpret cursor
    /// positioning or colors, it just removes the control codes so cmd.exe / PowerShell
    /// output is readable as plain text. Good enough for a first working version;
    /// a full VT100/xterm state machine can replace this later if real color/cursor
    /// support is needed.
    /// </summary>
    public static class AnsiSequenceStripper
    {
        public static string Strip(string input)
        {
            var sb = new StringBuilder(input.Length);
            int i = 0;
            while (i < input.Length)
            {
                char c = input[i];

                if (c == '\u001B' && i + 1 < input.Length) // ESC
                {
                    char next = input[i + 1];

                    if (next == '[') // CSI: ESC [ ... final-byte(0x40-0x7E)
                    {
                        int j = i + 2;
                        while (j < input.Length && !(input[j] >= 0x40 && input[j] <= 0x7E))
                            j++;
                        i = j + 1; // skip past final byte
                        continue;
                    }

                    if (next == ']') // OSC: ESC ] ... BEL or ESC \
                    {
                        int j = i + 2;
                        while (j < input.Length && input[j] != '\u0007' &&
                               !(input[j] == '\u001B' && j + 1 < input.Length && input[j + 1] == '\\'))
                            j++;
                        i = (j < input.Length && input[j] == '\u0007') ? j + 1 : j + 2;
                        continue;
                    }

                    // Other two-byte escape (ESC + one char), just skip both.
                    i += 2;
                    continue;
                }

                if (c == '\r')
                {
                    // Carriage return without newline is used for progress redraws;
                    // drop it rather than inserting a visible artifact.
                    i++;
                    continue;
                }

                if (c == '\b') // backspace
                {
                    if (sb.Length > 0) sb.Length--;
                    i++;
                    continue;
                }

                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }
    }
}
