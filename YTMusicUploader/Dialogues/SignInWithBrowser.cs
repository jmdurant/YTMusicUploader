using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace YTMusicUploader.Dialogues
{
    /// <summary>
    /// An embedded browser sign-in dialog. Hosts a WebView2 (Edge/Chromium) control pointed at the
    /// YouTube Music sign-in page and, once the user has signed in, harvests the authentication
    /// cookie automatically via the WebView2 cookie manager - removing the need to copy it out of
    /// the browser's developer tools by hand.
    ///
    /// Google blocks its OAuth 2.0 authorization endpoint inside embedded webviews, but this is a
    /// plain account sign-in (not OAuth); a desktop-Chrome User-Agent is set so Google's
    /// "this browser may not be secure" heuristic doesn't reject the sign-in. The manual cookie
    /// paste on the Connect dialog remains available as a fallback if this ever stops working.
    /// </summary>
    public class SignInWithBrowser : Form
    {
        /// <summary>
        /// The captured authentication cookie string (assembled from the WebView2 cookie jar),
        /// ready to be handed to the same validation the manual-paste box uses. Null if the user
        /// closed the window before signing in.
        /// </summary>
        public string CapturedCookie { get; private set; }

        // A recent desktop Chrome User-Agent. Google rejects sign-in from user agents it recognises
        // as embedded webviews, so we present as a normal desktop browser.
        private const string DesktopChromeUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/126.0.0.0 Safari/537.36";

        private const string SignInUrl =
            "https://accounts.google.com/ServiceLogin?continue=https%3A%2F%2Fmusic.youtube.com%2F";

        private readonly WebView2 _webView;
        private readonly Label _statusLabel;
        private readonly Timer _cookiePollTimer;
        private bool _polling;
        private bool _captured;

        public SignInWithBrowser()
        {
            Text = "Sign in to YouTube Music";
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(920, 760);
            MinimumSize = new Size(560, 480);
            ShowIcon = false;

            _statusLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 0, 0),
                Text = "Loading sign-in page...",
                Font = new Font("Segoe UI", 8.25f)
            };

            _webView = new WebView2 { Dock = DockStyle.Fill };

            Controls.Add(_webView);
            Controls.Add(_statusLabel);

            _cookiePollTimer = new Timer { Interval = 1500 };
            _cookiePollTimer.Tick += async (s, e) => await PollForAuthCookieAsync();

            Load += SignInWithBrowser_Load;
            FormClosed += SignInWithBrowser_FormClosed;
        }

        private async void SignInWithBrowser_Load(object sender, EventArgs e)
        {
            try
            {
                // Persist the browser profile so a signed-in session survives between attempts
                string userDataFolder = Path.Combine(Global.AppDataLocation, "WebView2");
                Directory.CreateDirectory(userDataFolder);

                var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await _webView.EnsureCoreWebView2Async(environment);

                _webView.CoreWebView2.Settings.UserAgent = DesktopChromeUserAgent;
                _webView.CoreWebView2.Navigate(SignInUrl);

                _statusLabel.Text = "Sign in to your Google account. This window closes automatically once you're connected.";
                _cookiePollTimer.Start();
            }
            catch (Exception ex)
            {
                // Most likely the WebView2 runtime isn't installed - fall back to manual paste
                MessageBox.Show(
                    "Couldn't start the embedded browser:" + Environment.NewLine + Environment.NewLine +
                    ex.Message + Environment.NewLine + Environment.NewLine +
                    "You can still connect by pasting your cookie manually. If this keeps happening, " +
                    "install the Microsoft Edge WebView2 Runtime.",
                    "Sign in to YouTube Music",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                DialogResult = DialogResult.Cancel;
                Close();
            }
        }

        private async System.Threading.Tasks.Task PollForAuthCookieAsync()
        {
            if (_polling || _captured || _webView.CoreWebView2 == null)
                return;

            _polling = true;
            try
            {
                var cookies = await _webView.CoreWebView2.CookieManager.GetCookiesAsync("https://music.youtube.com");
                if (cookies == null || cookies.Count == 0)
                    return;

                var sb = new StringBuilder();
                bool hasLoginInfo = false;
                bool hasApiSid = false;

                foreach (var cookie in cookies)
                {
                    if (string.IsNullOrEmpty(cookie.Name))
                        continue;

                    if (sb.Length > 0)
                        sb.Append("; ");
                    sb.Append(cookie.Name).Append('=').Append(cookie.Value);

                    if (cookie.Name == "LOGIN_INFO")
                        hasLoginInfo = true;
                    if (cookie.Name == "__Secure-3PAPISID" || cookie.Name == "SAPISID")
                        hasApiSid = true;
                }

                // LOGIN_INFO marks a signed-in YouTube session; __Secure-3PAPISID / SAPISID is
                // required to compute the SAPISIDHASH authorisation. Only capture once both exist
                if (hasLoginInfo && hasApiSid)
                {
                    _captured = true;
                    _cookiePollTimer.Stop();
                    CapturedCookie = sb.ToString();

                    _statusLabel.Text = "Connected - capturing session...";
                    DialogResult = DialogResult.OK;
                    Close();
                }
            }
            catch
            {
                // Transient cookie-read failures are fine; the next tick tries again
            }
            finally
            {
                _polling = false;
            }
        }

        private void SignInWithBrowser_FormClosed(object sender, FormClosedEventArgs e)
        {
            try
            {
                _cookiePollTimer.Stop();
                _cookiePollTimer.Dispose();
            }
            catch { }

            try
            {
                _webView.Dispose();
            }
            catch { }
        }
    }
}
