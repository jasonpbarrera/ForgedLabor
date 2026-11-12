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
    public class ScheduleFunction
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly HttpClient _httpClient;
        private readonly ILogger<ScheduleFunction> _logger;

        public ScheduleFunction(IHttpClientFactory httpClientFactory, ILogger<ScheduleFunction> logger)
        {
            _httpClient = httpClientFactory.CreateClient();
            _logger = logger;
        }

        // 1. Public Availability Endpoint (No PII returned)
        [Function("GetPublicSchedule")]
        public async Task<HttpResponseData> GetPublicSchedule(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "schedule")] HttpRequestData req)
        {
            string businessId = GetQueryParam(req.Url, "businessId") ?? "forged-labor";

            if (!TryGetCosmosCredentials(out string endpoint, out string key, out string databaseId))
            {
                var fallbackResp = req.CreateResponse(HttpStatusCode.OK);
                await fallbackResp.WriteAsJsonAsync(new
                {
                    configured = false,
                    stripeConfigured = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("STRIPE_SECRET_KEY")),
                    bookedSlots = Array.Empty<PublicSlotDto>()
                });
                return fallbackResp;
            }

            var docs = await QueryScheduleDocumentsAsync(endpoint, key, databaseId, businessId);
            var activeSlots = docs
                .Where(d => !string.Equals(d.Status, "cancelled", StringComparison.OrdinalIgnoreCase))
                .Select(d => new PublicSlotDto
                {
                    Date = d.Date,
                    TimeSlot = d.TimeSlot,
                    Status = d.Status,
                    CrewSize = d.CrewSize
                })
                .ToList();

            var resp = req.CreateResponse(HttpStatusCode.OK);
            await resp.WriteAsJsonAsync(new
            {
                configured = true,
                stripeConfigured = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("STRIPE_SECRET_KEY")),
                bookedSlots = activeSlots
            });
            return resp;
        }

        // 2. Public Booking Submission + Optional Stripe Checkout Deposit
        [Function("CreateBooking")]
        public async Task<HttpResponseData> CreateBooking(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "bookings")] HttpRequestData req)
        {
            BookingRequestDto? input;
            try
            {
                input = await JsonSerializer.DeserializeAsync<BookingRequestDto>(req.Body, JsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Invalid JSON payload on /api/bookings");
                return req.CreateResponse(HttpStatusCode.BadRequest);
            }

            if (input == null)
            {
                return req.CreateResponse(HttpStatusCode.BadRequest);
            }

            // Honeypot check
            if (!string.IsNullOrWhiteSpace(input.Honeypot))
            {
                return req.CreateResponse(HttpStatusCode.OK);
            }

            // Validate required fields
            if (string.IsNullOrWhiteSpace(input.Name) ||
                string.IsNullOrWhiteSpace(input.Phone) ||
                string.IsNullOrWhiteSpace(input.Email) ||
                string.IsNullOrWhiteSpace(input.Date) ||
                string.IsNullOrWhiteSpace(input.TimeSlot) ||
                string.IsNullOrWhiteSpace(input.ServiceType))
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteStringAsync("Name, Phone, Email, Service Type, Date, and Time Slot are required.");
                return bad;
            }

            if (!TryGetCosmosCredentials(out string endpoint, out string key, out string databaseId))
            {
                _logger.LogError("Missing Azure Cosmos DB configuration.");
                return req.CreateResponse(HttpStatusCode.InternalServerError);
            }

            string businessId = string.IsNullOrWhiteSpace(input.BusinessId) ? "forged-labor" : input.BusinessId.Trim();
            int crewSize = Math.Clamp(input.CrewSize, 0, 5);
            bool isConsultation = input.ServiceType.Contains("Consultation", StringComparison.OrdinalIgnoreCase);

            // Check for double-booking conflict
            var existingDocs = await QueryScheduleDocumentsAsync(endpoint, key, databaseId, businessId);
            bool conflict = existingDocs.Any(d =>
                !string.Equals(d.Status, "cancelled", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(d.Date, input.Date.Trim(), StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(d.TimeSlot, "Full Day", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(input.TimeSlot.Trim(), "Full Day", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(d.TimeSlot, input.TimeSlot.Trim(), StringComparison.OrdinalIgnoreCase)));

            if (conflict)
            {
                var conflictResp = req.CreateResponse(HttpStatusCode.Conflict);
                await conflictResp.WriteAsJsonAsync(new
                {
                    error = "That date and time window was just reserved. Please select another open slot."
                });
                return conflictResp;
            }

            // Server-side deposit calculation (in cents)
            int depositCents = isConsultation ? 0 : (crewSize >= 4 ? 25000 : 15000);
            decimal depositDollars = depositCents / 100m;

            var bookingDoc = new CosmosScheduleDocument
            {
                Id = Guid.NewGuid().ToString(),
                BusinessId = businessId,
                DocType = "booking",
                CreatedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                Date = input.Date.Trim(),
                TimeSlot = input.TimeSlot.Trim(),
                ServiceType = input.ServiceType.Trim(),
                CrewSize = isConsultation ? 0 : Math.Max(2, crewSize),
                Name = input.Name.Trim(),
                Phone = input.Phone.Trim(),
                Email = input.Email.Trim(),
                OriginAddress = input.OriginAddress?.Trim() ?? "",
                DestinationAddress = input.DestinationAddress?.Trim() ?? "",
                Notes = input.Notes?.Trim() ?? "",
                Source = "Website",
                DepositCents = depositCents,
                DepositPaid = false,
                Status = depositCents == 0 ? "confirmed" : "pending_deposit"
            };

            var (saved, statusCode, errorBody) = await CreateCosmosDocumentAsync(
                endpoint, key, databaseId, "leads", businessId, bookingDoc);

            if (!saved)
            {
                _logger.LogError("Cosmos DB booking insert failed ({StatusCode}): {Error}", statusCode, errorBody);
                return req.CreateResponse(HttpStatusCode.BadGateway);
            }

            // Send instant Brevo notification
            await TrySendBookingNotificationAsync(bookingDoc);

            // If deposit required and Stripe is configured, create a Stripe Checkout Session
            string? stripeSecretKey = Environment.GetEnvironmentVariable("STRIPE_SECRET_KEY");
            if (depositCents > 0 && !string.IsNullOrWhiteSpace(stripeSecretKey))
            {
                string returnBase = string.IsNullOrWhiteSpace(input.ReturnUrlBase)
                    ? "https://forgedlabor.com/appointments"
                    : input.ReturnUrlBase.TrimEnd('/');

                var (checkoutOk, checkoutUrl, sessionId) = await CreateStripeCheckoutSessionAsync(
                    stripeSecretKey,
                    bookingDoc,
                    returnBase);

                if (checkoutOk && !string.IsNullOrWhiteSpace(checkoutUrl))
                {
                    bookingDoc.StripeSessionId = sessionId;
                    await ReplaceCosmosDocumentAsync(endpoint, key, databaseId, "leads", businessId, bookingDoc.Id, bookingDoc);

                    var checkoutResp = req.CreateResponse(HttpStatusCode.Created);
                    await checkoutResp.WriteAsJsonAsync(new
                    {
                        status = "checkout_required",
                        bookingId = bookingDoc.Id,
                        checkoutUrl,
                        depositAmount = depositDollars,
                        stripeConfigured = true
                    });
                    return checkoutResp;
                }
            }

            var okResp = req.CreateResponse(HttpStatusCode.Created);
            await okResp.WriteAsJsonAsync(new
            {
                status = bookingDoc.Status,
                bookingId = bookingDoc.Id,
                depositAmount = depositDollars,
                stripeConfigured = !string.IsNullOrWhiteSpace(stripeSecretKey)
            });
            return okResp;
        }

        // 3. Verify Stripe Checkout Session on Return
        [Function("VerifyBookingPayment")]
        public async Task<HttpResponseData> VerifyBookingPayment(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "bookings/verify")] HttpRequestData req)
        {
            VerifyPaymentDto? input;
            try
            {
                input = await JsonSerializer.DeserializeAsync<VerifyPaymentDto>(req.Body, JsonOptions);
            }
            catch
            {
                return req.CreateResponse(HttpStatusCode.BadRequest);
            }

            if (input == null || string.IsNullOrWhiteSpace(input.BookingId) || string.IsNullOrWhiteSpace(input.SessionId))
            {
                return req.CreateResponse(HttpStatusCode.BadRequest);
            }

            string? stripeSecretKey = Environment.GetEnvironmentVariable("STRIPE_SECRET_KEY");
            if (string.IsNullOrWhiteSpace(stripeSecretKey) ||
                !TryGetCosmosCredentials(out string endpoint, out string key, out string databaseId))
            {
                return req.CreateResponse(HttpStatusCode.InternalServerError);
            }

            string businessId = string.IsNullOrWhiteSpace(input.BusinessId) ? "forged-labor" : input.BusinessId.Trim();

            using var stripeReq = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.stripe.com/v1/checkout/sessions/{Uri.EscapeDataString(input.SessionId)}");
            stripeReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", stripeSecretKey);

            var stripeResp = await _httpClient.SendAsync(stripeReq);
            if (!stripeResp.IsSuccessStatusCode)
            {
                return req.CreateResponse(HttpStatusCode.BadRequest);
            }

            using var stripeJson = await JsonDocument.ParseAsync(await stripeResp.Content.ReadAsStreamAsync());
            string paymentStatus = stripeJson.RootElement.TryGetProperty("payment_status", out var ps)
                ? ps.GetString() ?? ""
                : "";

            if (!string.Equals(paymentStatus, "paid", StringComparison.OrdinalIgnoreCase))
            {
                var unpaidResp = req.CreateResponse(HttpStatusCode.OK);
                await unpaidResp.WriteAsJsonAsync(new { verified = false, paymentStatus });
                return unpaidResp;
            }

            var doc = await GetCosmosDocumentAsync<CosmosScheduleDocument>(
                endpoint, key, databaseId, "leads", businessId, input.BookingId);

            if (doc == null)
            {
                return req.CreateResponse(HttpStatusCode.NotFound);
            }

            doc.DepositPaid = true;
            doc.Status = "confirmed";
            doc.StripeSessionId = input.SessionId;

            await ReplaceCosmosDocumentAsync(endpoint, key, databaseId, "leads", businessId, doc.Id, doc);

            var okResp = req.CreateResponse(HttpStatusCode.OK);
            await okResp.WriteAsJsonAsync(new
            {
                verified = true,
                status = doc.Status,
                date = doc.Date,
                timeSlot = doc.TimeSlot,
                crewSize = doc.CrewSize,
                serviceType = doc.ServiceType
            });
            return okResp;
        }

        // 4. Admin: List All Bookings & Manual Blocks
        [Function("AdminGetSchedule")]
        public async Task<HttpResponseData> AdminGetSchedule(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "admin/schedule")] HttpRequestData req)
        {
            if (!IsAdminAuthorized(req))
            {
                return req.CreateResponse(HttpStatusCode.Unauthorized);
            }

            string businessId = GetQueryParam(req.Url, "businessId") ?? "forged-labor";
            if (!TryGetCosmosCredentials(out string endpoint, out string key, out string databaseId))
            {
                return req.CreateResponse(HttpStatusCode.InternalServerError);
            }

            var docs = await QueryScheduleDocumentsAsync(endpoint, key, databaseId, businessId);
            var ordered = docs
                .OrderBy(d => d.Date)
                .ThenBy(d => d.TimeSlot)
                .ToList();

            var resp = req.CreateResponse(HttpStatusCode.OK);
            await resp.WriteAsJsonAsync(ordered);
            return resp;
        }

        // 5. Admin: Block Date/Time Slot for Independent Bookings (Phone, MovingHelper, Personal)
        [Function("AdminBlockSlot")]
        public async Task<HttpResponseData> AdminBlockSlot(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "admin/schedule/block")] HttpRequestData req)
        {
            if (!IsAdminAuthorized(req))
            {
                return req.CreateResponse(HttpStatusCode.Unauthorized);
            }

            AdminBlockDto? input;
            try
            {
                input = await JsonSerializer.DeserializeAsync<AdminBlockDto>(req.Body, JsonOptions);
            }
            catch
            {
                return req.CreateResponse(HttpStatusCode.BadRequest);
            }

            if (input == null || string.IsNullOrWhiteSpace(input.Date) || string.IsNullOrWhiteSpace(input.TimeSlot))
            {
                return req.CreateResponse(HttpStatusCode.BadRequest);
            }

            if (!TryGetCosmosCredentials(out string endpoint, out string key, out string databaseId))
            {
                return req.CreateResponse(HttpStatusCode.InternalServerError);
            }

            string businessId = string.IsNullOrWhiteSpace(input.BusinessId) ? "forged-labor" : input.BusinessId.Trim();
            var blockDoc = new CosmosScheduleDocument
            {
                Id = Guid.NewGuid().ToString(),
                BusinessId = businessId,
                DocType = "block",
                CreatedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                Date = input.Date.Trim(),
                TimeSlot = input.TimeSlot.Trim(),
                ServiceType = string.IsNullOrWhiteSpace(input.Source) ? "Independent Booking" : input.Source.Trim(),
                CrewSize = Math.Clamp(input.CrewSize, 2, 5),
                Name = string.IsNullOrWhiteSpace(input.CustomerName) ? "Blocked by Dispatch" : input.CustomerName.Trim(),
                Notes = input.Notes?.Trim() ?? "",
                Source = string.IsNullOrWhiteSpace(input.Source) ? "Phone / MovingHelper" : input.Source.Trim(),
                Status = "blocked"
            };

            var (saved, statusCode, errorBody) = await CreateCosmosDocumentAsync(
                endpoint, key, databaseId, "leads", businessId, blockDoc);

            if (!saved)
            {
                _logger.LogError("Failed to create schedule block ({Status}): {Error}", statusCode, errorBody);
                return req.CreateResponse(HttpStatusCode.BadGateway);
            }

            var resp = req.CreateResponse(HttpStatusCode.Created);
            await resp.WriteAsJsonAsync(blockDoc);
            return resp;
        }

        // 6. Admin: Cancel / Release a Booking or Block
        [Function("AdminCancelSlot")]
        public async Task<HttpResponseData> AdminCancelSlot(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "admin/schedule/cancel")] HttpRequestData req)
        {
            if (!IsAdminAuthorized(req))
            {
                return req.CreateResponse(HttpStatusCode.Unauthorized);
            }

            AdminCancelDto? input;
            try
            {
                input = await JsonSerializer.DeserializeAsync<AdminCancelDto>(req.Body, JsonOptions);
            }
            catch
            {
                return req.CreateResponse(HttpStatusCode.BadRequest);
            }

            if (input == null || string.IsNullOrWhiteSpace(input.Id))
            {
                return req.CreateResponse(HttpStatusCode.BadRequest);
            }

            if (!TryGetCosmosCredentials(out string endpoint, out string key, out string databaseId))
            {
                return req.CreateResponse(HttpStatusCode.InternalServerError);
            }

            string businessId = string.IsNullOrWhiteSpace(input.BusinessId) ? "forged-labor" : input.BusinessId.Trim();
            var doc = await GetCosmosDocumentAsync<CosmosScheduleDocument>(
                endpoint, key, databaseId, "leads", businessId, input.Id);

            if (doc == null)
            {
                return req.CreateResponse(HttpStatusCode.NotFound);
            }

            doc.Status = "cancelled";
            await ReplaceCosmosDocumentAsync(endpoint, key, databaseId, "leads", businessId, doc.Id, doc);

            var resp = req.CreateResponse(HttpStatusCode.OK);
            await resp.WriteAsJsonAsync(new { cancelled = true, id = doc.Id });
            return resp;
        }

        private static bool IsAdminAuthorized(HttpRequestData req)
        {
            string expectedPin = Environment.GetEnvironmentVariable("ADMIN_SCHEDULE_PIN") ?? "4254";
            if (!req.Headers.TryGetValues("X-Admin-Pin", out var values))
            {
                return false;
            }
            string provided = values.FirstOrDefault() ?? "";
            return string.Equals(provided.Trim(), expectedPin.Trim(), StringComparison.Ordinal);
        }

        private static string? GetQueryParam(Uri url, string key)
        {
            var query = url.Query.TrimStart('?');
            if (string.IsNullOrEmpty(query)) return null;
            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length == 2 && string.Equals(Uri.UnescapeDataString(parts[0]), key, StringComparison.OrdinalIgnoreCase))
                {
                    return Uri.UnescapeDataString(parts[1]);
                }
            }
            return null;
        }

        private async Task<(bool Ok, string? Url, string? SessionId)> CreateStripeCheckoutSessionAsync(
            string stripeSecretKey,
            CosmosScheduleDocument booking,
            string returnBase)
        {
            string separator = returnBase.Contains('?') ? "&" : "?";
            string successUrl = $"{returnBase}{separator}payment=success&bookingId={Uri.EscapeDataString(booking.Id)}&session_id={{CHECKOUT_SESSION_ID}}";
            string cancelUrl = $"{returnBase}{separator}payment=cancelled&bookingId={Uri.EscapeDataString(booking.Id)}";

            var formFields = new Dictionary<string, string>
            {
                ["mode"] = "payment",
                ["success_url"] = successUrl,
                ["cancel_url"] = cancelUrl,
                ["customer_email"] = booking.Email,
                ["line_items[0][price_data][currency]"] = "usd",
                ["line_items[0][price_data][unit_amount]"] = booking.DepositCents.ToString(CultureInfo.InvariantCulture),
                ["line_items[0][price_data][product_data][name]"] = $"Forged Labor Move Deposit ({booking.CrewSize} Movers)",
                ["line_items[0][price_data][product_data][description]"] = $"{booking.ServiceType} on {booking.Date} ({booking.TimeSlot}) — Applied toward final invoice.",
                ["line_items[0][quantity]"] = "1",
                ["metadata[bookingId]"] = booking.Id,
                ["metadata[businessId]"] = booking.BusinessId,
                ["metadata[date]"] = booking.Date,
                ["metadata[timeSlot]"] = booking.TimeSlot
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.stripe.com/v1/checkout/sessions");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", stripeSecretKey);
            request.Content = new FormUrlEncodedContent(formFields);

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                string err = await response.Content.ReadAsStringAsync();
                _logger.LogError("Stripe checkout session creation failed ({Status}): {Error}", response.StatusCode, err);
                return (false, null, null);
            }

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            string? url = doc.RootElement.TryGetProperty("url", out var u) ? u.GetString() : null;
            string? id = doc.RootElement.TryGetProperty("id", out var sid) ? sid.GetString() : null;
            return (!string.IsNullOrWhiteSpace(url), url, id);
        }

        private async Task TrySendBookingNotificationAsync(CosmosScheduleDocument booking)
        {
            string? brevoApiKey = Environment.GetEnvironmentVariable("BREVO_API_KEY");
            string? recipientEmail = Environment.GetEnvironmentVariable("LEAD_NOTIFICATION_EMAIL");
            string? senderEmail = Environment.GetEnvironmentVariable("BREVO_SENDER_EMAIL") ?? "info@jbdigitalservices.net";

            if (string.IsNullOrWhiteSpace(brevoApiKey) || string.IsNullOrWhiteSpace(recipientEmail))
            {
                return;
            }

            try
            {
                string depositStr = booking.DepositCents > 0
                    ? $"${booking.DepositCents / 100m:F2} ({booking.Status})"
                    : "Free Consultation ($0)";

                string htmlBody = $@"
                    <h2>New Online Schedule Booking — Forged Labor</h2>
                    <table style='border-collapse:collapse;font-family:sans-serif;'>
                        <tr><td style='padding:6px 12px;font-weight:bold;'>Date:</td><td style='padding:6px 12px;'>{WebUtility.HtmlEncode(booking.Date)}</td></tr>
                        <tr><td style='padding:6px 12px;font-weight:bold;'>Time Slot:</td><td style='padding:6px 12px;'>{WebUtility.HtmlEncode(booking.TimeSlot)}</td></tr>
                        <tr><td style='padding:6px 12px;font-weight:bold;'>Service:</td><td style='padding:6px 12px;'>{WebUtility.HtmlEncode(booking.ServiceType)}</td></tr>
                        <tr><td style='padding:6px 12px;font-weight:bold;'>Crew Size:</td><td style='padding:6px 12px;'>{(booking.CrewSize > 0 ? $"{booking.CrewSize} Movers" : "N/A (Consultation)")}</td></tr>
                        <tr><td style='padding:6px 12px;font-weight:bold;'>Deposit:</td><td style='padding:6px 12px;'>{WebUtility.HtmlEncode(depositStr)}</td></tr>
                        <tr><td style='padding:6px 12px;font-weight:bold;'>Customer:</td><td style='padding:6px 12px;'>{WebUtility.HtmlEncode(booking.Name)}</td></tr>
                        <tr><td style='padding:6px 12px;font-weight:bold;'>Phone:</td><td style='padding:6px 12px;'>{WebUtility.HtmlEncode(booking.Phone)}</td></tr>
                        <tr><td style='padding:6px 12px;font-weight:bold;'>Email:</td><td style='padding:6px 12px;'>{WebUtility.HtmlEncode(booking.Email)}</td></tr>
                        <tr><td style='padding:6px 12px;font-weight:bold;'>Origin:</td><td style='padding:6px 12px;'>{WebUtility.HtmlEncode(booking.OriginAddress)}</td></tr>
                        <tr><td style='padding:6px 12px;font-weight:bold;'>Destination:</td><td style='padding:6px 12px;'>{WebUtility.HtmlEncode(booking.DestinationAddress)}</td></tr>
                        <tr><td style='padding:6px 12px;font-weight:bold;'>Notes:</td><td style='padding:6px 12px;'>{WebUtility.HtmlEncode(booking.Notes)}</td></tr>
                    </table>";

                var payload = new
                {
                    sender = new { name = "Forged Labor Scheduler", email = senderEmail },
                    to = new[] { new { email = recipientEmail, name = "Forged Labor" } },
                    replyTo = new { email = booking.Email, name = booking.Name },
                    subject = $"New Booking: {booking.Date} ({booking.TimeSlot}) — {booking.Name}",
                    htmlContent = htmlBody
                };

                using var emailReq = new HttpRequestMessage(HttpMethod.Post, "https://api.brevo.com/v3/smtp/email");
                emailReq.Headers.Add("api-key", brevoApiKey);
                emailReq.Content = JsonContent.Create(payload);
                await _httpClient.SendAsync(emailReq);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Brevo booking notification failed.");
            }
        }

        private async Task<List<CosmosScheduleDocument>> QueryScheduleDocumentsAsync(
            string cosmosEndpoint, string cosmosKey, string databaseId, string businessId)
        {
            string resourceLink = $"dbs/{databaseId}/colls/leads";
            string requestUri = $"{cosmosEndpoint}/{resourceLink}/docs";
            string utcDate = DateTime.UtcNow.ToString("r", CultureInfo.InvariantCulture);
            string authHeader = GenerateCosmosAuthToken("post", "docs", resourceLink, utcDate, cosmosKey);

            var queryPayload = new
            {
                query = "SELECT * FROM c WHERE c.businessId = @businessId AND (c.docType = 'booking' OR c.docType = 'block')",
                parameters = new[]
                {
                    new { name = "@businessId", value = businessId }
                }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
            request.Headers.Add("Authorization", authHeader);
            request.Headers.Add("x-ms-date", utcDate);
            request.Headers.Add("x-ms-version", "2018-12-31");
            request.Headers.Add("x-ms-documentdb-isquery", "True");
            request.Headers.Add("x-ms-documentdb-partitionkey", JsonSerializer.Serialize(new[] { businessId }));
            request.Content = new StringContent(
                JsonSerializer.Serialize(queryPayload, JsonOptions),
                Encoding.UTF8,
                "application/query+json");

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                string err = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Cosmos query failed ({Status}): {Error}", response.StatusCode, err);
                return new List<CosmosScheduleDocument>();
            }

            var result = await response.Content.ReadFromJsonAsync<CosmosQueryResponse<CosmosScheduleDocument>>(JsonOptions);
            return result?.Documents ?? new List<CosmosScheduleDocument>();
        }

        private async Task<T?> GetCosmosDocumentAsync<T>(
            string cosmosEndpoint, string cosmosKey, string databaseId, string containerId, string partitionKeyValue, string docId)
        {
            string resourceLink = $"dbs/{databaseId}/colls/{containerId}/docs/{Uri.EscapeDataString(docId)}";
            string requestUri = $"{cosmosEndpoint}/{resourceLink}";
            string utcDate = DateTime.UtcNow.ToString("r", CultureInfo.InvariantCulture);
            string authHeader = GenerateCosmosAuthToken("get", "docs", resourceLink, utcDate, cosmosKey);

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Add("Authorization", authHeader);
            request.Headers.Add("x-ms-date", utcDate);
            request.Headers.Add("x-ms-version", "2018-12-31");
            request.Headers.Add("x-ms-documentdb-partitionkey", JsonSerializer.Serialize(new[] { partitionKeyValue }));

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return default;
            return await response.Content.ReadFromJsonAsync<T>(JsonOptions);
        }

        private async Task<(bool Success, HttpStatusCode StatusCode, string ErrorBody)> CreateCosmosDocumentAsync<T>(
            string cosmosEndpoint, string cosmosKey, string databaseId, string containerId, string partitionKeyValue, T document)
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
            request.Content = new StringContent(JsonSerializer.Serialize(document, JsonOptions), Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode) return (true, response.StatusCode, string.Empty);
            return (false, response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        private async Task<bool> ReplaceCosmosDocumentAsync<T>(
            string cosmosEndpoint, string cosmosKey, string databaseId, string containerId, string partitionKeyValue, string docId, T document)
        {
            string resourceLink = $"dbs/{databaseId}/colls/{containerId}/docs/{Uri.EscapeDataString(docId)}";
            string requestUri = $"{cosmosEndpoint}/{resourceLink}";
            string utcDate = DateTime.UtcNow.ToString("r", CultureInfo.InvariantCulture);
            string authHeader = GenerateCosmosAuthToken("put", "docs", resourceLink, utcDate, cosmosKey);

            using var request = new HttpRequestMessage(HttpMethod.Put, requestUri);
            request.Headers.Add("Authorization", authHeader);
            request.Headers.Add("x-ms-date", utcDate);
            request.Headers.Add("x-ms-version", "2018-12-31");
            request.Headers.Add("x-ms-documentdb-partitionkey", JsonSerializer.Serialize(new[] { partitionKeyValue }));
            request.Content = new StringContent(JsonSerializer.Serialize(document, JsonOptions), Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            return response.IsSuccessStatusCode;
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

        private static string GenerateCosmosAuthToken(
            string verb, string resourceType, string resourceId, string utcDate, string masterKeyBase64)
        {
            byte[] keyBytes = Convert.FromBase64String(masterKeyBase64);
            string payload = $"{verb.ToLowerInvariant()}\n{resourceType.ToLowerInvariant()}\n{resourceId}\n{utcDate.ToLowerInvariant()}\n\n";
            using var hmac = new HMACSHA256(keyBytes);
            byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            string signature = Convert.ToBase64String(hash);
            return Uri.EscapeDataString($"type=master&ver=1.0&sig={signature}");
        }
    }

    public class CosmosQueryResponse<T>
    {
        [JsonPropertyName("Documents")]
        public List<T> Documents { get; set; } = new();
    }

    public class PublicSlotDto
    {
        public string Date { get; set; } = string.Empty;
        public string TimeSlot { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public int CrewSize { get; set; }
    }

    public class BookingRequestDto
    {
        public string BusinessId { get; set; } = "forged-labor";
        public string ServiceType { get; set; } = string.Empty;
        public int CrewSize { get; set; } = 2;
        public string Date { get; set; } = string.Empty;
        public string TimeSlot { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Phone { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string? OriginAddress { get; set; }
        public string? DestinationAddress { get; set; }
        public string? Notes { get; set; }
        public string? ReturnUrlBase { get; set; }
        public string Honeypot { get; set; } = string.Empty;
    }

    public class VerifyPaymentDto
    {
        public string BusinessId { get; set; } = "forged-labor";
        public string BookingId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
    }

    public class AdminBlockDto
    {
        public string BusinessId { get; set; } = "forged-labor";
        public string Date { get; set; } = string.Empty;
        public string TimeSlot { get; set; } = string.Empty;
        public int CrewSize { get; set; } = 2;
        public string? Source { get; set; }
        public string? CustomerName { get; set; }
        public string? Notes { get; set; }
    }

    public class AdminCancelDto
    {
        public string BusinessId { get; set; } = "forged-labor";
        public string Id { get; set; } = string.Empty;
    }

    public class CosmosScheduleDocument
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("businessId")]
        public string BusinessId { get; set; } = string.Empty;

        [JsonPropertyName("docType")]
        public string DocType { get; set; } = "booking";

        [JsonPropertyName("createdAt")]
        public string CreatedAt { get; set; } = string.Empty;

        [JsonPropertyName("date")]
        public string Date { get; set; } = string.Empty;

        [JsonPropertyName("timeSlot")]
        public string TimeSlot { get; set; } = string.Empty;

        [JsonPropertyName("serviceType")]
        public string ServiceType { get; set; } = string.Empty;

        [JsonPropertyName("crewSize")]
        public int CrewSize { get; set; } = 2;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("phone")]
        public string Phone { get; set; } = string.Empty;

        [JsonPropertyName("email")]
        public string Email { get; set; } = string.Empty;

        [JsonPropertyName("originAddress")]
        public string OriginAddress { get; set; } = string.Empty;

        [JsonPropertyName("destinationAddress")]
        public string DestinationAddress { get; set; } = string.Empty;

        [JsonPropertyName("notes")]
        public string Notes { get; set; } = string.Empty;

        [JsonPropertyName("source")]
        public string Source { get; set; } = "Website";

        [JsonPropertyName("depositCents")]
        public int DepositCents { get; set; }

        [JsonPropertyName("depositPaid")]
        public bool DepositPaid { get; set; }

        [JsonPropertyName("stripeSessionId")]
        public string? StripeSessionId { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; } = "pending_deposit";
    }
}
