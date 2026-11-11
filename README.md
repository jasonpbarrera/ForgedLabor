# Forged Labor — Cleveland, TN Premier Movers

A high-performance, configuration-driven **.NET 10 Blazor WebAssembly + Azure Static Web Apps** website, consultation intake, and scheduling platform for **Forged Labor** (Owner: **Ben Wiseman**).

---

## Business & Technical Overview

* **Client:** Ben Wiseman
* **Business:** Forged Labor
* **Location:** 126 Bernham Drive, Suite 4, Cleveland, TN 37312
* **Direct Phone:** (423) 715-4254
* **Domain:** [forgedlabor.com](https://forgedlabor.com)
* **Live Azure SWA Staging:** [https://victorious-ocean-0f48d410f.3.azurestaticapps.net](https://victorious-ocean-0f48d410f.3.azurestaticapps.net)
* **Agency Portal Registration:** [JB Digital Services Agency Portal](https://purple-flower-020acac0f.4.azurestaticapps.net/clients)

### Tech Stack
* **Frontend:** Blazor WebAssembly (.NET 10) PWA
* **API:** Azure Static Web Apps Managed Functions (.NET 10 Isolated Worker)
* **Database:** Multi-Tenant Azure Cosmos DB (`SmallBusinessPlatform`, partitioned by `/businessId = "forged-labor"`)
* **Email & Lead Alerts:** Brevo Free Tier API (Instant lead alerts dispatched to Ben Wiseman)
* **Search Engine Crawlers:** IndexNow protocol (Microsoft Bing, Yandex, Seznam, Naver, DuckDuckGo, Yahoo)

---

## Core Capabilities & Pages

1. **Home (`/`)**:
   * Hero banner featuring real team imagery and tagline *"WE SHOW UP. IN SHAPE. ON TIME. READY TO ACCOMPLISH."*
   * "Your Stuff Matters" standard section (zero wall dings, zero dragging, athletic pacing, no surprise charges).
   * 4-pillar moving service showcase (Full Service, Labor-Only, Pickup & Delivery, Heavy Fitness Equipment).
   * Verified 5-star Google review showcase with over 100 5-star ratings.
2. **Services (`/services`)**:
   * Detailed breakdown of moving equipment, packing protocols, cargo and liability insurance coverage.
   * Hiring banner for punctual, fit crew members.
3. **Scheduling & Consultation (`/appointments`)**:
   * Fast intake form for move details, preferred dates, origin/destinations, and customer details.
   * Direct integration with Ben's online booking calendar.
   * Interactive Google Maps embed for the Cleveland, TN headquarters.
4. **About Ben Wiseman (`/about`)**:
   * The story, work ethic, physical readiness standard, and why Forged Labor was founded.
5. **Contact (`/contact`)**:
   * Direct call, email, office location, and general inquiry intake.

---

## Automated CI/CD & Crawler Registration (Agency Rule 1)

Whenever `main` is pushed to GitHub:
1. GitHub Actions sets up .NET 10.
2. `scripts/sync-client-config.py` generates `index.html`, `sitemap.xml`, `robots.txt`, and `manifest.webmanifest`.
3. The Managed Functions API and Blazor WASM client build and publish.
4. `Azure/static-web-apps-deploy@v1` deploys to `swa-forged-labor` using `AZURE_STATIC_WEB_APPS_API_TOKEN`.
5. Post-deploy crawler submission broadcasts all sitemap URLs to the IndexNow gateway.
