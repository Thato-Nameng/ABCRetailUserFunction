using ABCTester.Models;
using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Files.Shares;
using Azure.Storage.Queues;
using System.Globalization;
using System.Reflection;

var builder = WebApplication.CreateBuilder(args);

// The following line enables Application Insights telemetry collection.
builder.Services.AddApplicationInsightsTelemetry();

// This code adds other services for your application.
builder.Services.AddMvc();


var cultureInfo = new CultureInfo("en-US");
CultureInfo.DefaultThreadCurrentCulture = cultureInfo;
CultureInfo.DefaultThreadCurrentUICulture = cultureInfo;

builder.Services.AddControllersWithViews();

builder.Services.AddSession();

builder.Services.AddSingleton(new TableServiceClient(builder.Configuration["AzureStorage:ConnectionString"]));
builder.Services.AddSingleton(new BlobServiceClient(builder.Configuration["AzureStorage:ConnectionString"]));
builder.Services.AddSingleton(new QueueServiceClient(builder.Configuration["AzureStorage:ConnectionString"]));
builder.Services.AddSingleton(new ShareServiceClient(builder.Configuration["AzureStorage:ConnectionString"]));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var tableServiceClient = scope.ServiceProvider.GetRequiredService<TableServiceClient>();
    var blobServiceClient = scope.ServiceProvider.GetRequiredService<BlobServiceClient>();

    var customerTableClient = tableServiceClient.GetTableClient("CustomerProfiles");
    await customerTableClient.CreateIfNotExistsAsync();

    var productTableClient = tableServiceClient.GetTableClient("Products");
    await productTableClient.CreateIfNotExistsAsync();

    var customerBlobContainerClient = blobServiceClient.GetBlobContainerClient("customerimages");
    await customerBlobContainerClient.CreateIfNotExistsAsync();

    var productBlobContainerClient = blobServiceClient.GetBlobContainerClient("productimages");
    await productBlobContainerClient.CreateIfNotExistsAsync();

    var shareServiceClient = scope.ServiceProvider.GetRequiredService<ShareServiceClient>();

    var customerLogShareClient = shareServiceClient.GetShareClient("customerlogs");
    await customerLogShareClient.CreateIfNotExistsAsync();

    var logsDirectoryClient = customerLogShareClient.GetDirectoryClient("logs");
    await logsDirectoryClient.CreateIfNotExistsAsync();

    try
    {
        var adminProfile = await customerTableClient.GetEntityAsync<CustomerProfileModel>("CustomerProfile", "admin@gmail.com");
    }
    catch (RequestFailedException ex) when (ex.Status == 404)
    {
        var adminProfile = new CustomerProfileModel
        {
            PartitionKey = "CustomerProfile",
            RowKey = "admin@gmail.com",
            Name = "Admin",
            Surname = "User",
            Email = "admin@gmail.com",
            PhoneNumber = "0234567891",
            PasswordHash = HashPassword("AdminPassword123!"),
            Role = "Admin",
            CreatedDate = DateTime.UtcNow
        };

        await customerTableClient.AddEntityAsync(adminProfile);
        Console.WriteLine("Admin user created successfully.");
    }
}

string HashPassword(string password)
{
    using (var sha256 = System.Security.Cryptography.SHA256.Create())
    {
        var hashedBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(password));
        return BitConverter.ToString(hashedBytes).Replace("-", "").ToLower();
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");

    app.UseHsts();
}

app.UseHttpsRedirection();

app.Use(async (context, next) =>
{
    // Add security headers
    context.Response.Headers.Add("Strict-Transport-Security", "max-age=31536000; includeSubDomains; preload"); // Enforce HTTPS
    context.Response.Headers.Add("X-Content-Type-Options", "nosniff"); // Prevent MIME type sniffing
    context.Response.Headers.Add("X-Frame-Options", "DENY"); // Prevent clickjacking
    context.Response.Headers.Add("X-XSS-Protection", "1; mode=block"); // Enable XSS filter
    context.Response.Headers.Add("Content-Security-Policy", "default-src 'self';"); // Restrict resources to your domain

    await next();
});

app.UseStaticFiles();

app.UseRouting();

app.UseSession();

app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapControllerRoute(
    name: "customer",
    pattern: "{controller=Customer}/{action=Login}/{id?}");

app.Run();
