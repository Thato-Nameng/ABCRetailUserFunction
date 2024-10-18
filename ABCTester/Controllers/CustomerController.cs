using ABCTester.Models;
using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Files.Shares;
using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Security.Cryptography;

using System.Text.Json;
namespace ABCTester.Controllers
{
    public class CustomerController : Controller
    {
        private readonly TableServiceClient _tableServiceClient;
        private readonly BlobServiceClient _blobServiceClient;
        private readonly ILogger<CustomerController> _logger;
        private readonly ShareServiceClient _shareServiceClient;

        public CustomerController(IConfiguration configuration, ILogger<CustomerController> logger, ShareServiceClient shareServiceClient)
        {
            var connectionString = configuration.GetSection("AzureStorage:ConnectionString").Value;
            _tableServiceClient = new TableServiceClient(connectionString);
            _blobServiceClient = new BlobServiceClient(connectionString);
            _shareServiceClient = shareServiceClient;
            _logger = logger;
        }


        [HttpGet]
        public IActionResult Register() => View();


        [HttpPost]
        public async Task<IActionResult> Register(CustomerProfileModel model, string password)
        {
            try
            {
                // Hash the password
                model.PasswordHash = HashPassword(password);
                model.RowKey = model.Email;
                model.PartitionKey = "CustomerProfile";
                model.CreatedDate = DateTime.UtcNow;
                model.Role = "Customer";

                // Handle file upload (if provided)
                var file = Request.Form.Files["file"];
                if (file?.Length > 0)
                {
                    var blobClient = _blobServiceClient.GetBlobContainerClient("customerimages").GetBlobClient(file.FileName);
                    await blobClient.UploadAsync(file.OpenReadStream(), true);
                    model.ImageUrl = blobClient.Uri.ToString();
                }

                // Call the Azure Function to store customer profile
                using (var client = new HttpClient())
                {
                    var json = JsonSerializer.Serialize(model);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");
                    var response = await client.PostAsync("http://localhost:7002/api/StoreCustomerProfile", content);

                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogError($"Error calling function: {response.StatusCode}");
                        return View("Error");
                    }
                }

                return RedirectToAction("Login");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error registering customer");
                return View("Error");
            }
        }


        [HttpGet]
        public IActionResult Login() => View();

        [HttpPost]
        public async Task<IActionResult> Login(string email, string password)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient("CustomerProfiles");
                var customer = await tableClient.GetEntityAsync<CustomerProfileModel>("CustomerProfile", email);

