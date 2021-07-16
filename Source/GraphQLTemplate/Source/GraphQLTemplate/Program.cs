namespace GraphQLTemplate
{
    using System;
    using System.IO;
    using System.Reflection;
    using System.Threading.Tasks;
    using Boxed.AspNetCore;
    using GraphQLTemplate.Options;
#if ApplicationInsights
    using Microsoft.ApplicationInsights.Extensibility;
#endif
    using Microsoft.AspNetCore.Builder;
    using Microsoft.AspNetCore.Hosting;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Hosting;
#if Serilog
    using Serilog;
    using Serilog.Extensions.Hosting;
#endif

    public sealed class Program
    {
        public static async Task<int> Main(string[] args)
        {
#if Serilog
            Log.Logger = CreateBootstrapLogger();
#endif
            IHostEnvironment? hostEnvironment = null;

            try
            {
#if Serilog
                Log.Information("Initialising.");
#endif
                var host = CreateHostBuilder(args).Build();
                hostEnvironment = host.Services.GetRequiredService<IHostEnvironment>();
                hostEnvironment.ApplicationName = AssemblyInformation.Current.Product;

#if Serilog
                Log.Information(
                    "Started {Application} in {Environment} mode.",
                    hostEnvironment.ApplicationName,
                    hostEnvironment.EnvironmentName);
#endif
                await host.RunAsync().ConfigureAwait(false);
#if Serilog
                Log.Information(
                    "Stopped {Application} in {Environment} mode.",
                    hostEnvironment.ApplicationName,
                    hostEnvironment.EnvironmentName);
#endif
                return 0;
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception exception)
#pragma warning restore CA1031 // Do not catch general exception types
            {
                if (hostEnvironment is null)
                {
#if Serilog
                    Log.Fatal(exception, "Application terminated unexpectedly while initialising.");
#else
                    Console.WriteLine("Application terminated unexpectedly while initialising.");
#endif
                }
                else
                {
#if Serilog
                    Log.Fatal(
                        exception,
                        "{Application} terminated unexpectedly in {Environment} mode.",
                        hostEnvironment.ApplicationName,
                        hostEnvironment.EnvironmentName);
#else
                    Console.WriteLine($"{hostEnvironment.ApplicationName} terminated unexpectedly in {hostEnvironment.EnvironmentName} mode.");
#endif
                }

                return 1;
            }
#if Serilog
            finally
            {
                Log.CloseAndFlush();
            }
#endif
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            new HostBuilder()
                .UseContentRoot(Directory.GetCurrentDirectory())
                .ConfigureHostConfiguration(
                    configurationBuilder => configurationBuilder
                        .AddEnvironmentVariables(prefix: "DOTNET_")
                        .AddIf(
                            args is not null,
                            x => x.AddCommandLine(args)))
                .ConfigureAppConfiguration((hostingContext, config) =>
                    AddConfiguration(config, hostingContext.HostingEnvironment, args))
#if Serilog
                .UseSerilog(ConfigureReloadableLogger)
#endif
                .UseDefaultServiceProvider(
                    (context, options) =>
                    {
                        var isDevelopment = context.HostingEnvironment.IsDevelopment();
                        options.ValidateScopes = isDevelopment;
                        options.ValidateOnBuild = isDevelopment;
                    })
                .ConfigureWebHost(ConfigureWebHostBuilder)
                .UseConsoleLifetime();

        private static void ConfigureWebHostBuilder(IWebHostBuilder webHostBuilder) =>
            webHostBuilder
                .UseKestrel(
                    (builderContext, options) =>
                    {
                        options.AddServerHeader = false;
                        options.Configure(builderContext.Configuration.GetSection(nameof(ApplicationOptions.Kestrel)), reloadOnChange: false);
                    })
#if Azure
                .UseAzureAppServices()
#endif
                // Used for IIS and IIS Express for in-process hosting. Use UseIISIntegration for out-of-process hosting.
                .UseIIS()
                .ConfigureServices(
                    services => services.Configure<IISServerOptions>(options => options.AllowSynchronousIO = true))
                .UseStartup<Startup>();

        private static IConfigurationBuilder AddConfiguration(
            IConfigurationBuilder configurationBuilder,
            IHostEnvironment hostEnvironment,
            string[] args) =>
            configurationBuilder
                // Add configuration from the appsettings.json file.
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                // Add configuration from an optional appsettings.development.json, appsettings.staging.json or
                // appsettings.production.json file, depending on the environment. These settings override the ones in
                // the appsettings.json file.
                .AddJsonFile($"appsettings.{hostEnvironment.EnvironmentName}.json", optional: true, reloadOnChange: false)
                // Add configuration from files in the specified directory. The name of the file is the key and the
                // contents the value.
                .AddKeyPerFile(Path.Combine(Directory.GetCurrentDirectory(), "configuration"), optional: true, reloadOnChange: false)
                // This reads the configuration keys from the secret store. This allows you to store connection strings
                // and other sensitive settings, so you don't have to check them into your source control provider.
                // Only use this in Development, it is not intended for Production use. See
                // http://docs.asp.net/en/latest/security/app-secrets.html
                .AddIf(
                    hostEnvironment.IsDevelopment() && !string.IsNullOrEmpty(hostEnvironment.ApplicationName),
                    x => x.AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true, reloadOnChange: false))
                // Add configuration specific to the Development, Staging or Production environments. This config can
                // be stored on the machine being deployed to or if you are using Azure, in the cloud. These settings
                // override the ones in all of the above config files. See
                // http://docs.asp.net/en/latest/security/app-secrets.html
                .AddEnvironmentVariables()
#if ApplicationInsights
                // Push telemetry data through the Azure Application Insights pipeline faster in the development and
                // staging environments, allowing you to view results immediately.
                .AddApplicationInsightsSettings(developerMode: !hostEnvironment.IsProduction())
#endif
                // Add command line options. These take the highest priority.
                .AddIf(
                    args is not null,
                    x => x.AddCommandLine(args));
#if Serilog

        /// <summary>
        /// Creates a logger used during application initialisation.
        /// <see href="https://nblumhardt.com/2020/10/bootstrap-logger/"/>.
        /// </summary>
        /// <returns>A logger that can load a new configuration.</returns>
        private static ReloadableLogger CreateBootstrapLogger() =>
            new LoggerConfiguration()
                .WriteTo.Console()
                .WriteTo.Debug()
                .CreateBootstrapLogger();

        /// <summary>
        /// Configures a logger used during the applications lifetime.
        /// <see href="https://nblumhardt.com/2020/10/bootstrap-logger/"/>.
        /// </summary>
        private static void ConfigureReloadableLogger(
            HostBuilderContext context,
            IServiceProvider services,
            LoggerConfiguration configuration) =>
            configuration
                .ReadFrom.Configuration(context.Configuration)
                .ReadFrom.Services(services)
                .Enrich.WithProperty("Application", context.HostingEnvironment.ApplicationName)
                .Enrich.WithProperty("Environment", context.HostingEnvironment.EnvironmentName)
#if ApplicationInsights
                .WriteTo.Conditional(
                    x => context.HostingEnvironment.IsProduction(),
                    x => x.ApplicationInsights(
                        services.GetRequiredService<TelemetryConfiguration>(),
                        TelemetryConverter.Traces))
#endif
                .WriteTo.Conditional(
                    x => context.HostingEnvironment.IsDevelopment(),
                    x => x.Console().WriteTo.Debug());
#endif
    }
}
