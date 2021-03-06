﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PuppeteerSharp.Helpers;
using System.Linq;
using System.IO;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Collections;

namespace PuppeteerSharp
{
    public class Launcher
    {
        #region Constants
        internal static readonly string[] DefaultArgs = {
            "--disable-background-networking",
            "--disable-background-timer-throttling",
            "--disable-client-side-phishing-detection",
            "--disable-default-apps",
            "--disable-extensions",
            "--disable-hang-monitor",
            "--disable-popup-blocking",
            "--disable-prompt-on-repost",
            "--disable-sync",
            "--disable-translate",
            "--metrics-recording-only",
            "--no-first-run",
            "--remote-debugging-port=0",
            "--safebrowsing-disable-auto-update",
        };

        internal static readonly string[] AutomationArgs = {
            "--enable-automation",
            "--password-store=basic",
            "--use-mock-keychain"
        };
        #endregion

        #region Private members
        private Process _chromeProcess;
        private string _temporaryUserDataDir = null;
        private Connection _connection = null;
        private Timer _timer = null;
        private LaunchOptions _options;
        private TaskCompletionSource<bool> _waitForChromeToClose;
        private static int _processCount = 0;
        private bool _processLoaded;
        private const string UserDataDirArgument = "--user-data-dir";
        #endregion

        #region Properties
        public bool IsChromeClosed { get; internal set; }
        #endregion

        public Launcher()
        {
            _waitForChromeToClose = new TaskCompletionSource<bool>();
        }

        #region Public methods
        public async Task<Browser> LaunchAsync(LaunchOptions options, int chromiumRevision)
        {
            var chromeArguments = new List<string>(DefaultArgs);

            _options = options;

            if (options.AppMode)
            {
                options.Headless = false;
            }
            else
            {
                chromeArguments.AddRange(AutomationArgs);
            }

            var userDataDirOption = options.Args.FirstOrDefault(i => i.StartsWith(UserDataDirArgument, StringComparison.Ordinal));
            if (string.IsNullOrEmpty(userDataDirOption))
            {
                if (string.IsNullOrEmpty(options.UserDataDir))
                {
                    _temporaryUserDataDir = GetTemporaryDirectory();
                    chromeArguments.Add($"{UserDataDirArgument}={_temporaryUserDataDir}");
                }
                else
                {
                    chromeArguments.Add($"{UserDataDirArgument}={options.UserDataDir}");
                }
            }
            else
            {
                _options.UserDataDir = userDataDirOption.Replace($"{UserDataDirArgument}=", string.Empty);
            }

            if (options.Devtools)
            {
                chromeArguments.Add("--auto-open-devtools-for-tabs");
                options.Headless = false;
            }

            if (options.Headless)
            {
                chromeArguments.AddRange(new[]{
                    "--headless",
                    "--disable-gpu",
                    "--hide-scrollbars",
                    "--mute-audio"
                });
            }

            var chromeExecutable = options.ExecutablePath;

            if (string.IsNullOrEmpty(chromeExecutable))
            {
                var downloader = Downloader.CreateDefault();
                var revisionInfo = downloader.RevisionInfo(Downloader.CurrentPlatform, chromiumRevision);
                chromeExecutable = revisionInfo.ExecutablePath;
            }
            if (!File.Exists(chromeExecutable))
            {
                throw new FileNotFoundException("Failed to launch chrome! path to executable does not exist", chromeExecutable);
            }

            if (options.Args.Any())
            {
                chromeArguments.AddRange(options.Args);
            }

            _chromeProcess = new Process();
            _chromeProcess.EnableRaisingEvents = true;
            _chromeProcess.StartInfo.UseShellExecute = false;
            _chromeProcess.StartInfo.FileName = chromeExecutable;
            _chromeProcess.StartInfo.Arguments = string.Join(" ", chromeArguments);

            SetEnvVariables(_chromeProcess.StartInfo.Environment, options.Env, Environment.GetEnvironmentVariables());

            if (!options.DumpIO)
            {
                _chromeProcess.StartInfo.RedirectStandardOutput = false;
                _chromeProcess.StartInfo.RedirectStandardError = false;
            }

            _chromeProcess.Exited += async (sender, e) =>
            {
                await AfterProcessExit();
            };

            try
            {
                var connectionDelay = options.SlowMo;
                var browserWSEndpoint = await WaitForEndpoint(_chromeProcess, options.Timeout, options.DumpIO);
                var keepAliveInterval = options.KeepAliveInterval;

                _connection = await Connection.Create(browserWSEndpoint, connectionDelay, keepAliveInterval);
                _processLoaded = true;

                if (options.LogProcess)
                {
                    Console.WriteLine($"PROCESS COUNT: {Interlocked.Increment(ref _processCount)}");
                }

                return await Browser.CreateAsync(_connection, options, KillChrome);
            }
            catch (Exception ex)
            {
                ForceKillChrome();
                throw new ChromeProcessException("Failed to create connection", ex);
            }

        }

