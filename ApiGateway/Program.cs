using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using System.Net.Http.Headers;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// הגדרות – ניתן לשמור כתובת השירות של ה-PDFProcessingService כמשתנה קונפיגורציה
// כאן מניחים שהשירות זמין בכתובת http://pdfprocessingservice:5001
string pdfProcessingServiceUrl = "http://pdfprocessingservice:5001/process";

app.MapPost("/upload", async (HttpRequest request) =>
{
    // בדיקה שהבקשה היא מסוג multipart/form-data
    if (!request.HasFormContentType)
    {
        return Results.BadRequest("התוכן שהתקבל אינו תקין.");
    }

    var form = await request.ReadFormAsync();
    var file = form.Files.FirstOrDefault();
    if (file == null)
    {
        return Results.BadRequest("לא סופק קובץ.");
    }

    // בדיקת ה-Header / Content-Type או סיומת הקובץ
    // לדוגמה, נבדוק את Content-Type או את הסיומת:
    if (file.ContentType != "application/pdf" && !file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest("סוג הקובץ אינו נתמך. רק קבצי PDF נתמכים בשלב זה.");
    }

    // בדיקת גודל – נניח מקסימום 1 גיגהבייט
    long maxSizeBytes = 1L * 1024 * 1024 * 1024; // 1GB
    if (file.Length > maxSizeBytes)
    {
        return Results.BadRequest("גודל הקובץ עובר את המגבלה המותרת.");
    }

    // שמירה זמנית של הקובץ במערכת (ניתן לשפר ולהשתמש באחסון אובייקטים בסביבת פרודקשן)
    string tempFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}_{file.FileName}");
    using (var stream = new FileStream(tempFilePath, FileMode.Create))
    {
        await file.CopyToAsync(stream);
    }

    // שליחת הקובץ לשירות PDFProcessingService באמצעות HTTP POST
    using var httpClient = new HttpClient();
    using var content = new MultipartFormDataContent();
    using var fileStream = new FileStream(tempFilePath, FileMode.Open, FileAccess.Read);
    content.Add(new StreamContent(fileStream), "file", file.FileName);

    var response = await httpClient.PostAsync(pdfProcessingServiceUrl, content);

    // מחיקת הקובץ הזמני
    File.Delete(tempFilePath);

    if (response.IsSuccessStatusCode)
    {
        return Results.Ok("הקובץ נשלח לעיבוד בהצלחה.");
    }
    else
    {
        return Results.Problem("אירעה שגיאה בעת שליחת הקובץ לעיבוד.");
    }
});

app.Run("http://0.0.0.0:5000");
