using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using System;
using System.IO;
using Azure;
using System.Collections.Generic;
using Microsoft.Azure.Functions.Worker;
using Microsoft.AspNetCore.Mvc;

public static class OrderQueueFunction
{
    [FunctionName("ProcessOrderQueue")]
    public static async Task Run(
        [QueueTrigger("ordersqueue", Connection = "AzureWebJobsStorage")] string orderMessage,
        ILogger log)
    {
        log.LogInformation($"Processing message from ordersqueue: {orderMessage}");

        // Deserialize the order message
        var order = JsonConvert.DeserializeObject<OrderModel>(orderMessage);

        // Retrieve connection strings from environment variables
        string tableConnectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
        string blobConnectionString = Environment.GetEnvironmentVariable("AzureBlobStorage");

        if (string.IsNullOrEmpty(tableConnectionString) || string.IsNullOrEmpty(blobConnectionString))
        {
            log.LogError("Connection strings are missing.");
            return; // Exit as we can't proceed without valid connection strings
        }

        try
        {
            // Table Service Client for updating order status in Azure Table Storage
            var tableServiceClient = new TableServiceClient(tableConnectionString);
            var ordersTableClient = tableServiceClient.GetTableClient("Orders");

            // Fetch the existing order entity from Table Storage
            var entity = await ordersTableClient.GetEntityAsync<TableEntity>("Orders", order.OrderId);
            entity.Value["OrderStatus"] = order.OrderStatus;

            // Update the order status in Table Storage
            await ordersTableClient.UpdateEntityAsync(entity.Value, ETag.All, TableUpdateMode.Replace);

            log.LogInformation($"Order {order.OrderId} status updated to {order.OrderStatus} in Table Storage.");

            // Handle Blob Storage backup for the order
            var blobServiceClient = new BlobServiceClient(blobConnectionString);
            var blobContainerClient = blobServiceClient.GetBlobContainerClient("orderfiles");
            await blobContainerClient.CreateIfNotExistsAsync();

            var fileName = $"{order.CustomerEmail}_Order_{order.OrderId}.json";
            var blobClient = blobContainerClient.GetBlobClient(fileName);
            var orderJson = JsonConvert.SerializeObject(order);

            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(orderJson));
            await blobClient.UploadAsync(stream, true);

            log.LogInformation($"Order {order.OrderId}, {order.CustomerName}, {order.CustomerPhone}, {order.CustomerEmail}, {order.Products}, " +
                $"{order.TotalAmount}, {order.Date}, {order.OrderStatus} backup stored in Blob Storage.");
        }
        catch (Exception ex)
        {
            log.LogError($"Error processing order from ordersqueue: {ex.Message}");
        }
    }

    public class OrderModel
    {
        public string OrderId { get; set; }
        public string CustomerName { get; set; }
        public string CustomerPhone { get; set; }
        public string CustomerEmail { get; set; }
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
