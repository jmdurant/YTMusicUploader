using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace YTMusicUploader.Providers
{
    /// <summary>
    /// YouTube music HttpWebRequest abstract class
    ///
    /// Protocol details kept in line with sigma67's ytmusicapi:
    ///     https://github.com/sigma67/ytmusicapi
    /// </summary>
    public partial class Requests
    {
        /// <summary>
        /// User agent sent with every YouTube Music request (matches ytmusicapi's current value)
        /// </summary>
        public const string YTMusicUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:88.0) Gecko/20100101 Firefox/88.0";

        private static readonly object _visitorIdLock = new object();
        private static string _visitorId = null;
        private static long _visitorIdLastAttemptTicks = 0;
        private const int VisitorIdRetryBackoffSeconds = 60;

        /// <summary>
        /// Required headers for any YouTube music API request
        /// </summary>
        /// <param name="webRequest">HttpWebRequest to add the headers to</param>
        /// <param name="cookieValue">Cookie from a previous YouTube Music sign in via this application (stored in the database)</param>
        /// <returns>The same HttpWebReqest object with added default headers</returns>
        public static HttpWebRequest AddStandardHeaders(HttpWebRequest webRequest, string cookieValue)
        {
            webRequest.Accept = "*/*";
            webRequest.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            webRequest.UserAgent = YTMusicUserAgent;
            webRequest.Headers["Cookie"] = AppendConsentCookie(cookieValue);
            webRequest.Headers["origin"] = "https://music.youtube.com";
            webRequest.Method = "POST";

            return webRequest;
        }

        /// <summary>
        /// Adds the YouTube Music API specific headers (auth user, origin, visitor ID and
        /// SAPISIDHASH authorisation) to the request. The authorisation hash is time-stamped,
        /// so this must be called per request, not cached.
        /// </summary>
        public static HttpWebRequest AddApiHeaders(HttpWebRequest webRequest, string cookieValue)
        {
            webRequest.ContentType = "application/json; charset=UTF-8";
            webRequest.Headers["X-Goog-AuthUser"] = "0";
            webRequest.Headers["x-origin"] = "https://music.youtube.com";
            webRequest.Headers["X-Goog-Visitor-Id"] = GetVisitorId(cookieValue);
            webRequest.Headers["Authorization"] = GetAuthorisation(GetSAPISIDFromCookie(cookieValue));

            return webRequest;
        }

        /// <summary>
        /// Reads the response body, honouring the response's Content-Encoding header.
        /// Gzip and deflate are decompressed automatically by the request (AutomaticDecompression);
        /// Brotli is handled explicitly in case the server chooses it regardless.
        /// </summary>
        public static string ReadResponseBody(HttpWebResponse response)
        {
            string contentEncoding = response.ContentEncoding ?? string.Empty;
            if (contentEncoding.ToLowerInvariant().Contains("br"))
            {
                using (var brotli = new Brotli.BrotliStream(response.GetResponseStream(),
                                                            System.IO.Compression.CompressionMode.Decompress,
                                                            true))
                using (var streamReader = new StreamReader(brotli))
                {
                    return streamReader.ReadToEnd();
                }
            }

            using (var streamReader = new StreamReader(response.GetResponseStream()))
            {
                return streamReader.ReadToEnd();
            }
        }

        /// <summary>
        /// Patches a request body template with the values YouTube Music now requires:
        /// a date-derived client version (the old static '0.1' has been rejected by Google
        /// since September 2022) and a minimal context without the legacy experiment flags.
        /// </summary>
        /// <param name="templateJson">Request body template JSON (from the AppData folder)</param>
        /// <returns>Request body JSON with an up to date context</returns>
        public static string SetDynamicContext(string templateJson)
        {
            var body = JObject.Parse(templateJson);
            var context = body["context"] as JObject;
            if (context == null)
            {
                context = new JObject();
                body["context"] = context;
            }

            var client = context["client"] as JObject;
            if (client == null)
            {
                client = new JObject();
                context["client"] = client;
            }

            client["clientName"] = "WEB_REMIX";
            client["clientVersion"] = "1." + DateTime.UtcNow.ToString("yyyyMMdd") + ".01.00";
            client["hl"] = "en";

            // Fields YouTube Music no longer expects - remove if present in older templates
            client.Remove("experimentIds");
            client.Remove("experimentsToken");
            client.Remove("gl");
            client.Remove("locationInfo");
            client.Remove("musicAppInfo");
            client.Remove("utcOffsetMinutes");
            context.Remove("capabilities");
            context.Remove("request");

            if (context["user"] == null)
                context["user"] = new JObject();

            return body.ToString();
        }

        /// <summary>
        /// Fetches the current Google visitor ID (VISITOR_DATA) from the YouTube Music home page.
        /// Visitor IDs expire, so the previously hardcoded value cannot be used. A successfully
        /// fetched ID is cached for the lifetime of the process. If the fetch fails (e.g. the
        /// network is briefly down at startup), the hardcoded fallback is used for the current
        /// request only and the real fetch is retried on a later call - the stale fallback is never
        /// cached permanently, which would otherwise doom every request for the whole session.
        /// </summary>
        /// <param name="cookieValue">Cookie from a previous YouTube Music sign in via this application (stored in the database)</param>
        /// <returns>The visitor ID, or the configured fallback if it can't currently be fetched</returns>
        public static string GetVisitorId(string cookieValue)
        {
            if (_visitorId != null)
                return _visitorId;

            lock (_visitorIdLock)
            {
                if (_visitorId != null)
                    return _visitorId;

                // Don't re-hammer music.youtube.com on every request while it's unreachable -
                // back off between fetch attempts, using the fallback in the meantime
                long now = DateTime.UtcNow.Ticks;
                if (_visitorIdLastAttemptTicks != 0 &&
                    (now - _visitorIdLastAttemptTicks) < TimeSpan.FromSeconds(VisitorIdRetryBackoffSeconds).Ticks)
                {
                    return Global.GoogleVisitorId;
                }

                _visitorIdLastAttemptTicks = now;

                try
                {
                    var request = (HttpWebRequest)WebRequest.Create("https://music.youtube.com");
                    request.Method = "GET";
                    request.Accept = "*/*";
                    request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                    request.UserAgent = YTMusicUserAgent;

                    if (!string.IsNullOrEmpty(cookieValue))
                        request.Headers["Cookie"] = AppendConsentCookie(cookieValue);

                    using (var response = (HttpWebResponse)request.GetResponse())
                    {
                        string html = ReadResponseBody(response);
                        var match = Regex.Match(html, @"ytcfg\.set\s*\(\s*({.+?})\s*\)\s*;", RegexOptions.Singleline);
                        if (match.Success)
                        {
                            var ytcfg = JObject.Parse(match.Groups[1].Value);
                            string visitorData = (string)ytcfg["VISITOR_DATA"];
                            if (!string.IsNullOrEmpty(visitorData))
                                _visitorId = visitorData; // Only a real value is cached permanently
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.Out.WriteLine("GetVisitorId: " + e.Message);
                }

                // Fall back for this call without caching, so a later call retries the fetch
                return _visitorId ?? Global.GoogleVisitorId;
            }
        }

        /// <summary>
        /// Appends Google's SOCS consent cookie if not already present (ytmusicapi sends
        /// SOCS=CAI with every request to avoid consent interstitials).
        /// </summary>
        private static string AppendConsentCookie(string cookieValue)
        {
            if (string.IsNullOrEmpty(cookieValue))
                return cookieValue;

            if (cookieValue.Contains("SOCS="))
                return cookieValue;

            return cookieValue.TrimEnd().TrimEnd(';') + "; SOCS=CAI";
        }

        /// <summary>
        /// Converts a string to a byte array for use in a HttpWebRequest upload stream (UTF8 encoded).
        /// </summary>
        /// <param name="stringToEncode">String to convert to a UTF8 encoded byte array</param>
        /// <returns>UTF8 encoded byte arra</returns>
        public static byte[] GetPostBytes(string stringToEncode)
        {
            return Encoding.UTF8.GetBytes(stringToEncode);
        }

        /// <summary>
        /// Strips the SAPISID value from the cookie. Google now issues the __Secure-3PAPISID
        /// cookie (identical value) and some accounts no longer receive a plain SAPISID cookie
        /// at all, so __Secure-3PAPISID is preferred (matching ytmusicapi).
        /// </summary>
        /// <param name="cookieValue">Cookie from a previous YouTube Music sign in via this application (stored in the database)</param>
        /// <returns>The SAPISID value from the the cookie</returns>
        private static string GetSAPISIDFromCookie(string cookieValue)
        {
            var match = Regex.Match(cookieValue, @"(?:^|;\s*)__Secure-3PAPISID=([^;]+)");
            if (!match.Success)
                match = Regex.Match(cookieValue, @"(?:^|;\s*)SAPISID=([^;]+)");

            if (!match.Success)
                throw new ApplicationException(
                    "The cookie is missing the required value __Secure-3PAPISID. " +
                    "Try signing out of YouTube Music and back in, then copy the cookie again.");

            return match.Groups[1].Value.Trim().Trim('"');
        }

        /// <summary>
        /// Thanks to: Dave Thomas
        ///     https://stackoverflow.com/users/984724/dave-thomas
        ///     https://stackoverflow.com/a/32065323/5726546
        ///
        /// For reverse engineering generating the SAPISIDHASH hash from the cookie value for use in the HttpWebRequest
        /// 'Authorization' header so we can be authenticated with YouTube music to utilise it's API.
        /// </summary>
        /// <param name="sapisid">SAPISID which is from the cookie value</param>
        /// <returns>SAPISID hash</returns>
        private static string GetAuthorisation(string sapisid)
        {
            int unixTimestamp = (int)(DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1))).TotalSeconds;
            byte[] hash = new SHA1Managed().ComputeHash(Encoding.UTF8.GetBytes(unixTimestamp + " " + sapisid + " https://music.youtube.com"));
            var sb = new StringBuilder(hash.Length * 2);

            foreach (byte b in hash)
                sb.Append(b.ToString("x2"));

            return "SAPISIDHASH " + unixTimestamp + "_" + sb.ToString();
        }
    }
}
