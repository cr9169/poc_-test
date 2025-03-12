using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

/// <summary>
/// API Gateway service for handling PDF uploads and forwarding to the processing service
/// </summary>
var builder = WebApplication.CreateBuilder(args);

// Set size limits - updated to 100MB
const long MAX_FILE_SIZE = 100L * 1024 * 1024; // 100MB

// Set request size limit
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = MAX_FILE_SIZE;
});

// Set size limit for `multipart/form-data` files
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = MAX_FILE_SIZE;
});

// Add logger
builder.Logging.AddConsole();

var app = builder.Build();
var logger = app.Logger;

// PDF processing service URL
string pdfProcessingServiceUrl = "http://localhost:5001/process";

/// <summary>
/// Endpoint for uploading PDF files
/// </summary>
app.MapPost("/upload", async (HttpRequest request) =>
{
    // Initialize simple timing dictionary (in seconds)
    var timings = new Dictionary<string, double>();
    var stopwatch = new Stopwatch();
    stopwatch.Start();

    try
    {
        logger.LogInformation("Received new PDF upload request");

        if (!request.HasFormContentType)
        {
            logger.LogWarning("Request is not in valid format");
            return Results.BadRequest("The received content is not valid.");
        }

        // Read form data
        var formReadStart = stopwatch.Elapsed;
        var form = await request.ReadFormAsync();
        var file = form.Files.FirstOrDefault();
        timings["upload_time"] = (stopwatch.Elapsed - formReadStart).TotalSeconds;

        if (file == null)
        {
            logger.LogWarning("No file was provided in the request");
            return Results.BadRequest("No file was provided.");
        }

        logger.LogInformation($"Received file: {file.FileName}, size: {file.Length / 1024.0:F2} KB");

        // Validate file type and size
        if (file.ContentType != "application/pdf" && !file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("File type is not PDF");
            return Results.BadRequest("File type is not supported. Only PDF files are supported at this time.");
        }

        if (file.Length > MAX_FILE_SIZE)
        {
            logger.LogWarning($"File exceeds maximum allowed size ({MAX_FILE_SIZE / 1024.0 / 1024.0:F2} MB)");
            return Results.BadRequest($"File size exceeds the allowed limit of {MAX_FILE_SIZE / 1024.0 / 1024.0:F2} MB.");
        }

        // Send to processing service
        var processingStart = stopwatch.Elapsed;
        using var httpClient = new HttpClient();
        using var content = new MultipartFormDataContent();

        // Create Stream from file directly without intermediate storage
        var fileStream = file.OpenReadStream();
        var streamContent = new StreamContent(fileStream);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType);

        content.Add(
            streamContent,
            name: "file",
            fileName: file.FileName
        );

        // Set timeout and send
        httpClient.Timeout = TimeSpan.FromSeconds(60); // One minute timeout for processing
        logger.LogInformation("Sending file for processing...");

        var response = await httpClient.PostAsync(pdfProcessingServiceUrl, content);
        timings["processing_time"] = (stopwatch.Elapsed - processingStart).TotalSeconds;

        // Calculate total time
        stopwatch.Stop();
        timings["total_time"] = stopwatch.Elapsed.TotalSeconds;

        if (response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync();
            logger.LogInformation($"Response from service: {response.StatusCode}");

            // Try to parse processing service response
            Dictionary<string, object> result = new Dictionary<string, object>
            {
                { "message", "File processed successfully." },
                { "gateway_timings", new {
                    upload_seconds = Math.Round(timings["upload_time"], 2),
                    processing_seconds = Math.Round(timings["processing_time"], 2),
                    total_seconds = Math.Round(timings["total_time"], 2)
                }}
            };

            try
            {
                // Add the processing service response
                var processingResponse = JsonSerializer.Deserialize<Dictionary<string, object>>(responseBody,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (processingResponse != null)
                {
                    result["processing_details"] = processingResponse;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning($"Couldn't parse processing service response: {ex.Message}");
                result["processing_service_response"] = responseBody;
            }

            logger.LogInformation($"Request completed in {timings["total_time"]:F2} seconds");
            return Results.Ok(result);
        }
        else
        {
            var responseBody = await response.Content.ReadAsStringAsync();
            logger.LogWarning($"Processing service returned an error: {response.StatusCode}");
            return Results.Problem(
                detail: responseBody,
                title: "An error occurred while processing the file",
                statusCode: (int)response.StatusCode,
                instance: "/upload"
            );
        }
    }
    catch (Exception ex)
    {
        // Record error time
        stopwatch.Stop();
        timings["error_time"] = stopwatch.Elapsed.TotalSeconds;

        logger.LogError(ex, $"General error in API Gateway after {timings["error_time"]:F2} seconds");
        return Results.Problem(
            detail: ex.StackTrace,
            title: "General error in API Gateway",
            statusCode: 500,
            instance: "/upload",
            type: "https://httpstatuses.com/500"
        );
    }
});

/// <summary>
/// Health check endpoint
/// </summary>
/// <returns>A simple health status</returns>
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Run("http://0.0.0.0:5000");