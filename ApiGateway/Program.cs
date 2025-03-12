using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Hosting;
using System.Net.Http.Headers;

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

app.MapPost("/upload", async (HttpRequest request) =>
{
    try
    {
        logger.LogInformation("Received new PDF upload request");

        if (!request.HasFormContentType)
        {
            logger.LogWarning("Request is not in valid format");
            return Results.BadRequest("The received content is not valid.");
        }

        var form = await request.ReadFormAsync();
        var file = form.Files.FirstOrDefault();

        if (file == null)
        {
            logger.LogWarning("No file was provided in the request");
            return Results.BadRequest("No file was provided.");
        }

        logger.LogInformation($"Received file: {file.FileName}, size: {file.Length / 1024.0:F2} KB");

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

        // Faster transfer using direct stream instead of temporary storage
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

        // Add request information
        logger.LogInformation("Sending file for processing...");
        httpClient.Timeout = TimeSpan.FromSeconds(60); // One minute timeout for processing

        // Send file to PDFProcessingService
        var response = await httpClient.PostAsync(pdfProcessingServiceUrl, content);

        // Read response content from service
        var responseBody = await response.Content.ReadAsStringAsync();
        logger.LogInformation($"Response from service: {response.StatusCode}");

        if (response.IsSuccessStatusCode)
        {
            return Results.Ok(new
            {
                message = "File sent for processing successfully.",
                details = responseBody
            });
        }
        else
        {
            logger.LogWarning($"Processing service returned an error: {response.StatusCode}");
            return Results.Problem(
                detail: responseBody,
                title: "An error occurred while sending the file for processing",
                statusCode: (int)response.StatusCode,
                instance: "/upload"
            );
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "General error in API Gateway");
        return Results.Problem(
            detail: ex.StackTrace,
            title: "General error in API Gateway",
            statusCode: 500,
            instance: "/upload",
            type: "https://httpstatuses.com/500"
        );
    }
});

app.Run("http://0.0.0.0:5000");