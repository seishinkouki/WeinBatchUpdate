using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WeinBatchUpdate.Services;

namespace WeinBatchUpdate
{
    /// <summary>
    /// Handles the full firmware-update workflow for a single Weintek HMI device.
    ///
    /// The update pipeline consists of the following sequential stages:
    /// <list type="number">
    ///   <item>Fetch RSA public key from the device</item>
    ///   <item>Authenticate with encrypted credentials</item>
    ///   <item>Reset the project environment</item>
    ///   <item>Upload the firmware file (<c>.exob</c>) via multipart POST</item>
    ///   <item>Trigger server-side decompression</item>
    ///   <item>Reboot the device</item>
    ///   <item>Restore device services</item>
    ///   <item>Verify the device state</item>
    /// </list>
    ///
    /// Progress is reported through the <see cref="IProgress{ProgressReport}"/> interface
    /// so the UI can display status text and a progress bar.
    ///
    /// If <see cref="SimulationMode"/> is enabled, network calls are replaced with
    /// artificial delays for testing.
    /// </summary>
    public class HMIUpdater
    {
        // ── Private fields ──────────────────────────────────────────────────────
        private readonly HttpClient _client;
        private readonly string _baseUrl;
        private readonly IProgress<ProgressReport>? _progress;

        /// <summary>
        /// Bearer token obtained after successful login. Sent with every
        /// authenticated request via the <c>Authorization</c> header.
        /// </summary>
        private string? _accessToken;

        // ── Public properties ────────────────────────────────────────────────────

        /// <summary>
        /// When <c>true</c>, network requests are skipped and replaced with
        /// simulated delays. Useful for UI testing without real hardware.
        /// </summary>
        public bool SimulationMode { get; set; }

        // ── Nested types ────────────────────────────────────────────────────────

        /// <summary>
        /// Lightweight DTO for reporting progress through
        /// <see cref="IProgress{T}"/>.
        /// </summary>
        public class ProgressReport
        {
            /// <summary>Progress value between 0.0 and 1.0, or -1 on error.</summary>
            public double Value { get; set; }

            /// <summary>Localization key for the current step description.</summary>
            public string? Status { get; set; }

            /// <summary>Optional detail message (e.g. error description).</summary>
            public string? Message { get; set; }
        }

        // ── Constructor ─────────────────────────────────────────────────────────

        /// <summary>
        /// Creates a new updater for the target device.
        /// </summary>
        /// <param name="client">Shared <see cref="HttpClient"/> instance.</param>
        /// <param name="targetIp">Device IP address (optionally prefixed with <c>http://</c>).</param>
        /// <param name="progress">Optional progress callback.</param>
        public HMIUpdater(HttpClient client, string? targetIp, IProgress<ProgressReport>? progress = null)
        {
            if (targetIp == null) throw new ArgumentNullException(nameof(targetIp));
            _client = client;
            // Support both raw IP addresses and full URLs
            _baseUrl = targetIp.StartsWith("http") ? targetIp : $"http://{targetIp}";
            _progress = progress;
        }

        // ── Private helpers ──────────────────────────────────────────────────────

        /// <summary>
        /// Reports progress to the UI thread via <see cref="IProgress{T}"/>.
        /// </summary>
        /// <param name="value">Progress value (0-1) or -1 on error.</param>
        /// <param name="status">Localization key for the current step.</param>
        /// <param name="message">Optional detail message.</param>
        private void Report(double value, string status, string? message = null)
        {
            _progress?.Report(new ProgressReport { Value = value, Status = status, Message = message });
        }

        // ── Public API ───────────────────────────────────────────────────────────

