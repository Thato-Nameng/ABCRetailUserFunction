using Azure;
using Azure.Data.Tables;

namespace ABCTester.Models
{
    public class ProductModel : ITableEntity
    {
        public string PartitionKey { get; set; } = "Products";
        public string RowKey { get; set; } = Guid.NewGuid().ToString();
        public string ProductId { get; set; } = Guid.NewGuid().ToString();
        public string ProductName { get; set; } = string.Empty;
        public double Price { get; set; }
        public int Quantity { get; set; }
        public string ImageUrl { get; set; } = string.Empty;
        public DateTime CreatedDate { get; set; }

        public ETag ETag { get; set; }
        public DateTimeOffset? Timestamp { get; set; }
    }
}
