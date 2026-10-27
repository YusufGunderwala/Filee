// Windows clipboard interop: sets clipboard text directly from background threads without UI thread affinity.

using System.Runtime.InteropServices;

namespace Filee.Platform.Windows;

internal static class NativeClipboard
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    public static bool SetText(string text)
    {
        if (text is null)
            return false;

        // Try opening clipboard with retries (other apps may briefly hold it open)
        var opened = false;
        for (var i = 0; i < 10; i++)
        {
            if (NativeMethods.OpenClipboard(0))
            {
                opened = true;
                break;
            }
            Thread.Sleep(20);
        }

        if (!opened)
            return false;

        try
        {
            NativeMethods.EmptyClipboard();
            var bytes = (text.Length + 1) * sizeof(char);
            var hGlobal = NativeMethods.GlobalAlloc(GMEM_MOVEABLE, (nuint)bytes);
            if (hGlobal == 0)
                return false;

            var target = NativeMethods.GlobalLock(hGlobal);
            if (target == 0)
            {
                NativeMethods.GlobalFree(hGlobal);
                return false;
            }

            try
            {
                Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                Marshal.WriteInt16(target + (text.Length * sizeof(char)), 0);
            }
            finally
            {
                NativeMethods.GlobalUnlock(hGlobal);
            }

            if (NativeMethods.SetClipboardData(CF_UNICODETEXT, hGlobal) == 0)
            {
                NativeMethods.GlobalFree(hGlobal);
                return false;
            }

            return true;
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }
}
