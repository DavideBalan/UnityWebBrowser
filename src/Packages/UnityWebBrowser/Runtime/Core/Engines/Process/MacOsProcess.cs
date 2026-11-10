// UnityWebBrowser (UWB)
// Copyright (c) 2021-2024 Voltstro-Studios
// 
// This project is under the MIT license.See the LICENSE.md file for more details.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using VoltstroStudios.UnityWebBrowser.Helper;
using VoltstroStudios.UnityWebBrowser.Logging;

#if UNITY_STANDALONE_OSX

namespace VoltstroStudios.UnityWebBrowser.Core.Engines.Process
{
#if ENABLE_IL2CPP
    /// <summary>
    ///     macOS <see cref="IProcess" /> implemented directly against libc.
    ///     <para>
    ///         IL2CPP does not implement <see cref="System.Diagnostics.Process" /> on macOS either, so the engine is
    ///         launched with posix_spawn and supervised with waitpid/kill instead. The child is placed in its own
    ///         process group (POSIX_SPAWN_SETPGROUP), so signalling the negated pid takes the CEF sub-processes down
    ///         with it.
    ///     </para>
    ///     <para>
    ///         Unlike <c>LinuxProcess</c>, stdout/stderr are redirected: IL2CPP handles blocking reads on a
    ///         background thread fine, and dropping them would silently disable
    ///         <see cref="Logging.ProcessLogHandler" /> on macOS, which the managed path keeps alive.
    ///     </para>
    /// </summary>
    internal sealed class MacOsProcess : IProcess
    {
        private const int SignalTerminate = 15;
        private const int SignalKill = 9;

        private const int WaitNoHang = 1;

        /// <summary>
        ///     EINTR, raised by read() when a signal interrupts a blocking call.
        /// </summary>
        private const int InterruptedSystemCall = 4;

        /// <summary>
        ///     POSIX_SPAWN_SETPGROUP. Makes the child the leader of a new process group.
        /// </summary>
        private const short SpawnSetProcessGroup = 0x02;

        private const int StdoutFileDescriptor = 1;
        private const int StderrFileDescriptor = 2;

        /// <summary>
        ///     posix_spawn_file_actions_t and posix_spawnattr_t are opaque. Over-allocating costs nothing and protects
        ///     us against layout changes.
        /// </summary>
        private const int OpaqueStructSize = 1024;

        /// <summary>
        ///     How long the engine is given to react to SIGTERM before SIGKILL is sent.
        /// </summary>
        private static readonly TimeSpan TerminateGracePeriod = TimeSpan.FromSeconds(5);

        private readonly IWebBrowserLogger logger;
        private readonly object stateLock = new();

        private int processId;
        private bool exited;
        private int exitCode;

        public MacOsProcess(IWebBrowserLogger logger)
        {
            this.logger = logger;
        }

        public bool HasExited
        {
            get
            {
                lock (stateLock)
                {
                    return processId == 0 || exited || Reap(WaitNoHang);
                }
            }
        }

        public int ExitCode
        {
            get
            {
                lock (stateLock)
                {
                    Reap(WaitNoHang);
                    return exitCode;
                }
            }
        }

