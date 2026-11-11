# New Client Launch Playbook (API-Automated SOP)

This playbook walks through launching a new **$0/month small business website** and tracking client setup & billing in **Azure Cosmos DB (`SmallBusinessPlatform`)**, **Azure Static Web Apps**, **Cloudflare**, and **Brevo**.

---

## One-Time Master Setup (Do This Once for Your Agency)

Before launching your first client, provision your shared multi-tenant **Azure Cosmos DB Free Tier** account (`1,000 RU/s` + `25 GB` free forever, never pauses):

1. **Provision Azure Cosmos DB (`SmallBusinessPlatform`):**
   * Run [`scripts/setup-cosmos.sh`](../scripts/setup-cosmos.sh) using the Azure CLI:
     ```bash
     bash scripts/setup-cosmos.sh
     ```
   * This creates your free-tier Cosmos DB account and 3 containers partitioned by `/businessId` (documented in [`database/cosmos_schema.json`](../database/cosmos_schema.json)):
     * **`clients`**: Stores client identity, owner contact info, hosting/DNS infrastructure status, billing plan rates, and their full `appsettings.json` configuration snapshot.
     * **`leads`**: Stores all customer inquiries submitted via `POST /api/leads`.
     * **`billing`**: Stores setup charges, custom work hours/invoices, recurring subscriptions, and payment statuses.
2. **Save Your Master Agency Credentials (`.env.agency`):**
   * Copy [`.env.agency.example`](../.env.agency.example) to `.env.agency` (git-ignored) and paste in:
     * `COSMOS_CONNECTION_STRING` (printed at the end of `scripts/setup-cosmos.sh`)
     * `CLOUDFLARE_API_TOKEN` (from Cloudflare Dashboard → My Profile → API Tokens, with `Zone:DNS:Edit` and `Zone:Email Routing Rules:Edit`)
     * `BREVO_API_KEY` (from Brevo → SMTP & API → API Keys)

---

## Phase 1: Client Intake Questionnaire (5 Minutes)

Collect these details from the business owner:

- [ ] **Business Identity & Owner Info:**
  - Legal Company Name (e.g., *Apex Landscaping LLC*)
  - Short Display Name (e.g., *Apex Landscaping*)
  - Owner Full Name & Personal Gmail Address (where leads and routed emails will land)
  - Tagline / Slogan & 1-sentence SEO Description
  - City, State & Phone Number
  - Desired Custom Domain (e.g., `clientbusiness.com` — purchased on Cloudflare Registrar)
  - Facebook & Instagram URLs
- [ ] **Visual Brand:**
  - High-res Logo image (`logo.jpg` or `logo.png`)
  - 4 Hex Colors: `PrimaryColor`, `AccentColor`, `SurfaceColor`, `BackgroundColor`
- [ ] **Page Copy & Intake Options:**
  - **Home:** Hero description, 3–4 value pillars, and a short founder quote.
  - **About:** 3 bullet points ("Who I Am"), 1–2 paragraphs ("Why I Started"), and 4 core value words.
  - **Services:** 3–6 core services (Title + description + emoji/icon) + whether they are hiring.
  - **Contact Form:** List of services for the dropdown.
- [ ] **Billing & Plan Terms:**
  - Plan tier (e.g., `Free Community Tier` or `Managed Care Plan`), monthly rate (`$0` or custom), initial setup fee, and hourly rate for custom work.

---

## Phase 2: Repository & Site Customization (5 Minutes)

1. **Create Repo from Template:**
   * Click **"Use this template"** on `jasonpbarrera/SmallBusinessSiteTemplate` (or run `gh repo create <ClientRepo> --template jasonpbarrera/SmallBusinessSiteTemplate --public --clone`).
2. **Add Brand Assets:**
   * Generate favicons at `https://favicon.io` from the client's logo and drop them into `Client/wwwroot/`.
   * Place the client's logo at `Client/wwwroot/images/logo.jpg`.
3. **Configure `Client/wwwroot/appsettings.json`:**
   * Set `"BusinessId"` to a unique slug (e.g., `"apex-landscaping-tn"`).
   * Set `"Domain"` (e.g., `"https://apexlandscaping.com"`) and `"SeoDescription"`.
   * Fill in `BusinessSettings`, `ThemeSettings`, `HomePageSettings`, `AboutPageSettings`, `ServicesPageSettings`, and `ContactPageSettings`.
4. **Sync Static SEO & PWA Files (`Client/wwwroot/`):**
   * Run:
     ```bash
     python3 scripts/sync-client-config.py
     ```
   * This automatically updates `index.html`, `sitemap.xml`, `robots.txt`, and `manifest.webmanifest` from `appsettings.json` (and also runs automatically in GitHub Actions).

---

## Phase 3: Azure Static Web App & One-Command API Provisioning (5 Minutes)

