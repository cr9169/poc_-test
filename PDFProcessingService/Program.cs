using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using Nest;
using Microsoft.AspNetCore.Http.Features;
using System.Threading;

/// <summary>
/// PDF Processing Service - Processes PDF files and indexes their content to Elasticsearch
/// </summary>
var builder = WebApplication.CreateBuilder(args);

// Set size limits - updated to 100MB
const long MAX_FILE_SIZE = 100L * 1024 * 1024; // 100MB

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = MAX_FILE_SIZE;
});

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = MAX_FILE_SIZE;
});

// Add logger to service
builder.Logging.AddConsole();

var app = builder.Build();
var logger = app.Logger;

// Elasticsearch settings
var esSettings = new ConnectionSettings(new Uri("http://localhost:9200"))
    .DefaultIndex("pdf_documents7")
    .DisableDirectStreaming();
var esClient = new ElasticClient(esSettings);

/// <summary>
/// Endpoint for processing PDF files
/// </summary>
app.MapPost("/process", async (HttpRequest request) =>
{
    // Initialize simple timing dictionary (in seconds)
    var timings = new Dictionary<string, double>();
    var stopwatch = new Stopwatch();
    stopwatch.Start();

    try
    {
        logger.LogInformation("Received new PDF processing request");

        if (!request.HasFormContentType)
        {
            logger.LogWarning("Request is not in valid format");
            return Results.BadRequest("The received content is not valid.");
        }

        // Read form data
        var fileReadStart = stopwatch.Elapsed;
        var form = await request.ReadFormAsync();
        var file = form.Files.FirstOrDefault();
        timings["file_reading"] = (stopwatch.Elapsed - fileReadStart).TotalSeconds;

        if (file == null)
        {
            logger.LogWarning("No file was provided in the request");
            return Results.BadRequest("No file was provided.");
        }

        logger.LogInformation($"Received file: {file.FileName}, size: {file.Length / 1024.0:F2} KB");

        // Check file size
        if (file.Length > MAX_FILE_SIZE)
        {
            logger.LogWarning($"File exceeds maximum allowed size ({MAX_FILE_SIZE / 1024.0 / 1024.0:F2} MB)");
            return Results.BadRequest($"File size exceeds the allowed limit of {MAX_FILE_SIZE / 1024.0 / 1024.0:F2} MB.");
        }

        // Process content
        var processingStart = stopwatch.Elapsed;
        string parsedContent;

        // Determine processing strategy
        if (file.Length >= 10 * 1024 * 1024) // 10MB
        {
            logger.LogInformation("File larger than 10MB, processing in chunks");
            parsedContent = await ProcessStreamInChunksAsync(file.OpenReadStream(), file.Length);
        }
        else
        {
            logger.LogInformation("Small file, using simple processing");
            using (var streamReader = new StreamReader(file.OpenReadStream()))
            {
                parsedContent = await streamReader.ReadToEndAsync();
            }
        }
        timings["content_processing"] = (stopwatch.Elapsed - processingStart).TotalSeconds;

        // Create document for indexing with metadata
        var document = new
        {
            FileName = file.FileName,
            Content = parsedContent,
            Metadata = new
            {
                contentType = file.ContentType,
                fileName = file.FileName,
                fileSize = file.Length,
                extension = Path.GetExtension(file.FileName)
            },
            FileSize = file.Length,
            ProcessedAt = DateTime.UtcNow
        };

        // Index to Elasticsearch
        var indexingStart = stopwatch.Elapsed;
        var indexResponse = await SendToElasticsearchSafely(document);
        timings["elastic_indexing"] = (stopwatch.Elapsed - indexingStart).TotalSeconds;

        if (!indexResponse.IsValid)
        {
            logger.LogError($"Error in indexing: {indexResponse.DebugInformation}");
            return Results.Problem($"An error occurred while indexing the document. Debug Info: {indexResponse.DebugInformation}");
        }

        // Calculate total processing time
        stopwatch.Stop();
        timings["total_time"] = stopwatch.Elapsed.TotalSeconds;

        logger.LogInformation($"PDF processed and indexed successfully in {timings["total_time"]:F2} seconds");

        return Results.Ok(new
        {
            message = "PDF processed and indexed successfully.",
            documentId = indexResponse.Id,
            timings = new
            {
                total_seconds = Math.Round(timings["total_time"], 2),
                file_reading_seconds = Math.Round(timings["file_reading"], 2),
                content_processing_seconds = Math.Round(timings["content_processing"], 2),
                elastic_indexing_seconds = Math.Round(timings["elastic_indexing"], 2)
            }
        });
    }
    catch (Exception ex)
    {
        // Record error time
        stopwatch.Stop();
        timings["error_time"] = stopwatch.Elapsed.TotalSeconds;

        logger.LogError(ex, $"General error in PDF processing after {timings["error_time"]:F2} seconds");
        return Results.Problem(
            detail: ex.StackTrace,
            title: "General error in PDFProcessingService",
            statusCode: 500,
            instance: "/process",
            type: "https://httpstatuses.com/500"
        );
    }
});

