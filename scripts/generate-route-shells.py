#!/usr/bin/env python3
"""
Post-publish static route shell generator for Blazor WASM on Azure Static Web Apps.

Takes the MSBuild-fingerprinted `index.html` from the published wwwroot directory
and emits per-route `<route>/index.html` shells with route-specific:
  - <title>
  - <meta name="description">
  - <link rel="canonical">
  - Open Graph & Twitter Card tags (including dedicated commercial OG image)
  - Optional route-specific JSON-LD structured data

Usage:
  python3 scripts/generate-route-shells.py publish_output/wwwroot
"""

import html
import json
import re
import sys
from pathlib import Path

DOMAIN = "https://forgedlabor.com"

ROUTES = [
    {
        "path": "/about",
        "title": "About Ben, Bre & The Crew | Forged Labor",
        "description": "Meet Ben Wiseman, Bre Wiseman, Dez Caudill, Bruno, and the Forged Labor moving crew in Cleveland, TN. Locally owned, insured, and backed by 119+ five-star reviews.",
        "og_image": f"{DOMAIN}/images/og-home.png",
    },
    {
        "path": "/commercial",
        "title": "Commercial Fitness Equipment Installation & Gym Setups | Forged Labor",
        "description": "Professional commercial fitness equipment receiving, freight unloading, unboxing, assembly, installation, floor protection, leveling, and anchoring across Southeast TN, North GA, and AL.",
        "og_image": f"{DOMAIN}/images/og-commercial.png",
        "extra_json_ld": [
            {
                "@context": "https://schema.org",
                "@type": "Service",
                "name": "Commercial Fitness Equipment Installation & Gym Setups",
                "serviceType": "Commercial Fitness Equipment Installation",
                "provider": {
                    "@type": "MovingCompany",
                    "name": "Forged Labor",
                    "url": DOMAIN,
                    "telephone": "(423) 715-4254",
                },
                "areaServed": [
                    "Cleveland, TN",
                    "Chattanooga, TN",
                    "Knoxville, TN",
                    "Southeast Tennessee",
                    "North Georgia",
                    "Alabama",
                ],
                "description": "Freight receiving, unloading, staging, assembly, floor protection, precision leveling, concrete anchoring, facility relocations, and post-install cleanup for commercial gyms, collegiate weight rooms, schools, studios, and apartment fitness centers.",
                "url": f"{DOMAIN}/commercial",
            },
            {
                "@context": "https://schema.org",
                "@type": "FAQPage",
                "mainEntity": [
                    {
                        "@type": "Question",
                        "name": "Do you work alongside equipment manufacturer reps and installation leads?",
                        "acceptedAnswer": {
                            "@type": "Answer",
                            "text": "Yes. Our crew regularly works alongside brand representatives, project managers, and installation leads—following specs closely, organizing hardware, consolidating dunnage, and keeping multi-day installs on schedule.",
                        },
                    },
                    {
                        "@type": "Question",
                        "name": "Do you carry commercial insurance and provide Certificates of Insurance (COI)?",
                        "acceptedAnswer": {
                            "@type": "Answer",
                            "text": "Yes. Forged Labor carries full Commercial General Liability and Cargo insurance coverage for every commercial project, and Certificates of Insurance (COI) are available upon request.",
                        },
                    },
                    {
                        "@type": "Question",
                        "name": "How far will Forged Labor travel for commercial fitness installations?",
                        "acceptedAnswer": {
                            "@type": "Answer",
                            "text": "We are headquartered in Cleveland, TN along the I-75 corridor with daily coverage across Cleveland, Chattanooga, Ooltewah, Athens, and Knoxville, plus regional multi-day travel across Tennessee, North Georgia, Alabama, and the Southeast.",
                        },
                    },
                    {
                        "@type": "Question",
                        "name": "How do you protect rubber gym flooring, turf, and finished surfaces during installation?",
                        "acceptedAnswer": {
                            "@type": "Answer",
                            "text": "We stage plywood over vulnerable or high-traffic zones when needed, never drag heavy steel or machines across finished rubber, turf, or tile, and keep hardware and packaging consolidated throughout the build.",
                        },
                    },
                ],
            },
        ],
    },
    {
        "path": "/gallery",
        "title": "Behind The Scenes & Company Culture | Forged Labor",
        "description": "See Forged Labor in action: wall-to-wall truck packs, coordinated team lifts, commercial gym builds, and our Cleveland, TN crew culture.",
        "og_image": f"{DOMAIN}/images/og-home.png",
    },
    {
        "path": "/appointments",
        "title": "Schedule Your Move Online | Forged Labor",
        "description": "Check live availability and schedule your residential move, labor-only loading crew, or commercial fitness installation with Forged Labor in Cleveland & Chattanooga, TN.",
        "og_image": f"{DOMAIN}/images/og-home.png",
    },
    {
        "path": "/contact",
        "title": "Contact Forged Labor | Cleveland & Chattanooga Movers",
        "description": "Call (423) 715-4254 or send a message to schedule your move or commercial fitness installation with Forged Labor in Cleveland and Chattanooga, TN.",
        "og_image": f"{DOMAIN}/images/og-home.png",
    },
    {
        "path": "/movers-chattanooga-tn",
        "title": "Chattanooga, TN Movers | Full-Service & Labor-Only | Forged Labor",
        "description": "Trusted, insured Chattanooga, TN movers backed by 119+ five-star reviews. Full-service residential moves, labor-only loading/unloading, PODS, and heavy gym equipment moving.",
        "og_image": f"{DOMAIN}/images/og-home.png",
    },
    {
        "path": "/movers-ooltewah-tn",
        "title": "Ooltewah, TN Movers | Residential & Labor-Only Moving | Forged Labor",
        "description": "Top-rated Ooltewah and Hamilton County movers. Athletic, punctual, insured crews for full-service home moves, PODS/U-Haul loading, and heavy furniture delivery.",
        "og_image": f"{DOMAIN}/images/og-home.png",
    },
    {
        "path": "/labor-only-movers-cleveland-tn",
        "title": "Labor-Only Movers, PODS & U-Haul Loading in Cleveland, TN | Forged Labor",
        "description": "Verified U-Haul MovingHelper provider in Cleveland & Chattanooga, TN. Hire 2–5 strong, insured movers to load or unload your rental truck, PODS container, or storage unit.",
        "og_image": f"{DOMAIN}/images/og-home.png",
    },
    {
        "path": "/privacy",
        "title": "Privacy Policy | Forged Labor LLC",
        "description": "Privacy Policy for Forged Labor LLC covering booking inquiries, scheduling, transactional communications, and payment processing.",
        "og_image": f"{DOMAIN}/images/og-home.png",
    },
    {
        "path": "/terms",
        "title": "Terms of Service | Forged Labor LLC",
        "description": "Terms of Service, deposit, cancellation, and refund policies for Forged Labor LLC moving and commercial installation services.",
        "og_image": f"{DOMAIN}/images/og-home.png",
    },
]