                if (customer != null && VerifyPassword(password, customer.Value.PasswordHash))
                {
                    // Save user session information
                    HttpContext.Session.SetString("UserEmail", customer.Value.Email);
                    HttpContext.Session.SetString("UserName", $"{customer.Value.Name} {customer.Value.Surname}");
                    HttpContext.Session.SetString("UserRole", customer.Value.Role);

                    var loginTime = DateTime.UtcNow;
                    HttpContext.Session.SetString("LoginTime", loginTime.ToString("yyyy-MM-dd HH:mm:ss"));

                    await LogCustomerActivity(customer.Value.Email, "Login", loginTime, null);

                    return RedirectToAction("LoggedIn");
                }
                else
                {
                    ViewBag.Error = "Invalid login credentials.";
                    return View();
                }
            }
            catch (RequestFailedException ex)
            {
                if (ex.Status == 404)
                {
                    ViewBag.Error = "Customer not found. Please check your login details.";
                }
                else
                {
                    ViewBag.Error = "An error occurred during login. Please try again.";
                }
                return View();
            }
        }
        private async Task LogCustomerActivity(string customerEmail, string action, DateTime timestamp, TimeSpan? sessionDuration)
        {
            try
            {
                var shareClient = _shareServiceClient.GetShareClient("customerlogs");
                var directoryClient = shareClient.GetDirectoryClient("logs");

                // Ensure the directory exists
                await directoryClient.CreateIfNotExistsAsync();

                var fileClient = directoryClient.GetFileClient($"{customerEmail}_log.txt");

                // Read the existing content if any
                var existingContent = string.Empty;
                if (await fileClient.ExistsAsync())
                {
                    var downloadInfo = await fileClient.DownloadAsync();
                    using var reader = new StreamReader(downloadInfo.Value.Content);
                    existingContent = await reader.ReadToEndAsync();
                }

                var logDetails = new StringBuilder(existingContent);
                logDetails.AppendLine($"Action: {action}");
                logDetails.AppendLine($"Timestamp: {timestamp:yyyy-MM-dd HH:mm:ss}");
                if (sessionDuration.HasValue)
                {
                    logDetails.AppendLine($"Session Duration: {sessionDuration.Value.TotalMinutes} minutes");
                }
                logDetails.AppendLine("------------------------------------------------");

                // Upload the updated content to the file
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(logDetails.ToString()));
                await fileClient.CreateAsync(stream.Length);
                await fileClient.UploadRangeAsync(new Azure.HttpRange(0, stream.Length), stream);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error saving log for customer {customerEmail}: {ex.Message}");
            }
        }



        [HttpGet]
        public IActionResult LoggedIn()
        {
            return View();
        }

        private string HashPassword(string password)
        {
            using (var sha256 = SHA256.Create())
            {
                var hashedBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(password));
                return BitConverter.ToString(hashedBytes).Replace("-", "").ToLower();
            }
        }

        private bool VerifyPassword(string enteredPassword, string storedPasswordHash)
        {
            var enteredPasswordHash = HashPassword(enteredPassword);
            return enteredPasswordHash == storedPasswordHash;
        }

        [HttpGet]
        public async Task<IActionResult> ViewProfile()
        {
            // Get the logged-in user's email
            var userEmail = HttpContext.Session.GetString("UserEmail");
            if (string.IsNullOrEmpty(userEmail))
            {
                return RedirectToAction("Login");
            }

            try
            {
                // Retrieve the customer profile from Azure Table Storage
                var tableClient = _tableServiceClient.GetTableClient("CustomerProfiles");
                var customerProfile = await tableClient.GetEntityAsync<CustomerProfileModel>("CustomerProfile", userEmail);

                if (customerProfile != null)
                {// Pass the profile to the view for viewing and editing
                    return View(customerProfile.Value);
                }
                else
                {
                    ViewBag.Error = "Profile not found.";
                    return View("Error");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error retrieving customer profile: {ex.Message}");
                return View("Error");
            }
        }

        [HttpPost]
        public async Task<IActionResult> ViewProfile(CustomerProfileModel model, IFormFile file)
        {
            // Check if the user is logged in
            var userEmail = HttpContext.Session.GetString("UserEmail");
            if (string.IsNullOrEmpty(userEmail))
            {
                return RedirectToAction("Login");
            }

            if (ModelState.IsValid)
            {
                try
                {
                    // Retrieve the current profile from Azure Table Storage
                    var tableClient = _tableServiceClient.GetTableClient("CustomerProfiles");
                    var existingProfile = await tableClient.GetEntityAsync<CustomerProfileModel>("CustomerProfile", userEmail);

                    if (existingProfile != null)
                    {
                        // Update profile details
                        existingProfile.Value.Name = model.Name;
                        existingProfile.Value.Surname = model.Surname;
                        existingProfile.Value.PhoneNumber = model.PhoneNumber;

                        // Handle the image upload if a new file is provided
                        if (file?.Length > 0)
                        {
                            try
                            {
                                // Generate a unique filename to avoid overwriting
                                var uniqueFileName = Guid.NewGuid().ToString() + System.IO.Path.GetExtension(file.FileName);
                                var blobClient = _blobServiceClient.GetBlobContainerClient("customerimages").GetBlobClient(uniqueFileName);
                                await blobClient.UploadAsync(file.OpenReadStream(), true);
                                existingProfile.Value.ImageUrl = blobClient.Uri.ToString();
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError($"Error uploading profile image: {ex.Message}");
                                return View("Error");
                            }
                        }

                        await tableClient.UpdateEntityAsync(existingProfile.Value, ETag.All, TableUpdateMode.Replace);

                        ViewBag.Message = "Profile updated successfully.";
                        // Return the updated profile
                        return View(existingProfile.Value);
                    }
                    else
                    {
                        ViewBag.Error = "Profile not found.";
                        return View("Error");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error updating profile: {ex.Message}");
                    return View("Error");
                }
            }

            return View(model);
        }




        [HttpGet]
        public async Task<IActionResult> CreateAdmin()
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient("CustomerProfiles");

                await tableClient.CreateIfNotExistsAsync();

                var adminProfile = new CustomerProfileModel
                {
                    RowKey = "admin@gmail.com",
                    PartitionKey = "CustomerProfile",
                    Name = "Admin",
                    Surname = "User",
                    Email = "admin@gmail.com",
                    PhoneNumber = "0234567891",
                    PasswordHash = HashPassword("AdminPassword123!"),
                    Role = "Admin",
                    CreatedDate = DateTime.UtcNow,
                    ImageUrl = null
                };

                await tableClient.AddEntityAsync(adminProfile);

                return Content("Admin user created successfully. Email: admin@gmail.com, Password: AdminPassword123!");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating admin user");

                return View("Error");
            }
        }


        [HttpGet]
        public async Task<IActionResult> ViewPurchasedProducts()
        {
            var userEmail = HttpContext.Session.GetString("UserEmail");
            if (string.IsNullOrEmpty(userEmail))
            {
                return RedirectToAction("Login");
            }

            var ordersTableClient = _tableServiceClient.GetTableClient("Orders");
            var orders = new List<OrderModel>();

            try
            {
                await foreach (var entity in ordersTableClient.QueryAsync<TableEntity>(filter: $"PartitionKey eq 'Orders' and CustomerEmail eq '{userEmail}'"))
                {
                    var order = new OrderModel
                    {
                        OrderId = entity.RowKey,
                        CustomerName = entity.GetString("CustomerName"),
                        CustomerPhone = entity.GetString("CustomerPhone"),
                        TotalAmount = entity.GetDouble("TotalAmount").Value,
                        OrderStatus = entity.GetString("OrderStatus"),
                        Date = entity.GetDateTime("Date").Value,
                        Products = JsonSerializer.Deserialize<List<ProductModel>>(entity.GetString("Products"))
                    };

                    orders.Add(order);
                }

                return View(orders);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error retrieving customer orders: {ex.Message}");
                return View("Error");
            }
        }
    }
}