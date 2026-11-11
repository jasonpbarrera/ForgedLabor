#!/usr/bin/env python3
"""
Synchronizes static SEO and PWA files in Client/wwwroot/ directly from Client/wwwroot/appsettings.json:
  - Client/wwwroot/index.html (<title>, meta description, copyright, Open Graph tags, JSON-LD LocalBusiness schema)
  - Client/wwwroot/sitemap.xml (canonical domain URLs)
  - Client/wwwroot/robots.txt (Sitemap URL)
  - Client/wwwroot/manifest.webmanifest (PWA name, short_name, background_color, theme_color)

Usage:
  python3 scripts/sync-client-config.py
"""

from datetime import datetime
import html
import json
from pathlib import Path

ROOT_DIR = Path(__file__).resolve().parent.parent
WWWROOT = ROOT_DIR / "Client" / "wwwroot"
APPSETTINGS_PATH = WWWROOT / "appsettings.json"


def main() -> None:
    with open(APPSETTINGS_PATH, "r", encoding="utf-8") as f:
        config = json.load(f)

    biz = config.get("BusinessSettings", {})
    theme = config.get("ThemeSettings", {})
    home = config.get("HomePageSettings", {})

    company_name = biz.get("CompanyName", "Small Business")
    short_name = biz.get("ShortName", company_name)
    tagline = biz.get("Tagline", "")
    seo_description = biz.get("SeoDescription") or home.get("HeroDescription") or tagline
    domain = (biz.get("Domain") or "https://your-client-domain.com").rstrip("/")
    primary_email = biz.get("PrimaryEmail", "")
    phone = biz.get("PhoneNumber", "")
    city = biz.get("City", "")
    state = biz.get("State", "")
    logo_url = (biz.get("LogoUrl") or "images/logo.jpg").lstrip("/")
    full_logo_url = f"{domain}/{logo_url}"

    primary_color = theme.get("PrimaryColor", "#5c3a46")
    bg_color = theme.get("BackgroundColor", "#fffafb")
    year = datetime.now().year

    # 1. Generate Client/wwwroot/manifest.webmanifest
    manifest_data = {
        "name": company_name,
        "short_name": short_name,
        "start_url": "./",
        "display": "standalone",
        "background_color": bg_color,
        "theme_color": primary_color,
        "prefer_related_applications": False,
        "icons": [
            {
                "src": "android-chrome-512x512.png",
                "type": "image/png",
                "sizes": "512x512"
            },
            {
                "src": "android-chrome-192x192.png",
                "type": "image/png",
                "sizes": "192x192"
            }
        ]
    }
    manifest_path = WWWROOT / "manifest.webmanifest"
    manifest_path.write_text(json.dumps(manifest_data, indent=2) + "\n", encoding="utf-8")

    # 2. Generate Client/wwwroot/sitemap.xml
    sitemap_xml = f"""<?xml version="1.0" encoding="UTF-8"?>
<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
  <url>
    <loc>{html.escape(domain)}/</loc>
    <changefreq>weekly</changefreq>
    <priority>1.0</priority>
  </url>
  <url>
    <loc>{html.escape(domain)}/services</loc>
    <changefreq>monthly</changefreq>
    <priority>0.9</priority>
  </url>
  <url>
    <loc>{html.escape(domain)}/appointments</loc>
    <changefreq>weekly</changefreq>
    <priority>0.9</priority>
  </url>
  <url>
    <loc>{html.escape(domain)}/contact</loc>
    <changefreq>monthly</changefreq>
    <priority>0.9</priority>
  </url>
  <url>
    <loc>{html.escape(domain)}/about</loc>
    <changefreq>monthly</changefreq>
    <priority>0.8</priority>
  </url>
</urlset>
"""
    (WWWROOT / "sitemap.xml").write_text(sitemap_xml, encoding="utf-8")

    # 3. Generate Client/wwwroot/robots.txt
    robots_txt = f"""User-agent: *
Allow: /

Sitemap: {domain}/sitemap.xml
"""
    (WWWROOT / "robots.txt").write_text(robots_txt, encoding="utf-8")

    # 4. Generate Client/wwwroot/e3d29a08c8474f63bb9b7d8b5c9632f1.txt (IndexNow Search Crawler Key)
    indexnow_key = "e3d29a08c8474f63bb9b7d8b5c9632f1"
    (WWWROOT / f"{indexnow_key}.txt").write_text(indexnow_key, encoding="utf-8")

    # 5. Ensure staticwebapp.config.json excludes /*.txt from SPA fallback
    swa_config_path = WWWROOT / "staticwebapp.config.json"
    if swa_config_path.exists():
        try:
            with open(swa_config_path, "r", encoding="utf-8") as f:
                swa_cfg = json.load(f)
            exclude_list = swa_cfg.get("navigationFallback", {}).get("exclude", [])
            if "/*.txt" not in exclude_list:
                exclude_list.append("/*.txt")
                swa_cfg["navigationFallback"]["exclude"] = exclude_list
                with open(swa_config_path, "w", encoding="utf-8") as f:
                    json.dump(swa_cfg, f, indent=2)
        except Exception:
            pass

    # 6. Generate Client/wwwroot/index.html
    json_ld = {
        "@context": "https://schema.org",
        "@type": "LocalBusiness",
        "name": company_name,
        "image": full_logo_url,
        "@id": domain,
        "url": domain,
        "telephone": phone,
        "address": {
            "@type": "PostalAddress",
            "addressLocality": city,
            "addressRegion": state,
            "addressCountry": "US"
        },
        "openingHoursSpecification": {
            "@type": "OpeningHoursSpecification",
            "dayOfWeek": [
                "Monday",
                "Tuesday",
                "Wednesday",
                "Thursday",
                "Friday",
                "Saturday",
                "Sunday"
            ],
            "opens": "00:00",
            "closes": "23:59"
        }
    }
    json_ld_str = json.dumps(json_ld, indent=10)
    # Indent closing brace to align with <script> block
    indented_json_ld = "\n".join(
        ("        " + line if idx == 0 else line)
        for idx, line in enumerate(json_ld_str.splitlines())
    )

    page_title = f"{company_name} | {tagline}" if tagline else company_name
    index_html = f"""<!DOCTYPE html>
<html lang="en">

<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>{html.escape(page_title)}</title>
    <meta name="description" content="{html.escape(seo_description, quote=True)}" />

    <!-- Developer & Copyright Attribution -->
    <meta name="author" content="Jason Barrera" />
    <meta name="developer" content="Jason Barrera" />
    <meta name="contact" content="{html.escape(primary_email, quote=True)}" />
    <meta name="copyright" content="Copyright © {year} {html.escape(company_name, quote=True)}" />

    <!-- Open Graph for Social Sharing -->
    <meta property="og:title" content="{html.escape(company_name, quote=True)}" />
    <meta property="og:description" content="{html.escape(seo_description, quote=True)}" />
    <meta property="og:image" content="{html.escape(full_logo_url, quote=True)}" />
    <meta property="og:url" content="{html.escape(domain, quote=True)}" />
    <meta property="og:type" content="website" />

    <base href="/" />
    <link rel="preconnect" href="https://fonts.googleapis.com">
    <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
    <link href="https://fonts.googleapis.com/css2?family=Archivo:wght@400;500;600;700;800;900&family=Archivo+Black&display=swap" rel="stylesheet">
    <link rel="preload" id="webassembly" />
    <link rel="stylesheet" href="css/app.css" />

    <!-- Favicon Tags -->
    <link rel="apple-touch-icon" sizes="180x180" href="apple-touch-icon.png">
    <link rel="icon" type="image/png" sizes="32x32" href="favicon-32x32.png">
    <link rel="icon" type="image/png" sizes="16x16" href="favicon-16x16.png">

    <link href="SmallBusinessSite.Client.styles.css" rel="stylesheet" />
    <link href="manifest.webmanifest" rel="manifest" />
    <script type="importmap"></script>

    <script type="application/ld+json">
{indented_json_ld}
    </script>
</head>

<body>

    <div id="app">
        <div style="display:flex; justify-content:center; align-items:center; height:100vh; flex-direction:column; font-family:sans-serif;">
            <div class="spinner"></div>
            <p style="margin-top:15px; color:#555;">Loading...</p>
        </div>
    </div>

    <script src="_framework/blazor.webassembly#[.{{fingerprint}}].js"></script>
    <script>navigator.serviceWorker.register('service-worker.js', {{ updateViaCache: 'none' }});</script>
</body>

</html>
"""
    (WWWROOT / "index.html").write_text(index_html, encoding="utf-8")
    print(f"Synced index.html, sitemap.xml, robots.txt, and manifest.webmanifest for '{company_name}' ({domain}).")


if __name__ == "__main__":
    main()
