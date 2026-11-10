// UnityWebBrowser (UWB)
// Copyright (c) 2021-2024 Voltstro-Studios
// 
// This project is under the MIT license. See the LICENSE.md file for more details.

#if UNITY_STANDALONE_WIN

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using VoltstroStudios.UnityWebBrowser.Logging;

namespace VoltstroStudios.UnityWebBrowser.Core.Engines.Process
{
    /// <summary>
    ///     Windows <see cref="IProcess" />.
    ///     <para>
    ///         Under IL2CPP <see cref="System.Diagnostics.Process" /> is not implemented, so the engine is launched
    ///         with CreateProcessW and supervised through the Win32 process APIs instead. Standard output and error
    ///         are redirected through anonymous pipes that managed code reads on background threads, so the log
    ///         handlers keep receiving the engine's JSON log lines exactly as they do on the managed path.
    ///     </para>
    ///     <para>
    ///         Both paths put the engine in a job object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, so tearing the job
    ///         down also takes the CEF sub-processes with it.
    ///     </para>
    /// </summary>
    internal sealed class WindowProcess : IProcess
    {
        /// <summary>
        ///     CREATE_NO_WINDOW. Equivalent to <see cref="ProcessStartInfo.CreateNoWindow" />.
        /// </summary>
        private const uint CreateNoWindow = 0x08000000;

        /// <summary>
        ///     CREATE_UNICODE_ENVIRONMENT. Tells CreateProcessW that the environment block is UTF-16.
        /// </summary>
        private const uint CreateUnicodeEnvironment = 0x00000400;

        private const int StartfUseStdHandles = 0x00000100;

        private const uint HandleFlagInherit = 0x00000001;

        /// <summary>
        ///     Generic read access, for the NUL device that stands in as the engine's stdin.
        /// </summary>
        private const uint GenericRead = 0x80000000;

        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;

        private const uint OpenExisting = 3;

        private static readonly IntPtr InvalidHandleValue = new(-1);

        private const uint WaitObject0 = 0x00000000;

        /// <summary>
        ///     How long the engine is given to exit after the job is terminated before we stop waiting for it.
        /// </summary>
        private const uint TerminateWaitMilliseconds = 5000;

        private readonly IntPtr jobHandle;

#if ENABLE_IL2CPP
        private readonly IWebBrowserLogger logger;
        private readonly object stateLock = new();

        private IntPtr processHandle;
        private int processId;
        private bool exited;
        private bool disposed;
        private int exitCode;
#else
        private readonly System.Diagnostics.Process process;
#endif

        public WindowProcess(IWebBrowserLogger logger)
        {
#if ENABLE_IL2CPP
            this.logger = logger;
#endif

            //Job handle code from SO
            //https://stackoverflow.com/questions/3342941/kill-child-process-when-parent-process-is-killed
            string jobName = "UWBChildProcesses" + Guid.NewGuid().ToString("N");
            jobHandle = CreateJobObject(IntPtr.Zero, jobName);
            
            JOBOBJECT_BASIC_LIMIT_INFORMATION info = new()
            {
                // This is the key flag. When our process is killed, Windows will automatically
                //  close the job handle, and when that happens, we want the child processes to
                //  be killed, too.
                LimitFlags = JOBOBJECTLIMIT.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            };

            JOBOBJECT_EXTENDED_LIMIT_INFORMATION extendedInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = info
                };

            int length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            IntPtr extendedInfoPtr = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(extendedInfo, extendedInfoPtr, false);

                if (!SetInformationJobObject(jobHandle, JobObjectInfoType.ExtendedLimitInformation,
                        extendedInfoPtr, (uint)length))
                {
                    throw new Win32Exception();
                }
            }
            finally
            {
                Marshal.FreeHGlobal(extendedInfoPtr);
            }

#if !ENABLE_IL2CPP
            process = new System.Diagnostics.Process();
#endif
        }

