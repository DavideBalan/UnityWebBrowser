// UnityWebBrowser (UWB)
// Copyright (c) 2021-2024 Voltstro-Studios
// 
// This project is under the MIT license. See the LICENSE.md file for more details.

using System;

namespace VoltstroStudios.UnityWebBrowser.Core.Engines.Process
{
    internal interface IProcess : IDisposable
    {
        /// <summary>
        ///     Starts the process
        /// </summary>
        /// <param name="executable">Path to the executable</param>
        /// <param name="workingDir">Working directory of the process</param>
        /// <param name="arguments">Command line arguments</param>
        /// <param name="onLogLine">Invoked for every line the process writes to stdout</param>
        /// <param name="onErrorLine">Invoked for every line the process writes to stderr</param>
        public void StartProcess(string executable, string workingDir, string arguments,
            Action<string> onLogLine, Action<string> onErrorLine);

        public void KillProcess();
        
        public bool HasExited { get; }
        
        public int ExitCode { get; }
    }
}
