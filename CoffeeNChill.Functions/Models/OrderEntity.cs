using Azure;
using Azure.Data.Tables;
namespace CoffeeNChill.Functions.Models
{
    // The four stages of the barista lifecycle, in the only order they may happen.
    public static class OrderStatus
    {
        public const string Received = "Received";
        public const string Preparing = "Preparing";
        public const string Ready = "Ready";
        public const string Collected = "Collected";
        public static readonly string[] Lifecycle = { Received, Preparing, Ready, Collected };
        // Position of a status in the lifecycle (-1 if it is not a valid status).
        public static int IndexOf(string? status) => Array.IndexOf(Lifecycle, status ?? string.Empty);
        public static bool IsValid(string? status) => IndexOf(status) >= 0;
    }
    // Azure Table entity for one row in the "Orders" table.
    // PartitionKey = order date (yyyy-MM-dd), RowKey = OrderId (e.g. "ORD-2026-8801")
    public class OrderEntity : ITableEntity
    {
        public string PartitionKey { get; set; } = default!;
        public string RowKey { get; set; } = default!;
        public string CustomerName { get; set; } = string.Empty;
        // Azure Tables cannot store a List<string>, so the SKUs are saved as "COF-001,PAS-104".
        public string ItemSkus { get; set; } = string.Empty;
        // Azure Tables has no decimal type, so the price is stored as a double.
        public double TotalPrice { get; set; }
        public DateTimeOffset OrderTimestamp { get; set; }
        public string Status { get; set; } = OrderStatus.Received;
        // When each lifecycle stage was reached (null until that stage happens).
        public DateTimeOffset? ReceivedAt { get; set; }
        public DateTimeOffset? PreparingAt { get; set; }
        public DateTimeOffset? ReadyAt { get; set; }
        public DateTimeOffset? CollectedAt { get; set; }
        public DateTimeOffset LastUpdated { get; set; }
        // How many times the queue delivered the message when the record was created.
        public long DequeueCount { get; set; }
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }
        // Moves the order to a new status and records when that happened.
        public void ApplyStatus(string status)
        {
            var now = DateTimeOffset.UtcNow;
            Status = status;
            LastUpdated = now;
            switch (status)
            {
                case OrderStatus.Received: ReceivedAt = now; break;
                case OrderStatus.Preparing: PreparingAt = now; break;
                case OrderStatus.Ready: ReadyAt = now; break;
                case OrderStatus.Collected: CollectedAt = now; break;
            }
        }
    }
}
