using System;
using System.IO;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;


namespace FunctionApp
{
    public static class FunctionTable
    {

        [FunctionName("StoreCustomerProfile")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            log.LogInformation("Processing customer profile creation request...");

            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var customer = JsonConvert.DeserializeObject<CustomerProfileModel>(requestBody);

            if (customer == null || string.IsNullOrEmpty(customer.Email))
            {
                return new BadRequestObjectResult("Invalid customer data. Please provide all required fields.");
            }

            string tableConnectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
            string blobConnectionString = Environment.GetEnvironmentVariable("AzureBlobStorage");

            if (string.IsNullOrEmpty(tableConnectionString) || string.IsNullOrEmpty(blobConnectionString))
            {
                log.LogError("Connection strings are missing.");
                return new StatusCodeResult(500); 
            }

            var tableServiceClient = new TableServiceClient(tableConnectionString);
            var tableClient = tableServiceClient.GetTableClient("CustomerProfiles");

            await tableClient.CreateIfNotExistsAsync();

            if (!string.IsNullOrEmpty(customer.ImageUrl))
            {
                try
                {
                    var blobServiceClient = new BlobServiceClient(blobConnectionString);
                    var blobContainerClient = blobServiceClient.GetBlobContainerClient("customerimages");

                    if (customer.ImageUrl.StartsWith("data:image/"))
                    {
                        var base64Data = customer.ImageUrl.Substring(customer.ImageUrl.IndexOf(",") + 1);

                        byte[] imageBytes = Convert.FromBase64String(base64Data);
                        var blobClient = blobContainerClient.GetBlobClient($"{customer.Email}_profileimage");

                        using (var stream = new MemoryStream(imageBytes))
                        {
                            await blobClient.UploadAsync(stream, true);
                        }

                        customer.ImageUrl = blobClient.Uri.ToString();
                    }
                    else
                    {
                        customer.ImageUrl = customer.ImageUrl;
                    }
                }
                catch (Exception ex)
                {
                    log.LogError($"Error uploading customer image to Blob Storage: {ex.Message}");
                    return new StatusCodeResult(500);
                }
            }

            try
            {
                await tableClient.AddEntityAsync(customer);
            }
            catch (Exception ex)
            {
                log.LogError($"Error storing customer profile: {ex.Message}");
                return new StatusCodeResult(500);
            }

            log.LogInformation($"Customer Profile: {customer.PartitionKey}, {customer.RowKey}, {customer.Name}, {customer.Surname}, {customer.Email}, " +
                $"{customer.PhoneNumber}, {customer.PasswordHash}, {customer.Role},  {customer.ImageUrl}, {customer.CreatedDate} has been successfully stored.");
            return new OkObjectResult($"Customer {customer.PartitionKey}, {customer.RowKey}, {customer.Name}, {customer.Surname}, {customer.Email}, " +
                $"{customer.PhoneNumber}, {customer.PasswordHash}, {customer.Role}, {customer.ImageUrl}, {customer.CreatedDate} has been stored successfully.");
        }
    }

    public class CustomerProfileModel : ITableEntity
    {
        public string PartitionKey { get; set; } = "CustomerProfile";
        public string RowKey { get; set; } = string.Empty; // Email
        public string Name { get; set; } = string.Empty;
        public string Surname { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string PhoneNumber { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public string Role { get; set; } = "Customer";
        public string ImageUrl { get; set; } = string.Empty;
        public DateTime CreatedDate { get; set; }

        [JsonIgnore] // Ignore ETag to avoid issues with deserialization
        public ETag ETag { get; set; }
        public DateTimeOffset? Timestamp { get; set; }
    }
}
