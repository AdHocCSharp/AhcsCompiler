using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.FileProviders;
using Serilog;

const string LOG_FORMAT = "[{methodName}] {message}";
var methodName = MethodBase.GetCurrentMethod()?.Name ?? "<< unknown >>";
var builder = WebApplication.CreateBuilder(args);

//builder.WebHost.ConfigureKestrel(serverOptions =>
//{
//    serverOptions.Limits.MaxConcurrentConnections = 100;
//    serverOptions.Limits.MaxConcurrentUpgradedConnections = 100;
//    serverOptions.Limits.MaxRequestBodySize = 10 * 1024;
//    serverOptions.Limits.MinRequestBodyDataRate =
//        new MinDataRate(bytesPerSecond: 100, gracePeriod: TimeSpan.FromSeconds(10));
//    serverOptions.Limits.MinResponseDataRate =
//        new MinDataRate(bytesPerSecond: 100, gracePeriod: TimeSpan.FromSeconds(10));
//    // serverOptions.Listen(IPAddress.Loopback, 5000);
//    // serverOptions.Listen(IPAddress.Loopback, 5001,
//    //     listenOptions => listenOptions.UseHttps(@"C:\GitHub\AdHocCSharp\AhcsCompiler\docs\docfx_project\devCert.pfx", "devCert"));
//    serverOptions.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
//    serverOptions.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(1);
//});

builder.Logging
    .AddDebug()
    .AddConsole()
    .AddSerilog();

var app = builder.Build();
var logger = app.Services.GetService<ILogger<Program>>();


var siteRoot = Path.Combine(builder.Environment.ContentRootPath, "wwwroot");

app.UseStaticFiles();


//var message = $"siteRoot: {siteRoot}";
//logger?.LogDebug(LOG_FORMAT, methodName, message);

//if (Directory.Exists(siteRoot))
//{
//    //var staticFileOptions = new StaticFileOptions
//    //{
//    //    DefaultContentType = "text/html",
//    //    FileProvider = new PhysicalFileProvider(siteRoot),
//    //    ServeUnknownFileTypes = true,
//    //    OnPrepareResponse = (context) =>
//    //    {
//    //        message = context.Context.Request.GetEncodedUrl() ?? "<< null >>";
//    //        logger?.LogInformation(LOG_FORMAT, methodName, message);
//    //    }
//    //};

//}
//else
//{
//    throw new ApplicationException($"Cannot find [{siteRoot}] for the site root.");
//}
app.Run();
