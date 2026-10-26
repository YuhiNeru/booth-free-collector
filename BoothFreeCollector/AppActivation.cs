using System.Runtime.InteropServices;

namespace BoothFreeCollector;

internal static class AppActivation
{
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string text);
    [DllImport("user32")] static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    internal static readonly uint MessageId = RegisterWindowMessage("BoothFreeCollector.Show." + BrowserBridge.PipeName);
    // Activation only: no path, URI, authentication data or command payload.
    internal static bool Signal() => MessageId != 0 && PostMessage(new IntPtr(0xffff), MessageId, IntPtr.Zero, IntPtr.Zero);
}
