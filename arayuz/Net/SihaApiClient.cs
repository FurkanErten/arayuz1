// SihaApiClient.cs
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace arayuz_deneme_1.Net
{
    public sealed class SihaApiClient : IDisposable
    {
        private readonly HttpClient _http;

        private static readonly JsonSerializerOptions _jsonOpt = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };

        public string BaseUrl { get; }
        public string? Token { get; private set; }

        public SihaApiClient(string host, int port)
        {
            BaseUrl = $"http://{host}:{port}";
            _http = new HttpClient
            {
                BaseAddress = new Uri(BaseUrl),
                Timeout = TimeSpan.FromSeconds(6)
            };
        }

        public async Task<string> HealthAsync()
        {
            var r = await _http.GetAsync("/api/health");
            r.EnsureSuccessStatusCode();
            return await r.Content.ReadAsStringAsync();
        }

        public async Task<LoginResult> LoginAsync(string kadi, string sifre)
        {
            var body = new { kadi, sifre };
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/giris");
            req.Content = new StringContent(JsonSerializer.Serialize(body, _jsonOpt), Encoding.UTF8, "application/json");

            var r = await _http.SendAsync(req);
            var json = await r.Content.ReadAsStringAsync();

            if (!r.IsSuccessStatusCode)
                throw new Exception($"Login fail ({(int)r.StatusCode}): {json}");

            var doc = JsonDocument.Parse(json);
            int team = doc.RootElement.GetProperty("takim_numarasi").GetInt32();
            string token = doc.RootElement.GetProperty("token").GetString() ?? "";

            Token = token;
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            return new LoginResult { TeamNo = team, Token = token };
        }

        public async Task<TelemetryResponse?> SendTelemetryAsync(TelemetryPayload payload, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(Token))
                throw new Exception("Token yok. Önce login.");

            var req = new HttpRequestMessage(HttpMethod.Post, "/api/telemetri_gonder");
            req.Content = new StringContent(JsonSerializer.Serialize(payload, _jsonOpt), Encoding.UTF8, "application/json");

            var r = await _http.SendAsync(req, ct);
            var json = await r.Content.ReadAsStringAsync(ct);

            if (!r.IsSuccessStatusCode)
                throw new Exception($"telemetri_gonder fail ({(int)r.StatusCode}): {json}");

            return JsonSerializer.Deserialize<TelemetryResponse>(json, _jsonOpt);
        }

        // ✅ EKLENDİ: "serverdan telemetriyi al" gibi kullanmak için.
        // Not: API'nizde telemetri bilgisi, telemetri_gonder yanıtında dönüyor.
        public Task<TelemetryResponse?> GetTelemetryAsync(TelemetryPayload selfPayload, CancellationToken ct)
            => SendTelemetryAsync(selfPayload, ct);

        public async Task<QrCoord?> GetQrAsync(CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(Token))
                throw new Exception("Token yok. Önce login.");

            var r = await _http.GetAsync("/api/qr_koordinati", ct);
            var json = await r.Content.ReadAsStringAsync(ct);

            if (!r.IsSuccessStatusCode)
                throw new Exception($"qr_koordinati fail ({(int)r.StatusCode}): {json}");

            return JsonSerializer.Deserialize<QrCoord>(json, _jsonOpt);
        }

        public async Task<HssResponse?> GetHssAsync(CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(Token))
                throw new Exception("Token yok. Önce login.");

            var r = await _http.GetAsync("/api/hss_koordinatlari", ct);
            var json = await r.Content.ReadAsStringAsync(ct);

            if (!r.IsSuccessStatusCode)
                throw new Exception($"hss_koordinatlari fail ({(int)r.StatusCode}): {json}");

            return JsonSerializer.Deserialize<HssResponse>(json, _jsonOpt);
        }

        public void Dispose() => _http.Dispose();
    }

    public sealed class LoginResult
    {
        public int TeamNo { get; set; }
        public string Token { get; set; } = "";
    }

    public sealed class GpsSaati
    {
        public int saat { get; set; }
        public int dakika { get; set; }
        public int saniye { get; set; }
        public int milisaniye { get; set; }
    }

    public sealed class TelemetryPayload
    {
        public int takim_numarasi { get; set; }
        public double iha_enlem { get; set; }
        public double iha_boylam { get; set; }
        public double iha_irtifa { get; set; }
        public double iha_dikilme { get; set; }
        public double iha_yonelme { get; set; }
        public double iha_yatis { get; set; }
        public double iha_hiz { get; set; }
        public double iha_batarya { get; set; }
        public int iha_otonom { get; set; }
        public int iha_kilitlenme { get; set; }
        public double hedef_merkez_X { get; set; }
        public double hedef_merkez_Y { get; set; }
        public double hedef_genislik { get; set; }
        public double hedef_yukseklik { get; set; }
        public GpsSaati gps_saati { get; set; } = new GpsSaati();
    }

    public sealed class TelemetryResponse
    {
        public SunucuSaati? sunucusaati { get; set; }
        public KonumBilgisi[]? konumBilgileri { get; set; }
    }

    public sealed class SunucuSaati
    {
        public int gun { get; set; }
        public int saat { get; set; }
        public int dakika { get; set; }
        public int saniye { get; set; }
        public int milisaniye { get; set; }
    }

    public sealed class KonumBilgisi
    {
        public int takim_numarasi { get; set; }
        public double iha_enlem { get; set; }
        public double iha_boylam { get; set; }
        public double iha_irtifa { get; set; }
        public double iha_dikilme { get; set; }
        public double iha_yonelme { get; set; }
        public double iha_yatis { get; set; }
        public double iha_hizi { get; set; }
        public int zaman_farki { get; set; }
    }

    public sealed class QrCoord
    {
        [JsonPropertyName("qrEnlem")] public double qrEnlem { get; set; }
        [JsonPropertyName("qrBoylam")] public double qrBoylam { get; set; }
    }

    public sealed class HssResponse
    {
        [JsonPropertyName("sunucusaati")]
        public SunucuSaati? sunucusaati { get; set; }

        [JsonPropertyName("hss_koordinat_bilgileri")]
        public HssCoord[]? hss_koordinat_bilgileri { get; set; }
    }

    public sealed class HssCoord
    {
        [JsonPropertyName("id")] public int id { get; set; }
        [JsonPropertyName("hssEnlem")] public double hssEnlem { get; set; }
        [JsonPropertyName("hssBoylam")] public double hssBoylam { get; set; }
        [JsonPropertyName("hssYaricap")] public double hssYaricap { get; set; }
    }
}
