// UnityWebBrowser (UWB)
// Copyright (c) 2021-2024 Voltstro-Studios
// 
// This project is under the MIT license. See the LICENSE.md file for more details.

using System;
using System.IO;
using VoltstroStudios.UnityWebBrowser.Core.Engines.Process;
using VoltstroStudios.UnityWebBrowser.Helper;
using VoltstroStudios.UnityWebBrowser.Logging;
using VoltstroStudios.UnityWebBrowser.Shared.Core;

namespace VoltstroStudios.UnityWebBrowser.Core.Engines
{
    /// <summary>
    ///     Handler for the engine process
    /// </summary>
    internal sealed class EngineProcess : IDisposable
    {
        private readonly IProcess processHandle;
        private readonly Engine engine;
        private readonly IWebBrowserLogger logger;
        
        /// <summary>
        ///     Creates a new <see cref="EngineProcess"/> instance
        /// </summary>
        /// <param name="engine"></param>
        /// <param name="logger"></param>
        public EngineProcess(Engine engine, IWebBrowserLogger logger)
        {
#if UNITY_STANDALONE_WIN
            processHandle = new WindowProcess(logger);
#elif UNITY_STANDALONE_LINUX
            processHandle = new LinuxProcess(logger);
#elif UNITY_STANDALONE_OSX
            processHandle = new MacOsProcess(logger);
#endif
            
            this.engine = engine;
            this.logger = logger;
        }

        /// <summary>
        ///     Has the process exited?
        /// </summary>
        public bool HasExited => processHandle.HasExited;

        /// <summary>
        ///     What was the exit code of the process
        /// </summary>
        public int ExitCode => processHandle.ExitCode;

        /// <summary>
        ///     Starts the engine process
        /// </summary>
        /// <param name="arguments"></param>
        /// <param name="onLogLine"></param>
        /// <param name="onErrorLine"></param>
        public void StartProcess(string arguments, Action<string> onLogLine, Action<string> onErrorLine)
        {
            Platform platform = WebBrowserUtils.GetRunningPlatform();
            string engineFullProcessPath = engine.GetEngineAppPath(platform);
            string engineDirectory = engine.GetEngineWorkingPath(platform);
            
            logger.Debug($"Process Path: '{engineFullProcessPath}'\nWorking: '{engineDirectory}'");
            logger.Debug($"Arguments: '{arguments}'");
            
            processHandle.StartProcess(engineFullProcessPath, engineDirectory, arguments, onLogLine, onErrorLine);
        }

        /// <summary>
        ///     Kills the engine process
        /// </summary>
        public void KillProcess()
        {
            processHandle.KillProcess();
        }
        
        public void Dispose()
        {
            processHandle.Dispose();
        }
    }
}