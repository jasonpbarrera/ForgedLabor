using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace SmallBusinessSite.Api
{
    public class LeadsFunction
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly HttpClient _httpClient;
        private readonly ILogger<LeadsFunction> _logger;

        public LeadsFunction(IHttpClientFactory httpClientFactory, ILogger<LeadsFunction> logger)
        {
            _httpClient = httpClientFactory.CreateClient();
            _logger = logger;
        }

        [Function("SubmitLead")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "leads")] HttpRequestData req)
        {
            LeadSubmissionDto? input;
            try
            {
                input = await JsonSerializer.DeserializeAsync<LeadSubmissionDto>(req.Body, JsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Invalid JSON payload on /api/leads");
                return req.CreateResponse(HttpStatusCode.BadRequest);
            }

            if (input == null)
            {
                return req.CreateResponse(HttpStatusCode.BadRequest);
            }

            // 1. Server-side honeypot check: silently return 200 OK if filled by a bot
            if (!string.IsNullOrWhiteSpace(input.Honeypot))
            {
                return req.CreateResponse(HttpStatusCode.OK);
            }

            // 2. Basic field validation
            if (string.IsNullOrWhiteSpace(input.Name) ||
                string.IsNullOrWhiteSpace(input.Email) ||
                string.IsNullOrWhiteSpace(input.Phone) ||
                string.IsNullOrWhiteSpace(input.Message))
            {
                var badResp = req.CreateResponse(HttpStatusCode.BadRequest);
                await badResp.WriteStringAsync("Name, Email, Phone, and Message are required.");
                return badResp;
            }

            // Resolve Azure Cosmos DB credentials (supports either COSMOS_CONNECTION_STRING or COSMOS_ENDPOINT + COSMOS_KEY)
            if (!TryGetCosmosCredentials(out string cosmosEndpoint, out string cosmosKey, out string databaseId))
            {
                _logger.LogError("Missing Azure Cosmos DB configuration (set COSMOS_CONNECTION_STRING or COSMOS_ENDPOINT + COSMOS_KEY).");
                return req.CreateResponse(HttpStatusCode.InternalServerError);
            }

            string businessId = string.IsNullOrWhiteSpace(input.BusinessId) ? "default" : input.BusinessId.Trim();

            // 3. Insert lead document into Azure Cosmos DB `leads` container (partitioned by /businessId)
            var leadDocument = new CosmosLeadDocument
            {
                Id = Guid.NewGuid().ToString(),
                BusinessId = businessId,
                CreatedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                Name = input.Name.Trim(),
                Email = input.Email.Trim(),
                Phone = input.Phone.Trim(),
                PreferredContactMethod = input.PreferredContactMethod?.Trim() ?? "",
                ServiceType = input.ServiceType?.Trim() ?? "",
                PreferredStartDate = string.IsNullOrWhiteSpace(input.PreferredStartDate) ? null : input.PreferredStartDate.Trim(),
                Message = input.Message.Trim(),
                Status = "new"
            };

            var (saved, statusCode, errorBody) = await CreateCosmosDocumentAsync(
                cosmosEndpoint,
                cosmosKey,
                databaseId,
                containerId: "leads",
                partitionKeyValue: businessId,
                document: leadDocument);

            if (!saved)
            {
                _logger.LogError("Cosmos DB lead insert failed ({StatusCode}): {Error}", statusCode, errorBody);
                return req.CreateResponse(HttpStatusCode.BadGateway);
            }

            // 4. Optional: Trigger instant Brevo transactional email notification to business owner
            // Security: Never trust client-supplied recipient addresses. Resolve from server env var or Cosmos DB `clients` container.
            string? brevoApiKey = Environment.GetEnvironmentVariable("BREVO_API_KEY");
            string? recipientEmail = Environment.GetEnvironmentVariable("LEAD_NOTIFICATION_EMAIL");
            string? senderEmail = Environment.GetEnvironmentVariable("BREVO_SENDER_EMAIL");

            if (string.IsNullOrWhiteSpace(brevoApiKey) || string.IsNullOrWhiteSpace(recipientEmail) || string.IsNullOrWhiteSpace(senderEmail))
            {
                if (TryGetCosmosCredentials(out string cEndpoint, out string cKey, out string cDbId))
                {
                    var clientDoc = await LookupClientRecordAsync(cEndpoint, cKey, cDbId, businessId);
                    if (clientDoc != null)
                    {
                        if (string.IsNullOrWhiteSpace(brevoApiKey))
                            brevoApiKey = clientDoc.BrevoApiKey;

                        if (string.IsNullOrWhiteSpace(recipientEmail))
                            recipientEmail = !string.IsNullOrWhiteSpace(clientDoc.LeadNotificationEmail) ? clientDoc.LeadNotificationEmail : clientDoc.PrimaryEmail;

                        if (string.IsNullOrWhiteSpace(senderEmail))
                            senderEmail = clientDoc.BrevoSenderEmail;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(senderEmail))
            {
                senderEmail = "info@jbdigitalservices.net";
            }

            if (!string.IsNullOrWhiteSpace(brevoApiKey) && !string.IsNullOrWhiteSpace(recipientEmail))
            {
                try
                {
                    await SendBrevoNotificationAsync(brevoApiKey, senderEmail, recipientEmail, input);
                }
                catch (Exception ex)
                {
                    // Log email error without failing the lead submission since the lead is safely stored in Cosmos DB
                    _logger.LogError(ex, "Brevo email notification failed after saving lead to Cosmos DB.");
                }
            }

            var okResponse = req.CreateResponse(HttpStatusCode.Created);
            await okResponse.WriteAsJsonAsync(new { status = "saved", id = leadDocument.Id });
            return okResponse;
        }

        private static bool TryGetCosmosCredentials(out string endpoint, out string key, out string databaseId)
        {
            endpoint = Environment.GetEnvironmentVariable("COSMOS_ENDPOINT")?.TrimEnd('/') ?? "";
            key = Environment.GetEnvironmentVariable("COSMOS_KEY") ?? "";
            databaseId = Environment.GetEnvironmentVariable("COSMOS_DATABASE_NAME") ?? "SmallBusinessPlatform";

            string connString = Environment.GetEnvironmentVariable("COSMOS_CONNECTION_STRING") ?? "";
            if (!string.IsNullOrWhiteSpace(connString))
            {
                foreach (var part in connString.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    int idx = part.IndexOf('=');
                    if (idx <= 0) continue;
                    string k = part[..idx].Trim();
                    string v = part[(idx + 1)..].Trim();
                    if (k.Equals("AccountEndpoint", StringComparison.OrdinalIgnoreCase))
                        endpoint = v.TrimEnd('/');
                    else if (k.Equals("AccountKey", StringComparison.OrdinalIgnoreCase))
                        key = v;
                }
            }

            return !string.IsNullOrWhiteSpace(endpoint) && !string.IsNullOrWhiteSpace(key);
        }

        private async Task<(bool Success, HttpStatusCode StatusCode, string ErrorBody)> CreateCosmosDocumentAsync<T>(
            string cosmosEndpoint,
            string cosmosKey,
            string databaseId,
            string containerId,
            string partitionKeyValue,
            T document)
        {
            string resourceLink = $"dbs/{databaseId}/colls/{containerId}";
            string requestUri = $"{cosmosEndpoint}/{resourceLink}/docs";
            string utcDate = DateTime.UtcNow.ToString("r", CultureInfo.InvariantCulture);
            string authHeader = GenerateCosmosAuthToken("post", "docs", resourceLink, utcDate, cosmosKey);

            using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
            request.Headers.Add("Authorization", authHeader);
            request.Headers.Add("x-ms-date", utcDate);
            request.Headers.Add("x-ms-version", "2018-12-31");
            request.Headers.Add("x-ms-documentdb-partitionkey", JsonSerializer.Serialize(new[] { partitionKeyValue }));
            request.Content = new StringContent(
                JsonSerializer.Serialize(document, JsonOptions),
                Encoding.UTF8,
                "application/json");

            var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                return (true, response.StatusCode, string.Empty);
            }

            string err = await response.Content.ReadAsStringAsync();
            return (false, response.StatusCode, err);
        }

        private async Task<CosmosClientRecord?> LookupClientRecordAsync(
            string cosmosEndpoint,
            string cosmosKey,
            string databaseId,
            string businessId)
        {
            // Point-read (1 RU) on `clients` container where id == businessId and partitionKey == businessId
            string resourceLink = $"dbs/{databaseId}/colls/clients/docs/{Uri.EscapeDataString(businessId)}";
            string requestUri = $"{cosmosEndpoint}/{resourceLink}";
            string utcDate = DateTime.UtcNow.ToString("r", CultureInfo.InvariantCulture);
            string authHeader = GenerateCosmosAuthToken("get", "docs", resourceLink, utcDate, cosmosKey);

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Add("Authorization", authHeader);
            request.Headers.Add("x-ms-date", utcDate);
            request.Headers.Add("x-ms-version", "2018-12-31");
            request.Headers.Add("x-ms-documentdb-partitionkey", JsonSerializer.Serialize(new[] { businessId }));

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var clientDoc = await response.Content.ReadFromJsonAsync<CosmosClientRecord>(JsonOptions);
            return clientDoc?.IsActive == true ? clientDoc : null;
        }

        private static string GenerateCosmosAuthToken(
            string verb,
            string resourceType,
            string resourceId,
            string utcDate,
            string masterKeyBase64)
        {
            byte[] keyBytes = Convert.FromBase64String(masterKeyBase64);
            string payload = $"{verb.ToLowerInvariant()}\n{resourceType.ToLowerInvariant()}\n{resourceId}\n{utcDate.ToLowerInvariant()}\n\n";
            using var hmac = new HMACSHA256(keyBytes);
            byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            string signature = Convert.ToBase64String(hash);
            return Uri.EscapeDataString($"type=master&ver=1.0&sig={signature}");
        }

        private async Task SendBrevoNotificationAsync(
            string brevoApiKey,
            string senderEmail,
            string recipientEmail,
            LeadSubmissionDto lead)
        {
            string company = string.IsNullOrWhiteSpace(lead.CompanyName) ? lead.BusinessId : lead.CompanyName;
            string htmlBody = $@"
                <h2>New Website Inquiry — {WebUtility.HtmlEncode(company)}</h2>
                <table style='border-collapse:collapse;font-family:sans-serif;'>
                    <tr><td style='padding:6px 12px;font-weight:bold;'>Name:</td><td style='padding:6px 12px;'>{WebUtility.HtmlEncode(lead.Name)}</td></tr>
                    <tr><td style='padding:6px 12px;font-weight:bold;'>Phone:</td><td style='padding:6px 12px;'>{WebUtility.HtmlEncode(lead.Phone)}</td></tr>
                    <tr><td style='padding:6px 12px;font-weight:bold;'>Email:</td><td style='padding:6px 12px;'>{WebUtility.HtmlEncode(lead.Email)}</td></tr>
                    <tr><td style='padding:6px 12px;font-weight:bold;'>Preferred Contact:</td><td style='padding:6px 12px;'>{WebUtility.HtmlEncode(lead.PreferredContactMethod)}</td></tr>
                    <tr><td style='padding:6px 12px;font-weight:bold;'>Service Needed:</td><td style='padding:6px 12px;'>{WebUtility.HtmlEncode(lead.ServiceType)}</td></tr>
                    <tr><td style='padding:6px 12px;font-weight:bold;'>Preferred Start Date:</td><td style='padding:6px 12px;'>{WebUtility.HtmlEncode(lead.PreferredStartDate ?? "N/A")}</td></tr>
                    <tr><td style='padding:6px 12px;font-weight:bold;'>Message:</td><td style='padding:6px 12px;'>{WebUtility.HtmlEncode(lead.Message)}</td></tr>
                </table>";

            var payload = new
            {
                sender = new { name = company, email = senderEmail },
                to = new[] { new { email = recipientEmail, name = company } },
                replyTo = new { email = lead.Email, name = lead.Name },
                subject = $"New Inquiry from {lead.Name} ({lead.ServiceType})",
                htmlContent = htmlBody
            };

            using var emailReq = new HttpRequestMessage(HttpMethod.Post, "https://api.brevo.com/v3/smtp/email");
            emailReq.Headers.Add("api-key", brevoApiKey);
            emailReq.Content = JsonContent.Create(payload);

            var emailResp = await _httpClient.SendAsync(emailReq);
            if (!emailResp.IsSuccessStatusCode)
            {
                var body = await emailResp.Content.ReadAsStringAsync();
                _logger.LogWarning("Brevo returned {Status}: {Body}", emailResp.StatusCode, body);
            }
        }
    }

    public class LeadSubmissionDto
    {
        public string BusinessId { get; set; } = string.Empty;
        public string CompanyName { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Phone { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string PreferredContactMethod { get; set; } = string.Empty;
        public string ServiceType { get; set; } = string.Empty;
        public string? PreferredStartDate { get; set; }
        public string Message { get; set; } = string.Empty;
        public string Honeypot { get; set; } = string.Empty;
    }

    public class CosmosClientRecord
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("businessId")]
        public string BusinessId { get; set; } = string.Empty;

        [JsonPropertyName("primaryEmail")]
        public string? PrimaryEmail { get; set; }

        [JsonPropertyName("leadNotificationEmail")]
        public string? LeadNotificationEmail { get; set; }

        [JsonPropertyName("brevoApiKey")]
        public string? BrevoApiKey { get; set; }

        [JsonPropertyName("brevoSenderEmail")]
        public string? BrevoSenderEmail { get; set; }

        [JsonPropertyName("isActive")]
        public bool IsActive { get; set; } = true;
    }

    public class CosmosLeadDocument
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("businessId")]
        public string BusinessId { get; set; } = string.Empty;

        [JsonPropertyName("createdAt")]
        public string CreatedAt { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("email")]
        public string Email { get; set; } = string.Empty;

        [JsonPropertyName("phone")]
        public string Phone { get; set; } = string.Empty;

        [JsonPropertyName("preferredContactMethod")]
        public string PreferredContactMethod { get; set; } = string.Empty;

        [JsonPropertyName("serviceType")]
        public string ServiceType { get; set; } = string.Empty;

        [JsonPropertyName("preferredStartDate")]
        public string? PreferredStartDate { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; set; } = "new";
    }
}
