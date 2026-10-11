using System.Globalization;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Azure.Storage.Queues.Models;
using CoffeeNChill.Functions.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
namespace CoffeeNChill.Functions.Functions
{
    // Background function: fires automatically whenever a message lands on
    // order-processing-queue (placed there by the PlaceOrder producer endpoint).
    //
    // 1. Parses and validates the JSON payload into an Order
    // 2. Creates a row in the Orders table with Status = "Received"
    // 3. Walks the order through Received -> Preparing -> Ready -> Collected
    // 4. If anything fails the exception is rethrown, so the Functions host retries
    // the message and finally moves it to "order-processing-queue-poison"
    // (maxDequeueCount is set in host.json).
    public class ProcessOrderQueueFunction
    {
        private const string OrdersTableName = "Orders";
        private const int DefaultStepDelaySeconds = 5;
        private const int MaxDequeueCount = 3; // keep in sync with host.json
        private readonly TableClient _ordersTable;
        private readonly ILogger<ProcessOrderQueueFunction> _logger;
        private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };
        public ProcessOrderQueueFunction(
        TableServiceClient tableServiceClient,
        ILogger<ProcessOrderQueueFunction> logger)
        {
            _ordersTable = tableServiceClient.GetTableClient(OrdersTableName);
            _logger = logger;
        }
        [Function("ProcessOrderQueue")]
        public async Task Run(
        [QueueTrigger("order-processing-queue", Connection = "AzureWebJobsStorage")]

        QueueMessage message,
CancellationToken cancellationToken)
        {
            _logger.LogInformation(
            "ProcessOrderQueue triggered. MessageId={MessageId}, attempt {Attempt} of {Max}.",
            message.MessageId, message.DequeueCount, MaxDequeueCount);
            try
            {
                var order = ParseOrder(message.MessageText);
                await _ordersTable.CreateIfNotExistsAsync(cancellationToken);
                var entity = await CreateOrderRecordAsync(
                order, message.DequeueCount, cancellationToken);
                await AdvanceLifecycleAsync(entity, cancellationToken);
                _logger.LogInformation("Order {OrderId} fully processed (status: {Status}).",
                entity.RowKey, entity.Status);
            }
            catch (InvalidOrderMessageException ex)
            {
                // The message itself is bad (not valid JSON / missing fields), so retrying
                // will not help. Rethrow anyway: after MaxDequeueCount attempts the host
                // moves it to the poison queue.
                _logger.LogError(ex,
                "Invalid order message {MessageId} (attempt {Attempt} of {Max}). Reason: {Reason}. " +
                "It will be moved to 'order-processing-queue-poison' after the final attempt.",
                message.MessageId, message.DequeueCount, MaxDequeueCount, ex.Message);
                throw;
            }
            catch (Exception ex)
            {
                // Temporary failure (e.g. Azurite unreachable): rethrow so it is retried.
                _logger.LogError(ex,
                "Failed to process message {MessageId} (attempt {Attempt} of {Max}). " +
                "It will be retried and then moved to 'order-processing-queue-poison'.",
                message.MessageId, message.DequeueCount, MaxDequeueCount);
                throw;
            }
        }

