using Wms.Core;
using Wms.Core.Entities;
using Wms.Data;
using Wms.Data.Auditing;
using Wms.Data.Configuration;
using Wms.Data.Lpm;
using Wms.Web.Auth;
using Wms.Web.Components;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using MudBlazor;
using MudBlazor.Services;

namespace Wms.Web;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Kestrel (default) — Negotiate-on-HTTP.sys is gone. Entra OIDC works on
        // Linux App Service and locally on any OS.

        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents(o => o.DetailedErrors = true);

        // Bump the SignalR receive limit so large render diffs (e.g. an allocation
        // grid with thousands of rows) don't blow up with the default 32 KB cap.
        builder.Services.Configure<Microsoft.AspNetCore.SignalR.HubOptions>(o =>
        {
            o.MaximumReceiveMessageSize = 32 * 1024 * 1024; // 32 MB
        });
        builder.Services.Configure<Microsoft.AspNetCore.Components.Server.CircuitOptions>(o =>
        {
            o.DetailedErrors = true;
        });
        builder.Logging.AddFilter("Microsoft.AspNetCore.Components", LogLevel.Information);
        builder.Services.AddMudServices(config =>
        {
            config.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.TopRight;
            config.SnackbarConfiguration.NewestOnTop = true;
            config.SnackbarConfiguration.ShowCloseIcon = true;
            config.SnackbarConfiguration.VisibleStateDuration = 5000;
        });
        builder.Services.AddMemoryCache();
        builder.Services.AddHttpContextAccessor();

        builder.Services.AddDataProtection()
            .SetApplicationName("Wms")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "App_Data", "keys")));

        // Connection resolver replaces FileConnectionConfig / IConnectionConfig.
        // Reads WmsAzure + per-country + OnPremBackupDB conn strings from
        // IConfiguration (appsettings.json + App Service config + User Secrets).
        builder.Services.AddSingleton<IOnPremConnectionResolver, OnPremConnectionResolver>();

        builder.Services.AddScoped<ICurrentUser, AuthStateCurrentUser>();
        builder.Services.AddSingleton<AuditSaveChangesInterceptor>();
        builder.Services.AddScoped<IActionLogger, ActionLogger>();
        builder.Services.AddScoped<BuildingService>();
        builder.Services.AddScoped<ContainerAllocationService>();
        builder.Services.AddScoped<CdcStoreAllocationService>();
        builder.Services.AddScoped<BulkPoAllocationService>();
        builder.Services.AddScoped<Pass5FlaggedAllocationService>();
        builder.Services.AddScoped<ContainerAllocationDataSyncService>();
        builder.Services.AddScoped<OpenContainerService>();
        builder.Services.AddScoped<PendingForCountingService>();
        builder.Services.AddScoped<ManualAllocationService>();
        builder.Services.AddScoped<MaxCapService>();
        builder.Services.AddScoped<DivStoresTurnsService>();
        builder.Services.AddScoped<GinTrailerUpdateService>();
        builder.Services.AddScoped<Wms.Data.Encoding.ItemEncodingService>();

        // WMS Itemmaster external API — Building's Tier-3 fallback goes through
        // this before falling through to Generated Barcode / usa.upcbarcodes.
        builder.Services.Configure<Wms.Data.ItemMaster.ItemMasterApiOptions>(
            builder.Configuration.GetSection(Wms.Data.ItemMaster.ItemMasterApiOptions.SectionName));
        // Short HttpClient timeout so an unreachable API host fails fast and
        // the scan flow falls through to Generated Barcode / usa.upcbarcodes
        // instead of hanging on the default 100-second timeout.
        builder.Services.AddHttpClient<Wms.Data.ItemMaster.ItemMasterApiClient>(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(5);
        });

        builder.Services.AddScoped<ReportsService>();
        builder.Services.AddScoped<CountingCompletionTodayService>();
        builder.Services.AddScoped<WarehouseBoxesService>();
        builder.Services.AddScoped<WarehouseSohSummaryService>();
        builder.Services.AddScoped<EcomStockVarianceReportService>();
        builder.Services.AddScoped<WarehouseIncentivesService>();
        builder.Services.AddScoped<IncentivesSettingsService>();
        builder.Services.AddScoped<TechnoPairingService>();
        builder.Services.AddScoped<TechnoBuildingService>();
        builder.Services.AddScoped<TechnoPricingService>();
        builder.Services.AddScoped<EcomProductionReportsService>();
        builder.Services.AddScoped<TechnoTeamProductionService>();
        builder.Services.AddScoped<RfidProductionService>();
        builder.Services.AddScoped<JafzaExportCheckingService>();
        builder.Services.AddScoped<JafzaExportPairingService>();
        builder.Services.AddScoped<TransferGinGrnService>();
        builder.Services.AddScoped<ShipmentStatusService>();
        builder.Services.AddScoped<YotoVnaDashboardService>();
        builder.Services.AddScoped<SyncDataCountService>();
        builder.Services.AddScoped<MissingExcessSnapshotService>();
        builder.Services.AddScoped<CountingReportsService>();
        builder.Services.AddScoped<JafzaDivisionProductionService>();
        builder.Services.AddScoped<JafzaRoboProductionService>();
        builder.Services.AddScoped<JafzaExportProductionService>();
        builder.Services.AddScoped<JafzaBoxGrnProductionService>();
        builder.Services.AddScoped<OtsPoAllocationService>();
        builder.Services.AddScoped<CdcBoxAllocationService>();
        builder.Services.AddScoped<TcmImportService>();

        // Weekly sales pull from BigQuery (mvp-data-bi.cdm_silver.it_sales_qty) into
        // each active country's on-prem LPM_Weekly_SalesAmt.
        builder.Services.Configure<Wms.Data.Gcp.GcpBigQueryOptions>(
            builder.Configuration.GetSection(Wms.Data.Gcp.GcpBigQueryOptions.SectionName));
        builder.Services.AddScoped<WeeklySalesFromGcpService>();
        builder.Services.AddScoped<VolumeGroupWeeklyService>();

        // Weekly warehouse-stock-last-day pull from BigQuery (mvp-data-bi.cdm_silver.
        // wh_stock_last_day) into dbo.WMS_WHSTOCK_LASTDAY, filtered per active country
        // (unlike WeeklySalesFromGCP's source, this one already carries its own Country
        // column). Same "BigQuery" config section as WeeklySalesFromGcpService above.
        builder.Services.AddScoped<WhStockLastDayFromGcpService>();

        // On-demand ECOM SOH pull from BigQuery (mvp-data-bi.Ecom_Bronze.INCREFF_*_SOH)
        // into LPMSIM's dbo.LPM_ECOM_INCREFF_SOH. No timer yet — Refresh Now only.
        builder.Services.AddScoped<IncreffSohFromGcpService>();

        // Pull from a newer BigQuery source (mvp-data-bi.Ecom_Silver.
        // Increff_Item_Level_SOH) into LPMSIM's dbo.LPM_ECOM_INCREFF_SOH_NEW —
        // now what IncreffMfcsSohCompareService reads for IncreffSOH, after
        // being validated in parallel against the older dbo.LPM_ECOM_INCREFF_SOH.
        builder.Services.AddScoped<IncreffSohFromGcpNewService>();

        // On-demand comparison of that INCREFF feed against RACKS.dbo.lpm_locstock
        // (MFCS online-store stock) into dbo.LPM_ECOM_SOH_COMPARISON. No timer yet.
        builder.Services.AddScoped<IncreffMfcsSohCompareService>();

        // Generic (JobName, Country) activation + run-log access over the shared
        // WmsRptCountryConfig / WmsRptJobRun tables, used by the newer batch jobs.
        builder.Services.AddScoped<ScheduledJobService>();
        builder.Services.AddScoped<OtsWeeklyService>();

        // Aggregates dbo.LPM_Weekly_SalesAmt into dbo.LPM_SalesTurns for the current +
        // previous GST month, chained after WeeklySalesFromGCP succeeds each Sunday.
        builder.Services.AddScoped<LpmSalesTurnsRefreshService>();

        // Backfills DATAREPORTING.dbo.UPC_SUBCLASS.EAN13 from ORACLE_SKU. On-demand
        // only ("Generate Now" on the Nightly Batches admin page) — no timer.
        builder.Services.AddScoped<GenerateEan13Service>();

        // Pushes GIN-linked shop-issue transfers to the external store-inbounds API.
        // Each run enqueues new GINs into LPMSIM.dbo.APICallGIN, then POSTs unsent ones.
        // Hourly via ApiGinIntegrationBatchService (gated by its Nightly Batches toggle),
        // plus "Send Now" on demand.
        //
        // ApiKey is shared with ApiEpcIntegration below — both Altavant endpoints take
        // the same key — so it's read from the single top-level "apikey" App
        // Service setting rather than each service's own config section.
        builder.Services.Configure<ApiGinIntegrationOptions>(
            builder.Configuration.GetSection(ApiGinIntegrationOptions.SectionName));
        builder.Services.PostConfigure<ApiGinIntegrationOptions>(
            o => o.ApiKey = builder.Configuration["apikey"] ?? "");
        builder.Services.AddHttpClient<ApiGinIntegrationService>();

        // Pushes EPC/RFID tag events (DATAREPORTING.dbo.EPCBarcodes) to the external
        // EPC imports API. Hourly via ApiEpcIntegrationBatchService (gated by its
        // Nightly Batches toggle), plus "Send Now" on demand.
        // Shares the same "apikey" setting as ApiGinIntegration above.
        builder.Services.Configure<Wms.Data.Lpm.ApiEpcIntegrationOptions>(
            builder.Configuration.GetSection(Wms.Data.Lpm.ApiEpcIntegrationOptions.SectionName));
        builder.Services.PostConfigure<Wms.Data.Lpm.ApiEpcIntegrationOptions>(
            o => o.ApiKey = builder.Configuration["apikey"] ?? "");
        builder.Services.AddHttpClient<ApiEpcIntegrationService>();

        // Robotics chute-mapping/status APIs used by the Chute Mapping page.
        builder.Services.Configure<Wms.Data.Robotic.RoboticApiOptions>(
            builder.Configuration.GetSection(Wms.Data.Robotic.RoboticApiOptions.SectionName));
        builder.Services.AddHttpClient<Wms.Data.Robotic.ChuteMappingService>();
        builder.Services.AddHostedService<Wms.Web.Hosting.NightlyBatchService>();
        builder.Services.AddHostedService<Wms.Web.Hosting.WeeklySalesBatchService>();
        builder.Services.AddHostedService<Wms.Web.Hosting.WhStockLastDayBatchService>();
        builder.Services.AddHostedService<Wms.Web.Hosting.WeeklyVolumeGroupBatchService>();
        builder.Services.AddHostedService<Wms.Web.Hosting.WeeklyOtsBatchService>();
        builder.Services.AddHostedService<Wms.Web.Hosting.ToteMasterScheduledService>();
        builder.Services.AddHostedService<Wms.Web.Hosting.BoxesToWmsProdScheduledService>();
        builder.Services.AddHostedService<Wms.Web.Hosting.GenerateEan13BatchService>();
        builder.Services.AddHostedService<Wms.Web.Hosting.ApiEpcIntegrationBatchService>();
        builder.Services.AddHostedService<Wms.Web.Hosting.ApiGinIntegrationBatchService>();
        builder.Services.AddHostedService<Wms.Web.Hosting.PendingGoodsReceiptEmailScheduledService>();
        builder.Services.AddScoped<Wms.Web.Hosting.PendingGoodsReceiptEmailSender>();
        builder.Services.AddScoped<Wms.Data.Notifications.PendingGoodsReceiptEmailService>();

        // Weekly RD (RTV) / RS (MC HOLD) ageing mail — same shape as above, but
        // fires on a configured day + GST time instead of an interval.
        builder.Services.AddHostedService<Wms.Web.Hosting.RdRsAgeingEmailScheduledService>();
        builder.Services.AddScoped<Wms.Web.Hosting.RdRsAgeingEmailSender>();
        builder.Services.AddScoped<Wms.Data.Notifications.RdRsAgeingEmailService>();
        builder.Services.AddScoped<RdRsAgeingService>();

        // Daily 08:00 GST: pull ECOM SOH from BigQuery. Daily 08:15 GST: compare it
        // against MFCS stock — a fixed offset after the pull, not a wait-chain (see
        // IncreffMfcsSohCompareBatchService for the readiness-check/defer behavior).
        builder.Services.AddHostedService<Wms.Web.Hosting.IncreffSohFromGcpBatchService>();
        builder.Services.AddHostedService<Wms.Web.Hosting.IncreffMfcsSohCompareBatchService>();

        // Daily 08:05 GST: pull from the newer Silver-tier BigQuery source (now
        // what IncreffMfcsSohCompare's 08:15 GST compare reads), offset 5 minutes
        // after the older 08:00 GST pull so the two independent BigQuery queries
        // don't contend.
        builder.Services.AddHostedService<Wms.Web.Hosting.IncreffSohFromGcpNewBatchService>();

        // WMS DbContext — Azure SQL via AAD (Managed Identity in App Service,
        // AAD Default locally via `az login`). NO password in code.
        builder.Services.AddDbContextFactory<WmsDbContext>((sp, o) =>
        {
            var resolver = sp.GetRequiredService<IOnPremConnectionResolver>();
            o.UseSqlServer(resolver.GetWmsAzureConnectionString());
            o.AddInterceptors(sp.GetRequiredService<AuditSaveChangesInterceptor>());
        }, ServiceLifetime.Scoped);

        // Entra OIDC — mirrors Barcode Generator's MSAL flow but using
        // Microsoft.Identity.Web (the .NET equivalent of @azure/msal-node).
        builder.Services
            .AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
            .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));

        // Second scheme, "Bearer", alongside the cookie/OIDC login above — for
        // machine-to-machine callers of /api/v1 (see Admin > API > Client Apps and
        // OAuthController's client-credentials token endpoint). Calling
        // AddAuthentication() again (no default-scheme arg) does not touch the
        // OpenIdConnect default already configured; it just registers this handler.
        builder.Services.AddAuthentication().AddJwtBearer(options =>
        {
            var jwtSection = builder.Configuration.GetSection("ApiJwt");
            var signingKey = jwtSection["SigningKey"];
            options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = jwtSection["Issuer"],
                ValidateAudience = true,
                ValidAudience = jwtSection["Audience"],
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
                    System.Text.Encoding.UTF8.GetBytes(string.IsNullOrEmpty(signingKey) ? new string('0', 32) : signingKey)),
                ClockSkew = TimeSpan.FromSeconds(30),
            };
        });
        builder.Services.AddScoped<Wms.Data.Api.ApiClientAppService>();
        builder.Services.AddScoped<Wms.Data.Api.StoreGrnService>();
        builder.Services.AddScoped<Wms.Data.Api.StoreStocktakeService>();

        if (builder.Environment.IsDevelopment())
        {
            // OIDC's correlation/nonce cookies default to SameSite=None, which browsers
            // only honor when the Secure flag is set — but local dev runs over plain
            // http://localhost, so the cookie never survives the round trip back from
            // Entra and sign-in fails ("message.State is null or empty" / "Correlation
            // failed"). Production is HTTPS-only so it keeps the default (stricter) policy.
            //
            // PostConfigure (not Configure) so this runs AFTER Microsoft.Identity.Web's
            // own option setup, which otherwise clobbers these values. Forcing
            // response_type=code + response_mode=query makes the callback a plain GET
            // redirect — required for SameSite=Lax cookies to actually be sent, since
            // Lax excludes cross-site POSTs (the default form_post response mode).
            builder.Services.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
            {
                options.ResponseType = Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectResponseType.Code;
                options.ResponseMode = Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectResponseMode.Query;
                options.CorrelationCookie.SameSite = SameSiteMode.Lax;
                options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.NonceCookie.SameSite = SameSiteMode.Lax;
                options.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            });
        }

        // Auth cookie has a hard 24-hour lifetime from sign-in. SlidingExpiration is OFF
        // so the cookie does NOT roll on activity — users are signed in once per day
        // (morning login lasts the whole working day, then re-auth next morning).
        // Pairs with the in-browser idle timer in App.razor.
        builder.Services.Configure<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>(
            Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme,
            o =>
            {
                o.ExpireTimeSpan    = TimeSpan.FromHours(24);
                o.SlidingExpiration = false;
            });

        builder.Services.AddControllersWithViews();
        builder.Services.AddRazorPages()
            .AddMicrosoftIdentityUI();

        builder.Services.AddScoped<IClaimsTransformation, WmsClaimsTransformer>();

        builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, Wms.Web.Auth.MenuAccessHandler>();
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(AuthPolicies.RequireActiveUser, p => p
                .RequireAuthenticatedUser()
                .RequireClaim(WmsClaimsTransformer.ActiveClaim, "1"));

            // One policy per menu key: Authorize(Policy = MenuKeys.X) on pages
            // and AuthorizeView Policy="MenuKeys.X" in NavMenu both resolve here.
            foreach (var menu in MenuKeys.All)
            {
                options.AddPolicy(menu.Key, p => p
                    .RequireAuthenticatedUser()
                    .AddRequirements(new Wms.Web.Auth.MenuAccessRequirement(menu.Key)));
            }

            // All endpoints require auth by default. AllowAnonymous on the
            // OIDC sign-in/sign-out endpoints is set by Microsoft.Identity.Web.UI.
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
        });
        builder.Services.AddCascadingAuthenticationState();

        var app = builder.Build();

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error", createScopeForErrors: true);
            app.UseHsts();
        }

        app.UseHttpsRedirection();
        app.UseStaticFiles();
        app.UseRouting();
        app.UseAntiforgery();

        app.UseAuthentication();
        app.UseAuthorization();

        // Logs every /api/v1/* call — method, path, status code, client identity, timing.
        // Deliberately never reads request/response bodies, so it never touches the JSON payload.
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/api/v1", out _))
            {
                await next();
                return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            Exception? thrown = null;
            try
            {
                await next();
            }
            catch (Exception ex)
            {
                thrown = ex;
                throw;
            }
            finally
            {
                sw.Stop();
                try
                {
                    var dbFactory = context.RequestServices.GetRequiredService<IDbContextFactory<WmsDbContext>>();
                    await using var db = await dbFactory.CreateDbContextAsync();
                    db.ApiRequestLogs.Add(new ApiRequestLog
                    {
                        Timestamp = DateTime.UtcNow,
                        Method = context.Request.Method,
                        Path = context.Request.Path.Value ?? "",
                        StatusCode = thrown is null ? context.Response.StatusCode : 500,
                        ClientId = context.User.FindFirst("client_id")?.Value,
                        ClientName = context.User.FindFirst("name")?.Value,
                        ClientIp = context.Connection.RemoteIpAddress?.ToString(),
                        DurationMs = (int)sw.ElapsedMilliseconds,
                    });
                    await db.SaveChangesAsync();
                }
                catch
                {
                    // Logging must never break the actual API response.
                }
            }
        });

        // Microsoft.Identity.Web.UI controllers + razor pages handle /MicrosoftIdentity/Account/*.
        app.MapControllers();
        app.MapRazorPages();

        app.MapStaticAssets();
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

        app.Run();
    }
}
