using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Runpad.Contracts;

namespace Runpad.Modules.Monitoring;

public sealed class MonitoringModule : IRunpadModule
{
    public Task StartAsync(ModuleContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<object?> InvokeAsync(string method, JsonElement payload, CancellationToken cancellationToken)
    {
        if (method != "snapshot") throw new InvalidOperationException("Supported method: snapshot.");
        int? battery = null; bool? plugged = null;
        ulong? memoryTotal = null, memoryAvailable = null;
        if (OperatingSystem.IsWindows())
        {
            if (GetSystemPowerStatus(out var power))
            {
                if ((power.BatteryFlag & 128) == 0 && power.BatteryLifePercent <= 100) battery = power.BatteryLifePercent;
                if (power.ACLineStatus <= 1) plugged = power.ACLineStatus == 1;
            }
            var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (GlobalMemoryStatusEx(ref memory)) { memoryTotal = memory.TotalPhys; memoryAvailable = memory.AvailPhys; }
        }
        return Task.FromResult<object?>(new { computer = Environment.MachineName, user = Environment.UserName,
            os = RuntimeInformation.OSDescription, batteryPercent = battery, acConnected = plugged,
            memoryTotal, memoryAvailable, uptimeSeconds = Environment.TickCount64 / 1000,
            processorCount = Environment.ProcessorCount, moduleMemoryBytes = Process.GetCurrentProcess().WorkingSet64 });
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSystemPowerStatus(out PowerStatus status);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [StructLayout(LayoutKind.Sequential)] private struct PowerStatus { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public uint BatteryLifeTime, BatteryFullLifeTime; }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus { public uint Length, MemoryLoad; public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual; }
}
