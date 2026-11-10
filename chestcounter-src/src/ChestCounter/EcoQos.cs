using System.Runtime.InteropServices;

namespace ChestCounter;

/// <summary>Windows 10 1709+ / 11 power throttling ("efficiency mode"): lets Windows run the tool on efficient cores at low clocks.</summary>
internal static class EcoQos
{
    private const int ProcessPowerThrottling = 4;
    private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, uint size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    public static void Enable()
    {
        var state = new PROCESS_POWER_THROTTLING_STATE
        {
            Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
            StateMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
        };
        SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
    }
}
