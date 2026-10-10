using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services.AddSingleton(sp =>
{
    var connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage")
        ?? "UseDevelopmentStorage=true";

    var serviceClient = new TableServiceClient(connectionString);
    var tableClient = serviceClient.GetTableClient("MenuItems");
    tableClient.CreateIfNotExists();

    return tableClient;
});

builder.Services.AddSingleton(sp =>
{
    var connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage")
        ?? "UseDevelopmentStorage=true";

    var blobServiceClient = new BlobServiceClient(connectionString);
    var containerClient = blobServiceClient.GetBlobContainerClient("staff-docs");
    containerClient.CreateIfNotExists();

    return containerClient;
});

// Order queue producer.
// Messages are Base64-encoded because the Functions queue trigger expects Base64 by default.
// Retries are kept short so a missing Azurite fails fast instead of hanging the request.
builder.Services.AddSingleton(sp =>
{
    var connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage")
        ?? "UseDevelopmentStorage=true";

    var options = new QueueClientOptions
    {
        MessageEncoding = QueueMessageEncoding.Base64
    };
    options.Retry.MaxRetries = 2;
    options.Retry.Delay = TimeSpan.FromMilliseconds(500);

    return new QueueClient(connectionString, "order-processing-queue", options);
});

builder.Build().Run();