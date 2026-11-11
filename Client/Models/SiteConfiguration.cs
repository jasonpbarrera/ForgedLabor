namespace SmallBusinessSite.Client.Models
{
    public class BusinessSettings
    {
        /// <summary>
        /// Unique slug/identifier used to partition records in the shared multi-tenant Azure Cosmos DB database (e.g., "acme-local-services").
        /// </summary>
        public string BusinessId { get; set; } = "acme-local-services";
        public string CompanyName { get; set; } = string.Empty;
        public string ShortName { get; set; } = string.Empty;
        public string Tagline { get; set; } = string.Empty;
        public string SeoDescription { get; set; } = string.Empty;
        public string Domain { get; set; } = string.Empty;
        public string PhoneNumber { get; set; } = string.Empty;
        public string PrimaryEmail { get; set; } = string.Empty;
        public string AdminEmail { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string FacebookUrl { get; set; } = string.Empty;
        public string InstagramUrl { get; set; } = string.Empty;
        public string LogoUrl { get; set; } = "images/logo.jpg";
    }

    public class ThemeSettings
    {
        public string PrimaryColor { get; set; } = "#1e3a5f";
        public string AccentColor { get; set; } = "#d97706";
        public string SurfaceColor { get; set; } = "#f1f5f9";
        public string BackgroundColor { get; set; } = "#f8fafc";
    }

    public class FeatureCard
    {
        public string Icon { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }

    public class HomePageSettings
    {
        public string HeroDescription { get; set; } = string.Empty;
        public string PrimaryCtaText { get; set; } = "Get Started Today";
        public string SecondaryCtaText { get; set; } = "Meet The Founder";
        public string ValuesHeading { get; set; } = string.Empty;
        public string ValuesSubtitle { get; set; } = string.Empty;
        public List<FeatureCard> Pillars { get; set; } = new();
        public string StoryHeading { get; set; } = "Why We Started";
        public string StoryQuote { get; set; } = string.Empty;
        public string StoryAttribution { get; set; } = string.Empty;
    }

    public class AboutPageSettings
    {
        public string NavTitle { get; set; } = "Meet Me";
        public string Heading { get; set; } = "Meet Me ♡";
        public string Subheading { get; set; } = "I'm so glad you're here!";
        public string LeadText { get; set; } = string.Empty;
        public string WhoIAmTitle { get; set; } = "♡ Who I Am";
        public List<string> WhoIAmBullets { get; set; } = new();
        public string WhyStartedTitle { get; set; } = "♡ Why I Started My Company";
        public List<string> WhyStartedParagraphs { get; set; } = new();
        public string ClosingQuote { get; set; } = string.Empty;
        public string ClosingText { get; set; } = string.Empty;
        public string ThankYouNote { get; set; } = "Thank you for supporting our small business!";
        public List<string> CoreValues { get; set; } = new();
    }

    public class ServicesPageSettings
    {
        public string Heading { get; set; } = string.Empty;
        public string LeadText { get; set; } = string.Empty;
        public List<FeatureCard> Services { get; set; } = new();
        public string SpotlightHeading { get; set; } = string.Empty;
        public string SpotlightText { get; set; } = string.Empty;
        public bool ShowHiringBanner { get; set; } = true;
        public string HiringHeading { get; set; } = "We Are Hiring!";
        public string HiringText { get; set; } = string.Empty;
    }

    public class ContactPageSettings
    {
        public string Heading { get; set; } = "Let's Get in Touch";
        public string LeadText { get; set; } = string.Empty;
        public string ApiEndpoint { get; set; } = "/api/leads";
        public List<string> ContactMethods { get; set; } = new() { "Phone Call", "Text Message", "Email" };
        public string ServiceSelectLabel { get; set; } = "Service Needed";
        public List<string> ServiceOptions { get; set; } = new();
        public bool ShowStartDate { get; set; } = true;
        public bool StartDateRequired { get; set; } = false;
        public string StartDateLabel { get; set; } = "Preferred Start Date (Optional)";
    }

    public class LeadSubmissionRequest
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
}
