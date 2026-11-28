using ReplayKitShared;

namespace ReplayKitSetup
{
    // the defaults the installer prefs give the microphone; the endpoints themselves are read through AudioEndpoints in shared/
    public static class Audio
    {
        // literal string obs uses for "follow the system defualt device".
        public const string DEFAULT_DEVICE_ID = SettingDefaults.MicDeviceId;
        public const string DEFAULT_DEVICE_NAME = "Default (system)";
    }
}