app.Run("http://0.0.0.0:5001");

/// <summary>
/// Processes a large stream in chunks with parallel processing
/// </summary>
/// <param name="stream">The input stream to process</param>
/// <param name="streamLength">The length of the stream in bytes</param>
/// <returns>The processed content as a string</returns>
static async Task<string> ProcessStreamInChunksAsync(Stream stream, long streamLength)
{
    const int CHUNK_SIZE = 2 * 1024 * 1024; // 2MB chunks
    byte[] buffer = new byte[CHUNK_SIZE];
    var results = new ConcurrentDictionary<int, string>();
    int chunkIndex = 0;
    int maxConcurrentTasks = Math.Min(Environment.ProcessorCount, 4); // Limit concurrent tasks

    // Simulating a manual limited task queue for controlled parallel processing
    var tasks = new List<Task>();
    var chunkDataList = new List<(int Index, byte[] Data, int Length)>();

    // Read entire stream and divide it into chunks
    int bytesRead;
    while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
    {
        var currentChunkIndex = chunkIndex++;
        var chunkData = new byte[bytesRead];
        Buffer.BlockCopy(buffer, 0, chunkData, 0, bytesRead);
        chunkDataList.Add((currentChunkIndex, chunkData, bytesRead));
    }

    // Process chunks in parallel but with limited concurrency
    var semaphore = new SemaphoreSlim(maxConcurrentTasks);
    foreach (var chunk in chunkDataList)
    {
        await semaphore.WaitAsync();
        tasks.Add(Task.Run(async () =>
        {
            try
            {
                string chunkContent = Encoding.UTF8.GetString(chunk.Data, 0, chunk.Length);
                results[chunk.Index] = ProcessChunkData(chunkContent);
            }
            finally
            {
                semaphore.Release();
            }
        }));
    }

    // Wait for all tasks to complete
    await Task.WhenAll(tasks);

    // Combine results in order
    var combinedBuilder = new StringBuilder((int)streamLength); // Pre-allocate approximate size
    foreach (var kvp in results.OrderBy(kvp => kvp.Key))
    {
        combinedBuilder.Append(kvp.Value);
    }

    return combinedBuilder.ToString();
}

/// <summary>
/// Processes a chunk of data
/// </summary>
/// <param name="data">The text data chunk to process</param>
/// <returns>The processed data</returns>
static string ProcessChunkData(string data)
{
    // You can add more advanced processing logic here
    return data.Trim();
}

/// <summary>
/// Sends a document to Elasticsearch with safety measures for large documents
/// </summary>
/// <param name="document">The document to index</param>
/// <returns>The index response from Elasticsearch</returns>
static async Task<IndexResponse> SendToElasticsearchSafely(object document)
{
    try
    {
        // Elasticsearch settings specific to sending
        var esSettings = new ConnectionSettings(new Uri("http://localhost:9200"))
            .DefaultIndex("pdf_documents7")
            .RequestTimeout(TimeSpan.FromMinutes(2)); // Increase timeout for larger documents

        var client = new ElasticClient(esSettings);

        // Check if document is too large
        var documentContent = document.GetType().GetProperty("Content").GetValue(document) as string;
        if (documentContent != null && documentContent.Length > 10 * 1024 * 1024) // 10MB
        {
            // Split into smaller pieces
            return await IndexLargeDocument(client, document, documentContent);
        }

        // Normal sending if document is reasonable size
        return await client.IndexDocumentAsync(document);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error sending to Elasticsearch: {ex.Message}");
        throw;
    }
}

/// <summary>
/// Indexes a large document by creating a truncated version to avoid Elasticsearch limits
/// </summary>
/// <param name="client">The Elasticsearch client</param>
/// <param name="document">The document to index</param>
/// <param name="content">The content of the document</param>
/// <returns>The index response from Elasticsearch</returns>
static async Task<IndexResponse> IndexLargeDocument(ElasticClient client, object document, string content)
{
    // Create shortened version of document
    var documentType = document.GetType();
    var props = documentType.GetProperties();

    // Create new dynamic object with shortened content
    var reducedDocument = new Dictionary<string, object>();
    foreach (var prop in props)
    {
        if (prop.Name == "Content")
        {
            // Split content into parts
            const int maxContentSize = 5 * 1024 * 1024; // 5MB max per part
            string summary = content.Length > maxContentSize
                ? content.Substring(0, maxContentSize) + $"... [truncated: {content.Length - maxContentSize} chars]"
                : content;

            reducedDocument["Content"] = summary;
            reducedDocument["ContentTruncated"] = content.Length > maxContentSize;
            reducedDocument["FullContentSize"] = content.Length;
        }
        else
        {
            reducedDocument[prop.Name] = prop.GetValue(document);
        }
    }

    // Send shortened version to Elasticsearch
    return await client.IndexAsync(new IndexRequest<object>(reducedDocument, "pdf_documents7"));
}