        public void StartProcess(string executable, string workingDir, string arguments,
            Action<string> onLogLine, Action<string> onErrorLine)
        {
            lock (stateLock)
            {
                if (processId != 0)
                    throw new InvalidOperationException("The engine process has already been started!");

                List<string> argv = new() { executable };
                argv.AddRange(PosixProcessNative.ParseArguments(arguments));

                IntPtr fileActions = Marshal.AllocHGlobal(OpaqueStructSize);
                IntPtr spawnAttributes = Marshal.AllocHGlobal(OpaqueStructSize);
                IntPtr executablePath = PosixProcessNative.AllocUtf8(executable);
                IntPtr workingDirectory = PosixProcessNative.AllocUtf8(workingDir);
                IntPtr[] argumentPointers = PosixProcessNative.AllocUtf8Array(argv);
                IntPtr[] environmentPointers = PosixProcessNative.AllocUtf8Array(PosixProcessNative.GetEnvironment());

                // pipe() fills these in; -1 means "nothing to close".
                int[] stdoutPipe = { -1, -1 };
                int[] stderrPipe = { -1, -1 };

                bool fileActionsInitialised = false;
                bool spawnAttributesInitialised = false;

                try
                {
                    ThrowOnError(pipe(stdoutPipe), nameof(pipe));
                    ThrowOnError(pipe(stderrPipe), nameof(pipe));

                    ThrowOnError(posix_spawn_file_actions_init(fileActions), nameof(posix_spawn_file_actions_init));
                    fileActionsInitialised = true;

                    ThrowOnError(posix_spawnattr_init(spawnAttributes), nameof(posix_spawnattr_init));
                    spawnAttributesInitialised = true;

                    // The engine resolves its CEF resources relative to the working directory. There is no portable
                    // posix_spawn option for this; the _np variant needs macOS 10.15, which is also the oldest macOS
                    // that current CEF builds support.
                    ThrowOnError(posix_spawn_file_actions_addchdir_np(fileActions, workingDirectory),
                        nameof(posix_spawn_file_actions_addchdir_np));

                    // Give the child our pipe ends. Every descriptor is then closed in the child: the write ends
                    // after being duplicated onto fds 1 and 2, and the read ends because they belong to us. Without
                    // closing the parent's write end the child would also keep it open, and we would never see EOF.
                    ThrowOnError(posix_spawn_file_actions_adddup2(fileActions, stdoutPipe[1], StdoutFileDescriptor),
                        nameof(posix_spawn_file_actions_adddup2));
                    ThrowOnError(posix_spawn_file_actions_adddup2(fileActions, stderrPipe[1], StderrFileDescriptor),
                        nameof(posix_spawn_file_actions_adddup2));
                    ThrowOnError(posix_spawn_file_actions_addclose(fileActions, stdoutPipe[0]),
                        nameof(posix_spawn_file_actions_addclose));
                    ThrowOnError(posix_spawn_file_actions_addclose(fileActions, stderrPipe[0]),
                        nameof(posix_spawn_file_actions_addclose));
                    ThrowOnError(posix_spawn_file_actions_addclose(fileActions, stdoutPipe[1]),
                        nameof(posix_spawn_file_actions_addclose));
                    ThrowOnError(posix_spawn_file_actions_addclose(fileActions, stderrPipe[1]),
                        nameof(posix_spawn_file_actions_addclose));

                    ThrowOnError(posix_spawnattr_setpgroup(spawnAttributes, 0), nameof(posix_spawnattr_setpgroup));
                    ThrowOnError(posix_spawnattr_setflags(spawnAttributes, SpawnSetProcessGroup),
                        nameof(posix_spawnattr_setflags));

                    ThrowOnError(
                        posix_spawn(out int spawnedId, executablePath, fileActions, spawnAttributes,
                            argumentPointers, environmentPointers), nameof(posix_spawn));

                    processId = spawnedId;
                    logger.Debug($"Spawned engine process with pid {processId}.");

                    // The child owns the write ends now. Holding them here would keep the pipes open past its death.
                    close(stdoutPipe[1]);
                    close(stderrPipe[1]);

                    int stdoutRead = stdoutPipe[0];
                    int stderrRead = stderrPipe[0];

                    // Handed over to the readers; the finally block must not close them.
                    stdoutPipe[0] = -1;
                    stdoutPipe[1] = -1;
                    stderrPipe[0] = -1;
                    stderrPipe[1] = -1;

                    ProcessOutputPump.Start($"UWB engine stdout ({processId})",
                        (buffer, count) => ReadFileDescriptor(stdoutRead, buffer, count), onLogLine,
                        () => close(stdoutRead));
                    ProcessOutputPump.Start($"UWB engine stderr ({processId})",
                        (buffer, count) => ReadFileDescriptor(stderrRead, buffer, count), onErrorLine,
                        () => close(stderrRead));
                }
                finally
                {
                    // Destroying an uninitialised struct is undefined behaviour, so only undo what succeeded.
                    if (fileActionsInitialised)
                        posix_spawn_file_actions_destroy(fileActions);

                    if (spawnAttributesInitialised)
                        posix_spawnattr_destroy(spawnAttributes);

                    Marshal.FreeHGlobal(fileActions);
                    Marshal.FreeHGlobal(spawnAttributes);
                    Marshal.FreeHGlobal(executablePath);
                    Marshal.FreeHGlobal(workingDirectory);
                    PosixProcessNative.FreeUtf8Array(argumentPointers);
                    PosixProcessNative.FreeUtf8Array(environmentPointers);

                    CloseDescriptor(stdoutPipe[0]);
                    CloseDescriptor(stdoutPipe[1]);
                    CloseDescriptor(stderrPipe[0]);
                    CloseDescriptor(stderrPipe[1]);
                }
            }
        }

        public void KillProcess()
        {
            lock (stateLock)
            {
                if (processId == 0 || exited || Reap(WaitNoHang))
                    return;

                Signal(SignalTerminate);

                Stopwatch stopwatch = Stopwatch.StartNew();
                while (stopwatch.Elapsed < TerminateGracePeriod)
                {
                    if (Reap(WaitNoHang))
                        return;

                    Thread.Sleep(25);
                }

                logger.Warn(
                    $"Engine process {processId} did not exit within {TerminateGracePeriod.TotalSeconds} seconds of SIGTERM, sending SIGKILL.");
                Signal(SignalKill);
                Reap(0);
            }
        }

        public void Dispose()
        {
            lock (stateLock)
            {
                if (processId == 0 || exited)
                    return;

                // Never leave the engine (or a zombie) behind.
                Signal(SignalKill);
                Reap(0);
            }
        }

        /// <summary>
        ///     Sends <paramref name="signal" /> to the whole process group, so the CEF sub-processes go with it.
        /// </summary>
        private void Signal(int signal)
        {
            if (kill(-processId, signal) == 0)
                return;

            // The group may already be gone; fall back to the process itself.
            if (kill(processId, signal) != 0)
                logger.Warn($"Failed to send signal {signal} to engine process {processId}.");
        }

