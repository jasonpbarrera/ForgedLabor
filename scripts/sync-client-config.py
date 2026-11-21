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

    analytics = config.get("AnalyticsSettings", {})
    gtm_id = (analytics.get("GtmContainerId") or "").strip()
    ga4_id = (analytics.get("Ga4MeasurementId") or "").strip()
    clarity_id = (analytics.get("ClarityProjectId") or "").strip()
    gsc_verification = (analytics.get("GoogleSiteVerification") or "").strip()
    og_home_url = f"{domain}/images/og-home.png"

    # 2. Generate Client/wwwroot/sitemap.xml
    sitemap_xml = f"""<?xml version="1.0" encoding="UTF-8"?>
<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
  <url>
    <loc>{html.escape(domain)}/</loc>
    <changefreq>weekly</changefreq>
    <priority>1.0</priority>
  </url>
  <url>
    <loc>{html.escape(domain)}/about</loc>
    <changefreq>monthly</changefreq>
    <priority>0.9</priority>
  </url>
  <url>
    <loc>{html.escape(domain)}/commercial</loc>
    <changefreq>weekly</changefreq>
    <priority>0.95</priority>
  </url>
  <url>
    <loc>{html.escape(domain)}/gallery</loc>
    <changefreq>weekly</changefreq>
    <priority>0.85</priority>
  </url>
  <url>
    <loc>{html.escape(domain)}/appointments</loc>
    <changefreq>weekly</changefreq>
    <priority>0.9</priority>
  </url>
  <url>
    <loc>{html.escape(domain)}/movers-chattanooga-tn</loc>
    <changefreq>monthly</changefreq>
    <priority>0.9</priority>
  </url>
  <url>
    <loc>{html.escape(domain)}/movers-ooltewah-tn</loc>
    <changefreq>monthly</changefreq>
    <priority>0.85</priority>
  </url>
  <url>
    <loc>{html.escape(domain)}/labor-only-movers-cleveland-tn</loc>
    <changefreq>monthly</changefreq>
    <priority>0.9</priority>
  </url>
  <url>
    <loc>{html.escape(domain)}/services</loc>
    <changefreq>monthly</changefreq>
    <priority>0.85</priority>
  </url>
  <url>
    <loc>{html.escape(domain)}/contact</loc>
    <changefreq>monthly</changefreq>
    <priority>0.8</priority>
  </url>
  <url>
    <loc>{html.escape(domain)}/privacy</loc>
    <changefreq>yearly</changefreq>
    <priority>0.4</priority>
  </url>
  <url>
    <loc>{html.escape(domain)}/terms</loc>
    <changefreq>yearly</changefreq>
    <priority>0.4</priority>
  </url>
</urlset>
"""
    (WWWROOT / "sitemap.xml").write_text(sitemap_xml, encoding="utf-8")

    # 3. Generate Client/wwwroot/robots.txt
    robots_txt = f"""User-agent: *
Allow: /
Disallow: /admin/

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
        "@type": "MovingCompany",
        "name": company_name,
        "legalName": f"{company_name} LLC",
        "image": og_home_url,
        "@id": domain,
        "url": domain,
        "telephone": phone,
        "priceRange": "$$",
        "address": {
            "@type": "PostalAddress",
            "streetAddress": "126 Bernham Drive, Suite 4",
            "addressLocality": city,
            "addressRegion": state,
            "postalCode": "37312",
            "addressCountry": "US"
        },
        "geo": {
            "@type": "GeoCoordinates",
            "latitude": 35.212568,
            "longitude": -84.875402
        },
        "sameAs": [
            "https://www.facebook.com/Forgedlabor",
            "https://www.instagram.com/forgedlabor",
            "https://maps.app.goo.gl/FkAPVz7Y3y5YTgL2A",
            "https://www.uhaul.com/MovingHelp/Cleveland-TN-37312/1/Forged-Labor/?id=220C1EA5C52A41"
        ],
        "aggregateRating": {
            "@type": "AggregateRating",
            "ratingValue": "5.0",
            "reviewCount": "119",
            "bestRating": "5",
            "worstRating": "1"
        },
        "areaServed": [
            "Cleveland, TN",
            "Chattanooga, TN",
            "Bradley County, TN",
            "Hamilton County, TN",
            "Ooltewah, TN",
            "Athens, TN",
            "Southeast Tennessee"
        ],
        "hasOfferCatalog": {
            "@type": "OfferCatalog",
            "name": "Moving & Labor Services",
            "itemListElement": [
                {
                    "@type": "Offer",
                    "itemOffered": {
                        "@type": "Service",
                        "name": "Full Service Residential Moving"
                    }
                },
                {
                    "@type": "Offer",
                    "itemOffered": {
                        "@type": "Service",
                        "name": "Commercial Moving & Fitness Equipment Installation"
                    }
                },
                {
                    "@type": "Offer",
                    "itemOffered": {
                        "@type": "Service",
                        "name": "Labor-Only Loading and Unloading"
                    }
                },
                {
                    "@type": "Offer",
                    "itemOffered": {
                        "@type": "Service",
                        "name": "Store Pickup and Furniture Delivery"
                    }
                }
            ]
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
        },
        "creator": {
            "@type": "Organization",
            "name": "JB Digital Services",
            "url": "https://jbdigitalservices.net",
            "founder": {
                "@type": "Person",
                "name": "Jason Barrera"
            }
        }
    }
    faq_ld = {
        "@context": "https://schema.org",
        "@type": "FAQPage",
        "mainEntity": [
            {
                "@type": "Question",
                "name": "Is Forged Labor insured for residential and commercial moves?",
                "acceptedAnswer": {
                    "@type": "Answer",
                    "text": "Yes. Every move includes both Commercial General Liability and Cargo insurance coverage protecting your belongings during loading, transport, and unloading."
                }
            },
            {
                "@type": "Question",
                "name": "What areas around Cleveland, TN does Forged Labor serve?",
                "acceptedAnswer": {
                    "@type": "Answer",
                    "text": "We serve Cleveland, Chattanooga, Ooltewah, Athens, Bradley County, Hamilton County, and all of Southeast Tennessee, plus regional travel for commercial fitness equipment installations."
                }
            },
            {
                "@type": "Question",
                "name": "Can I hire Forged Labor just to load or unload my U-Haul or PODS container?",
                "acceptedAnswer": {
                    "@type": "Answer",
                    "text": "Yes. Our Labor-Only service provides 2 to 5 strong, insured movers equipped with commercial furniture pads, straps, and dollies to load or unload your rental truck, PODS container, or storage unit."
                }
            },
            {
                "@type": "Question",
                "name": "Does Forged Labor move gym equipment, pianos, or hot tubs?",
                "acceptedAnswer": {
                    "@type": "Answer",
                    "text": "We specialize in heavy-duty home gym and commercial fitness equipment moving and installation (power racks, treadmills, cable crossovers, collegiate weight rooms). However, we do not move pianos or hot tubs."
                }
            }
        ]
    }

    json_ld_str = json.dumps(json_ld, indent=8)
    faq_ld_str = json.dumps(faq_ld, indent=8)

    gsc_tag = f'    <meta name="google-site-verification" content="{html.escape(gsc_verification, quote=True)}" />\n' if gsc_verification else ""
    gtm_head_snippet = ""
    gtm_body_snippet = ""
    if gtm_id:
        gtm_esc = html.escape(gtm_id, quote=True)
        gtm_head_snippet = f"""    <!-- Google Tag Manager -->
    <script async src="https://www.googletagmanager.com/gtm.js?id={gtm_esc}"></script>
    <script>
        window.dataLayer = window.dataLayer || [];
        window.dataLayer.push({{'gtm.start': new Date().getTime(), event: 'gtm.js'}});
        function gtag(){{dataLayer.push(arguments);}}
        gtag('js', new Date());
    </script>
