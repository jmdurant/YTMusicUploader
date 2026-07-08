using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using YTMusicUploader.Business;

namespace YTMusicUploader.Providers
{
    /// <summary>
    /// YouTube Music API Request Methods
    /// 
    /// Thanks to: sigma67: 
    ///     https://ytmusicapi.readthedocs.io/en/latest/ 
    ///     https://github.com/sigma67/ytmusicapi
    /// </summary>
    public partial class Requests
    {
        /// <summary>
        /// HttpWebRequest POST request to send to YouTube to upload a music file.
        /// </summary>
        /// <param name="mainForm">Instance of the main form to utilise the public methods of and update status'</param>
        /// <param name="cookieValue">Cookie from a previous YouTube Music sign in via this application (stored in the database)</param>
        /// <param name="filePath">Full path to file we're uploading</param>
        /// <param name="maxUploadSpeed">Throttle database bandwidth speed (bytes per second)</param>
        /// <param name="error">OUTPUT error string</param>
        /// <returns>True if the upload is successful, false otherwise</returns>
        public static bool UploadTrack(
            MainForm mainForm,
            string cookieValue,
            string filePath,
            int maxUploadSpeed,
            out string error)
        {
            error = null;

            try
            {
                if (!File.Exists(filePath))
                {
                    error = "File no longer exists. Will refresh on next scan.";
                    return false;
                }

                if (new FileInfo(filePath).Length >= 314572800) // 300 MB - YouTube Music's upload limit
                {
                    error = "File is larger than the 300 MB YouTube Music upload limit.";
                    return false;
                }
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }

            if (Global.PreferPythonBridge && BridgeService.TryEnsureSession(cookieValue))
            {
                try
                {
                    var args = new JObject
                    {
                        ["path"] = filePath
                    };

                    if (maxUploadSpeed > 0) // 0 or -1 = unthrottled
                        args["maxBytesPerSecond"] = maxUploadSpeed;

                    // Report upload progress to the main form the same way the native path's
                    // ThrottledStream does (percentage uploaded and current speed)
                    var stopWatch = Stopwatch.StartNew();
                    BridgeService.InvokeWithProgress("upload_song", args, (sent, total) =>
                    {
                        double percentage = total > 0 ? sent / (double)total * 100 : 0;
                        if (percentage > 100)
                            percentage = 100;

                        double bytesPerSecond = sent / stopWatch.Elapsed.TotalSeconds;

                        string speed = (bytesPerSecond / 1048576).ToString("0.0") + " MB /s";
                        mainForm.SetStatusMessage(
                                    "Uploading: " + percentage.ToString("0") + "% " +
                                    "(" + speed + ")",
                                    "Uploading " + speed);
                    });

                    // A 'conflict' status means the song is already uploaded, which the native
                    // implementation also treats as a success
                    return true;
                }
                catch (BridgeUnavailableException)
                {
                    // Bridge process died - fall through to the native implementation below
                }
                catch (BridgeException e)
                {
                    // Command failure - same as the native failure path
                    error = e.Message;
                    return false;
                }
            }

            try
            {
                long fileSize = new FileInfo(filePath).Length;

                var startUploadRequest = (HttpWebRequest)WebRequest.Create(Global.YTMusicUploadUrl);
                startUploadRequest = AddStandardHeaders(startUploadRequest, cookieValue);

                startUploadRequest.ContentType = "application/x-www-form-urlencoded;charset=utf-8";
                startUploadRequest.Headers["X-Goog-AuthUser"] = "0";
                startUploadRequest.Headers["x-origin"] = "https://music.youtube.com";
                startUploadRequest.Headers["X-Goog-Visitor-Id"] = GetVisitorId(cookieValue);
                startUploadRequest.Headers["Authorization"] = GetAuthorisation(GetSAPISIDFromCookie(cookieValue));
                startUploadRequest.Headers["X-Goog-Upload-Command"] = "start";
                startUploadRequest.Headers["X-Goog-Upload-Header-Content-Length"] = fileSize.ToString();
                startUploadRequest.Headers["X-Goog-Upload-Protocol"] = "resumable";

                byte[] postBytes = GetPostBytes("filename=" + Path.GetFileName(filePath));
                startUploadRequest.ContentLength = postBytes.Length;

                using (var requestStream = startUploadRequest.GetRequestStream())
                {
                    requestStream.Write(postBytes, 0, postBytes.Length);
                    requestStream.Close();
                }

                postBytes = null;
                using (var initialResponse = (HttpWebResponse)startUploadRequest.GetResponse())
                {
                    string uploadUrl = initialResponse.Headers["X-Goog-Upload-URL"];
                    var uploadRequest = (HttpWebRequest)WebRequest.Create(uploadUrl);
                    uploadRequest = AddStandardHeaders(uploadRequest, cookieValue);

                    uploadRequest.ContentType = "application/x-www-form-urlencoded;charset=utf-8";
                    uploadRequest.Headers["X-Goog-AuthUser"] = "0";
                    uploadRequest.Headers["x-origin"] = "https://music.youtube.com";
                    uploadRequest.Headers["X-Goog-Visitor-Id"] = GetVisitorId(cookieValue);
                    uploadRequest.Headers["Authorization"] = GetAuthorisation(GetSAPISIDFromCookie(cookieValue));
                    uploadRequest.Headers["X-Goog-Upload-Command"] = "upload, finalize";
                    uploadRequest.Headers["X-Goog-Upload-Offset"] = "0";

                    byte[] songBytes = File.ReadAllBytes(filePath);
                    uploadRequest.ContentLength = songBytes.Length;

                    using (var uploadStream = uploadRequest.GetRequestStream())
                    {
                        using (var throttledStream = new ThrottledStream(
                                                            new MemoryStream(songBytes),
                                                            mainForm,
                                                            songBytes.Length,
                                                            maxUploadSpeed == 0 || maxUploadSpeed == -1
                                                                ? int.MaxValue
                                                                : maxUploadSpeed))
                        {
                            throttledStream.CopyTo(uploadStream);
                            uploadStream.Close();
                        }
                    }

                    songBytes = null;
                    using (var responseUploaded = (HttpWebResponse)uploadRequest.GetResponse())
                    {
                        if (responseUploaded.StatusCode == HttpStatusCode.OK)
                            return true;
                        else
                        {
                            if (responseUploaded.StatusCode != HttpStatusCode.Conflict) // Already uploaded
                            {
                                error = responseUploaded.StatusCode.ToString();
                                return false;
                            }

                            return true;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                if (!e.Message.Contains("409")) // Already uploaded
                {
                    error = e.Message;
                    return false;
                }

                return true;
            }
        }
    }
}
