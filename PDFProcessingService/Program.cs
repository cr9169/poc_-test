using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Nest; // לקוח Elasticsearch

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// הגדרות Elasticsearch – נניח שה- Elasticsearch זמין בכתובת dns פנימית
var esSettings = new ConnectionSettings(new Uri("http://elasticsearch:9200"))
    .DefaultIndex("pdf_documents");
var esClient = new ElasticClient(esSettings);

app.MapPost("/process", async (HttpRequest request) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest("התוכן שהתקבל אינו תקין.");

    var form = await request.ReadFormAsync();
    var file = form.Files.FirstOrDefault();
    if (file == null)
        return Results.BadRequest("לא סופק קובץ.");

    // שמירה זמנית של הקובץ לעיבוד
    string tempFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.pdf");
    using (var stream = new FileStream(tempFilePath, FileMode.Create))
    {
        await file.CopyToAsync(stream);
    }

    // נניח שמקסימום גודל הקובץ הוא 1GB, נבחר באסטרטגיה:
    // אם הקובץ גדול מ-100 מגה, נשתמש בעיבוד מקבילי, אחרת קריאה סינכרונית.
    long fileSize = new FileInfo(tempFilePath).Length;
    const long parallelThreshold = 100L * 1024 * 1024; // 100MB
    string parsedContent;
    if (fileSize >= parallelThreshold)
    {
        parsedContent = await ProcessLargeFileInChunksAsync(tempFilePath);
    }
    else
    {
        // עיבוד פשוט - במקרה של קובץ קטן
        parsedContent = await File.ReadAllTextAsync(tempFilePath);
    }

    // כאן אפשר להוסיף חילוץ מטא-דאטה נוסף, למשל שמירת שם הקובץ, גודל, וכו'
    var document = new
    {
        FileName = file.FileName,
        Content = parsedContent,
        ProcessedAt = DateTime.UtcNow
    };

    // שליחת המסמך ל-Elasticsearch (ניתן להרחיב לשימוש ב-Bulk API בעת הצורך)
    var indexResponse = await esClient.IndexDocumentAsync(document);

    // ניקוי הקובץ הזמני
    File.Delete(tempFilePath);

    return indexResponse.IsValid
        ? Results.Ok("PDF עבר עיבוד ואונדקס בהצלחה.")
        : Results.Problem("אירעה שגיאה בעת אינדוקס המסמך.");
});

app.Run("http://0.0.0.0:5001");


// פונקציה לעיבוד קבצים גדולים באמצעות חלוקה ל-chunks
static async Task<string> ProcessLargeFileInChunksAsync(string filePath)
{
    long fileSize = new FileInfo(filePath).Length;
    // נשתמש בגודל chunk של 10MB
    long chunkSize = 10L * 1024 * 1024;
    int chunkCount = (int)System.Math.Ceiling((double)fileSize / chunkSize);

    var results = new ConcurrentDictionary<int, string>();

    using (var mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, "LargePdfMap", fileSize))
    {
        Parallel.For(0, chunkCount, new ParallelOptions { MaxDegreeOfParallelism = System.Environment.ProcessorCount }, i =>
        {
            long offset = i * chunkSize;
            long size = System.Math.Min(chunkSize, fileSize - offset);
            using (var stream = mmf.CreateViewStream(offset, size, MemoryMappedFileAccess.Read))
            using (var reader = new StreamReader(stream))
            {
                string chunkContent = reader.ReadToEnd();
                // ניתן להוסיף עיבוד מתקדם (למשל ניקוי טקסט, חילוץ נתונים) עבור כל chunk
                results[i] = ProcessChunkData(chunkContent);
            }
        });
    }

    // איחוד תוצאות לפי הסדר המקורי
    var combined = string.Join("", results.OrderBy(kvp => kvp.Key).Select(kvp => kvp.Value));
    return combined;
}

static string ProcessChunkData(string data)
{
    // לדוגמה: ניקוי בסיסי של הטקסט
    return data.Trim();
}
