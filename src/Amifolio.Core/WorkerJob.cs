using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Amifolio.Core;

internal sealed class WorkerJob : SafeHandleZeroOrMinusOneIsInvalid
{
    private WorkerJob():base(true){}
    [StructLayout(LayoutKind.Sequential)]private struct BasicLimits{internal long ProcessTime,JobTime;internal uint Flags;internal nuint MinimumWorkingSet,MaximumWorkingSet;internal uint ActiveProcesses;internal nuint Affinity;internal uint Priority,Scheduling;}
    [StructLayout(LayoutKind.Sequential)]private struct IoCounters{internal ulong ReadOperations,WriteOperations,OtherOperations,ReadBytes,WriteBytes,OtherBytes;}
    [StructLayout(LayoutKind.Sequential)]private struct ExtendedLimits{internal BasicLimits Basic;internal IoCounters Io;internal nuint ProcessMemory,JobMemory,PeakProcessMemory,PeakJobMemory;}
    [StructLayout(LayoutKind.Sequential)]private struct CpuLimits{internal uint Flags,Rate;}
    internal static WorkerJob Attach(Process process)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("解析资源限制需要 Windows。");
        var job=CreateJobObject(IntPtr.Zero,null);
        try
        {
            if(job.IsInvalid)throw new IOException("worker_limits");
            var limits=new ExtendedLimits{Basic=new(){Flags=0x2000|0x100|0x8,ActiveProcesses=1},ProcessMemory=256u*1024u*1024u};
            Set(job,9,limits);Set(job,15,new CpuLimits{Flags=1|4,Rate=2500});
            if(!AssignProcessToJobObject(job,process.Handle))throw new IOException("worker_limits");
            process.PriorityClass=ProcessPriorityClass.BelowNormal;return job;
        }
        catch{job.Dispose();throw;}
    }
    private static void Set<T>(WorkerJob job,int kind,T value)where T:struct
    {
        var size=Marshal.SizeOf<T>();var pointer=Marshal.AllocHGlobal(size);
        try{Marshal.StructureToPtr(value,pointer,false);if(!SetInformationJobObject(job,kind,pointer,(uint)size))throw new IOException("worker_limits");}
        finally{Marshal.FreeHGlobal(pointer);}
    }
    protected override bool ReleaseHandle()=>CloseHandle(handle);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern WorkerJob CreateJobObject(IntPtr attributes,string? name);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetInformationJobObject(WorkerJob job,int kind,IntPtr info,uint length);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool AssignProcessToJobObject(WorkerJob job,IntPtr process);
    [DllImport("kernel32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool CloseHandle(IntPtr handle);
}
