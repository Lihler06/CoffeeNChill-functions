using System.Net;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Azure.Storage.Queues;
using CoffeeNChill.Functions.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
namespace CoffeeNChill.Functions.Functions
{
    // Read-only endpoints so Postman (and the video) can SEE what ProcessOrderQueue did,
    // plus one demo endpoint that deliberately sends a bad message to prove poison handling.
    public class OrderStatusFunctions
    {
        private const string OrdersTableName = "Orders";
        private const string MainQueueName = "order-processing-queue";
        private const string PoisonQueueName = "order-processing-queue-poison";
        private readonly TableClient _ordersTable;
        private readonly QueueServiceClient _queueServiceClient;
        private readonly ILogger<OrderStatusFunctions> _logger;
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        public OrderStatusFunctions(
        TableServiceClient tableServiceClient,
        QueueServiceClient queueServiceClient,
        ILogger<OrderStatusFunctions> logger)
        {
            _ordersTable = tableServiceClient.GetTableClient(OrdersTableName);
            _queueServiceClient = queueServiceClient;
            _logger = logger;
        }
        // GET /api/orders/status/{orderId} -> current status of one order
        [Function("GetOrderStatus")]
        public async Task<HttpResponseData> GetOrderStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get",
Route = "orders/status/{orderId}")] HttpRequestData req,
        string orderId)
        {
            if (string.IsNullOrWhiteSpace(orderId))
                return await Json(req, HttpStatusCode.BadRequest,
                new { error = "orderId is required." });
            var id = orderId.Trim();
            try
            {
                await _ordersTable.CreateIfNotExistsAsync();
                var matches = new List<OrderEntity>();
                await foreach (var entity in
                _ordersTable.QueryAsync<OrderEntity>(o => o.RowKey == id))
                {
                    matches.Add(entity);
                }
                if (matches.Count == 0)
                    return await Json(req, HttpStatusCode.NotFound, new
                    {
                        error = $"Order '{id}' was not found. " +
                    "It may still be waiting in the queue."
                    });
                var latest = matches.OrderByDescending(o => o.OrderTimestamp).First();

                return await Json(req, HttpStatusCode.OK, OrderStatusDto.FromEntity(latest));
            }
            catch (RequestFailedException ex)
            {
                _logger.LogError(ex, "Orders table error while reading order {OrderId}.", id);
                return await Json(req, HttpStatusCode.ServiceUnavailable,
                new { error = "The Orders table is currently unavailable. Try again shortly." });
            }
        }
        // GET /api/orders/status?date=2026-10-12&status=Ready -> list orders (filters optional)
        [Function("GetAllOrders")]
        public async Task<HttpResponseData> GetAllOrders(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get",
Route = "orders/status")] HttpRequestData req)
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var date = query["date"]?.Trim();
            var status = query["status"]?.Trim();
            // Both values are validated, so they are safe to use inside the table filter.
            if (!string.IsNullOrEmpty(date) &&
            !DateOnly.TryParseExact(date, "yyyy-MM-dd", out _))
                return await Json(req, HttpStatusCode.BadRequest,
                new { error = "date must use the format yyyy-MM-dd." });
            if (!string.IsNullOrEmpty(status))
            {
                status = OrderStatus.Lifecycle.FirstOrDefault(s =>
                s.Equals(status, StringComparison.OrdinalIgnoreCase));
                if (status is null)
                    return await Json(req, HttpStatusCode.BadRequest, new
                    {
                        error = "status must be one of: " + string.Join(", ", OrderStatus.Lifecycle)
                    });
            }
            var filters = new List<string>();
            if (!string.IsNullOrEmpty(date)) filters.Add($"PartitionKey eq '{date}'");
            if (!string.IsNullOrEmpty(status)) filters.Add($"Status eq '{status}'");
            string? filter = filters.Count == 0 ? null : string.Join(" and ", filters);
            try
            {
                await _ordersTable.CreateIfNotExistsAsync();
                var orders = new List<OrderStatusDto>();
                await foreach (var entity in _ordersTable.QueryAsync<OrderEntity>(filter))
                    orders.Add(OrderStatusDto.FromEntity(entity));
                return await Json(req, HttpStatusCode.OK, new
                {
                    count = orders.Count,
                    orders = orders.OrderByDescending(o => o.OrderTimestamp)
                });
            }
            catch (RequestFailedException ex)
            {
                _logger.LogError(ex, "Orders table error while listing orders.");
                return await Json(req, HttpStatusCode.ServiceUnavailable,
                new { error = "The Orders table is currently unavailable. Try again shortly." });
            }
        }
        // GET /api/orders/poison -> peek (without removing) messages that failed processing
        [Function("GetPoisonMessages")]
        public async Task<HttpResponseData> GetPoisonMessages(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get",
Route = "orders/poison")] HttpRequestData req)
        {
            try
            {
                var poisonQueue = _queueServiceClient.GetQueueClient(PoisonQueueName);
                var peeked = await poisonQueue.PeekMessagesAsync(maxMessages: 32);
                var messages = peeked.Value.Select(m => new
                {
                    messageId = m.MessageId,
                    insertedOn = m.InsertedOn,
                    dequeueCount = m.DequeueCount,
                    body = DecodeBody(m.MessageText)
                }).ToList();
                return await Json(req, HttpStatusCode.OK,
                new { queue = PoisonQueueName, count = messages.Count, messages });
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // The poison queue only exists once a message has failed,
                // so "not found" simply means "no failures yet".
                return await Json(req, HttpStatusCode.OK,
                new { queue = PoisonQueueName, count = 0, messages = Array.Empty<object>() });
            }
            catch (RequestFailedException ex)
            {
                _logger.LogError(ex, "Queue error while reading the poison queue.");
                return await Json(req, HttpStatusCode.ServiceUnavailable,
                new { error = "The poison queue is currently unavailable. Try again shortly." });
            }
        }
        // POST /api/orders/poison-test -> DEMO ONLY: puts a malformed message on
        // order-processing-queue so you can show it being retried and moved to the
        // poison queue. Remove this function before the final deployment.
        [Function("SimulatePoisonMessage")]
        public async Task<HttpResponseData> SimulatePoisonMessage(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post",
Route = "orders/poison-test")] HttpRequestData req)
        {
            try
            {
                var queue = _queueServiceClient.GetQueueClient(MainQueueName);
                await queue.CreateIfNotExistsAsync();
                var badMessage = $"THIS-IS-NOT-VALID-JSON-{Guid.NewGuid():N}";
                // The trigger expects Base64, exactly like the real producer sends it.
                var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(badMessage));
                await queue.SendMessageAsync(encoded);
                _logger.LogWarning("Demo poison message placed on {Queue}.", MainQueueName);
                return await Json(req, HttpStatusCode.Accepted, new
                {
                    message = "A malformed message was queued. " +
                "After 3 failed attempts it moves to the poison queue.",
                    checkWith = "GET /api/orders/poison"
                });
            }
            catch (RequestFailedException ex)
            {
                _logger.LogError(ex, "Queue error while placing the demo poison message.");
                return await Json(req, HttpStatusCode.ServiceUnavailable,
                new { error = "The order queue is currently unavailable. Try again shortly." });
            }
        }
        // Poison messages are stored as Base64 text; show the readable version if possible.
        private static string DecodeBody(string raw)
        {
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(raw));
            }
            catch (FormatException)
            {
                return raw;
            }
        }
        // Writes a camelCase JSON response with the given status code.
        private static async Task<HttpResponseData> Json(
        HttpRequestData req, HttpStatusCode statusCode, object payload)
        {
            var response = req.CreateResponse(statusCode);
            response.Headers.Add("Content-Type", "application/json; charset=utf-8");
            await response.WriteStringAsync(JsonSerializer.Serialize(payload, JsonOptions));
            return response;
        }
    }
}