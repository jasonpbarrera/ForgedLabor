#!/usr/bin/env python3
"""
Master Agency CLI & Client Onboarding Automation for SmallBusinessSiteTemplate.
Manages the 3-container Azure Cosmos DB backend (`clients`, `leads`, `billing`)
and automates end-to-end client provisioning across:
  1. Azure Cosmos DB (`clients` + `billing` containers)
  2. Cloudflare API (Zone lookup, CNAME to Azure SWA, Email Routing rules -> Owner's Gmail)
  3. Brevo API (Sender domain creation & automatic DKIM/SPF TXT injection into Cloudflare DNS)

Usage Examples:
  # 1. Register or update the current appsettings.json client in Cosmos DB (and optionally provision Cloudflare + Brevo)
  python3 scripts/onboard-client.py onboard --owner-name "Alex Morgan" --owner-gmail "alex@gmail.com" --plan "Free Community Tier" --rate 0

  # 2. Log a custom work charge or invoice for a client in the `billing` container
  python3 scripts/onboard-client.py add-charge --business-id "acme-local-services" --title "Custom Photo Gallery Section" --amount 150 --status "pending"

  # 3. View your Agency Dashboard (all clients, lead counts, and billing totals from Cosmos DB)
  python3 scripts/onboard-client.py status
"""

import argparse
import base64
from datetime import datetime, timezone
import hashlib
import hmac
import json
import os
from pathlib import Path
import urllib.error
import urllib.parse
import urllib.request
import uuid

ROOT_DIR = Path(__file__).resolve().parent.parent
APPSETTINGS_PATH = ROOT_DIR / "Client" / "wwwroot" / "appsettings.json"
ENV_FILE = ROOT_DIR / ".env.agency"


def load_env_file() -> None:
    if not ENV_FILE.exists():
        return
    for line in ENV_FILE.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        k, v = line.split("=", 1)
        os.environ.setdefault(k.strip(), v.strip().strip('"').strip("'"))


def parse_cosmos_credentials() -> tuple[str, str, str]:
    endpoint = os.environ.get("COSMOS_ENDPOINT", "").rstrip("/")
    key = os.environ.get("COSMOS_KEY", "")
    db_name = os.environ.get("COSMOS_DATABASE_NAME", "SmallBusinessPlatform")

    conn_str = os.environ.get("COSMOS_CONNECTION_STRING", "")
    if conn_str:
        for part in conn_str.split(";"):
            if "=" not in part:
                continue
            k, v = part.split("=", 1)
            k = k.strip().lower()
            v = v.strip()
            if k == "accountendpoint":
                endpoint = v.rstrip("/")
            elif k == "accountkey":
                key = v

    if not endpoint or not key:
        raise RuntimeError(
            "Missing Cosmos DB credentials. Set COSMOS_CONNECTION_STRING (or COSMOS_ENDPOINT + COSMOS_KEY) in environment or .env.agency."
        )
    return endpoint, key, db_name


def cosmos_auth_header(verb: str, resource_type: str, resource_link: str, utc_date: str, master_key_b64: str) -> str:
    key_bytes = base64.b64decode(master_key_b64)
    payload = f"{verb.lower()}\n{resource_type.lower()}\n{resource_link}\n{utc_date.lower()}\n\n"
    digest = hmac.new(key_bytes, payload.encode("utf-8"), hashlib.sha256).digest()
    sig = base64.b64encode(digest).decode("utf-8")
    return urllib.parse.quote(f"type=master&ver=1.0&sig={sig}")


def cosmos_upsert_document(container: str, partition_key: str, doc: dict) -> dict:
    endpoint, key, db_name = parse_cosmos_credentials()
    resource_link = f"dbs/{db_name}/colls/{container}"
    url = f"{endpoint}/{resource_link}/docs"
    utc_date = datetime.now(timezone.utc).strftime("%a, %d %b %Y %H:%M:%S GMT")
    auth = cosmos_auth_header("post", "docs", resource_link, utc_date, key)

    body = json.dumps(doc).encode("utf-8")
    req = urllib.request.Request(url, data=body, method="POST")
    req.add_header("Authorization", auth)
    req.add_header("x-ms-date", utc_date)
    req.add_header("x-ms-version", "2018-12-31")
    req.add_header("x-ms-documentdb-is-upsert", "True")
    req.add_header("x-ms-documentdb-partitionkey", json.dumps([partition_key]))
    req.add_header("Content-Type", "application/json")

    with urllib.request.urlopen(req) as resp:
        return json.loads(resp.read().decode("utf-8"))