def replace_tag(html_text: str, pattern: str, replacement: str) -> str:
    return re.sub(pattern, lambda _: replacement, html_text, count=1, flags=re.IGNORECASE | re.DOTALL)


def build_shell(base_html: str, route: dict) -> str:
    url = f"{DOMAIN}{route['path']}"
    title = html.escape(route["title"])
    desc = html.escape(route["description"], quote=True)
    og_img = html.escape(route["og_image"], quote=True)
    url_esc = html.escape(url, quote=True)

    out = base_html
    out = replace_tag(out, r"<title>.*?</title>", f"<title>{title}</title>")
    out = replace_tag(out, r'<meta\s+name="description"\s+content="[^"]*"\s*/?>', f'<meta name="description" content="{desc}" />')
    out = replace_tag(out, r'<link\s+rel="canonical"\s+href="[^"]*"\s*/?>', f'<link rel="canonical" href="{url_esc}" />')
    out = replace_tag(out, r'<meta\s+property="og:title"\s+content="[^"]*"\s*/?>', f'<meta property="og:title" content="{title}" />')
    out = replace_tag(out, r'<meta\s+property="og:description"\s+content="[^"]*"\s*/?>', f'<meta property="og:description" content="{desc}" />')
    out = replace_tag(out, r'<meta\s+property="og:image"\s+content="[^"]*"\s*/?>', f'<meta property="og:image" content="{og_img}" />')
    out = replace_tag(out, r'<meta\s+property="og:url"\s+content="[^"]*"\s*/?>', f'<meta property="og:url" content="{url_esc}" />')
    out = replace_tag(out, r'<meta\s+name="twitter:title"\s+content="[^"]*"\s*/?>', f'<meta name="twitter:title" content="{title}" />')
    out = replace_tag(out, r'<meta\s+name="twitter:description"\s+content="[^"]*"\s*/?>', f'<meta name="twitter:description" content="{desc}" />')
    out = replace_tag(out, r'<meta\s+name="twitter:image"\s+content="[^"]*"\s*/?>', f'<meta name="twitter:image" content="{og_img}" />')

    extra_ld = route.get("extra_json_ld")
    if extra_ld:
        scripts = []
        for obj in extra_ld:
            scripts.append(f'    <script type="application/ld+json">\n{json.dumps(obj, indent=6)}\n    </script>')
        injection = "\n".join(scripts) + "\n</head>"
        out = out.replace("</head>", injection, 1)

    return out


def main() -> None:
    if len(sys.argv) < 2:
        print("Usage: python3 scripts/generate-route-shells.py <published_wwwroot_dir>")
        sys.exit(1)

    wwwroot = Path(sys.argv[1]).resolve()
    index_path = wwwroot / "index.html"
    if not index_path.exists():
        print(f"Error: {index_path} not found")
        sys.exit(1)

    base_html = index_path.read_text(encoding="utf-8")
    for route in ROUTES:
        rel = route["path"].lstrip("/")
        target_dir = wwwroot / rel
        target_dir.mkdir(parents=True, exist_ok=True)
        shell_html = build_shell(base_html, route)
        (target_dir / "index.html").write_text(shell_html, encoding="utf-8")

    print(f"Generated {len(ROUTES)} route-specific SEO HTML shells in {wwwroot}.")


if __name__ == "__main__":
    main()
