using Azure;
using Azure.Data.Tables;

namespace ABCTester.Models
{
    public class CustomerProfileModel : ITableEntity
    {
        public string PartitionKey { get; set; } = "CustomerProfile";
        public string RowKey { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Surname { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string PhoneNumber { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public string Role { get; set; } = "Customer";
        public string ImageUrl { get; set; } = string.Empty;
        public DateTime CreatedDate { get; set; }

        public ETag ETag { get; set; }
        public DateTimeOffset? Timestamp { get; set; }
    }
}