        /// <summary>
        /// Executes the full firmware-update pipeline for a single device.
        /// </summary>
        /// <param name="user">Admin username for the HMI web interface.</param>
        /// <param name="pass">Admin password.</param>
        /// <param name="localPath">Local path to the firmware file (<c>.exob</c>).</param>
        /// <returns><c>true</c> if all stages completed successfully; <c>false</c> otherwise.</returns>
        public async Task<bool> ExecuteFullUpdateAsync(string? user, string? pass, string? localPath)
        {
            // Validate required arguments
            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(pass) || string.IsNullOrEmpty(localPath))
            {
                Report(-1, "ParameterError");
                return false;
            }
            try
            {
                // Stage 1: Obtain the RSA public key used for credential encryption
                Report(0.05, "GetPublicKey");
                string pubKey = await GetPublicKeyAsync();

                // Stage 2: Authenticate with RSA-encrypted credentials
                Report(0.1, "LoginDevice");
                await LoginAsync(user, pass, pubKey);

                // Stage 3: Reset the project environment on the device
                Report(0.2, "PrepareEnv");
                await SendRequestWithTimeoutAsync(HttpMethod.Get, "/cgi/reset_pj.cgi?resetsele=6", null, TimeSpan.FromSeconds(20));

                // Stage 4: Upload firmware binary to the device
                Report(0.3, "UploadFirmware");
                await UploadAsync(localPath);

                // Stage 5: Trigger file decompression on the server
                Report(0.7, "DecompressFirmware");
                await SendRequestWithTimeoutAsync(HttpMethod.Post, "/api/v1/project/management/decompression", null, TimeSpan.FromMinutes(5));

                // Stage 6: Reboot the device to apply the new firmware
                Report(0.85, "RestartDevice");
                await SendRequestWithTimeoutAsync(HttpMethod.Get, "/cgi/reset_pj.cgi?resetsele=1", null, TimeSpan.FromSeconds(20));

                // Stage 7: Restart device services post-reboot
                Report(0.9, "RestoreService");
                await SendRequestWithTimeoutAsync(HttpMethod.Get, "/cgi/reset_pj.cgi?resetsele=15", null, TimeSpan.FromSeconds(20));

                // Stage 8: Final health check
                Report(0.95, "CheckStatus");
                await SendRequestWithTimeoutAsync(HttpMethod.Get, "/cgi/reset_pj.cgi?resetsele=19", null, TimeSpan.FromSeconds(20));

                Report(1.0, "Complete");
                return true;
            }
            catch (OperationCanceledException)
            {
                // Timeout during one of the HTTP operations
                Report(-1, "Error", LocalizationService.Instance["ConnectionTimeout"]);
                return false;
            }
            catch (Exception ex)
            {
                // All other errors (network, server, parsing, etc.)
                Report(-1, "Error", ex.Message);
                return false;
            }
        }

        // ── HTTP operations ──────────────────────────────────────────────────────

        /// <summary>
        /// Fetches the RSA public key from the device, which is needed to encrypt
        /// credentials during login.
        /// </summary>
        private async Task<string> GetPublicKeyAsync()
        {
            if (SimulationMode) { await Task.Delay(800); return ""; }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var response = await _client.GetAsync($"{_baseUrl}/api/v1/system/rsa/public-key", cts.Token);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();

            // The response JSON contains a "public_key" field
            using JsonDocument doc = JsonDocument.Parse(content);
            if (!doc.RootElement.TryGetProperty("public_key", out JsonElement property))
            {
                throw new Exception(LocalizationService.Instance["InvalidPublicKeyResponse"]);
            }
            return property.GetString() ?? "";
        }