def cosmos_query_container(container: str, sql_query: str) -> list[dict]:
    endpoint, key, db_name = parse_cosmos_credentials()
    resource_link = f"dbs/{db_name}/colls/{container}"
    url = f"{endpoint}/{resource_link}/docs"
    utc_date = datetime.now(timezone.utc).strftime("%a, %d %b %Y %H:%M:%S GMT")
    auth = cosmos_auth_header("post", "docs", resource_link, utc_date, key)

    body = json.dumps({"query": sql_query, "parameters": []}).encode("utf-8")
    req = urllib.request.Request(url, data=body, method="POST")
    req.add_header("Authorization", auth)
    req.add_header("x-ms-date", utc_date)
    req.add_header("x-ms-version", "2018-12-31")
    req.add_header("x-ms-documentdb-isquery", "True")
    req.add_header("x-ms-documentdb-query-enablecrosspartition", "True")
    req.add_header("Content-Type", "application/query+json")

    with urllib.request.urlopen(req) as resp:
        data = json.loads(resp.read().decode("utf-8"))
        return data.get("Documents", [])


def api_request(url: str, method: str = "GET", headers: dict | None = None, payload: dict | None = None) -> dict:
    data = json.dumps(payload).encode("utf-8") if payload is not None else None
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("Content-Type", "application/json")
    for k, v in (headers or {}).items():
        req.add_header(k, v)
    with urllib.request.urlopen(req) as resp:
        raw = resp.read().decode("utf-8")
        return json.loads(raw) if raw else {}


def configure_cloudflare_and_brevo(domain: str, primary_email: str, admin_email: str, owner_gmail: str, swa_hostname: str) -> dict:
    """
    Uses Cloudflare v4 API + Brevo v3 API to:
      1. Look up Cloudflare Zone ID for `domain`
      2. Create CNAME record pointing `domain` / `www` to `swa_hostname` (if provided)
      3. Create Cloudflare Email Routing rules forwarding `primary_email` & `admin_email` -> `owner_gmail`
      4. Register `domain` in Brevo and automatically create Brevo DKIM TXT records in Cloudflare DNS
    """
    cf_token = os.environ.get("CLOUDFLARE_API_TOKEN", "")
    brevo_key = os.environ.get("BREVO_API_KEY", "")
    clean_domain = domain.replace("https://", "").replace("http://", "").strip("/")

    infra_status = {
        "cloudflareZoneId": "",
        "cloudflareEmailRoutingEnabled": False,
        "brevoDomainVerified": False
    }

    if not cf_token:
        print("  [i] CLOUDFLARE_API_TOKEN not set; skipping live Cloudflare DNS/Email API calls.")
        return infra_status

    cf_headers = {"Authorization": f"Bearer {cf_token}"}
    zones = api_request(f"https://api.cloudflare.com/client/v4/zones?name={clean_domain}", headers=cf_headers)
    result_list = zones.get("result", [])
    if not result_list:
        print(f"  [!] Zone '{clean_domain}' not found in Cloudflare account.")
        return infra_status

    zone_id = result_list[0]["id"]
    infra_status["cloudflareZoneId"] = zone_id
    print(f"  [+] Found Cloudflare Zone ID: {zone_id} for {clean_domain}")

    # Optional: Point CNAME to Azure Static Web App hostname
    if swa_hostname:
        for sub in ("@", "www"):
            try:
                api_request(
                    f"https://api.cloudflare.com/client/v4/zones/{zone_id}/dns_records",
                    method="POST",
                    headers=cf_headers,
                    payload={"type": "CNAME", "name": sub, "content": swa_hostname, "proxied": False, "ttl": 3600}
                )
                print(f"  [+] Added Cloudflare DNS CNAME ({sub} -> {swa_hostname})")
            except urllib.error.HTTPError as e:
                print(f"  [i] DNS CNAME '{sub}' note: {e.code}")

    # Configure Cloudflare Email Routing rules -> owner_gmail
    if owner_gmail:
        for addr in filter(None, [primary_email, admin_email]):
            try:
                api_request(
                    f"https://api.cloudflare.com/client/v4/zones/{zone_id}/email/routing/rules",
                    method="POST",
                    headers=cf_headers,
                    payload={
                        "name": f"Forward {addr} to owner",
                        "enabled": True,
                        "matchers": [{"type": "literal", "field": "to", "value": addr}],
                        "actions": [{"type": "forward", "value": [owner_gmail]}]
                    }
                )
                infra_status["cloudflareEmailRoutingEnabled"] = True
                print(f"  [+] Configured Cloudflare Email Routing: {addr} -> {owner_gmail}")
            except urllib.error.HTTPError as e:
                print(f"  [i] Email routing rule '{addr}' note: {e.code}")

    # Register domain in Brevo and auto-populate DNS records in Cloudflare
    if brevo_key:
        brevo_headers = {"api-key": brevo_key, "Accept": "application/json"}
        try:
            brevo_resp = api_request(
                "https://api.brevo.com/v3/senders/domains",
                method="POST",
                headers=brevo_headers,
                payload={"name": clean_domain}
            )
            dns_records = brevo_resp.get("dns_records", {})
            for rec_key, rec_val in dns_records.items():
                if isinstance(rec_val, dict) and rec_val.get("type") and rec_val.get("value"):
                    api_request(
                        f"https://api.cloudflare.com/client/v4/zones/{zone_id}/dns_records",
                        method="POST",
                        headers=cf_headers,
                        payload={
                            "type": rec_val["type"],
                            "name": rec_val.get("host_name", "@"),
                            "content": rec_val["value"],
                            "ttl": 3600
                        }
                    )
            infra_status["brevoDomainVerified"] = True
            print(f"  [+] Registered '{clean_domain}' in Brevo and synced DKIM/SPF records to Cloudflare DNS")
        except urllib.error.HTTPError as e:
            print(f"  [i] Brevo domain setup note: {e.code}")

    return infra_status