        public async Task TryDeleteUserDataDir(int times = 10, TimeSpan? delay = null)
        {
            if (!IsChromeClosed)
            {
                throw new InvalidOperationException("Unable to delete user data dir, Chorme is still open");
            }

            if (times <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(times));
            }

            if (delay == null)
            {
                delay = new TimeSpan(0, 0, 0, 0, 100);
            }

            string folder = string.IsNullOrEmpty(_temporaryUserDataDir) ? _options.UserDataDir : _temporaryUserDataDir;
            int attempts = 0;
            while (true)
            {
                try
                {
                    attempts++;
                    Directory.Delete(folder, true);
                    break;
                }
                catch (UnauthorizedAccessException)
                {
                    if (attempts == times)
                    {
                        throw;
                    }

                    await Task.Delay(delay.Value);
                }
            }
        }

        public static string GetExecutablePath()
        {
            var downloader = Downloader.CreateDefault();
            var revisionInfo = downloader.RevisionInfo(Downloader.CurrentPlatform, Downloader.DefaultRevision);
            return revisionInfo.ExecutablePath;
        }

        public static string GetTemporaryDirectory()
        {
            string tempDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tempDirectory);
            return tempDirectory;
        }

        #endregion

        #region Private methods

        private Task<string> WaitForEndpoint(Process chromeProcess, int timeout, bool dumpio)
        {
            var taskWrapper = new TaskCompletionSource<string>();
            var output = string.Empty;

            chromeProcess.StartInfo.RedirectStandardOutput = true;
            chromeProcess.StartInfo.RedirectStandardError = true;

            EventHandler exitedEvent = (sender, e) =>
            {
                if (_options.LogProcess && !_processLoaded)
                {
                    Console.WriteLine($"PROCESS COUNT: {Interlocked.Increment(ref _processCount)}");
                }

                CleanUp();

                taskWrapper.SetException(new ChromeProcessException($"Failed to launch chrome! {output}"));
            };

            chromeProcess.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    output += e.Data + "\n";
                    var match = Regex.Match(e.Data, "^DevTools listening on (ws:\\/\\/.*)");

                    if (!match.Success)
                    {
                        return;
                    }

                    CleanUp();
                    taskWrapper.SetResult(match.Groups[1].Value);

                    //Restore defaults for Redirects
                    if (!dumpio)
                    {
                        chromeProcess.StartInfo.RedirectStandardOutput = false;
                        chromeProcess.StartInfo.RedirectStandardError = false;
                    }

                    chromeProcess.Exited -= exitedEvent;
                }
            };

            chromeProcess.Exited += exitedEvent;

            if (timeout > 0)
            {
                //We have to declare timer before initializing it because if we don't do this 
                //we can't dispose it in the action created in the constructor
                _timer = new Timer((state) =>
                {
                    taskWrapper.SetException(
                        new ChromeProcessException($"Timed out after {timeout} ms while trying to connect to Chrome! "));
                    _timer.Dispose();
                }, null, timeout, 0);

            }

            chromeProcess.Start();
            chromeProcess.BeginErrorReadLine();
            return taskWrapper.Task;
        }

        private void CleanUp()
        {
            _timer?.Dispose();
            _timer = null;
            _chromeProcess?.RemoveExitedEvent();
        }

        private async Task AfterProcessExit()
        {
            if (IsChromeClosed)
            {
                return;
            }

            if (_options.LogProcess)
            {
                Console.WriteLine($"PROCESS COUNT: {Interlocked.Decrement(ref _processCount)}");
            }

            IsChromeClosed = true;

            if (_temporaryUserDataDir != null)
            {
                await TryDeleteUserDataDir();
            }

            if (_waitForChromeToClose.Task.Status != TaskStatus.RanToCompletion)
            {
                _waitForChromeToClose.SetResult(true);
            }

        }

        private async Task KillChrome()
        {
            if (!string.IsNullOrEmpty(_temporaryUserDataDir))
            {
                ForceKillChrome();
            }
            else if (_connection != null)
            {
                await _connection.SendAsync("Browser.close", null);
            }

            await _waitForChromeToClose.Task;

        }

        private void ForceKillChrome()
        {
            try
            {
                if (_chromeProcess.Id != 0 && !_chromeProcess.HasExited && Process.GetProcessById(_chromeProcess.Id) != null)
                {
                    _chromeProcess.Kill();
                    _chromeProcess.WaitForExit();
                }
            }
            catch (InvalidOperationException ex) when (ex.Message == "No process is associated with this object.")
            {
                // swallow
            }
        }


        private static void SetEnvVariables(IDictionary<string, string> environment, IDictionary<string, string> customEnv,
                                            IDictionary realEnv)
        {
            foreach (DictionaryEntry item in realEnv)
            {
                environment[item.Key.ToString()] = item.Value.ToString();
            }

            if (customEnv != null)
            {
                foreach (var item in customEnv)
                {
                    environment[item.Key] = item.Value;
                }
            }
        }

        #endregion

    }
}
