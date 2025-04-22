// API (Upload & Search Endpoints)
using Microsoft.AspNetCore.Mvc;
using Azure.Storage.Blobs;
using Azure;
using Azure.AI.FormRecognizer.DocumentAnalysis;
using Azure.AI.OpenAI;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using OpenAI.Chat;
using DocumentSummarizerAPI.Helper;

namespace DocumentSummarizerAPI.Controllers;

[ApiController]
[Route("api")]
public class DocumentController : ControllerBase
{
    private readonly IConfiguration _config;

    public DocumentController(IConfiguration config)
    {
        _config = config;
    }

    [HttpPost("upload")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> UploadDocument(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest("No file uploaded");

        // 1. Upload to Azure Blob Storage
        var serviceClient = new BlobServiceClient(_config["Blob:ConnectionString"]);
        var containerClient = serviceClient.GetBlobContainerClient(_config["Blob:ContainerName"]);
        var blobClient = containerClient.GetBlobClient(file.FileName);

        try
        {
            await containerClient.CreateIfNotExistsAsync();
            await blobClient.UploadAsync(file.OpenReadStream(), overwrite: true);
        }
        catch (RequestFailedException ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, $"Azure Blob Storage error: {ex.ErrorCode}, {ex.Message}");
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, $"Azure Blob upload error: {ex.Message}");
        }

        var sasUri = blobClient.GenerateSasUri(Azure.Storage.Sas.BlobSasPermissions.Read, DateTimeOffset.UtcNow.AddMinutes(10));

        // 2. Extract text using Azure Document Intelligence
        var documentClient = new DocumentAnalysisClient(new Uri(_config["AzureAI:DocumentEndpoint"]), new AzureKeyCredential(_config["AzureAI:DocumentKey"]));
        var operation = await documentClient.AnalyzeDocumentFromUriAsync(WaitUntil.Completed, "prebuilt-read", sasUri);
        string fullText = string.Join(" ", operation.Value.Pages.SelectMany(p => p.Lines).Select(l => l.Content));

        // 3. Summarize text with Azure OpenAI
        var openAIClient = new AzureOpenAIClient(new Uri(_config["AzureAI:OpenAIEndpoint"]), new AzureKeyCredential(_config["AzureAI:OpenAIDocumentKey"]));

        var chatClient = openAIClient.GetChatClient(_config["AzureAI:DeploymentName"]);
        var completion = await chatClient.CompleteChatAsync(new List<ChatMessage>
        {
            ChatMessage.CreateSystemMessage("You are a helpful assistant that summarizes documents."),
            ChatMessage.CreateUserMessage($"Summarize the following document: {fullText}")
        });

        string summary = completion.Value.Content[0].Text;

        // 4. Index with Cognitive Search here
        var searchEndpoint = _config["AzureSearch:Endpoint"];
        var searchKey = _config["AzureSearch:Key"];
        var indexName = _config["AzureSearch:IndexName"];
        await EnsureSearchIndex.EnsureSearchIndexExistsAsync(searchEndpoint, indexName, searchKey);
        var searchClient = new SearchClient(new Uri(searchEndpoint), indexName, new AzureKeyCredential(searchKey));

        var document = new EnsureSearchIndex.SearchDoc
        {
            Id = Guid.NewGuid().ToString(),
            Content = summary
        };
        await searchClient.UploadDocumentsAsync(new[] { document });
        return Ok(new { summary });

    }

    [HttpGet("search")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> SearchDocuments([FromQuery] string q)
    {
        var searchEndpoint = _config["AzureSearch:Endpoint"];
        var searchIndexName = _config["AzureSearch:IndexName"];
        var searchKey = _config["AzureSearch:Key"];

        if (string.IsNullOrEmpty(searchEndpoint))
            return StatusCode(StatusCodes.Status500InternalServerError, "Azure Search endpoint is not configured.");
        if (string.IsNullOrEmpty(searchIndexName))
            return StatusCode(StatusCodes.Status500InternalServerError, "Azure Search index name is not configured.");
        if (string.IsNullOrEmpty(searchKey))
            return StatusCode(StatusCodes.Status500InternalServerError, "Azure Search key is not configured.");

        var searchClient = new SearchClient(new Uri(searchEndpoint), searchIndexName, new AzureKeyCredential(searchKey));

        var results = await searchClient.SearchAsync<SearchDocument>(q);

        if (results.GetRawResponse().Status != 200)
            return StatusCode(StatusCodes.Status500InternalServerError, $"Search failed with status code: {results.GetRawResponse().Status}");

        try
        {
            var output = results.Value.GetResults()
          .Select(r => r.Document["Content"]?.ToString())
          .Where(s => !string.IsNullOrEmpty(s))
          .ToList();

            return Ok(new { results = output });
        }
        catch(Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, $"Error processing search results: {ex.Message}");
        }

    }
}