        /// <summary>
        ///     Collects the child's exit status, preventing a zombie. IL2CPP has no runtime child reaper, so this is
        ///     the only thing that will ever do it.
        /// </summary>
        /// <param name="options">WNOHANG to poll, 0 to block until the child exits.</param>
        /// <returns>True once the child has been reaped.</returns>
        private bool Reap(int options)
        {
            if (exited)
                return true;

            if (processId == 0)
                return false;

            int result = waitpid(processId, out int status, options);
            if (result == 0)
                return false;

            if (result < 0)
            {
                // ECHILD - something else already reaped it, or it was never ours. Either way it is gone.
                exited = true;
                exitCode = -1;
                return true;
            }

            exited = true;
            exitCode = GetExitCode(status);
            return true;
        }

        /// <summary>
        ///     Decodes a wait status the same way WEXITSTATUS/WTERMSIG do. Signalled children are reported as
        ///     128 + signal, matching the shell convention.
        /// </summary>
        private static int GetExitCode(int status)
        {
            int terminationSignal = status & 0x7F;
            if (terminationSignal == 0)
                return (status >> 8) & 0xFF;

            return 128 + terminationSignal;
        }

        private static int ReadFileDescriptor(int descriptor, byte[] buffer, int count)
        {
            while (true)
            {
                long result = read(descriptor, buffer, (IntPtr)count).ToInt64();
                if (result > 0)
                    return (int)result;

                if (result == 0)
                    return 0;

                // A signal interrupted the read; it is not the end of the stream.
                if (Marshal.GetLastWin32Error() != InterruptedSystemCall)
                    return 0;
            }
        }

        private static void CloseDescriptor(int descriptor)
        {
            if (descriptor >= 0)
                close(descriptor);
        }

        private static void ThrowOnError(int result, string call)
        {
            if (result != 0)
                throw new InvalidOperationException($"{call} failed with errno {result}.");
        }

        /// <summary>
        ///     posix_spawn gives the exit status directly (0 or an errno), unlike fork/exec where the errno comes from
        ///     the child. Negative returns are not expected, but are still treated as errors.
        /// </summary>
        [DllImport(PosixProcessNative.Libc, SetLastError = true)]
        private static extern int posix_spawn(out int pid, IntPtr path, IntPtr fileActions, IntPtr attrp,
            IntPtr[] argv, IntPtr[] envp);

        [DllImport(PosixProcessNative.Libc, SetLastError = true)]
        private static extern int posix_spawn_file_actions_init(IntPtr fileActions);

        [DllImport(PosixProcessNative.Libc, SetLastError = true)]
        private static extern int posix_spawn_file_actions_destroy(IntPtr fileActions);

        [DllImport(PosixProcessNative.Libc, SetLastError = true)]
        private static extern int posix_spawn_file_actions_addchdir_np(IntPtr fileActions, IntPtr path);

        [DllImport(PosixProcessNative.Libc, SetLastError = true)]
        private static extern int posix_spawn_file_actions_adddup2(IntPtr fileActions, int fileDescriptor,
            int newFileDescriptor);

        [DllImport(PosixProcessNative.Libc, SetLastError = true)]
        private static extern int posix_spawn_file_actions_addclose(IntPtr fileActions, int fileDescriptor);

        [DllImport(PosixProcessNative.Libc, SetLastError = true)]
        private static extern int posix_spawnattr_init(IntPtr attrp);

        [DllImport(PosixProcessNative.Libc, SetLastError = true)]
        private static extern int posix_spawnattr_destroy(IntPtr attrp);

        [DllImport(PosixProcessNative.Libc, SetLastError = true)]
        private static extern int posix_spawnattr_setflags(IntPtr attrp, short flags);

        [DllImport(PosixProcessNative.Libc, SetLastError = true)]
        private static extern int posix_spawnattr_setpgroup(IntPtr attrp, int processGroup);

        [DllImport(PosixProcessNative.Libc, SetLastError = true)]
        private static extern int pipe(int[] fileDescriptors);

        [DllImport(PosixProcessNative.Libc, SetLastError = true)]
        private static extern IntPtr read(int fileDescriptor, byte[] buffer, IntPtr count);

        [DllImport(PosixProcessNative.Libc, SetLastError = true)]
        private static extern int close(int fileDescriptor);

        [DllImport(PosixProcessNative.Libc, SetLastError = true)]
        private static extern int waitpid(int pid, out int status, int options);

        [DllImport(PosixProcessNative.Libc, SetLastError = true)]
        private static extern int kill(int pid, int signal);
    }
#else
    internal sealed class MacOsProcess : IProcess
    {
        private readonly System.Diagnostics.Process process;
        
        public MacOsProcess(IWebBrowserLogger logger)
        {
            process = new System.Diagnostics.Process();
        }

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
        }

        public void KillProcess()
        {
            process.KillTree();
        }

        public bool HasExited => process.HasExited;
        public int ExitCode => process.ExitCode;
        
        public void Dispose()
        {
            process.Dispose();
        }
    }
#endif
}

#endif