"""
        gtm_body_snippet = f"""    <noscript><iframe src="https://www.googletagmanager.com/ns.html?id={gtm_esc}" height="0" width="0" style="display:none;visibility:hidden"></iframe></noscript>
"""
    ga4_snippet = ""
    if ga4_id:
        ga4_snippet = f"""    <!-- Google Analytics 4 -->
    <script async src="https://www.googletagmanager.com/gtag/js?id={html.escape(ga4_id, quote=True)}"></script>
    <script>
        window.dataLayer = window.dataLayer || [];
        function gtag(){{dataLayer.push(arguments);}}
        gtag('js', new Date());
        gtag('config', '{html.escape(ga4_id, quote=True)}');
    </script>
"""
    clarity_snippet = ""
    if clarity_id:
        clarity_snippet = f"""    <!-- Microsoft Clarity -->
    <script type="text/javascript">
        (function(c,l,a,r,i,t,y){{
            c[a]=c[a]||function(){{(c[a].q=c[a].q||[]).push(arguments)}};
            t=l.createElement(r);t.async=1;t.src="https://www.clarity.ms/tag/"+i;
            y=l.getElementsByTagName(r)[0];y.parentNode.insertBefore(t,y);
        }})(window, document, "clarity", "script", "{html.escape(clarity_id, quote=True)}");
    </script>
