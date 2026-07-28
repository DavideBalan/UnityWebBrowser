// UnityWebBrowser (UWB)
// Copyright (c) 2021-2024 Voltstro-Studios
//
// This project is under the MIT license. See the LICENSE.md file for more details.

#if UNITY_STANDALONE_LINUX

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using VoltstroStudios.UnityWebBrowser.Logging;

namespace VoltstroStudios.UnityWebBrowser.Core.Engines.Process
{
    /// <summary>
    ///     Linux <see cref="IProcess" /> implemented directly against libc.
    ///     <para>
    ///         Unity's IL2CPP backend does not support <see cref="System.Diagnostics.Process" />, so the engine is
    ///         launched with posix_spawn and supervised with waitpid/kill instead. The child is placed in its own
    ///         session (POSIX_SPAWN_SETSID) so that killing the negated pid takes the CEF sub-processes down with it,
    ///         which removes the need for the pgrep/kill recursion the managed implementation used.
    ///     </para>
    ///     <para>
    ///         Standard output and error are deliberately not redirected: reading pipe file descriptors back into
    ///         managed code is the part of the API IL2CPP is least reliable at, and the engine already writes its own
    ///         log file via the -log-path argument. The log event handlers passed to
    ///         <see cref="StartProcess" /> are therefore never invoked.
    ///     </para>
    /// </summary>
    internal sealed class LinuxProcess : IProcess
    {
        /// <summary>
        ///     The SONAME rather than "libc". IL2CPP resolves DllImport names with dlopen, and dlopen("libc.so")
        ///     only succeeds where the glibc development package is installed - "libc.so" is a linker script, not a
        ///     shared object. "libc.so.6" is present on every glibc system.
        /// </summary>
        private const string Libc = "libc.so.6";

        private const int SignalTerminate = 15;
        private const int SignalKill = 9;

        private const int WaitNoHang = 1;

        /// <summary>
        ///     0755 - rwxr-xr-x.
        /// </summary>
        private const int ExecutableMode = 493;

        /// <summary>
        ///     glibc's POSIX_SPAWN_SETSID. Makes the child a session (and process group) leader.
        /// </summary>
        private const short SpawnSetSid = 0x80;

        /// <summary>
        ///     posix_spawn_file_actions_t and posix_spawnattr_t are opaque. glibc's are 80 and 336 bytes
        ///     respectively; over-allocating costs nothing and protects us against layout changes.
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

