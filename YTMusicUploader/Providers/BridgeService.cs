using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace YTMusicUploader.Providers
{
    /// <summary>
    /// Thrown when the Python bridge returns an error response (ok: false) for a request
    /// </summary>
    public class BridgeException : Exception
    {
        /// <summary>
        /// Error type reported by the Python bridge (i.e. Python exception class name), if any
        /// </summary>
        public string ErrorType;

        public BridgeException(string message, string errorType = null)
            : base(message)
        {
            ErrorType = errorType;
        }
    }

    /// <summary>
    /// Thrown when the Python bridge process is dead or could not be started / initialised
    /// (after one transparent restart attempt)
    /// </summary>
    public class BridgeUnavailableException : Exception
    {
        public BridgeUnavailableException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// Client for the Python sidecar bridge process which hosts sigma67's 'ytmusicapi' library.
    ///
    /// Spawns [WorkingDirectory]\PythonBridge\ytm_bridge.py with a discovered Python runtime and
    /// communicates over the child process's stdin / stdout using a JSON Lines protocol (UTF-8):
    ///
    ///   Request:  {"id": [int], "cmd": "[name]", "args": {...}}
    ///   Response: {"id": [int], "ok": true, "result": ...}
    ///          or {"id": [int], "ok": false, "error": "...", "errorType": "..."}
    ///   Progress: {"event": "progress", "id": [request id], "sent": [long], "total": [long]}
    ///
    /// Progress events may arrive before the final response with the same id. The child's stderr
    /// is treated as diagnostics and the last ~50 lines are kept for error reporting. All requests
    /// are serialised (one in-flight request at a time) as the application calls in from multiple
    /// threads.
    /// </summary>
    public static class BridgeService
    {
        private const int StdErrRingSize = 50;
        private const int InitTimeoutSeconds = 60;
        private const int PythonValidateTimeoutMs = 15000;
        private const int ShutdownWaitMs = 2000;

        // Serialises all public operations and process lifecycle changes (single in-flight request)
        private static readonly object _gate = new object();

        // Guards the pending request table and the stderr ring buffer (touched by reader threads)
        private static readonly object _readerSync = new object();

        private static readonly Dictionary<int, PendingRequest> _pendingRequests = new Dictionary<int, PendingRequest>();
        private static readonly Queue<string> _stdErrRing = new Queue<string>();

        private static Process _process = null;
        private static StreamWriter _stdIn = null;
        private static string _pythonPath = null;
        private static bool _pythonDiscoveryFailed = false;
        private static string _cachedCookie = null;
        private static int _nextRequestId = 0;
        private static volatile bool _initialised = false;
        private static volatile string _lastError = null;
        private static volatile string _ytMusicApiVersion = null;

        /// <summary>
        /// Response slot for a single in-flight request. The stdout reader thread completes it
        /// (or fails it on child process death) and signals the waiting caller
        /// </summary>
        private class PendingRequest
        {
            public ManualResetEventSlim Completed = new ManualResetEventSlim(false);
            public JObject Response = null;
            public Action<long, long> OnProgress = null;

            // Timestamp (UtcNow.Ticks) of the last sign of life for this request - refreshed by
            // each incoming progress event. Read/written via Interlocked so the timeout is treated
            // as an idle timeout: a long upload that keeps reporting progress is never killed
            public long LastActivityTicks = 0;
        }

        static BridgeService()
        {
            // Ensure the Python child process never outlives the application
            AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
            {
                try
                {
                    Shutdown();
                }
                catch
                {
                    // Never throw during process exit
                }
            };
        }

        /// <summary>
        /// True when a usable Python runtime was found, the bridge process is running and the
        /// 'init' command succeeded. Never throws
        /// </summary>
        public static bool IsAvailable
        {
            get
            {
                try
                {
                    return _initialised && ProcessAlive();
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Description of the last failure (Python not found, init error, bridge crash etc.)
        /// for logging purposes. Null if no failure has occurred
        /// </summary>
        public static string LastError
        {
            get { return _lastError; }
        }

        /// <summary>
        /// Version of the 'ytmusicapi' Python library reported by the bridge's init result.
        /// Null when unavailable
        /// </summary>
        public static string YtMusicApiVersion
        {
            get { return _ytMusicApiVersion; }
        }

        /// <summary>
        /// Starts (or restarts) the bridge process if needed and sends the 'init' command with the
        /// given YouTube Music cookie. Re-initialises when the cookie differs from the last one used.
        /// Thread-safe and never throws - on any failure IsAvailable becomes false and LastError
        /// is set
        /// </summary>
        /// <param name="cookieValue">YouTube Music authentication cookie header value</param>
        /// <returns>True if the bridge is running and initialised with this cookie</returns>
        public static bool TryEnsureSession(string cookieValue)
        {
            lock (_gate)
            {
                try
                {
                    if (_initialised &&
                        ProcessAlive() &&
                        string.Equals(cookieValue, _cachedCookie, StringComparison.Ordinal))
                    {
                        return true;
                    }

                    return EnsureSessionLocked(cookieValue);
                }
                catch (Exception e)
                {
                    _initialised = false;
                    _lastError = "Unexpected error establishing bridge session: " + e.Message;
                    return false;
                }
            }
        }

        /// <summary>
        /// Sends a request to the bridge and blocks until the matching response arrives. Calls are
        /// serialised (one in-flight request at a time) so this is safe to use from multiple threads
        /// </summary>
        /// <param name="cmd">Bridge command name</param>
        /// <param name="args">Command arguments (may be null)</param>
        /// <param name="timeoutSeconds">Seconds to wait for the response before the bridge is
        /// considered stuck (it's then killed and restarted)</param>
        /// <returns>The 'result' token of an ok: true response</returns>
        /// <exception cref="BridgeException">The bridge returned ok: false (or timed out)</exception>
        /// <exception cref="BridgeUnavailableException">The bridge process is dead or unavailable,
        /// after attempting one transparent restart and re-init with the cached cookie</exception>
        public static JToken Invoke(string cmd, JObject args, int timeoutSeconds = 120)
        {
            lock (_gate)
            {
                return InvokeLocked(cmd, args, timeoutSeconds, null);
            }
        }

        /// <summary>
        /// Same as <see cref="Invoke"/>, but dispatches streaming progress events - (sent, total)
        /// bytes - to the given callback as they arrive (i.e. during uploads). The callback is
        /// invoked on a background thread
        /// </summary>
        /// <param name="cmd">Bridge command name</param>
        /// <param name="args">Command arguments (may be null)</param>
        /// <param name="onProgress">Callback receiving (sent, total) byte counts</param>
        /// <param name="timeoutSeconds">Seconds to wait for the final response</param>
        /// <returns>The 'result' token of an ok: true response</returns>
        /// <exception cref="BridgeException">The bridge returned ok: false (or timed out)</exception>
        /// <exception cref="BridgeUnavailableException">The bridge process is dead or unavailable,
        /// after attempting one transparent restart and re-init with the cached cookie</exception>
        public static JToken InvokeWithProgress(string cmd, JObject args, Action<long, long> onProgress, int timeoutSeconds = 3600)
        {
            lock (_gate)
            {
                return InvokeLocked(cmd, args, timeoutSeconds, onProgress);
            }
        }

        /// <summary>
        /// Best-effort shutdown: sends the 'shutdown' command, waits briefly for a clean exit and
        /// kills the child process if it doesn't comply. Hooked to AppDomain.ProcessExit so the
        /// child never outlives the application. Never throws
        /// </summary>
        public static void Shutdown()
        {
            lock (_gate)
            {
                try
                {
                    if (ProcessAlive())
                    {
                        try
                        {
                            var request = new JObject
                            {
                                ["id"] = ++_nextRequestId,
                                ["cmd"] = "shutdown",
                                ["args"] = new JObject()
                            };

                            _stdIn.WriteLine(request.ToString(Formatting.None));
                        }
                        catch
                        {
                            // Pipe may already be broken - proceed to kill
                        }

                        try
                        {
                            if (!_process.WaitForExit(ShutdownWaitMs))
                                _process.Kill();
                        }
                        catch
                        {
                            // Process may have exited in the meantime
                        }
                    }
                }
                catch
                {
                    // Best-effort only
                }
                finally
                {
                    CleanUpProcessLocked();
                }
            }
        }

        #region Session / invoke internals (all called while holding _gate)

        /// <summary>
        /// Sends a request with one transparent restart + re-init (using the cached cookie) if the
        /// bridge process turns out to be dead. Must be called while holding _gate
        /// </summary>
        private static JToken InvokeLocked(string cmd, JObject args, int timeoutSeconds, Action<long, long> onProgress)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (!_initialised || !ProcessAlive())
                {
                    if (_cachedCookie == null)
                    {
                        _lastError = "Bridge session was never initialised - call TryEnsureSession first";
                        throw new BridgeUnavailableException(_lastError);
                    }

                    if (!EnsureSessionLocked(_cachedCookie))
                        throw new BridgeUnavailableException(_lastError ?? "Unable to start the Python bridge process");
                }

                try
                {
                    return SendRequestLocked(cmd, args, timeoutSeconds, onProgress, true);
                }
                catch (BridgeUnavailableException)
                {
                    // Child died mid-request - clean up and allow one restart + re-init on the
                    // next loop iteration before giving up
                    KillProcessLocked();

                    if (attempt == 1)
                        throw;
                }
            }

            throw new BridgeUnavailableException(_lastError ?? "The Python bridge is unavailable");
        }

        /// <summary>
        /// Starts the bridge process if it's not running and sends the 'init' command with the
        /// given cookie. Must be called while holding _gate. Sets LastError and returns false on
        /// failure (never throws to callers that expect a bool)
        /// </summary>
        private static bool EnsureSessionLocked(string cookieValue)
        {
            _initialised = false;

            if (!ProcessAlive())
            {
                KillProcessLocked();

                if (!StartProcessLocked())
                    return false;
            }

            try
            {
                var initArgs = new JObject
                {
                    ["cookie"] = cookieValue
                };

                var result = SendRequestLocked("init", initArgs, InitTimeoutSeconds, null, false);

                _cachedCookie = cookieValue;
                _ytMusicApiVersion = ExtractYtMusicApiVersion(result);
                _initialised = true;

                return true;
            }
            catch (BridgeException e)
            {
                _lastError = "Bridge init failed: " + e.Message;
                KillProcessLocked();
                return false;
            }
            catch (BridgeUnavailableException e)
            {
                _lastError = "Bridge process died during init: " + e.Message;
                KillProcessLocked();
                return false;
            }
            catch (Exception e)
            {
                _lastError = "Unexpected error during bridge init: " + e.Message;
                KillProcessLocked();
                return false;
            }
        }

        /// <summary>
        /// Writes a single request line to the child's stdin and blocks until the stdout reader
        /// thread completes it, the child dies, or the timeout elapses (in which case the stuck
        /// process is killed and, when permitted, restarted). Must be called while holding _gate
        /// </summary>
        private static JToken SendRequestLocked(string cmd, JObject args, int timeoutSeconds, Action<long, long> onProgress, bool restartOnTimeout)
        {
            int id = ++_nextRequestId;
            var pending = new PendingRequest { OnProgress = onProgress };

            lock (_readerSync)
                _pendingRequests[id] = pending;

            try
            {
                var request = new JObject
                {
                    ["id"] = id,
                    ["cmd"] = cmd,
                    ["args"] = args ?? new JObject()
                };

                Interlocked.Exchange(ref pending.LastActivityTicks, DateTime.UtcNow.Ticks);

                try
                {
                    _stdIn.WriteLine(request.ToString(Formatting.None));
                }
                catch (Exception e)
                {
                    _lastError = "Failed to write to the bridge process: " + e.Message + GetStdErrTailSuffix();
                    throw new BridgeUnavailableException(_lastError);
                }

                if (!WaitForCompletionOrIdleTimeout(pending, timeoutSeconds))
                {
                    // A stuck bridge must not wedge the application: kill it, then (best-effort)
                    // restart and re-init so subsequent calls can succeed. This is an *idle*
                    // timeout - a long upload that keeps reporting progress resets it and survives
                    _lastError = "Bridge command '" + cmd + "' timed out after " + timeoutSeconds + "s of inactivity" + GetStdErrTailSuffix();
                    KillProcessLocked();

                    if (restartOnTimeout && _cachedCookie != null)
                    {
                        string timeoutError = _lastError;
                        EnsureSessionLocked(_cachedCookie);
                        _lastError = timeoutError;
                    }

                    throw new BridgeException(_lastError, "Timeout");
                }

                if (pending.Response == null)
                {
                    // Completed without a response: the child process died
                    _lastError = "The bridge process exited unexpectedly" + GetStdErrTailSuffix();
                    throw new BridgeUnavailableException(_lastError);
                }

                if (pending.Response.Value<bool?>("ok") == true)
                    return pending.Response["result"];

                string error = pending.Response.Value<string>("error") ?? "Unknown bridge error";
                string errorType = pending.Response.Value<string>("errorType");

                throw new BridgeException(error, errorType);
            }
            finally
            {
                lock (_readerSync)
                    _pendingRequests.Remove(id);
            }
        }

        /// <summary>
        /// Waits for the request to complete, treating <paramref name="timeoutSeconds"/> as an idle
        /// timeout: the wait only fails if no progress event and no final response arrive within that
        /// window. Progress events refresh the request's activity timestamp, so an actively
        /// transferring upload of any duration is never killed. Returns true if the request completed
        /// </summary>
        private static bool WaitForCompletionOrIdleTimeout(PendingRequest pending, int timeoutSeconds)
        {
            long idleTimeoutTicks = TimeSpan.FromSeconds(timeoutSeconds).Ticks;

            // Poll frequently enough to notice idleness promptly, but never longer than the timeout
            int pollMs = (int)Math.Min(2000L, Math.Max(1L, timeoutSeconds * 1000L));

            while (true)
            {
                if (pending.Completed.Wait(pollMs))
                    return true;

                long idleTicks = DateTime.UtcNow.Ticks - Interlocked.Read(ref pending.LastActivityTicks);
                if (idleTicks >= idleTimeoutTicks)
                    return false;
            }
        }

        /// <summary>
        /// Pulls the ytmusicapi library version out of the init result, tolerating a few
        /// plausible key names. Returns null when not present
        /// </summary>
        private static string ExtractYtMusicApiVersion(JToken initResult)
        {
            try
            {
                var resultObject = initResult as JObject;
                if (resultObject == null)
                    return null;

                return (string)(resultObject["ytmusicapiVersion"]
                             ?? resultObject["ytmusicapi_version"]
                             ?? resultObject["version"]);
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region Process management

        /// <summary>
        /// Returns true when the child process object exists and hasn't exited. Never throws
        /// </summary>
        private static bool ProcessAlive()
        {
            try
            {
                return _process != null && !_process.HasExited;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Locates a Python runtime, validates the bridge script exists and spawns the child
        /// process with redirected UTF-8 stdio, plus dedicated stdout / stderr reader threads.
        /// Must be called while holding _gate. Sets LastError and returns false on failure
        /// </summary>
        private static bool StartProcessLocked()
        {
            string scriptPath = Path.Combine(Global.WorkingDirectory, "PythonBridge", "ytm_bridge.py");
            if (!File.Exists(scriptPath))
            {
                _lastError = "Python bridge script not found: " + scriptPath;
                return false;
            }

            // Negative-cache a failed discovery for the process lifetime: probing for Python can
            // take several seconds, and per-song calls must not stall repeatedly re-probing
            if (_pythonPath == null && !_pythonDiscoveryFailed)
            {
                _pythonPath = FindPython();

                if (_pythonPath == null)
                    _pythonDiscoveryFailed = true;
            }

            if (_pythonPath == null)
            {
                _lastError = "No usable Python runtime found (checked the 'PythonBridgePython' app setting, " +
                             "the bundled runtime at " + Path.Combine(Global.WorkingDirectory, "Python", "python.exe") +
                             " and py.exe / python.exe on PATH with the 'ytmusicapi' package installed)";
                return false;
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = _pythonPath,
                    Arguments = "-u \"" + scriptPath + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false),
                    WorkingDirectory = Global.WorkingDirectory
                };

                var process = new Process
                {
                    StartInfo = startInfo,
                    EnableRaisingEvents = true
                };

                process.Exited += (sender, e) => OnProcessDeath(process);

                process.Start();

                // Force UTF-8 without BOM for requests, regardless of the system default encoding
                _stdIn = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false))
                {
                    AutoFlush = true
                };

                _process = process;

                var stdOutThread = new Thread(() => ReadStdOutLoop(process))
                {
                    IsBackground = true,
                    Name = "YTM Bridge stdout reader"
                };

                var stdErrThread = new Thread(() => ReadStdErrLoop(process))
                {
                    IsBackground = true,
                    Name = "YTM Bridge stderr reader"
                };

                stdOutThread.Start();
                stdErrThread.Start();

                return true;
            }
            catch (Exception e)
            {
                _lastError = "Failed to start the Python bridge process: " + e.Message;
                _pythonPath = null; // Re-run discovery next time in case this runtime is broken
                CleanUpProcessLocked();
                return false;
            }
        }

        /// <summary>
        /// Kills (if necessary) and disposes the current child process, failing any in-flight
        /// request. Must be called while holding _gate. Never throws
        /// </summary>
        private static void KillProcessLocked()
        {
            try
            {
                if (_process != null && !_process.HasExited)
                    _process.Kill();
            }
            catch
            {
                // Process may already be gone
            }

            CleanUpProcessLocked();
        }

        /// <summary>
        /// Releases process handles and marks the bridge uninitialised. Must be called while
        /// holding _gate. Never throws
        /// </summary>
        private static void CleanUpProcessLocked()
        {
            _initialised = false;

            try
            {
                if (_stdIn != null)
                    _stdIn.Dispose();
            }
            catch
            {
                // Ignore
            }

            try
            {
                if (_process != null)
                    _process.Dispose();
            }
            catch
            {
                // Ignore
            }

            _stdIn = null;
            _process = null;

            FailAllPendingRequests();
        }

        /// <summary>
        /// Handles the child process exiting (crash or otherwise): fails the in-flight request
        /// and marks the bridge unavailable. Ignores stale notifications from previous process
        /// generations. Never throws (runs on a threadpool / reader thread)
        /// </summary>
        private static void OnProcessDeath(Process process)
        {
            try
            {
                // A newer process may already have been started - only react to the current one
                if (!ReferenceEquals(process, _process))
                    return;

                _initialised = false;

                if (_lastError == null)
                    _lastError = "The bridge process exited unexpectedly" + GetStdErrTailSuffix();

                FailAllPendingRequests();
            }
            catch
            {
                // Never let exceptions escape from background threads
            }
        }

        /// <summary>
        /// Completes every pending request without a response so waiting callers wake up and
        /// translate the null response into a BridgeUnavailableException. Never throws
        /// </summary>
        private static void FailAllPendingRequests()
        {
            try
            {
                lock (_readerSync)
                {
                    foreach (var pending in _pendingRequests.Values)
                    {
                        try
                        {
                            pending.Completed.Set();
                        }
                        catch
                        {
                            // Ignore
                        }
                    }
                }
            }
            catch
            {
                // Never let exceptions escape from background threads
            }
        }

        #endregion

        #region Stdout / stderr reader threads

        /// <summary>
        /// Dedicated background thread: reads JSON lines from the child's stdout and routes them.
        /// Lines with an "event" property are progress notifications dispatched to the in-flight
        /// request's callback; anything else completes the pending request with the matching id.
        /// A null line means the child died. Never throws
        /// </summary>
        private static void ReadStdOutLoop(Process process)
        {
            try
            {
                var reader = process.StandardOutput;

                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    JObject message;
                    try
                    {
                        message = JObject.Parse(line);
                    }
                    catch
                    {
                        // Not JSON - treat as noise
                        continue;
                    }

                    try
                    {
                        if (message["event"] != null)
                            DispatchProgress(message);
                        else if (message["id"] != null)
                            CompletePendingRequest(message);
                    }
                    catch
                    {
                        // Never let a bad message take the reader thread down
                    }
                }
            }
            catch
            {
                // Stream broken - fall through to death handling
            }

            // EOF on stdout: the child has died (or is exiting)
            OnProcessDeath(process);
        }

        /// <summary>
        /// Routes a progress event - {"event": "progress", "id": n, "sent": x, "total": y} - to
        /// the matching in-flight request's callback, if any
        /// </summary>
        private static void DispatchProgress(JObject message)
        {
            int id = message.Value<int?>("id") ?? -1;

            PendingRequest pending;
            lock (_readerSync)
                _pendingRequests.TryGetValue(id, out pending);

            if (pending == null)
                return;

            // A progress event is a sign of life - refresh the idle-timeout clock even if no
            // progress callback is attached
            Interlocked.Exchange(ref pending.LastActivityTicks, DateTime.UtcNow.Ticks);

            if (pending.OnProgress == null)
                return;

            long sent = message.Value<long?>("sent") ?? 0;
            long total = message.Value<long?>("total") ?? 0;

            try
            {
                pending.OnProgress(sent, total);
            }
            catch
            {
                // A misbehaving progress callback must not break the reader thread
            }
        }

        /// <summary>
        /// Stores the final response on the matching pending request and signals the waiting caller
        /// </summary>
        private static void CompletePendingRequest(JObject message)
        {
            int id = message.Value<int?>("id") ?? -1;

            PendingRequest pending;
            lock (_readerSync)
                _pendingRequests.TryGetValue(id, out pending);

            if (pending == null)
                return; // Late response for a timed-out / abandoned request

            pending.Response = message;
            pending.Completed.Set();
        }

        /// <summary>
        /// Dedicated background thread: captures the child's stderr diagnostics into a ring buffer
        /// (last ~50 lines) used to enrich error messages. Never throws
        /// </summary>
        private static void ReadStdErrLoop(Process process)
        {
            try
            {
                var reader = process.StandardError;

                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    lock (_readerSync)
                    {
                        _stdErrRing.Enqueue(line);

                        while (_stdErrRing.Count > StdErrRingSize)
                            _stdErrRing.Dequeue();
                    }
                }
            }
            catch
            {
                // Never let exceptions escape from background threads
            }
        }

        /// <summary>
        /// Returns the captured stderr tail formatted as an error message suffix, or an empty
        /// string when nothing has been captured
        /// </summary>
        private static string GetStdErrTailSuffix()
        {
            try
            {
                lock (_readerSync)
                {
                    if (_stdErrRing.Count == 0)
                        return string.Empty;

                    return Environment.NewLine + "Bridge stderr tail:" + Environment.NewLine +
                           string.Join(Environment.NewLine, _stdErrRing.ToArray());
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        #endregion

        #region Python discovery

        /// <summary>
        /// Locates a usable Python runtime, in order of preference:
        ///  1. The 'PythonBridgePython' App.config app setting (when present and the file exists)
        ///  2. The bundled embeddable runtime at [WorkingDirectory]\Python\python.exe or
        ///     [WorkingDirectory]\PythonBridge\Python\python.exe (where Setup-PythonBridge.ps1 puts it)
        ///  3. py.exe, then python.exe from PATH - validated by successfully importing 'ytmusicapi'
        /// Returns null when nothing usable is found
        /// </summary>
        private static string FindPython()
        {
            // 1. Explicit App.config override
            try
            {
                string configured = ConfigurationManager.AppSettings["PythonBridgePython"];
                if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
                    return configured;
            }
            catch
            {
                // Tolerate a missing / malformed config section
            }

            // 2. Bundled embeddable runtime shipped alongside the application. Check both the exe
            // directory and the PythonBridge subfolder (the location Setup-PythonBridge.ps1 uses)
            try
            {
                foreach (string bundled in new[]
                {
                    Path.Combine(Global.WorkingDirectory, "Python", "python.exe"),
                    Path.Combine(Global.WorkingDirectory, "PythonBridge", "Python", "python.exe")
                })
                {
                    if (File.Exists(bundled))
                        return bundled;
                }
            }
            catch
            {
                // Ignore and fall through to PATH discovery
            }

            // 3. Runtimes on PATH - must actually have the ytmusicapi package installed
            foreach (string candidate in new[] { "py.exe", "python.exe" })
            {
                if (ValidatePythonCandidate(candidate))
                    return candidate;
            }

            return null;
        }

        /// <summary>
        /// Validates a Python candidate by running '[python] -c "import ytmusicapi"' with a hidden
        /// window and a 15 second timeout. Returns true only on exit code 0. Never throws
        /// </summary>
        private static bool ValidatePythonCandidate(string pythonExe)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = pythonExe,
                    Arguments = "-c \"import ytmusicapi\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (var process = Process.Start(startInfo))
                {
                    if (!process.WaitForExit(PythonValidateTimeoutMs))
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch
                        {
                            // Ignore
                        }

                        return false;
                    }

                    return process.ExitCode == 0;
                }
            }
            catch
            {
                // Executable not found on PATH or failed to launch
                return false;
            }
        }

        #endregion
    }
}