#if ENABLE_IL2CPP
        #region Native (IL2CPP)

        public bool HasExited
        {
            get
            {
                lock (stateLock)
                {
                    if (processHandle == IntPtr.Zero || exited)
                        return true;

                    if (WaitForSingleObject(processHandle, 0) != WaitObject0)
                        return false;

                    GetExitCodeProcess(processHandle, out exitCode);
                    exited = true;
                    return true;
                }
            }
        }

        public int ExitCode
        {
            get
            {
                lock (stateLock)
                {
                    //WebBrowserClient only reads this after HasExited, but it is cheap to answer correctly anyway.
                    if (!exited && processHandle != IntPtr.Zero &&
                        WaitForSingleObject(processHandle, 0) == WaitObject0)
                    {
                        GetExitCodeProcess(processHandle, out exitCode);
                        exited = true;
                    }

                    return exitCode;
                }
            }
        }

        public void StartProcess(string executable, string workingDir, string arguments,
            Action<string> onLogLine, Action<string> onErrorLine)
        {
            lock (stateLock)
            {
                if (disposed)
                    throw new ObjectDisposedException(nameof(WindowProcess));

                if (processHandle != IntPtr.Zero)
                    throw new InvalidOperationException("The engine process has already been started!");

                IntPtr stdoutRead = IntPtr.Zero;
                IntPtr stdoutWrite = IntPtr.Zero;
                IntPtr stderrRead = IntPtr.Zero;
                IntPtr stderrWrite = IntPtr.Zero;
                IntPtr stdInput = IntPtr.Zero;
                IntPtr environment = IntPtr.Zero;

                try
                {
                    CreatePipe(out stdoutRead, out stdoutWrite);
                    CreatePipe(out stderrRead, out stderrWrite);

                    //Unity players and the editor may run without a usable stdin. STARTF_USESTDHANDLES requires
                    //a real handle, so the engine gets the NUL device; it never reads stdin anyway.
                    stdInput = CreateNullInputHandle();

                    environment = BuildEnvironmentBlock();

                    STARTUPINFO startupInfo = new()
                    {
                        cb = Marshal.SizeOf<STARTUPINFO>(),
                        dwFlags = StartfUseStdHandles,
                        hStdInput = stdInput,
                        hStdOutput = stdoutWrite,
                        hStdError = stderrWrite
                    };

                    //The executable is quoted for the child's argv[0]; the arguments are already in the format that
                    //ProcessStartInfo.Arguments produces, so they are passed through untouched.
                    StringBuilder commandLine = new($"{QuoteExecutable(executable)} {arguments}");

                    if (!CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, true,
                            CreateNoWindow | CreateUnicodeEnvironment, environment, workingDir, ref startupInfo,
                            out PROCESS_INFORMATION processInformation))
                        throw new Win32Exception(Marshal.GetLastWin32Error(),
                            $"Failed to start the engine process '{executable}'.");

                    processHandle = processInformation.hProcess;
                    processId = processInformation.dwProcessId;
                    CloseHandle(processInformation.hThread);

                    logger.Debug($"Spawned engine process with pid {processId}.");

                    //Assign this process to the job handle before it spawns any CEF sub-process, so that they are
                    //all part of the tree we tear down.
                    bool success = AssignProcessToJobObject(jobHandle, processHandle);
                    if (!success && !HasExited)
                        throw new Win32Exception();

                    //From here on the read ends belong to the pumps. The handles are copied into locals because the
                    //finally block zeroes the originals: capturing those directly would make the pumps read from a
                    //null handle as soon as it runs.
                    IntPtr stdoutReadHandle = stdoutRead;
                    IntPtr stderrReadHandle = stderrRead;
                    stdoutRead = IntPtr.Zero;
                    stderrRead = IntPtr.Zero;

                    ProcessOutputPump.Start($"UWB engine stdout ({processId})",
                        (buffer, count) => ReadPipe(stdoutReadHandle, buffer, count), onLogLine);
                    ProcessOutputPump.Start($"UWB engine stderr ({processId})",
                        (buffer, count) => ReadPipe(stderrReadHandle, buffer, count), onErrorLine);
                }
                finally
                {
                    //The child holds its own copies of the write ends; keeping ours open would hide EOF from us.
                    CloseHandleIfValid(stdoutWrite);
                    CloseHandleIfValid(stderrWrite);
                    CloseHandleIfValid(stdoutRead);
                    CloseHandleIfValid(stderrRead);
                    CloseHandleIfValid(stdInput);

                    if (environment != IntPtr.Zero)
                        Marshal.FreeHGlobal(environment);
                }
            }
        }

        public void KillProcess()
        {
            lock (stateLock)
            {
                if (processHandle == IntPtr.Zero || disposed)
                    return;

                //The job owns the engine and every CEF child it spawned.
                if (!TerminateJobObject(jobHandle, 0))
                    logger.Warn(
                        $"Failed to terminate the engine job object (Win32 error {Marshal.GetLastWin32Error()}).");

                WaitForSingleObject(processHandle, TerminateWaitMilliseconds);
            }
        }

        public void Dispose()
        {
            lock (stateLock)
            {
                if (disposed)
                    return;

                disposed = true;

                if (processHandle == IntPtr.Zero)
                    return;

                if (WaitForSingleObject(processHandle, 0) != WaitObject0)
                    TerminateJobObject(jobHandle, 0);

                CloseHandle(processHandle);
                processHandle = IntPtr.Zero;
                exited = true;

                //Closing the job handle is what guarantees that nothing survives us.
                CloseHandleIfValid(jobHandle);
            }
        }

        private static int ReadPipe(IntPtr pipe, byte[] buffer, int count)
        {
            if (!ReadFile(pipe, buffer, (uint)count, out uint read, IntPtr.Zero))
                return 0;

            return (int)read;
        }

        private static void CreatePipe(out IntPtr read, out IntPtr write)
        {
            SECURITY_ATTRIBUTES attributes = new()
            {
                nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                //Only the write end is shared with the child; see SetHandleInformation below.
                bInheritHandle = 1
            };

            if (!CreatePipeNative(out read, out write, ref attributes, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Failed to create a pipe for the engine process output.");

            //The child must not inherit the read end, otherwise its own copy keeps the pipe alive after it dies.
            if (!SetHandleInformation(read, HandleFlagInherit, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Failed to make the engine process output pipe non-inheritable.");
        }

        /// <summary>
        ///     Opens the NUL device as an inheritable handle to use as the engine's stdin.
        /// </summary>
        private static IntPtr CreateNullInputHandle()
        {
            SECURITY_ATTRIBUTES attributes = new()
            {
                nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                bInheritHandle = 1
            };

            IntPtr handle = CreateFileW("NUL", GenericRead, FileShareRead | FileShareWrite, ref attributes,
                OpenExisting, 0, IntPtr.Zero);

            if (handle == InvalidHandleValue)
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Failed to open the NUL device for the engine process stdin.");

            return handle;
        }

        /// <summary>
        ///     Builds the UTF-16 environment block CreateProcessW expects: sorted by name, '=' separated, and
        ///     terminated by an extra NUL.
        /// </summary>
        private static IntPtr BuildEnvironmentBlock()
        {
            SortedDictionary<string, string> variables = new(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
                variables[(string)entry.Key] = entry.Value?.ToString() ?? string.Empty;

            StringBuilder block = new();
            foreach (KeyValuePair<string, string> variable in variables)
                block.Append(variable.Key).Append('=').Append(variable.Value).Append('\0');

            block.Append('\0');
            return Marshal.StringToHGlobalUni(block.ToString());
        }

        /// <summary>
        ///     Quotes an executable path following the CommandLineToArgvW rules, which are the same rules
        ///     <see cref="ProcessStartInfo.Arguments" /> follows on Windows.
        /// </summary>
        private static string QuoteExecutable(string executable)
        {
            if (executable.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
                return executable;

            StringBuilder quoted = new("\"");
            int backslashes = 0;

            foreach (char character in executable)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (character == '"')
                {
                    quoted.Append('\\', (backslashes * 2) + 1);
                    quoted.Append('"');
                }
                else
                {
                    quoted.Append('\\', backslashes);
                    quoted.Append(character);
                }

                backslashes = 0;
            }

            quoted.Append('\\', backslashes * 2);
            quoted.Append('"');
            return quoted.ToString();
        }

        private static void CloseHandleIfValid(IntPtr handle)
        {
            if (handle != IntPtr.Zero)
                CloseHandle(handle);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            public int bInheritHandle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessW(string lpApplicationName, StringBuilder lpCommandLine,
            IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags,
            IntPtr lpEnvironment, string lpCurrentDirectory, ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            ref SECURITY_ATTRIBUTES lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", EntryPoint = "CreatePipe", SetLastError = true)]
        private static extern bool CreatePipeNative(out IntPtr hReadPipe, out IntPtr hWritePipe,
            ref SECURITY_ATTRIBUTES lpPipeAttributes, uint nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead,
            out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr hProcess, out int lpExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        #endregion
#else
        #region Managed

        public void StartProcess(string executable, string workingDir, string arguments, Action<string> onLogLine,
            Action<string> onErrorLine)
        {
            ProcessStartInfo startInfo = new(executable, arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDir
            };

            process.StartInfo = startInfo;
            process.OutputDataReceived += (sender, e) => onLogLine?.Invoke(e.Data);
            process.ErrorDataReceived += (sender, e) => onErrorLine?.Invoke(e.Data);
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            
            //Assign this process to the job handle
            bool success = AssignProcessToJobObject(jobHandle, process.Handle);
            if (!success && !process.HasExited)
                throw new Win32Exception();
        }

        public void KillProcess()
        {
            TerminateJobObject(jobHandle, 0);
        }

        public bool HasExited => process.HasExited;
        public int ExitCode => process.ExitCode;

        public void Dispose()
        {
            process.Dispose();
        }

        #endregion
#endif

        #region WIN32 Native
        
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string name);

        [DllImport("kernel32.dll")]
        private static extern bool SetInformationJobObject(IntPtr job, JobObjectInfoType infoType,
            IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

        public enum JobObjectInfoType
        {
            AssociateCompletionPortInformation = 7,
            BasicLimitInformation = 2,
            BasicUIRestrictions = 4,
            EndOfJobTimeInformation = 6,
            ExtendedLimitInformation = 9,
            SecurityLimitInformation = 5,
            GroupInformation = 11
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public Int64 PerProcessUserTimeLimit;
            public Int64 PerJobUserTimeLimit;
            public JOBOBJECTLIMIT LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public UInt32 ActiveProcessLimit;
            public Int64 Affinity;
            public UInt32 PriorityClass;
            public UInt32 SchedulingClass;
        }

        [Flags]
        public enum JOBOBJECTLIMIT : uint
        {
            JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IO_COUNTERS
        {
            public UInt64 ReadOperationCount;
            public UInt64 WriteOperationCount;
            public UInt64 OtherOperationCount;
            public UInt64 ReadTransferCount;
            public UInt64 WriteTransferCount;
            public UInt64 OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        #endregion
    }
}

#endif
