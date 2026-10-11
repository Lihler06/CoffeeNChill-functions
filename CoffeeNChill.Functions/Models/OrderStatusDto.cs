namespace CoffeeNChill.Functions.Models
{
    // What the status endpoints return to the client (the entity is never exposed directly).
    public class OrderStatusDto
    {
        public string OrderId { get; set; } = string.Empty;
        public string OrderDate { get; set; } = string.Empty;
        public string CustomerName { get; set; } = string.Empty;
        public List<string> SelectedItemSKUs { get; set; } = new();
        public double TotalPrice { get; set; }
        public DateTimeOffset OrderTimestamp { get; set; }
        public string Status { get; set; } = string.Empty;
        public DateTimeOffset? ReceivedAt { get; set; }
        public DateTimeOffset? PreparingAt { get; set; }
        public DateTimeOffset? ReadyAt { get; set; }
        public DateTimeOffset? CollectedAt { get; set; }
        public DateTimeOffset LastUpdated { get; set; }
        public static OrderStatusDto FromEntity(OrderEntity entity) => new()
        {
            OrderId = entity.RowKey,
            OrderDate = entity.PartitionKey,
            CustomerName = entity.CustomerName,
            SelectedItemSKUs = entity.ItemSkus
        .Split(',', StringSplitOptions.RemoveEmptyEntries)
        .ToList(),
            TotalPrice = entity.TotalPrice,
            OrderTimestamp = entity.OrderTimestamp,
            Status = entity.Status,
            ReceivedAt = entity.ReceivedAt,
            PreparingAt = entity.PreparingAt,
            ReadyAt = entity.ReadyAt,
            CollectedAt = entity.CollectedAt,
            LastUpdated = entity.LastUpdated
        };
    }
}
