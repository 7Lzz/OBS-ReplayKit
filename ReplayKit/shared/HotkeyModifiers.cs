namespace ReplayKitShared
{
    // the modifier fields of an obs hotkey binding, in the order obs writes them
    internal static class HotkeyModifiers
    {
        internal static readonly string[] Order = { "control", "alt", "shift", "command" };
    }
}
