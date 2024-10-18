using ABCTester.Models;
using Azure;
using Azure.Core;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Files.Shares;
using Azure.Storage.Queues;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ABCRetail.Controllers
{
    public class HomeController : Controller
    {
        private readonly TableServiceClient _tableServiceClient;
        private readonly BlobServiceClient _blobServiceClient;
        private readonly QueueServiceClient _queueServiceClient;
        private readonly ShareServiceClient _shareServiceClient;
        private readonly ILogger<HomeController> _logger;

        public HomeController(ILogger<HomeController> logger, IConfiguration configuration)
        {
            _logger = logger;
            var connectionString = configuration.GetSection("AzureStorage:ConnectionString").Value;
            _tableServiceClient = new TableServiceClient(connectionString);
            _blobServiceClient = new BlobServiceClient(connectionString);
            _queueServiceClient = new QueueServiceClient(connectionString);
            _shareServiceClient = new ShareServiceClient(connectionString);
        }

        public IActionResult Index()
        {
            ViewBag.UserName = HttpContext.Session.GetString("UserName");
            ViewBag.LoginTime = HttpContext.Session.GetString("LoginTime");
            ViewBag.UserRole = HttpContext.Session.GetString("UserRole");

            return View();
        }

        public IActionResult Privacy() => View();

        [HttpPost]
        public async Task<IActionResult> AddCustomerProfile(CustomerProfileModel model)
        {
            try
            {
                // Azure Table Storage for customer profiles
                var tableClient = _tableServiceClient.GetTableClient("CustomerProfiles");
                await tableClient.CreateIfNotExistsAsync();

                // Azure Blob Storage for customer images
                var file = Request.Form.Files["file"];
                if (file?.Length > 0)
                {
                    var blobClient = _blobServiceClient.GetBlobContainerClient("customerimages").GetBlobClient(file.FileName);
                    await blobClient.UploadAsync(file.OpenReadStream(), true);
                    model.ImageUrl = blobClient.Uri.ToString();
                }

                model.RowKey = model.Email;
                model.PartitionKey = "CustomerProfile";
                model.CreatedDate = DateTime.UtcNow;
                await tableClient.AddEntityAsync(model);

                // Azure Files for backup of customer profiles
                var profileData = JsonSerializer.Serialize(model);
                var directoryClient = _shareServiceClient.GetShareClient("customerfiles").GetDirectoryClient("profiles");
                await directoryClient.CreateIfNotExistsAsync();
                var fileClient = directoryClient.GetFileClient($"{model.Email}.json");
                using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(profileData));
                await fileClient.CreateAsync(stream.Length);
                await fileClient.UploadRangeAsync(new Azure.HttpRange(0, stream.Length), stream);

                _logger.LogInformation($"Customer profile for {model.Email} added successfully.");
                return RedirectToAction("Index");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error adding customer profile: {ex.Message}");
                return View("Error");
            }
        }

        [HttpGet]
        public IActionResult AddProduct()
        {
            // Ensure only admins can access this page
            if (HttpContext.Session.GetString("UserRole") != "Admin")
            {
                return RedirectToAction("Login", "Customer");
            }

            // Explicitly reference the correct view path
            return View("~/Views/Admin/AddProduct.cshtml");
        }



        [HttpPost]
        public async Task<IActionResult> AddProduct(ProductModel model, IFormFile file)
        {
            try
            {
                if (file?.Length > 0)
                {
                    // Convert the uploaded file to Base64
                    using var ms = new MemoryStream();
                    await file.CopyToAsync(ms);
                    var fileBytes = ms.ToArray();
                    model.ImageUrl = $"data:image/{file.ContentType.Split('/')[1]};base64,{Convert.ToBase64String(fileBytes)}";
                }

                // Call the Azure Function to store the product
                using var client = new HttpClient();
                var json = JsonSerializer.Serialize(model);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await client.PostAsync("http://localhost:7002/api/StoreProduct", content); // Update this URL to your Azure Function URL
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError($"Error calling function: {response.StatusCode}");
                    return View("Error");
                }

                return RedirectToAction("ViewProduct");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error adding product: {ex.Message}");
                return View("Error");
            }
        }

        [HttpGet]
        public async Task<IActionResult> ViewProduct()
        {
            var tableClient = _tableServiceClient.GetTableClient("Products");
            var products = new List<ProductModel>();

            await foreach (var entity in tableClient.QueryAsync<ProductModel>())
            {
                products.Add(entity);
            }

            return View(products);
        }

        [HttpGet]
        public async Task<IActionResult> ViewCustomerProfiles()
        {
            if (HttpContext.Session.GetString("UserRole") != "Admin")
            {
                return RedirectToAction("Login", "Customer");
            }

            try
            {
                var tableClient = _tableServiceClient.GetTableClient("CustomerProfiles");
                var customerProfiles = new List<CustomerProfileModel>();
                await foreach (var entity in tableClient.QueryAsync<CustomerProfileModel>())
                {
                    customerProfiles.Add(entity);
                }

                return View("~/Views/Admin/ViewCustomerProfiles.cshtml", customerProfiles);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error retrieving customer profiles: {ex.Message}");
                return View("Error");
            }
        }


        [HttpGet]
        public async Task<IActionResult> Logout()
        {
            var loginTime = DateTime.Parse(HttpContext.Session.GetString("LoginTime"));
            var logoutTime = DateTime.UtcNow;
            var sessionDuration = logoutTime - loginTime;

            await LogCustomerActivity(HttpContext.Session.GetString("UserEmail"), "Logout", logoutTime, sessionDuration);

            HttpContext.Session.Clear();

            return RedirectToAction("Login", "Customer");
        }

        private async Task LogCustomerActivity(string customerEmail, string action, DateTime timestamp, TimeSpan? sessionDuration)
        {
            try
            {
                var shareClient = _shareServiceClient.GetShareClient("customerlogs");
                var directoryClient = shareClient.GetDirectoryClient("logs");

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

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddToCart(string productId)
        {
            var tableClient = _tableServiceClient.GetTableClient("Products");
            try
            {
                var product = await tableClient.GetEntityAsync<ProductModel>("Products", productId);
                if (product != null)
                {
                    var cart = HttpContext.Session.GetString("Cart");
                    var cartItems = string.IsNullOrEmpty(cart) ? new List<ProductModel>() : JsonSerializer.Deserialize<List<ProductModel>>(cart);

                    var existingProduct = cartItems.FirstOrDefault(p => p.ProductId == product.Value.ProductId);
                    if (existingProduct != null)
                    {
                        existingProduct.Quantity++;
                    }
                    else
                    {
                        product.Value.Quantity = 1;
                        cartItems.Add(product.Value);
                    }

                    HttpContext.Session.SetString("Cart", JsonSerializer.Serialize(cartItems));
                    HttpContext.Session.SetInt32("CartCount", cartItems.Sum(p => p.Quantity));

                    return RedirectToAction("ViewProduct");
                }
            }
            catch (RequestFailedException ex)
            {
                _logger.LogError($"Product with ID {productId} could not be found. Error: {ex.Message}");
                return View("Error");
            }

            return RedirectToAction("ViewProduct");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult UpdateCart(Dictionary<string, int> quantities)
        {
            var cart = HttpContext.Session.GetString("Cart");
            if (!string.IsNullOrEmpty(cart))
            {
                var cartItems = JsonSerializer.Deserialize<List<ProductModel>>(cart);
                foreach (var item in cartItems)
                {
                    if (quantities.ContainsKey(item.ProductId))
                    {
                        item.Quantity = quantities[item.ProductId];
                    }
                }

                // Save updated cart to session
                HttpContext.Session.SetString("Cart", JsonSerializer.Serialize(cartItems));
                HttpContext.Session.SetInt32("CartCount", cartItems.Sum(p => p.Quantity));

                return RedirectToAction("ViewCart");
            }

            return RedirectToAction("ViewCart");
        }

        [HttpGet]
        public IActionResult RemoveFromCart(string productId)
        {
            var cart = HttpContext.Session.GetString("Cart");
            if (!string.IsNullOrEmpty(cart))
            {
                var cartItems = JsonSerializer.Deserialize<List<ProductModel>>(cart);
                var itemToRemove = cartItems.FirstOrDefault(p => p.ProductId == productId);

                if (itemToRemove != null)
                {
                    cartItems.Remove(itemToRemove);
                    HttpContext.Session.SetString("Cart", JsonSerializer.Serialize(cartItems));
                    HttpContext.Session.SetInt32("CartCount", cartItems.Sum(p => p.Quantity));
                }
            }

            return RedirectToAction("ViewCart");
        }

        [HttpGet]
        public IActionResult ViewCart()
        {
            var cart = HttpContext.Session.GetString("Cart");
            var cartItems = string.IsNullOrEmpty(cart)
                ? new List<ProductModel>()
                : JsonSerializer.Deserialize<List<ProductModel>>(cart);

            return View(cartItems);
        }


        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ProcessOrder()
        {
            var cart = HttpContext.Session.GetString("Cart");
            var cartItems = string.IsNullOrEmpty(cart)
                ? new List<ProductModel>()
                : JsonSerializer.Deserialize<List<ProductModel>>(cart);

            var userEmail = HttpContext.Session.GetString("UserEmail");
            if (string.IsNullOrEmpty(userEmail))
            {
                return RedirectToAction("Login", "Customer");
            }

            if (cartItems.Any())
            {
                try
                {
                    var tableClient = _tableServiceClient.GetTableClient("CustomerProfiles");
                    var customerProfile = await tableClient.GetEntityAsync<CustomerProfileModel>("CustomerProfile", userEmail);

                    var totalAmount = cartItems.Sum(x => x.Quantity * x.Price);
                    var order = new OrderModel
                    {
                        OrderId = Guid.NewGuid().ToString(),
                        CustomerName = $"{customerProfile.Value.Name} {customerProfile.Value.Surname}",
                        CustomerPhone = customerProfile.Value.PhoneNumber,
                        CustomerEmail = customerProfile.Value.Email,
                        Products = cartItems,
                        TotalAmount = totalAmount,
                        Date = DateTime.UtcNow,
                        OrderStatus = "Processing"
                    };

                    // Call the Azure Function to store the order
                    using (var client = new HttpClient())
                    {
                        var json = JsonSerializer.Serialize(order);
                        var content = new StringContent(json, Encoding.UTF8, "application/json");
                        var response = await client.PostAsync("http://localhost:7002/api/StoreCustomerOrder", content);

                        if (!response.IsSuccessStatusCode)
                        {
                            _logger.LogError($"Error calling function: {response.StatusCode}");
                            return View("Error");
                        }
                    }

                    HttpContext.Session.Remove("Cart");
                    HttpContext.Session.SetInt32("CartCount", 0);

                    ViewBag.Message = "Order processed successfully!";
                    return RedirectToAction("ViewProduct");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error processing order: {ex.Message}");
                    return View("Error");
                }
            }

            return RedirectToAction("ViewCart");
        }


        
        [HttpGet]
        public async Task<IActionResult> ViewOrders(string customerId)
        {
            if (HttpContext.Session.GetString("UserRole") != "Admin")
                return RedirectToAction("Login", "Customer");

            var ordersTableClient = _tableServiceClient.GetTableClient("Orders");
            var orders = new List<OrderModel>();

            try
            {
                await foreach (var entity in ordersTableClient.QueryAsync<TableEntity>(filter: $"PartitionKey eq 'Orders' and CustomerEmail eq '{customerId}'"))
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

        [HttpPost]
        public async Task<IActionResult> UpdateOrderStatus(string orderId)
        {
            if (HttpContext.Session.GetString("UserRole") != "Admin")
                return RedirectToAction("Login", "Customer");

            var ordersTableClient = _tableServiceClient.GetTableClient("Orders");

            try
            {
                var entity = await ordersTableClient.GetEntityAsync<TableEntity>("Orders", orderId);
                entity.Value["OrderStatus"] = "Sent";

                await ordersTableClient.UpdateEntityAsync(entity.Value, ETag.All, TableUpdateMode.Replace);

                ViewBag.Message = "Order status updated to 'Sent'.";
                return RedirectToAction("ViewOrders", new { customerId = entity.Value.GetString("CustomerEmail") });
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error updating order status: {ex.Message}");
                return View("Error");
            }
        }


        [HttpGet]
        public async Task<IActionResult> ViewCustomerFiles(string customerId)
        {
            if (HttpContext.Session.GetString("UserRole") != "Admin")
                return RedirectToAction("Login", "Customer");

            try
            {
                var shareClient = _shareServiceClient.GetShareClient("customerfiles");
                var directoryClient = shareClient.GetDirectoryClient("orders");

                var files = directoryClient.GetFilesAndDirectories();
                var fileNamePattern = $"{customerId}_Order_";
                var filesForCustomer = new List<string>();

                foreach (var item in files)
                {
                    if (!item.IsDirectory && item.Name.StartsWith(fileNamePattern))
                    {
                        filesForCustomer.Add(item.Name);
                    }
                }

                var customerFilesContent = new StringBuilder();
                foreach (var file in filesForCustomer)
                {
                    var fileClient = directoryClient.GetFileClient(file);
                    var downloadInfo = await fileClient.DownloadAsync();
                    using var reader = new StreamReader(downloadInfo.Value.Content);
                    var fileContent = await reader.ReadToEndAsync();
                    customerFilesContent.AppendLine(fileContent);
                }

                var logShareClient = _shareServiceClient.GetShareClient("customerlogs");
                var logDirectoryClient = logShareClient.GetDirectoryClient("logs");
                var logFileClient = logDirectoryClient.GetFileClient($"{customerId}_log.txt");

                string customerLogsContent;
                if (await logFileClient.ExistsAsync())
                {
                    var logDownloadInfo = await logFileClient.DownloadAsync();
                    using var logReader = new StreamReader(logDownloadInfo.Value.Content);
                    customerLogsContent = await logReader.ReadToEndAsync();
                }
                else
                {
                    customerLogsContent = "No logs available.";
                }

                var viewModel = new CustomerFileLogViewModel
                {
                    CustomerName = customerId,
                    CustomerFilesContent = customerFilesContent.ToString(),
                    CustomerLogsContent = customerLogsContent
                };

                return View("ViewCustomerFiles", viewModel);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error accessing customer files or logs: {ex.Message}");
                return View("Error");
            }
        }


        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error() => View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}