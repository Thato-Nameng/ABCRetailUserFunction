using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;


namespace FunctionApp
{
    public static class FunctionOrder
    {
        [FunctionName("StoreCustomerOrder")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            log.LogInformation("Processing customer order creation request...");

            // Read the request body and deserialize the order data
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var order = JsonConvert.DeserializeObject<OrderModel>(requestBody);

            // Check if order data is valid
            if (order == null || string.IsNullOrEmpty(order.CustomerEmail))
            {
                return new BadRequestObjectResult("Invalid order data. Please provide all required fields.");
            }

            // Retrieve connection strings from environment variables
            string tableConnectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
            string blobConnectionString = Environment.GetEnvironmentVariable("AzureBlobStorage");

            // Ensure connection strings are not null or empty
            if (string.IsNullOrEmpty(tableConnectionString) || string.IsNullOrEmpty(blobConnectionString))
            {
                log.LogError("Connection strings are missing.");
                return new StatusCodeResult(500); // Internal server error due to missing configuration
            }

            // Table Service Client for storing customer orders
            var tableServiceClient = new TableServiceClient(tableConnectionString);
            var ordersTableClient = tableServiceClient.GetTableClient("Orders");

            // Create the table if it doesn't exist
            await ordersTableClient.CreateIfNotExistsAsync();

            try
            {
                // Add the customer order to Azure Table Storage
                var tableEntity = new TableEntity("Orders", order.OrderId)
                {
                    { "CustomerName", order.CustomerName },
                    { "CustomerEmail", order.CustomerEmail },
                    { "CustomerPhone", order.CustomerPhone },
                    { "TotalAmount", order.TotalAmount },
                    { "OrderStatus", order.OrderStatus },
                    { "Date", order.Date }
                };

                var productsJson = JsonConvert.SerializeObject(order.Products);
                tableEntity["Products"] = productsJson;

                await ordersTableClient.AddEntityAsync(tableEntity);

                log.LogInformation($"Order {order.OrderId} for customer {order.CustomerEmail} has been successfully stored.");

                // Handle Blob Storage for order backup (optional)
                var blobServiceClient = new BlobServiceClient(blobConnectionString);
                var blobContainerClient = blobServiceClient.GetBlobContainerClient("orderfiles");
                await blobContainerClient.CreateIfNotExistsAsync();

                var fileName = $"{order.CustomerEmail}_Order_{order.OrderId}.json";
                var blobClient = blobContainerClient.GetBlobClient(fileName);
                var orderJson = JsonConvert.SerializeObject(order);

                using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(orderJson));
                await blobClient.UploadAsync(stream, true);

                log.LogInformation($"Order {order.OrderId} for customer {order.CustomerEmail} has been backed up to Blob Storage.");

                return new OkObjectResult($"Order {order.OrderId}, {order.CustomerName}, {order.CustomerPhone}, {order.CustomerEmail}, {order.Products}, " +
                $"{order.TotalAmount}, {order.Date}, {order.OrderStatus} has been stored successfully.");
            }
            catch (Exception ex)
            {
                log.LogError($"Error storing customer order: {ex.Message}");
                return new StatusCodeResult(500); // Internal server error
            }
        }
    }


    public class OrderModel
    {
        public string OrderId { get; set; } = Guid.NewGuid().ToString();
        public string CustomerName { get; set; } = string.Empty;
        public string CustomerPhone { get; set; } = string.Empty;
        public string CustomerEmail { get; set; } = string.Empty;
        public List<ProductModel> Products { get; set; } = new List<ProductModel>();
        public double TotalAmount { get; set; }
        public DateTime Date { get; set; }
        public string OrderStatus { get; set; } = "Processing";
    }

    public class ProductModel
    {
        public string ProductId { get; set; }
        public string ProductName { get; set; }
        public double Price { get; set; }
        public int Quantity { get; set; }
    }
}