def cmd_onboard(args: argparse.Namespace) -> None:
    config_path = Path(args.config).resolve()
    config = json.loads(config_path.read_text(encoding="utf-8"))
    biz = config.get("BusinessSettings", {})

    business_id = biz.get("BusinessId", "demo-business")
    company_name = biz.get("CompanyName", "")
    short_name = biz.get("ShortName", "")
    domain = biz.get("Domain", "").replace("https://", "").replace("http://", "").strip("/")
    primary_email = biz.get("PrimaryEmail", "")
    admin_email = biz.get("AdminEmail", "")
    phone = biz.get("PhoneNumber", "")
    city = biz.get("City", "")
    state = biz.get("State", "")
    now_iso = datetime.now(timezone.utc).isoformat()

    print(f"==> Onboarding Client: {company_name} ({business_id})")

    infra = configure_cloudflare_and_brevo(
        domain=domain,
        primary_email=primary_email,
        admin_email=admin_email,
        owner_gmail=args.owner_gmail,
        swa_hostname=args.swa_hostname
    )
    infra["githubRepo"] = args.github_repo
    infra["azureStaticWebAppName"] = args.swa_name
    infra["azureStaticWebAppHostname"] = args.swa_hostname

    client_doc = {
        "id": business_id,
        "businessId": business_id,
        "companyName": company_name,
        "shortName": short_name,
        "ownerName": args.owner_name,
        "ownerGmail": args.owner_gmail,
        "primaryEmail": primary_email,
        "adminEmail": admin_email,
        "leadNotificationEmail": args.notification_email or primary_email,
        "phone": phone,
        "city": city,
        "state": state,
        "domain": domain,
        "isActive": True,
        "onboardingStatus": args.status,
        "infrastructure": infra,
        "billingSummary": {
            "planName": args.plan,
            "monthlyRateUsd": args.rate,
            "customWorkHourlyRateUsd": args.hourly_rate,
            "domainRenewalResponsibility": args.domain_payer
        },
        "siteConfiguration": config,
        "updatedAt": now_iso,
        "createdAt": now_iso
    }

    try:
        cosmos_upsert_document("clients", business_id, client_doc)
        print(f"  [+] Saved Client Profile & Site Configuration to Cosmos DB ('clients' container, id='{business_id}')")
    except Exception as ex:
        print(f"  [i] Direct Cosmos DB connection note: {ex}")

    # Seamlessly sync with the live Agency Management Portal API
    agency_portal_url = os.environ.get("AGENCY_PORTAL_URL", "https://purple-flower-020acac0f.4.azurestaticapps.net").rstrip("/")
    agency_key = os.environ.get("AGENCY_PASSCODE") or os.environ.get("ADMIN_PASSCODE", "JBDigital2026!")
    if agency_portal_url and agency_key:
        portal_payload = {
            "businessId": business_id,
            "companyName": company_name,
            "shortName": short_name,
            "tagline": biz.get("Tagline", ""),
            "seoDescription": biz.get("SeoDescription", ""),
            "ownerName": args.owner_name or "",
            "ownerGmail": args.owner_gmail or "",
            "primaryEmail": primary_email,
            "adminEmail": admin_email,
            "leadNotificationEmail": args.notification_email or primary_email or args.owner_gmail,
            "phoneNumber": phone,
            "city": city,
            "state": state,
            "primaryColor": config.get("ThemeSettings", {}).get("PrimaryColor", "#1e3a8a"),
            "accentColor": config.get("ThemeSettings", {}).get("AccentColor", "#f97316"),
            "domain": f"https://{domain}" if domain and not domain.startswith("http") else domain,
            "gitHubRepo": args.github_repo or "",
            "azureSwaName": args.swa_name or "",
            "azureSwaDefaultHostname": args.swa_hostname or "",
            "provisioningStatus": "Provisioned" if args.swa_hostname else "Pending",
            "billingStatus": "Active"
        }
        try:
            req = urllib.request.Request(
                f"{agency_portal_url}/api/clients",
                headers={"X-Agency-Key": agency_key, "Content-Type": "application/json"},
                data=json.dumps(portal_payload).encode("utf-8"),
                method="POST"
            )
            with urllib.request.urlopen(req) as resp:
                print(f"  [+] Synced Client to Agency Portal ({agency_portal_url}/clients)")
        except Exception as ex:
            print(f"  [i] Agency Portal API note: {ex}")

    if args.initial_setup_fee is not None:
        billing_id = f"setup-{business_id}"
        billing_doc = {
            "id": billing_id,
            "businessId": business_id,
            "createdAt": now_iso,
            "type": "one_time_setup",
            "title": "Initial Website Launch & Setup",
            "description": f"Onboarding & launch package ({args.plan})",
            "hours": 1.0,
            "amountUsd": args.initial_setup_fee,
            "billingCycle": "one-time",
            "status": "waived" if args.initial_setup_fee == 0 else "pending",
            "dueDate": None,
            "paidAt": now_iso if args.initial_setup_fee == 0 else None,
            "notes": "Auto-logged during client onboarding."
        }
        cosmos_upsert_document("billing", business_id, billing_doc)
        print(f"  [+] Logged initial setup billing record ('billing' container, amount=${args.initial_setup_fee:.2f})")

    # Phase 4: Broadcast to Search Crawlers (IndexNow Protocol)
    if domain and "example-business.com" not in domain:
        print("\nPhase 4: Broadcasting URLs to Search Crawlers (IndexNow Protocol)...")
        host = domain.replace("https://", "").replace("http://", "").split("/")[0]
        site_urls = [f"{domain}/", f"{domain}/services", f"{domain}/contact", f"{domain}/about"]
        key = "e3d29a08c8474f63bb9b7d8b5c9632f1"
        payload = json.dumps({
            "host": host,
            "key": key,
            "keyLocation": f"{domain}/{key}.txt",
            "urlList": site_urls
        }).encode("utf-8")
        for ep in ["https://api.indexnow.org/indexnow", "https://www.bing.com/indexnow"]:
            try:
                crawl_req = urllib.request.Request(
                    ep,
                    data=payload,
                    headers={"Content-Type": "application/json; charset=utf-8", "User-Agent": "JBDigitalOnboarding/1.0"}
                )
                with urllib.request.urlopen(crawl_req, timeout=10) as c_resp:
                    print(f"  [+] Submitted {len(site_urls)} URLs to {ep} (HTTP {c_resp.status})")
            except Exception as ex:
                print(f"  [i] Crawler submission notice ({ep}): {ex}")


