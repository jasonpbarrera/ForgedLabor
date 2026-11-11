using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SmallBusinessSite.Client.Models;

namespace SmallBusinessSite.Client
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var builder = WebAssemblyHostBuilder.CreateDefault(args);
            builder.RootComponents.Add<App>("#app");
            builder.RootComponents.Add<HeadOutlet>("head::after");

            // Bind all site sections from wwwroot/appsettings.json
            builder.Services.Configure<BusinessSettings>(builder.Configuration.GetSection("BusinessSettings"));
            builder.Services.Configure<ThemeSettings>(builder.Configuration.GetSection("ThemeSettings"));
            builder.Services.Configure<HomePageSettings>(builder.Configuration.GetSection("HomePageSettings"));
            builder.Services.Configure<AboutPageSettings>(builder.Configuration.GetSection("AboutPageSettings"));
            builder.Services.Configure<ServicesPageSettings>(builder.Configuration.GetSection("ServicesPageSettings"));
            builder.Services.Configure<ContactPageSettings>(builder.Configuration.GetSection("ContactPageSettings"));

            builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

            await builder.Build().RunAsync();
        }
    }
}
