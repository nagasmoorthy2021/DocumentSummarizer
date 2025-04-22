using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Indexes;
using Azure;
using System.ComponentModel.DataAnnotations;
using System.Runtime.CompilerServices;

namespace DocumentSummarizerAPI.Helper
{
    public static class EnsureSearchIndex
    {
        public static async Task EnsureSearchIndexExistsAsync(string endpoint, string indexName, string key)
        {
            var indexClient = new SearchIndexClient(
                new Uri(endpoint),
                new AzureKeyCredential(key)
            );
            var response = indexClient.GetIndexNamesAsync();
            List<string> indexnames = new List<string>();
            await foreach (var name in response)
            {
                indexnames.Add(name);
            }
            if (indexnames.Contains(indexName))
            {
                return;
            }

            var fields = new List<SearchField>
            {
                new SimpleField("Id", SearchFieldDataType.String) { IsKey = true },
                new SearchableField("Content") { IsFilterable = true, IsSortable = true, AnalyzerName = LexicalAnalyzerName.EnLucene }
            };
            var indexDefinition = new SearchIndex(indexName, fields);
            await indexClient.CreateOrUpdateIndexAsync(indexDefinition);
        }

        public class SearchDoc
        {
            [SimpleField(IsKey = true)]
            [Required]
            public string Id { get; set; }

            [SearchableField(IsSortable = true, IsFilterable = true)]
            [Required]

            public string Content { get; set; }
        }

    }
}