def cmd_add_charge(args: argparse.Namespace) -> None:
    now_iso = datetime.now(timezone.utc).isoformat()
    charge_id = args.id or f"chg-{uuid.uuid4().hex[:8]}"
    billing_doc = {
        "id": charge_id,
        "businessId": args.business_id,
        "createdAt": now_iso,
        "type": args.type,
        "title": args.title,
        "description": args.description,
        "hours": args.hours,
        "amountUsd": args.amount,
        "billingCycle": args.cycle,
        "status": args.status,
        "dueDate": args.due_date,
        "paidAt": now_iso if args.status == "paid" else None,
        "notes": args.notes
    }
    cosmos_upsert_document("billing", args.business_id, billing_doc)
    print(f"==> Saved billing record '{charge_id}' for '{args.business_id}': ${args.amount:.2f} ({args.status})")


def cmd_status(_args: argparse.Namespace) -> None:
    clients = cosmos_query_container("clients", "SELECT * FROM c")
    leads = cosmos_query_container("leads", "SELECT c.businessId, c.status FROM c")
    bills = cosmos_query_container("billing", "SELECT c.businessId, c.amountUsd, c.status FROM c")

    print("\n======================================================================================")
    print(f"{'BUSINESS ID':<24} {'COMPANY NAME':<30} {'STATUS':<10} {'LEADS':<8} {'BILLED ($)':<12}")
    print("======================================================================================")
    for c in clients:
        bid = c.get("businessId", "")
        c_leads = [l for l in leads if l.get("businessId") == bid]
        c_bills = [b for b in bills if b.get("businessId") == bid and b.get("status") != "waived"]
        total_billed = sum(float(b.get("amountUsd", 0)) for b in c_bills)
        print(
            f"{bid:<24} {c.get('companyName', '')[:28]:<30} {c.get('onboardingStatus', 'live'):<10} "
            f"{len(c_leads):<8} ${total_billed:<11.2f}"
        )
    print("======================================================================================\n")


