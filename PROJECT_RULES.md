# JB Digital Services — Mandatory Project Rules

> **Effective Date:** November 2026  
> **Applicability:** All Agency Platforms, Client Sites, Starter Templates, and Automated CI/CD Pipelines.

---

## Rule 1: Mandatory Search Crawler Submission for All New & Updated Sites

### Requirement
**Every website launched, onboarded, or deployed under the agency must immediately and automatically be submitted to major search engine crawlers.** No client site may go live without active crawler registration.

### Standard Protocols & Search Engines Reached
1. **IndexNow 3.0 Protocol:**
   - Every site must host the agency verification key file at `Client/wwwroot/e3d29a08c8474f63bb9b7d8b5c9632f1.txt` containing the key `e3d29a08c8474f63bb9b7d8b5c9632f1`.
   - `staticwebapp.config.json` must exclude `/*.txt` from SPA fallback rewrite rules.
   - All production URLs must be submitted to the IndexNow gateway:
     - `https://api.indexnow.org/indexnow`
     - `https://www.bing.com/indexnow`
     - `https://yandex.com/indexnow`
   - Reaches: **Microsoft Bing**, **Yandex**, **Seznam.cz**, **Naver**, and syndicates automatically to **DuckDuckGo**, **Yahoo**, and **Ecosia**.
2. **Googlebot Discovery & Google Search Console:**
   - Every site must generate a compliant `sitemap.xml` and a `robots.txt` that declares `Sitemap: https://<domain>/sitemap.xml`.
   - The client site must be registered in **Google Search Console** with the sitemap submitted.
   - Canonical links must be included on the agency portfolio showcase (`https://jbdigitalservices.net/portfolio`) to provide continuous Googlebot discovery backlinks.

---

## Rule 2: Automation Touchpoints for Search Submission

Search crawler submission is built into four distinct operational layers so that no site is ever missed:

1. **GitHub Actions CI/CD Deployment:**
   - The deployment workflow (`.github/workflows/azure-static-web-apps.yml`) includes an automated post-deploy step: whenever `main` is deployed to Azure Static Web Apps, all URLs are automatically broadcast to IndexNow.
2. **Agency Portal UI & API:**
   - Saving or updating a client in the Agency Portal (`/clients` or `POST /api/clients`) automatically triggers crawler submission for the client's domain.
   - The Agency Portal provides a dedicated **Search Crawlers & SEO Hub** (`/seo`) with a 1-click **"Submit All to Crawlers"** action.
3. **Template Setup & Sync CLI (`sync-client-config.py`):**
   - Running `python3 scripts/sync-client-config.py` automatically validates and outputs `sitemap.xml`, `robots.txt`, `staticwebapp.config.json`, and the IndexNow verification key file.
4. **Onboarding Script (`onboard-client.py`):**
   - Running `python3 scripts/onboard-client.py onboard` executes an IndexNow broadcast as Phase 4 of the onboarding run.

---

## Rule 3: Client Onboarding Verification Checklist

When onboarding any new client, verify:
- [ ] Domain configured in `Client/wwwroot/appsettings.json`.
- [ ] `Client/wwwroot/e3d29a08c8474f63bb9b7d8b5c9632f1.txt` present.
- [ ] `Client/wwwroot/staticwebapp.config.json` excludes `/*.txt`.
- [ ] Sitemaps generated at `Client/wwwroot/sitemap.xml`.
- [ ] `Client/wwwroot/robots.txt` points to `sitemap.xml`.
- [ ] CI/CD workflow executed and crawler submission step succeeded.
- [ ] Client site registered in Google Search Console and Bing Webmaster Tools.
- [ ] Client featured on `https://jbdigitalservices.net/portfolio`.
