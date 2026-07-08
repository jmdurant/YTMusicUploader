using JBToolkit.StreamHelpers;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Net;
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
        /// HttpWebRequest POST request to send to YouTube to check if the user's is authenticated (signed in) by determining 
        /// if a generic request is successful given the current authentication cookie value we have stored.        /// 
        /// In this case, we're actually perform a request for personally uploaded music files
        /// </summary>
        /// <param name="cookieValue">Cookie from a previous YouTube Music sign in via this application (stored in the database)</param>
        /// <returns>True if successfully authenticated, false otherwise</returns>
        public static bool IsAuthenticated(string cookieValue)
        {
            if (Global.PreferPythonBridge && BridgeService.TryEnsureSession(cookieValue))
            {
                try
                {
                    return BridgeService.Invoke("is_authenticated", new Newtonsoft.Json.Linq.JObject())
                                        .ToObject<bool>();
                }
                catch (BridgeUnavailableException)
                {
                    // Bridge process died - fall through to the native implementation below
                }
                catch (BridgeException e)
                {
                    // Command failure (i.e. bad / expired credentials) - same as the native failure path
                    Console.Out.WriteLine(e.Message);

                    return false;
                }
            }

            try
            {
                var request = (HttpWebRequest)WebRequest.Create(Global.YTMusicBaseUrl + "browse" + Global.YTMusicParams);
                request = AddStandardHeaders(request, cookieValue);
                request = AddApiHeaders(request, cookieValue);

                byte[] postBytes = GetPostBytes(
                                        SetDynamicContext(
                                            SafeFileStream.ReadAllText(
                                                Path.Combine(Global.WorkingDirectory, @"AppData\check_auth_context.json"))));

                request.ContentLength = postBytes.Length;
                using (var requestStream = request.GetRequestStream())
                {
                    requestStream.Write(postBytes, 0, postBytes.Length);
                    requestStream.Close();
                }

                postBytes = null;
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    string result = ReadResponseBody(response);
                    object json = JsonConvert.DeserializeObject(result);
                }
            }
            catch (Exception ex)
            {
                Console.Out.WriteLine(ex.Message);

                return false;
            }

            return true;
        }
    }
}