        public LinuxProcess(IWebBrowserLogger logger)
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
            DataReceivedEventHandler onLogEvent, DataReceivedEventHandler onErrorLogEvent)
        {
            lock (stateLock)
            {
                if (processId != 0)
                    throw new InvalidOperationException("The engine process has already been started!");

                List<string> argv = new() { executable };
                argv.AddRange(ParseArguments(arguments));

                IntPtr fileActions = Marshal.AllocHGlobal(OpaqueStructSize);
                IntPtr spawnAttributes = Marshal.AllocHGlobal(OpaqueStructSize);
                IntPtr executablePath = AllocUtf8(executable);
                IntPtr workingDirectory = AllocUtf8(workingDir);
                IntPtr[] argumentPointers = AllocUtf8Array(argv);
                IntPtr[] environmentPointers = AllocUtf8Array(GetEnvironment());

                bool fileActionsInitialised = false;
                bool spawnAttributesInitialised = false;

                try
                {
                    // Builds produced on a Windows agent cross-compiling to Linux carry no permission bits, so the
                    // engine arrives without +x. Restore it rather than failing with EACCES.
                    if (chmod(executablePath, ExecutableMode) != 0)
                        logger.Debug($"Could not chmod '{executable}'; assuming it is already executable.");

                    ThrowOnError(posix_spawn_file_actions_init(fileActions), nameof(posix_spawn_file_actions_init));
                    fileActionsInitialised = true;

                    ThrowOnError(posix_spawnattr_init(spawnAttributes), nameof(posix_spawnattr_init));
                    spawnAttributesInitialised = true;

                    // The engine resolves its CEF resources relative to the working directory.
                    ThrowOnError(posix_spawn_file_actions_addchdir_np(fileActions, workingDirectory),
                        nameof(posix_spawn_file_actions_addchdir_np));
                    ThrowOnError(posix_spawnattr_setflags(spawnAttributes, SpawnSetSid),
                        nameof(posix_spawnattr_setflags));

                    ThrowOnError(
                        posix_spawn(out int spawnedId, executablePath, fileActions, spawnAttributes,
                            argumentPointers, environmentPointers), nameof(posix_spawn));

                    processId = spawnedId;
                    logger.Debug($"Spawned engine process with pid {processId}.");
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
                    FreeUtf8Array(argumentPointers);
                    FreeUtf8Array(environmentPointers);
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
                KillProcessCore();
            }
        }

        private void KillProcessCore()
        {
            Signal(SignalKill);
            Reap(0);
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

        /// <summary>
        ///     Splits a command line built for <see cref="ProcessStartInfo.Arguments" /> into an argv list, following
        ///     the same double-quote and backslash rules the .NET runtime applies on Unix.
        /// </summary>
        internal static List<string> ParseArguments(string arguments)
        {
            List<string> parsed = new();
            if (string.IsNullOrWhiteSpace(arguments))
                return parsed;

            StringBuilder current = new();
            bool hasArgument = false;
            bool inQuotes = false;

            for (int i = 0; i < arguments.Length; i++)
            {
                char character = arguments[i];

                if (character == '\\')
                {
                    int backslashes = 0;
                    while (i < arguments.Length && arguments[i] == '\\')
                    {
                        backslashes++;
                        i++;
                    }

                    if (i < arguments.Length && arguments[i] == '"')
                    {
                        current.Append('\\', backslashes / 2);
                        if (backslashes % 2 == 0)
                        {
                            inQuotes = !inQuotes;
                        }
                        else
                        {
                            current.Append('"');
                        }

                        hasArgument = true;
                    }
                    else
                    {
                        current.Append('\\', backslashes);
                        if (backslashes > 0)
                            hasArgument = true;

                        i--;
                    }

                    continue;
                }

                if (character == '"')
                {
                    inQuotes = !inQuotes;
                    hasArgument = true;
                    continue;
                }

                if (!inQuotes && (character == ' ' || character == '\t'))
                {
                    if (hasArgument)
                    {
                        parsed.Add(current.ToString());
                        current.Clear();
                        hasArgument = false;
                    }

                    continue;
                }

                current.Append(character);
                hasArgument = true;
            }

            if (hasArgument)
                parsed.Add(current.ToString());

            return parsed;
        }

        private static List<string> GetEnvironment()
        {
            List<string> environment = new();
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
                environment.Add($"{entry.Key}={entry.Value}");

            return environment;
        }

        private static void ThrowOnError(int result, string call)
        {
            if (result != 0)
                throw new InvalidOperationException($"{call} failed with errno {result}.");
        }

        private static IntPtr AllocUtf8(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            IntPtr pointer = Marshal.AllocHGlobal(bytes.Length + 1);
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            Marshal.WriteByte(pointer, bytes.Length, 0);
            return pointer;
        }

        /// <summary>
        ///     Builds a NULL terminated char* array. Default string[] marshalling does not append the terminator
        ///     that execve requires.
        /// </summary>
        private static IntPtr[] AllocUtf8Array(IReadOnlyList<string> values)
        {
            IntPtr[] pointers = new IntPtr[values.Count + 1];
            for (int i = 0; i < values.Count; i++)
                pointers[i] = AllocUtf8(values[i]);

            pointers[values.Count] = IntPtr.Zero;
            return pointers;
        }

        private static void FreeUtf8Array(IntPtr[] pointers)
        {
            foreach (IntPtr pointer in pointers)
            {
                if (pointer != IntPtr.Zero)
                    Marshal.FreeHGlobal(pointer);
            }
        }

        [DllImport(Libc, SetLastError = true)]
        private static extern int posix_spawn(out int pid, IntPtr path, IntPtr fileActions, IntPtr attrp,
            IntPtr[] argv, IntPtr[] envp);

        [DllImport(Libc, SetLastError = true)]
        private static extern int posix_spawn_file_actions_init(IntPtr fileActions);

        [DllImport(Libc, SetLastError = true)]
        private static extern int posix_spawn_file_actions_destroy(IntPtr fileActions);

        /// <summary>
        ///     glibc 2.29 or newer. posix_spawn has no portable working directory option.
        /// </summary>
        [DllImport(Libc, SetLastError = true)]
        private static extern int posix_spawn_file_actions_addchdir_np(IntPtr fileActions, IntPtr path);

        [DllImport(Libc, SetLastError = true)]
        private static extern int posix_spawnattr_init(IntPtr attrp);

        [DllImport(Libc, SetLastError = true)]
        private static extern int posix_spawnattr_destroy(IntPtr attrp);

        [DllImport(Libc, SetLastError = true)]
        private static extern int posix_spawnattr_setflags(IntPtr attrp, short flags);

        [DllImport(Libc, SetLastError = true)]
        private static extern int waitpid(int pid, out int status, int options);

        [DllImport(Libc, SetLastError = true)]
        private static extern int kill(int pid, int signal);

        [DllImport(Libc, SetLastError = true)]
        private static extern int chmod(IntPtr path, int mode);
    }
}

#endif
