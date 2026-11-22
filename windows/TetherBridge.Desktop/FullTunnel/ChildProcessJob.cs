using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TetherBridge.Desktop.FullTunnel;

// A process placed in this job is terminated when the final job handle closes.
public sealed class ChildProcessJob : IDisposable
{
    private readonly SafeFileHandle handle;
    public ChildProcessJob()
    {
        handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var info = new ExtendedLimitInformation();
        info.BasicLimitInformation.LimitFlags = 0x00002000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        var size = Marshal.SizeOf<ExtendedLimitInformation>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(handle, 9, ptr, (uint)size)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch { handle.Dispose(); throw; }
        finally { Marshal.FreeHGlobal(ptr); }
    }
    public void Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!AssignProcessToJobObject(handle, process.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public Process StartSuspendedThenResume(string executable, IReadOnlyList<string> arguments, string workingDirectory, bool restrictedEnvironment = false)
    {
        if (!Path.IsPathFullyQualified(executable) || !Path.IsPathFullyQualified(workingDirectory)) throw new ArgumentException("Absolute paths are required.");
        static string Quote(string value)
        {
            if (value.Contains('"') || value.Contains('\0')) throw new ArgumentException("Unsupported process argument.");
            var trailing = value.Length - value.TrimEnd('\\').Length;
            return "\"" + value + new string('\\', trailing) + "\"";
        }
        var command = new StringBuilder(Quote(executable));
        foreach (var arg in arguments) command.Append(' ').Append(Quote(arg));
        var startup = new StartupInfoEx { StartupInfo = new StartupInfo { Size = (uint)Marshal.SizeOf<StartupInfoEx>() } };
        var environment = IntPtr.Zero; var attributes = IntPtr.Zero; var jobList = IntPtr.Zero;
        var initialized = false; var handleRef = false;
        ProcessInformation info;
        try
        {
            if (restrictedEnvironment)
            {
                var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                var block = $"PATH={Environment.SystemDirectory}\0SystemRoot={windows}\0WINDIR={windows}\0\0";
                environment = Marshal.StringToHGlobalUni(block);
            }
            nuint size = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            if (size == 0 || size > 1024 * 1024) throw new Win32Exception(Marshal.GetLastWin32Error());
            attributes = Marshal.AllocHGlobal(checked((int)size));
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
            initialized = true;
            handle.DangerousAddRef(ref handleRef);
            jobList = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(jobList, handle.DangerousGetHandle());
            const int ProcThreadAttributeJobList = 0x0002000d;
            if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)ProcThreadAttributeJobList, jobList, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            startup.AttributeList = attributes;
            var flags = 0x00000004u | 0x08000000u | 0x00080000u | (restrictedEnvironment ? 0x00000400u : 0u);
            if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false, flags, environment, workingDirectory, ref startup, out info))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            if (initialized) DeleteProcThreadAttributeList(attributes);
            if (jobList != IntPtr.Zero) Marshal.FreeHGlobal(jobList);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            if (handleRef) handle.DangerousRelease();
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
        }
        using var nativeProcess = new SafeFileHandle(info.Process, ownsHandle: true);
        using var nativeThread = new SafeFileHandle(info.Thread, ownsHandle: true);
        Process? process = null;
        try
        {
            process = Process.GetProcessById(checked((int)info.ProcessId));
            _ = process.Handle; // Retain an independent handle even if the child exits immediately.
            if (ResumeThread(info.Thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
            return process;
        }
        catch
        {
            TerminateProcess(info.Process, 1);
            process?.Dispose();
            throw;
        }
    }

    public void Dispose() => handle.Dispose();

    [StructLayout(LayoutKind.Sequential)] private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public uint Size; public IntPtr Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, Reserved2; public IntPtr Reserved2Pointer, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment, string workingDirectory, ref StartupInfoEx startup, out ProcessInformation information);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr attributes, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr attributes, uint flags, IntPtr attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returnSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr attributes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, IntPtr information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