        // Turns the queue JSON into a validated Order, or throws InvalidOrderMessageException.
        private static Order ParseOrder(string messageText)
        {
            if (string.IsNullOrWhiteSpace(messageText))
                throw new InvalidOrderMessageException("The queue message was empty.");
            Order? order;
            try
            {
                order = JsonSerializer.Deserialize<Order>(messageText, JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new InvalidOrderMessageException("The queue message is not valid JSON.", ex);
            }
            if (order is null)
                throw new InvalidOrderMessageException("The message could not be read as an order.");
            if (string.IsNullOrWhiteSpace(order.OrderId))
                throw new InvalidOrderMessageException("OrderId is missing.");
            if (HasInvalidKeyCharacters(order.OrderId))
                throw new InvalidOrderMessageException("OrderId has characters Azure Tables rejects.");
            if (string.IsNullOrWhiteSpace(order.CustomerName))
                throw new InvalidOrderMessageException("CustomerName is missing.");
            if (order.SelectedItemSKUs is null || order.SelectedItemSKUs.Count == 0
            || order.SelectedItemSKUs.Any(string.IsNullOrWhiteSpace))
                throw new InvalidOrderMessageException("SelectedItemSKUs needs at least one SKU.");
            if (order.TotalPrice <= 0)
                throw new InvalidOrderMessageException("TotalPrice must be greater than zero.");
            return order;
        }
        // Writes the new row (Status = Received). Queues deliver "at least once", so if the
        // row already exists (a redelivered message) we carry on with it instead of failing.
        private async Task<OrderEntity> CreateOrderRecordAsync(
        Order order, long dequeueCount, CancellationToken cancellationToken)
        {
            var orderTime = ToUtc(order.OrderTimestamp);
            var entity = new OrderEntity
            {
                PartitionKey = orderTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                RowKey = order.OrderId.Trim(),
                CustomerName = order.CustomerName.Trim(),
                ItemSkus = string.Join(",", order.SelectedItemSKUs.Select(s => s.Trim())),
                TotalPrice = (double)order.TotalPrice,
                OrderTimestamp = new DateTimeOffset(orderTime),
                DequeueCount = dequeueCount
            };
            entity.ApplyStatus(OrderStatus.Received);
            try
            {
                await _ordersTable.AddEntityAsync(entity, cancellationToken);
                _logger.LogInformation(
                "Order {OrderId} saved to Orders table with status Received.", entity.RowKey);
                return entity;
            }
            catch (RequestFailedException ex) when (ex.Status == 409)
            {
                _logger.LogWarning(
                "Order {OrderId} already exists in Orders table. Resuming its lifecycle.",
                entity.RowKey);
                var existing = await _ordersTable.GetEntityAsync<OrderEntity>(
                entity.PartitionKey, entity.RowKey, cancellationToken: cancellationToken);
                return existing.Value;
            }
        }

        // Simulates the barista: Received -> Preparing -> Ready -> Collected,
        // saving each new status to the Orders table after a short delay.
        private async Task AdvanceLifecycleAsync(
        OrderEntity entity, CancellationToken cancellationToken)
        {
            var delay = GetStepDelay();
            var current = OrderStatus.IndexOf(entity.Status);
            for (var i = current + 1; i < OrderStatus.Lifecycle.Length; i++)
            {
                await Task.Delay(delay, cancellationToken);
                entity.ApplyStatus(OrderStatus.Lifecycle[i]);
                await _ordersTable.UpdateEntityAsync(
                entity, ETag.All, TableUpdateMode.Replace, cancellationToken);
                _logger.LogInformation(
                "Order {OrderId} status changed to {Status}.", entity.RowKey, entity.Status);
            }
        }
        // Delay between statuses. Set ORDER_STATUS_DELAY_SECONDS to change it (default 5).
        private static TimeSpan GetStepDelay()
        {
            var setting = Environment.GetEnvironmentVariable("ORDER_STATUS_DELAY_SECONDS");
            var seconds = int.TryParse(setting, out var parsed) ? parsed : DefaultStepDelaySeconds;
            return TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 60));
        }
        private static DateTime ToUtc(DateTime value)
        {
            if (value == default) return DateTime.UtcNow; // no timestamp supplied
            return value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();
        }
        // Azure Table keys may not contain / \ # ? or control characters.
        private static bool HasInvalidKeyCharacters(string value) =>
        value.Any(c => c is '/' or '\\' or '#' or '?' || char.IsControl(c));
    }
    // Thrown when a queue message cannot be turned into a valid order.
    public class InvalidOrderMessageException : Exception
    {
        public InvalidOrderMessageException(string message) : base(message) { }
        public InvalidOrderMessageException(string message, Exception inner)

            : base(message, inner) { }
    }
}