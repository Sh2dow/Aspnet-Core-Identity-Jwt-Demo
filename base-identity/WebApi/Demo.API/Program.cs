using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RainstormTech.API.Components;
using RainstormTech.Components.Authorization;
using RainstormTech.Data.Data;
using RainstormTech.Helpers;
using RainstormTech.Models.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using RainstormTech.Services.Interfaces;
using RainstormTech.Services.User;
using RainstormTech.Services.CoreServices;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace RainstormTech
{
    public class Program
    {
        public static void Main(string[] args)
        {

            var builder = WebApplication.CreateBuilder(args);

            // Application Insights
            // TODO: let's do this for prod and works only on windows app services
            builder.Services.AddApplicationInsightsTelemetry(builder.Configuration);

            // enable caching
            builder.Services.AddMemoryCache();

            // Register the Swagger generator, defining 1 or more Swagger documents
            builder.Services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1",
                    new Microsoft.OpenApi.Models.OpenApiInfo
                    {
                        Title = "Sweet System API",
                        Version = "v1",
                        Description = "API for handling user/content data",
                        // TermsOfService = new Uri("https://example.com/terms"),
                        Contact = new Microsoft.OpenApi.Models.OpenApiContact
                        {
                            Name = "Some Developer",
                            Email = string.Empty,
                            Url = new System.Uri("https://rainstormtech.com"),
                        },
                    });

                var filePath = System.IO.Path.Combine(System.AppContext.BaseDirectory, "RainstormTech.API.xml");
                c.IncludeXmlComments(filePath);
            });

            // define SQL Server connection string
            builder.Services.AddDbContext<ApplicationContext>(options =>
                options
                    .UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"),
                        o => o.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))
                    );

            // as of 3.1.1 the internal .net core JSON doesn't handle referenceloophandling so we still need to use Newtonsoft
            builder.Services.AddControllers()
                .AddNewtonsoftJson(options => options.SerializerSettings.ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore);

            // add identity services
            builder.Services.AddIdentity<ApplicationUser, ApplicationRole>()
                .AddEntityFrameworkStores<ApplicationContext>()
                .AddDefaultTokenProviders();

            // enable CORS
            builder.Services.AddCors(options =>
                options.AddPolicy("DeafultPolicy",
                    builder =>
                    {
                        builder.WithOrigins("http://localhost:5001");
                    })
            );

            // add appsettings availability
            builder.Services.AddSingleton(builder.Configuration);

            // ability to grab httpcontext
            builder.Services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();

            // automapper - tell it where to find the automapper profile
            // builder.Services.AddAutoMapper(typeof(Startup));
            builder.Services.AddAutoMapper(typeof(AutoMapperProfile));

            builder.Services.AddMvc();

            Newtonsoft.Json.JsonConvert.DefaultSettings = () => new Newtonsoft.Json.JsonSerializerSettings
            {
                NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore,
                ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
            };

            builder.Services.AddLogging(loggingBuilder =>
            {
                loggingBuilder.AddConfiguration(builder.Configuration.GetSection("Logging"));
                loggingBuilder.AddConsole();
                loggingBuilder.AddDebug();
                //  loggingBuilder.AddSerilog();
                //  loggingBuilder.AddFilter<ApplicationInsightsLoggerProvider>
                //             (typeof(Program).FullName, LogLevel.Trace);

            });

            // configure strongly typed settings objects
            var appSettingsSection = builder.Configuration.GetSection("AppSettings");
            builder.Services.Configure<AppSettings>(appSettingsSection);

            // get security key
            var appSettings = appSettingsSection.Get<AppSettings>();
            var key = System.Text.Encoding.ASCII.GetBytes(appSettings.Secret);

            // configure jwt authentication
            builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme;
                options.DefaultScheme = Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(options =>
            {
                // optionally can make sure the user still exists in the db on each call
                options.Events = new Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        var userService = context.HttpContext.RequestServices.GetRequiredService<IUserService>();
                        var user = userService.GetUser(context.Principal.Identity.Name);
                        if (user == null)
                        {
                            // return unauthorized if user no longer exists
                            context.Fail("Unauthorized");
                        }
                        return System.Threading.Tasks.Task.CompletedTask;
                    }
                };

                options.SaveToken = true;
                options.RequireHttpsMetadata = false;
                options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters()
                {
                    ValidateIssuer = false,
                    ValidateAudience = false,
                    // ValidAudience = "http://dotnetdetail.net",
                    // ValidIssuer = "http://dotnetdetail.net",
                    IssuerSigningKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(key)
                };
            });

            // add authorization
            builder.Services.AddAuthorization(options => { });

            // handle authorization policies dynamically
            builder.Services.AddScoped<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, PermissionAuthorizationHandler>();
            builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider, PermissionPolicyProvider>();

            // configure DI for application services

            /* Authentication / users / roles */
            builder.Services.AddScoped<IAuthenticationService, AuthenticationService>();
            builder.Services.AddScoped<IUserService, UserService>();
            builder.Services.AddScoped<IRoleService, RoleService>();

            builder.Services.Configure<KestrelServerOptions> (options =>
            {
                options.Limits.MaxRequestBodySize = 737280000;
            });



            var app = builder.Build();

            if (app.Environment.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
                app.UseHttpsRedirection();
                app.UseDeveloperExceptionPage();
                app.UseHttpsRedirection();
            }
            else
            {
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }

            // Enable middleware to serve generated Swagger as a JSON endpoint.
            app.UseSwagger();

            // Enable middleware to serve swagger-ui (HTML, JS, CSS, etc.),
            // specifying the Swagger JSON endpoint.
            app.UseSwaggerUI(c =>
            {
                c.SwaggerEndpoint("/swagger/v1/swagger.json", "Sweet System V1");
            });

            // handle DB seeding
            SeedDb.Initialize(app.Services.GetRequiredService<IServiceScopeFactory>().CreateScope().ServiceProvider);

            // use the following if hosting files/images using StandardFileService 
            var cachePeriod = app.Environment.IsDevelopment() ? "600" : "604800";
            app.UseStaticFiles(new StaticFileOptions
            {
                /*FileProvider = new PhysicalFileProvider(
                    Path.Combine(Directory.GetCurrentDirectory(), "assets")),
                    RequestPath = "/assets", */

                OnPrepareResponse = ctx =>
                {
                    ctx.Context.Response.Headers.Append("Cache-Control", $"public, max-age={cachePeriod}");
                }
            }); // cuz we're hosting some images

            app.UseRouting();

            app.UseCors(
                options => options.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()
            );

            app.UseAuthentication();
            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
            });

            app.Run();
        }
    }
}