        /// <summary>
        /// Authenticates with the device using RSA-encrypted credentials.
        /// On success, stores the access token in <see cref="_accessToken"/>.
        /// </summary>
        private async Task LoginAsync(string? user, string? pass, string? pemKey)
        {
            if (SimulationMode) { await Task.Delay(600); return; }
            if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass) || string.IsNullOrEmpty(pemKey))
                throw new ArgumentException(LocalizationService.Instance["CredentialsEmpty"]);

            // Build OAuth2 password-grant request with RSA-encrypted fields
            var dict = new Dictionary<string, string>
            {
                { "grant_type", "password" },
                { "username", EncryptRsa(user, pemKey) },
                { "password", EncryptRsa(pass, pemKey) }
            };
            var content = new FormUrlEncodedContent(dict);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var response = await _client.PostAsync($"{_baseUrl}/api/v1/auth/rsa/token/admin", content, cts.Token);
            response.EnsureSuccessStatusCode();
            var respContent = await response.Content.ReadAsStringAsync();

            // Extract the bearer token from the JSON response
            using JsonDocument doc = JsonDocument.Parse(respContent);
            if (!doc.RootElement.TryGetProperty("access_token", out JsonElement property))
            {
                _accessToken = "";
            }
            _accessToken = property.GetString();
        }

        /// <summary>
        /// Uploads a firmware file via multipart form-data POST.
        /// </summary>
        private async Task UploadAsync(string? filePath)
        {
            if (SimulationMode) { await Task.Delay(2000); return; }
            if (string.IsNullOrEmpty(filePath)) throw new ArgumentException(LocalizationService.Instance["FilePathEmpty"], nameof(filePath));
            if (!File.Exists(filePath)) throw new FileNotFoundException(LocalizationService.Instance["FirmwareNotExist"], filePath);

            using var content = new MultipartFormDataContent();
            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            var fileContent = new StreamContent(fileStream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(fileContent, "file", Path.GetFileName(filePath));

            await SendRequestWithTimeoutAsync(HttpMethod.Post, "/api/v1/project/management/update", content, TimeSpan.FromMinutes(15));
        }

        /// <summary>
        /// Sends an HTTP request with a configurable timeout.
        /// If an access token is available, it is attached as a Bearer token.
        /// </summary>
        /// <param name="method">HTTP method.</param>
        /// <param name="relativeUrl">API endpoint relative to the device base URL.</param>
        /// <param name="content">Optional request body.</param>
        /// <param name="timeout">Timeout duration (defaults to 30 seconds).</param>
        /// <returns>The response body as a string.</returns>
        private async Task<string> SendRequestWithTimeoutAsync(HttpMethod method, string relativeUrl, HttpContent? content = null, TimeSpan? timeout = null)
        {
            if (SimulationMode) { await Task.Delay(500); return "{}"; }

            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
            var request = new HttpRequestMessage(method, $"{_baseUrl}{relativeUrl}");

            // Assign body content (POST requires a non-null body)
            if (content != null) request.Content = content;
            else if (method == HttpMethod.Post) request.Content = new ByteArrayContent(new byte[0]);

            // Attach bearer token if authenticated
            if (!string.IsNullOrEmpty(_accessToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            }

            var response = await _client.SendAsync(request, cts.Token);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadAsStringAsync();

            // Check for CGI-level errors in the response body
            if (relativeUrl.Contains(".cgi") && result.Contains("error", StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception(string.Format(LocalizationService.Instance["DeviceCommandError"], result));
            }
            return result;
        }

        // ── RSA encryption ──────────────────────────────────────────────────────

        /// <summary>
        /// Encrypts a UTF-8 string using RSA PKCS#1 v1.5 padding.
        /// Handles both PEM and raw DER public-key formats.
        /// </summary>
        /// <param name="text">Plaintext to encrypt.</param>
        /// <param name="pem">Public key in PEM format (with or without header/footer lines).</param>
        /// <returns>Base64-encoded ciphertext.</returns>
        private string EncryptRsa(string? text, string? pem)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(pem))
                throw new ArgumentException(LocalizationService.Instance["TextOrKeyEmpty"]);

            using var rsa = RSA.Create();

            try
            {
                // Attempt to import a standard PEM key
                rsa.ImportFromPem(pem);
            }
            catch
            {
                // If ImportFromPem fails, manually strip PEM headers and try raw DER formats
                var cleanPem = Regex.Replace(pem, @"-+[A-Z ]+-+", "");
                var base64 = Regex.Replace(cleanPem, @"\s", "");
                var der = Convert.FromBase64String(base64);

                try
                {
                    // Try PKCS#1 (SubjectPublicKeyInfo) format
                    rsa.ImportRSAPublicKey(der, out _);
                }
                catch
                {
                    // Fall back to raw RSAPublicKey format
                    rsa.ImportSubjectPublicKeyInfo(der, out _);
                }
            }

            var data = Encoding.UTF8.GetBytes(text);
            var encrypted = rsa.Encrypt(data, RSAEncryptionPadding.Pkcs1);
            return Convert.ToBase64String(encrypted);
        }
    }
}