def main() -> None:
    load_env_file()
    parser = argparse.ArgumentParser(description="Agency Client Onboarding & Cosmos DB Tracking CLI")
    sub = parser.add_subparsers(dest="command", required=True)

    p_onboard = sub.add_parser("onboard", help="Onboard or update a client in Cosmos DB (+ Cloudflare/Brevo)")
    p_onboard.add_argument("--config", default=str(APPSETTINGS_PATH), help="Path to client's appsettings.json")
    p_onboard.add_argument("--owner-name", default="", help="Client business owner's full name")
    p_onboard.add_argument("--owner-gmail", default="", help="Owner's personal Gmail for Cloudflare Email Routing")
    p_onboard.add_argument("--notification-email", default="", help="Override email for lead notifications")
    p_onboard.add_argument("--status", default="live", choices=["intake", "provisioning", "live", "paused"])
    p_onboard.add_argument("--plan", default="Free Community Tier", help="Plan name")
    p_onboard.add_argument("--rate", type=float, default=0.0, help="Monthly recurring rate (USD)")
    p_onboard.add_argument("--hourly-rate", type=float, default=75.0, help="Custom work hourly rate (USD)")
    p_onboard.add_argument("--initial-setup-fee", type=float, default=0.0, help="Initial setup fee (USD)")
    p_onboard.add_argument("--domain-payer", default="client", choices=["client", "agency"])
    p_onboard.add_argument("--github-repo", default="", help="GitHub repo (owner/name)")
    p_onboard.add_argument("--swa-name", default="", help="Azure Static Web App name")
    p_onboard.add_argument("--swa-hostname", default="", help="Azure Static Web App default hostname")
    p_onboard.set_defaults(func=cmd_onboard)

    p_charge = sub.add_parser("add-charge", help="Record a custom work charge, subscription, or invoice in Cosmos DB")
    p_charge.add_argument("--business-id", required=True, help="Client businessId slug")
    p_charge.add_argument("--id", default="", help="Optional custom invoice/charge ID")
    p_charge.add_argument("--type", default="custom_work", choices=["custom_work", "subscription", "domain_renewal", "one_time_setup"])
    p_charge.add_argument("--title", required=True, help="Short title of work or charge")
    p_charge.add_argument("--description", default="", help="Detailed description of work completed")
    p_charge.add_argument("--hours", type=float, default=None, help="Billable hours (optional)")
    p_charge.add_argument("--amount", type=float, required=True, help="Amount in USD")
    p_charge.add_argument("--cycle", default="one-time", choices=["one-time", "monthly", "annual"])
    p_charge.add_argument("--status", default="pending", choices=["draft", "pending", "paid", "waived"])
    p_charge.add_argument("--due-date", default=None, help="Due date (YYYY-MM-DD)")
    p_charge.add_argument("--notes", default="", help="Internal billing notes")
    p_charge.set_defaults(func=cmd_add_charge)

    p_status = sub.add_parser("status", help="Display all clients, lead counts, and billing totals from Cosmos DB")
    p_status.set_defaults(func=cmd_status)

    args = parser.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