"""

    page_title = f"{company_name} | {tagline}" if tagline else company_name
    index_html = f"""<!DOCTYPE html>
<html lang="en">

<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>{html.escape(page_title)}</title>
    <meta name="description" content="{html.escape(seo_description, quote=True)}" />
    <link rel="canonical" href="{html.escape(domain, quote=True)}/" />
{gsc_tag}
    <!-- Developer & Copyright Attribution -->
    <meta name="author" content="Jason Barrera, JB Digital Services" />
    <meta name="creator" content="JB Digital Services" />
    <meta name="publisher" content="JB Digital Services" />
    <meta name="developer" content="Jason Barrera" />
    <meta name="contact" content="{html.escape(primary_email, quote=True)}" />
    <meta name="copyright" content="Copyright © {year} {html.escape(company_name, quote=True)}" />

    <!-- Open Graph for Social Sharing -->
    <meta property="og:title" content="{html.escape(company_name, quote=True)} | Cleveland &amp; Chattanooga Movers" />
    <meta property="og:description" content="{html.escape(seo_description, quote=True)}" />
    <meta property="og:image" content="{html.escape(og_home_url, quote=True)}" />
    <meta property="og:image:width" content="1200" />
    <meta property="og:image:height" content="630" />
    <meta property="og:url" content="{html.escape(domain, quote=True)}/" />
    <meta property="og:type" content="website" />

    <!-- Twitter Card -->
    <meta name="twitter:card" content="summary_large_image" />
    <meta name="twitter:title" content="{html.escape(company_name, quote=True)} | Cleveland &amp; Chattanooga Movers" />
    <meta name="twitter:description" content="{html.escape(seo_description, quote=True)}" />
    <meta name="twitter:image" content="{html.escape(og_home_url, quote=True)}" />

    <base href="/" />
    <link rel="preconnect" href="https://fonts.googleapis.com">
    <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
    <link href="https://fonts.googleapis.com/css2?family=Archivo:wght@400;500;600;700;800;900&family=Archivo+Black&display=swap" rel="stylesheet">
    <link rel="preload" id="webassembly" />
    <link rel="stylesheet" href="css/app.css" />

    <!-- Favicon Tags -->
    <link rel="icon" href="favicon.ico?v=2" sizes="any">
    <link rel="apple-touch-icon" sizes="180x180" href="apple-touch-icon.png?v=2">
    <link rel="icon" type="image/png" sizes="32x32" href="favicon-32x32.png?v=2">
    <link rel="icon" type="image/png" sizes="16x16" href="favicon-16x16.png?v=2">

    <link href="SmallBusinessSite.Client.styles.css" rel="stylesheet" />
    <link href="manifest.webmanifest" rel="manifest" />
    <script type="importmap"></script>

    <script type="application/ld+json">
{json_ld_str}
    </script>
    <script type="application/ld+json">
{faq_ld_str}
    </script>
{gtm_head_snippet}{ga4_snippet}{clarity_snippet}    <script src="js/analytics.js" defer></script>
</head>

<body>
{gtm_body_snippet}
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
