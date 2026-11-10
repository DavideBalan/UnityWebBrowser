// UnityWebBrowser (UWB)
// Copyright (c) 2021-2024 Voltstro-Studios
//
// This project is under the MIT license. See the LICENSE.md file for more details.

#if UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX

using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace VoltstroStudios.UnityWebBrowser.Core.Engines.Process
{
    /// <summary>
    ///     POSIX plumbing shared by the native <see cref="IProcess" /> implementations.
    ///     <para>
    ///         These exist because IL2CPP does not implement <see cref="System.Diagnostics.Process" />, so the engine
    ///         has to be spawned against libc directly. Everything here is platform independent POSIX; the pieces
    ///         that are not (dlopen names, spawn flags, process groups) live in the platform classes.
    ///     </para>
    /// </summary>
    internal static class PosixProcessNative
    {
#if UNITY_STANDALONE_OSX
        /// <summary>
        ///     macOS has no libc.so; the C library is part of the dyld shared cache and reached through libSystem.
        /// </summary>
        internal const string Libc = "/usr/lib/libSystem.B.dylib";
#else
        /// <summary>
        ///     The SONAME rather than "libc". IL2CPP resolves DllImport names with dlopen, and dlopen("libc.so")
        ///     only succeeds where the glibc development package is installed - "libc.so" is a linker script, not a
        ///     shared object. "libc.so.6" is present on every glibc system.
        /// </summary>
        internal const string Libc = "libc.so.6";
#endif

        /// <summary>
        ///     Splits a command line built for <see cref="System.Diagnostics.ProcessStartInfo.Arguments" /> into an
        ///     argv list, following the same double-quote and backslash rules the .NET runtime applies on Unix.
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

        internal static List<string> GetEnvironment()
        {
            List<string> environment = new();
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
                environment.Add($"{entry.Key}={entry.Value}");

            return environment;
        }

        internal static IntPtr AllocUtf8(string value)
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
        internal static IntPtr[] AllocUtf8Array(IReadOnlyList<string> values)
        {
            IntPtr[] pointers = new IntPtr[values.Count + 1];
            for (int i = 0; i < values.Count; i++)
                pointers[i] = AllocUtf8(values[i]);

            pointers[values.Count] = IntPtr.Zero;
            return pointers;
        }

        internal static void FreeUtf8Array(IntPtr[] pointers)
        {
            foreach (IntPtr pointer in pointers)
            {
                if (pointer != IntPtr.Zero)
                    Marshal.FreeHGlobal(pointer);
            }
        }
    }
}

#endif
