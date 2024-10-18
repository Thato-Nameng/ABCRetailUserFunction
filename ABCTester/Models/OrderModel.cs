namespace ABCTester.Models
{
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
}
