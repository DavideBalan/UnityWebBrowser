// UnityWebBrowser (UWB)
// Copyright (c) 2021-2022 Voltstro-Studios
// 
// This project is under the MIT license. See the LICENSE.md file for more details.

using System;
using System.Diagnostics;
using Newtonsoft.Json;
using UnityEngine.Scripting;
using VoltstroStudios.UnityWebBrowser.Core;
using VoltstroStudios.UnityWebBrowser.Shared;

namespace VoltstroStudios.UnityWebBrowser.Logging
{
    /// <summary>
    ///     Handles UWB logs
    /// </summary>
    [Preserve]
    public class ProcessLogHandler
    {
        private readonly IWebBrowserLogger logger;
        private readonly bool ignoreLogProcessJsonErrors;

        internal ProcessLogHandler(WebBrowserClient client)
        {
            logger = client.logger;
            ignoreLogProcessJsonErrors = client.ignoreLogProcessJsonErrors;
        }

        public event Action<string> OnProcessOutputLog;

        public event Action<string> OnProcessErrorLog;

        internal void HandleErrorProcessLog(object sender, DataReceivedEventArgs e)
        {
            HandleErrorProcessLog(e.Data);
        }

        internal void HandleOutputProcessLog(object sender, DataReceivedEventArgs e)
        {
            HandleOutputProcessLog(e.Data);
        }

        /// <summary>
        ///     Handles a stderr line coming from the engine process.
        ///     <para>
        ///         The native process implementations (used under IL2CPP, where
        ///         <see cref="System.Diagnostics.Process" /> is not implemented) read the pipes themselves, so they
        ///         pass the line as a plain <see cref="string" /> instead of a
        ///         <see cref="DataReceivedEventArgs" />, whose constructor is internal.
        ///     </para>
        /// </summary>
        internal void HandleErrorProcessLog(string line)
        {
            if (line == null)
                return;

            OnProcessErrorLog?.Invoke(line);
        }

        /// <summary>
        ///     Handles a stdout line coming from the engine process
        /// </summary>
        internal void HandleOutputProcessLog(string line)
        {
            if (line == null)
                return;

            try
            {
                JsonLogStructure logStructure = ReadJsonLog(line);

                if (logStructure.Level is LogSeverity.Debug or LogSeverity.Info)
                    logger.Debug($"[{logStructure.Category}]: {logStructure.Message}");
                else if (logStructure.Level == LogSeverity.Warn)
                    logger.Warn($"[{logStructure.Category}]: {logStructure.Message}");
                else if (logStructure.Level == LogSeverity.Error)
                    logger.Error($"[{logStructure.Category}]: {logStructure.Message}\n{logStructure.Exception}");
                else if (logStructure.Level == LogSeverity.Fatal)
                    logger.Error($"[{logStructure.Category}]: {logStructure.Message}\n{logStructure.Exception}");
            }
            catch (JsonException)
            {
                if(ignoreLogProcessJsonErrors)
                    return;
                
                logger.Error($"A log message from the UWB engine was not in a JSON format!\n\nRaw Log Message:\n{line}");
            }
            catch (Exception ex)
            {
                logger.Error($"An error occured with processing a log event from the UWB engine!\n\nRaw Log Message:\n{line}\n\nException:\n{ex}");
            }

            OnProcessOutputLog?.Invoke(line);
        }

        /// <summary>
        ///     Reads json and deserializes it as <see cref="JsonLogStructure" />
        /// </summary>
        /// <param name="json"></param>
        /// <returns></returns>
        internal static JsonLogStructure ReadJsonLog(string json)
        {
            JsonLogStructure logStructure = JsonConvert.DeserializeObject<JsonLogStructure>(json);
            return logStructure;
        }
    }
}