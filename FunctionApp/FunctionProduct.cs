using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.IO;
using System.Threading.Tasks;
using System;


namespace FunctionApp
{
    public static class FunctionProduct
    {
        [FunctionName("StoreProduct")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            log.LogInformation("Processing product creation request...");

            // Read the request body and deserialize the product data
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var product = JsonConvert.DeserializeObject<ProductModels>(requestBody);

            // Check if product data is valid
            if (product == null || string.IsNullOrEmpty(product.ProductName))
            {
                return new BadRequestObjectResult("Invalid product data. Please provide all required fields.");
            }

            // Ensure CreatedDate is set to UTC
            if (product.CreatedDate == default(DateTime))
            {
                product.CreatedDate = DateTime.UtcNow;
            }

            // Retrieve connection strings from environment variables
            string tableConnectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
            string blobConnectionString = Environment.GetEnvironmentVariable("AzureBlobStorage");

            if (string.IsNullOrEmpty(tableConnectionString) || string.IsNullOrEmpty(blobConnectionString))
            {
                log.LogError("Connection strings are missing.");
                return new StatusCodeResult(500); // Internal server error due to missing configuration
            }

            // Table Service Client for storing product profiles
            var tableServiceClient = new TableServiceClient(tableConnectionString);
            var tableClient = tableServiceClient.GetTableClient("Products");

            // Create the table if it doesn't exist
            await tableClient.CreateIfNotExistsAsync();

            // Handle Blob Storage for product image
            if (!string.IsNullOrEmpty(product.ImageUrl))
            {
                try
                {
                    var blobServiceClient = new BlobServiceClient(blobConnectionString);
                    var blobContainerClient = blobServiceClient.GetBlobContainerClient("productimages");

                    if (product.ImageUrl.StartsWith("data:image/"))
                    {
                        var base64Data = product.ImageUrl.Substring(product.ImageUrl.IndexOf(",") + 1);
                        byte[] imageBytes = Convert.FromBase64String(base64Data);
                        var blobClient = blobContainerClient.GetBlobClient($"{product.ProductName}_productimage");

                        using (var stream = new MemoryStream(imageBytes))
                        {
                            await blobClient.UploadAsync(stream, true);
                        }

                        product.ImageUrl = blobClient.Uri.ToString();
                    }
                }
                catch (Exception ex)
                {
                    log.LogError($"Error uploading product image to Blob Storage: {ex.Message}");
                    return new StatusCodeResult(500);
                }
            }

            try
            {
                await tableClient.AddEntityAsync(product);
            }
            catch (Exception ex)
            {
                log.LogError($"Error storing product: {ex.Message}");
                return new StatusCodeResult(500);
            }

            log.LogInformation($"Product {product.PartitionKey}, {product.RowKey}, {product.ProductName}, {product.Price}, {product.Quantity}, " +
                $"{product.ImageUrl}, {product.CreatedDate} has been successfully stored.");
            return new OkObjectResult($"Product  {product.PartitionKey}, {product.RowKey}, {product.ProductName}, {product.Price}, {product.Quantity}, " +
                $"{product.ImageUrl}, {product.CreatedDate} has been stored successfully.");
        }
    }

    public class ProductModels : ITableEntity
    {
        public string PartitionKey { get; set; } = "Products";
        public string RowKey { get; set; } = Guid.NewGuid().ToString(); // Unique product identifier
        public string ProductName { get; set; } = string.Empty;
        public double Price { get; set; }
        public int Quantity { get; set; }
        public string ImageUrl { get; set; } = string.Empty;
        public DateTime CreatedDate { get; set; }

        [JsonIgnore] // Ignore ETag to avoid deserialization issues
        public ETag ETag { get; set; }

        [JsonIgnore] // Ignore Timestamp to avoid deserialization issues
        public DateTimeOffset? Timestamp { get; set; }
    }
}