1. **Create Azure Static Web App:**
   * Create a Free Tier **Static Web App** connected to the client's GitHub repository (`main` branch).
   * In **Azure Portal → Static Web App → Environment variables**, set:
     * `COSMOS_CONNECTION_STRING` = `AccountEndpoint=https://...;AccountKey=...;`
     * `BREVO_API_KEY` = `xkeysib-...`
     * `BREVO_SENDER_EMAIL` = `info@clientdomain.com`
     * `LEAD_NOTIFICATION_EMAIL` = `info@clientdomain.com` (optional—`LeadsFunction` automatically looks up the client's `leadNotificationEmail` / `primaryEmail` from the Cosmos DB `clients` container if omitted!)
2. **Run the Master Onboarding Script (`scripts/onboard-client.py`):**
   * With your `.env.agency` configured, run one command to register the client in Cosmos DB (`clients` + `billing`), point Cloudflare DNS to Azure SWA, create Cloudflare Email Routing rules (`info@` / `admin@` → Owner's Gmail), and authenticate the domain in Brevo (`DKIM`/`SPF`):
     ```bash
     python3 scripts/onboard-client.py onboard \
       --owner-name "Jane Smith" \
       --owner-gmail "janesmith@gmail.com" \
       --plan "Free Community Tier" \
       --rate 0 \
       --initial-setup-fee 0 \
       --github-repo "jasonpbarrera/ApexLandscaping" \
       --swa-name "swa-apex-landscaping" \
       --swa-hostname "swa-apex-landscaping.azurestaticapps.net"
     ```

---

## Phase 4: Mandatory Search Crawler & SEO Submission (Agency Standard)

> [!IMPORTANT]
> **Agency Standard Rule:** All newly provisioned sites **must immediately be submitted to search crawlers**.

1. **Automated Submission:**
   - The onboarding script (`scripts/onboard-client.py`) and CI/CD GitHub Action automatically submit the site's URLs to the **IndexNow network** (Microsoft Bing, Yandex, Seznam, Naver, DuckDuckGo, Yahoo) upon first deploy.
2. **Agency Portal Verification:**
   - In the Agency Portal, navigate to **[Search Crawlers & SEO Hub (`/seo`)](https://purple-flower-020acac0f.4.azurestaticapps.net/seo)** to trigger a 1-click broadcast or verify crawler response codes.
3. **Google Search Console Registration:**
   - Open **Google Search Console** using the direct 1-click link on `/seo` for the client's domain.
   - Submit `https://<clientdomain>/sitemap.xml`.
   - Feature the client on `jbdigitalservices.net/portfolio` to establish an indexed discovery backlink.

---

## Phase 5: Tracking Custom Work, Billing & Client Status

Your Cosmos DB database (`SmallBusinessPlatform`) tracks all client setups, leads, and charges so you can manage everything from the CLI (or your future Agency Portal UI):

* **View All Clients, Lead Counts & Billing Totals:**
  ```bash
  python3 scripts/onboard-client.py status
  ```
* **Log Custom Work or a Service Charge (`billing` container):**
  ```bash
  python3 scripts/onboard-client.py add-charge \
    --business-id "apex-landscaping-tn" \
    --type "custom_work" \
    --title "Custom Before/After Photo Gallery" \
    --hours 2.0 \
    --amount 150.00 \
    --status "pending" \
    --due-date "2026-12-01"
  ```

---

## Phase 5: Smoke Test & Handover

1. **End-to-End Smoke Test:**
   * Open the live site on desktop and mobile (`360x800` viewport).
   * Submit a test inquiry on `/contact`.
   * Verify:
     1. A new document appears in Azure Cosmos DB (`SmallBusinessPlatform` → `leads` container with `businessId = '<client-slug>'`).
     2. An instant HTML lead notification email arrives in the owner's inbox via Brevo, and clicking **Reply** replies directly to the customer's email address.
2. **Search Console & GBP:**
   * Submit `https://<client-domain>/sitemap.xml` in Google Search Console.
   * Verify the client's Google Business Profile matches the website NAP (Name, Address, Phone).

---

## Phase 6: Client Email Handover (Send & Receive in Gmail)

Because Cloudflare Email Routing forwards inbound emails (`info@<client-domain>` / `admin@<client-domain>`) directly to the client's personal Gmail inbox, business owners can also **send outgoing emails directly from Gmail** using their custom domain address at zero cost.

### Option A: Via Brevo SMTP Relay (Recommended)
1. In the client's Gmail, go to **Settings (Gear icon)** ➔ **See all settings** ➔ **Accounts and Import**.
2. Under **"Send mail as:"**, click **Add another email address**.
3. Name: Business Owner / Company Name; Email: `info@<client-domain>` (or `contact@<client-domain>`). Keep "Treat as an alias" checked.
4. SMTP Configuration:
   * **SMTP Server:** `smtp-relay.brevo.com`
   * **Port:** `587`
   * **Username:** The Brevo SMTP relay username (from Brevo Dashboard → SMTP & API)
   * **Password:** The Brevo API Key / SMTP Key
   * **Secured connection using TLS:** Checked
5. Verification: Enter the confirmation code sent to their custom domain address (which routes directly into their Gmail inbox).
6. Under "When replying to a message:", select **"Reply from the same address the message was sent to"**.

### Option B: Via Native Gmail SMTP (Google App Password)
1. In the client's Google Account (with 2-Step Verification enabled), generate a 16-character [App Password](https://myaccount.google.com/apppasswords).
2. In Gmail Settings ➔ **Accounts and Import** ➔ **Send mail as:** ➔ **Add another email address**.
3. SMTP Server: `smtp.gmail.com`, Port: `587`, Username: `<client>@gmail.com`, Password: the 16-character App Password (TLS).
4. Verify using the code sent to the inbox. *(Note: Ensure `include:_spf.google.com` is present in the domain's SPF DNS record).*

