namespace CoffeeNChill.Functions.Models
{
    // this is what the client sends to POST /api/orders/queue
    public class OrderRequestDto
    {
        public string? OrderId { get; set; }
        public string? CustomerName { get; set; }
        public List<string>? SelectedItemSKUs { get; set; }
        public decimal? TotalPrice { get; set; }
    }

    
    public class Order
    {
        public string OrderId { get; set; } = string.Empty;
        public string CustomerName { get; set; } = string.Empty;
        public List<string> SelectedItemSKUs { get; set; } = new();
        public decimal TotalPrice { get; set; }
        public DateTime OrderTimestamp { get; set; }
    }
}