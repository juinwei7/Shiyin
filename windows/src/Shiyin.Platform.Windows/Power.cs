using System.Diagnostics;
using System.Runtime.InteropServices;
using Shiyin.Services;

namespace Shiyin.Platform;

/// <summary>錄音、轉錄、整理時不讓系統睡眠（R1.9、design-windows §6）：PowerCreateRequest＋PowerSetRequest，`powercfg /requests` 看得到</summary>
public sealed class PowerRequests : IPowerRequests
{
    public IDisposable KeepAwake(string reason) => new Request(reason);

    sealed class Request : IDisposable
    {
        IntPtr handle;
        readonly IntPtr text;

        public Request(string reason)
        {
            text = Marshal.StringToHGlobalUni(reason);
            var context = new Native.REASON_CONTEXT
            {
                Version = Native.POWER_REQUEST_CONTEXT_VERSION, Flags = Native.POWER_REQUEST_CONTEXT_SIMPLE_STRING, SimpleReasonString = text,
            };
            handle = Native.PowerCreateRequest(ref context);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1) || !Native.PowerSetRequest(handle, Native.PowerRequestSystemRequired))
                Log.Error($"power request failed: {Marshal.GetLastPInvokeError()}");
        }

        public void Dispose()
        {
            if (handle != IntPtr.Zero && handle != new IntPtr(-1))
            {
                Native.PowerClearRequest(handle, Native.PowerRequestSystemRequired);
                Native.CloseHandle(handle);
            }
            handle = IntPtr.Zero;
            Marshal.FreeHGlobal(text);
        }
    }
}

/// <summary>子程序放進 Job Object（design-windows §10）：拾音當掉、被結束時，claude、codex 這些子程序一起結束</summary>
public sealed class ChildProcesses
{
    readonly IntPtr job;

    public ChildProcesses()
    {
        job = Native.CreateJobObject(IntPtr.Zero, null);
        var info = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (job == IntPtr.Zero || !Native.SetInformationJobObject(job, Native.JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf(info)))
            Log.Error($"job object failed: {Marshal.GetLastPInvokeError()}");
    }

    public void Add(Process process)
    {
        if (job == IntPtr.Zero) return;
        try
        {
            if (!Native.AssignProcessToJobObject(job, process.Handle)) Log.Error($"assign job failed: {Marshal.GetLastPInvokeError()}");
        }
        catch (InvalidOperationException) { }
    }
}
