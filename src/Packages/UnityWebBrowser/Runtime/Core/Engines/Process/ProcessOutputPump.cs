// UnityWebBrowser (UWB)
// Copyright (c) 2021-2024 Voltstro-Studios
//
// This project is under the MIT license. See the LICENSE.md file for more details.

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace VoltstroStudios.UnityWebBrowser.Core.Engines.Process
{
    /// <summary>
    ///     Pumps a process output stream on a background thread and hands complete lines to a callback.
    ///     <para>
    ///         The native <see cref="IProcess" /> implementations need this because they launch the engine against
    ///         the OS directly: <see cref="System.Diagnostics.Process.BeginOutputReadLine" /> does not exist for them,
    ///         and the whole point is that <see cref="System.Diagnostics.Process" /> is unavailable under IL2CPP.
    ///     </para>
    ///     <para>
    ///         Line endings are not included in the callback and both '\n' and "\r\n" are handled, matching what
    ///         <see cref="System.Diagnostics.DataReceivedEventArgs.Data" /> would have produced.
    ///     </para>
    /// </summary>
    internal static class ProcessOutputPump
    {
        private const int BufferSize = 4096;

        /// <summary>
        ///     Starts a background thread reading <paramref name="read" /> until it returns 0 or a negative value.
        /// </summary>
        /// <param name="name">Thread name, for debugging</param>
        /// <param name="read">Reads up to the requested amount of bytes into the buffer, returning the amount read</param>
        /// <param name="onLine">Invoked for every complete line</param>
        /// <param name="onCompleted">Invoked once the stream has ended or failed, on the reader thread</param>
        internal static void Start(string name, Func<byte[], int, int> read, Action<string> onLine,
            Action onCompleted = null)
        {
            Thread thread = new Thread(() => Pump(read, onLine, onCompleted))
            {
                IsBackground = true,
                Name = name
            };

            thread.Start();
        }

        private static void Pump(Func<byte[], int, int> read, Action<string> onLine, Action onCompleted)
        {
            byte[] buffer = new byte[BufferSize];
            List<byte> pending = new(BufferSize);

            try
            {
                while (true)
                {
                    int count;
                    try
                    {
                        count = read(buffer, buffer.Length);
                    }
                    catch (Exception)
                    {
                        // The stream is gone. There is nowhere sensible to report this: the caller is shutting down
                        // or the engine died, and both are already being handled.
                        break;
                    }

                    if (count <= 0)
                        break;

                    for (int i = 0; i < count; i++)
                    {
                        if (buffer[i] != (byte)'\n')
                        {
                            pending.Add(buffer[i]);
                            continue;
                        }

                        Emit(pending, onLine);
                        pending.Clear();
                    }
                }

                // The engine's last line may not be newline terminated.
                if (pending.Count > 0)
                    Emit(pending, onLine);
            }
            finally
            {
                try
                {
                    onCompleted?.Invoke();
                }
                catch (Exception)
                {
                    // Releasing the stream must never take the reader thread down with an exception.
                }
            }
        }

        private static void Emit(List<byte> pending, Action<string> onLine)
        {
            byte[] bytes = pending.ToArray();
            int length = bytes.Length;
            if (length > 0 && bytes[length - 1] == (byte)'\r')
                length--;

            string line = Encoding.UTF8.GetString(bytes, 0, length);

            try
            {
                onLine?.Invoke(line);
            }
            catch (Exception)
            {
                // A throwing log handler must not stop us from draining the pipe, otherwise the engine blocks on a
                // full pipe buffer and appears to hang.
            }
        }
    }
}
